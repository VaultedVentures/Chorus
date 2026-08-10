using Chorus.Core;

namespace Chorus.App;

/// <summary>
/// Owns the connection lifecycle: connect → hold → auto-reconnect with a
/// 3 s backoff. Reconnects resume the persisted gateway session id. Manual
/// reconnect (button / tray) cancels the current attempt immediately.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ChorusClient _client;
    private readonly AppSettings _settings;
    private readonly VoiceConsoleForm _form;
    private readonly TrayDaemon _tray;
    private readonly SessionState _state;
    private CancellationTokenSource? _attemptCts;
    private volatile bool _reconnectRequested;

    public ConnectionManager(ChorusClient client, AppSettings settings,
        VoiceConsoleForm form, TrayDaemon tray, SessionState state)
    {
        _client = client;
        _settings = settings;
        _form = form;
        _tray = tray;
        _state = state;
    }

    public void RequestReconnect()
    {
        _reconnectRequested = true;
        _attemptCts?.Cancel();
    }

    public async Task RunAsync(CancellationToken appCt)
    {
        while (!appCt.IsCancellationRequested)
        {
            _reconnectRequested = false;
            _attemptCts = new CancellationTokenSource();
            using var attemptCts = _attemptCts;

            try
            {
                _form.SetConnection("connecting…");
                await _client.ConnectAsync(
                    _settings.LoadSessionId() ?? "", _settings.Device, "converse",
                    _state.Agent, attemptCts.Token);

                _settings.SaveSessionId(_client.SessionId);
                _form.SetConnection($"connected · {_settings.GatewayUrl}");
                _tray.ShowBalloon("CHORUS", "Connected. Hold Win+Shift+T to talk.");

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnFail(Exception _) => tcs.TrySetResult();
                _client.ConnectionFailed += OnFail;
                try
                {
                    await tcs.Task.WaitAsync(attemptCts.Token);
                }
                finally
                {
                    _client.ConnectionFailed -= OnFail;
                }
            }
            catch (OperationCanceledException)
            {
                // manual reconnect or app shutdown
            }
            catch (Exception ex)
            {
                _form.SetConnection($"error: {ex.Message}");
            }

            if (appCt.IsCancellationRequested) break;
            _form.SetConnection("disconnected — reconnecting…");

            if (!_reconnectRequested)
            {
                try { await Task.Delay(3000, appCt); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
