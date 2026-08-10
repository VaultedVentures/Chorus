using System.Net.WebSockets;
using System.Text;

namespace Chorus.Core;

/// <summary>
/// CHORUS protocol client: owns the WebSocket, the send path, and the
/// receive loop. The gobbledygook rule is enforced HERE, at the message-type
/// layer (docs/chorus-protocol-v1.md §6):
///   binary frame  → OpusDecoder24k ONLY (never JSON, never spoken as text)
///   text frame    → ServerEvent.Parse ONLY (never OPUS-decoded)
/// </summary>
public sealed class ChorusClient : IAsyncDisposable
{
    private readonly string _url;
    private readonly OpusDecoder24k _decoder = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _seq;
    private volatile bool _byeSent;

    /// <summary>Raised on the receive-loop thread for every JSON event.</summary>
    public event Action<ServerEvent>? EventReceived;

    /// <summary>Raised on the receive-loop thread for every decoded TTS frame (24 kHz PCM).</summary>
    public event Action<short[]>? TtsFrameDecoded;

    /// <summary>Raised when the receive loop exits abnormally (disconnect, protocol error).</summary>
    public event Action<Exception>? ConnectionFailed;

    public ChorusClient(string url) => _url = url;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public string? SessionId { get; private set; }

    public async Task ConnectAsync(string sessionId, string device, string mode, string agent,
        CancellationToken ct)
    {
        await DisconnectAsync();
        _byeSent = false;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(_url), cts.Token);
        _ws = ws;
        // receive loop FIRST: SendHelloAsync waits for hello_ack, which only
        // arrives through the loop (and failures surface via ConnectionFailed).
        _ = Task.Run(() => ReceiveLoopAsync(cts.Token), CancellationToken.None);
        await SendHelloAsync(sessionId, device, mode, agent, cts.Token);
    }

    public Task SendPttAsync(bool down, CancellationToken ct = default) =>
        SendTextAsync($"{{\"type\":\"ptt\",\"state\":\"{(down ? "down" : "up")}\"}}", ct);

    public Task SendWakeAsync(CancellationToken ct = default) =>
        SendTextAsync("{\"type\":\"wake\"}", ct);

    public Task SendVadAsync(string state, CancellationToken ct = default) =>
        SendTextAsync($"{{\"type\":\"vad\",\"state\":\"{state}\"}}", ct);

    public Task SendBargeInAsync(CancellationToken ct = default) =>
        SendTextAsync("{\"type\":\"barge_in\"}", ct);

    public Task SendCancelAsync(CancellationToken ct = default) =>
        SendTextAsync("{\"type\":\"cancel\"}", ct);

    public Task SendPingAsync(CancellationToken ct = default) =>
        SendTextAsync("{\"type\":\"ping\"}", ct);

    public async Task SendByeAsync(CancellationToken ct = default)
    {
        _byeSent = true;
        await SendTextAsync("{\"type\":\"bye\"}", ct);
    }

    /// <summary>Send one mic frame: control marker text, then the OPUS binary frame.</summary>
    public async Task SendAudioFrameAsync(byte[] opus, CancellationToken ct = default)
    {
        await SendTextAsync($"{{\"type\":\"audio\",\"seq\":{_seq++}}}", ct);
        await SendBinaryAsync(opus, ct);
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* already gone */ }
            _ws.Dispose();
            _ws = null;
        }
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _decoder.Dispose();
        _sendLock.Dispose();
    }

    // -- send path (serialized: ClientWebSocket allows one outstanding send) --
    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendLockedAsync(bytes, WebSocketMessageType.Text, ct);
    }

    private async Task SendBinaryAsync(byte[] data, CancellationToken ct)
    {
        await SendLockedAsync(data, WebSocketMessageType.Binary, ct);
    }

    private async Task SendLockedAsync(byte[] data, WebSocketMessageType kind, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws is null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("not connected");
            await _ws.SendAsync(data, kind, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendHelloAsync(string sessionId, string device, string mode, string agent,
        CancellationToken ct)
    {
        var hello =
            $"{{\"type\":\"hello\",\"proto\":\"{Protocol.Proto}\",\"device\":\"{device}\"," +
            $"\"mode\":\"{mode}\",\"agent\":\"{agent}\"" +
            (string.IsNullOrEmpty(sessionId) ? "" : $",\"session_id\":\"{sessionId}\"") + "}";
        await SendTextAsync(hello, ct);
        // block until hello_ack so SessionId is known before the app proceeds
        var tcs = new TaskCompletionSource<ServerEvent.HelloAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(ServerEvent e)
        {
            if (e is ServerEvent.HelloAck ack)
                tcs.TrySetResult(ack);
        }
        EventReceived += OnEvent;
        try
        {
            var ack = await tcs.Task.WaitAsync(ct);
            SessionId = ack.SessionId;
        }
        finally
        {
            EventReceived -= OnEvent;
        }
    }

    // -- receive path (the gobbledygook-critical branch) --
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _ws!.ReceiveAsync(buffer, ct);
                if (msg.MessageType == WebSocketMessageType.Close)
                {
                    // a close AFTER we sent bye is the normal protocol end
                    if (!_byeSent)
                        ConnectionFailed?.Invoke(new WebSocketException("gateway closed the connection"));
                    return;
                }
                if (msg.MessageType == WebSocketMessageType.Binary)
                {
                    // OPUS audio ONLY — never parsed as JSON, never read aloud as text
                    var opus = new byte[msg.Count];
                    Array.Copy(buffer, opus, msg.Count);
                    var pcm = _decoder.DecodeFrame(opus);
                    if (pcm.Length > 0)
                        TtsFrameDecoded?.Invoke(pcm);
                }
                else
                {
                    // JSON event ONLY — never fed to the OPUS decoder
                    var json = Encoding.UTF8.GetString(buffer, 0, msg.Count);
                    EventReceived?.Invoke(ServerEvent.Parse(json));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            ConnectionFailed?.Invoke(ex);
        }
    }
}
