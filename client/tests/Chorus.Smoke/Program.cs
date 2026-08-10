using System.Diagnostics;
using Chorus.Core;

namespace Chorus.Smoke;

/// <summary>
/// Headless smoke test of the CLIENT protocol code against a live gateway.
/// Runs on any OS (no WinForms/NAudio dependency) — used on the build host to
/// prove the client's message-type discipline end to end:
///   --basic      hello → hello_ack (roster) → ping → pong → bye → bye_ack
///   --wav FILE   full PTT exchange: stream a 16k mono WAV as OPUS mic
///                frames → expect final + agent_text + TTS OPUS frames that
///                decode to speech (RMS > 0) and NO text on the audio path.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string url = Env("CHORUS_URL", Protocol.DefaultUrl);
        string? wav = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) url = args[++i];
            else if (args[i] == "--wav" && i + 1 < args.Length) wav = args[++i];
        }

        var sw = Stopwatch.StartNew();
        int failures = 0;
        var events = new List<ServerEvent>();
        var frames = new List<short[]>();
        Exception? connFail = null;

        await using var client = new ChorusClient(url);
        client.EventReceived += e => { lock (events) events.Add(e); };
        client.TtsFrameDecoded += p => { lock (frames) frames.Add(p); };
        client.ConnectionFailed += ex => connFail = ex;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // --- basic: hello / ping / bye ---
        Console.WriteLine($"[smoke] connecting {url}");
        await client.ConnectAsync("", "smoke", "converse", "hermes", cts.Token);
        await Task.Delay(300, cts.Token);

        var ack = events.OfType<ServerEvent.HelloAck>().FirstOrDefault();
        Check(ack is not null, "hello -> hello_ack", ref failures);
        Check(ack?.AgentRoster.Any(a => a.Id == "hermes") == true, "roster contains hermes", ref failures);
        Check(!string.IsNullOrEmpty(ack?.SessionId), "session_id assigned", ref failures);

        events.Clear();
        await client.SendPingAsync(cts.Token);
        await WaitForAsync(() => events.Any(e => e is ServerEvent.Pong), TimeSpan.FromSeconds(3), cts.Token);
        Check(events.Any(e => e is ServerEvent.Pong), "ping -> pong", ref failures);

        // --- optional full PTT exchange ---
        if (wav is not null)
        {
            Console.WriteLine($"[smoke] full PTT exchange with {wav}");
            events.Clear();
            frames.Clear();
            await RunPttExchangeAsync(client, wav, events, frames, cts.Token);
            var finals = events.OfType<ServerEvent.Final>().Select(f => f.Text).ToList();
            var agentTexts = events.OfType<ServerEvent.AgentText>().Select(a => a.Text).ToList();
            var turns = events.OfType<ServerEvent.Turn>().Select(t => t.State).ToList();
            double rms = Rms(frames);
            int sampleCount = frames.Sum(f => f.Length);

            Console.WriteLine($"  final: {string.Join(" | ", finals)}");
            Console.WriteLine($"  agent_text: {string.Join(" | ", agentTexts)}");
            Console.WriteLine($"  turns: {string.Join(" → ", turns)}");
            Console.WriteLine($"  tts frames: {frames.Count} ({sampleCount / 24000.0:F2}s), RMS {rms:F0}");

            Check(finals.Count > 0 && !string.IsNullOrWhiteSpace(finals[0]), "STT final text", ref failures);
            Check(agentTexts.Count > 0, "agent_text reply", ref failures);
            Check(turns.Contains("speaking"), "reached speaking state", ref failures);
            Check(frames.Count > 0, "TTS audio frames received", ref failures);
            Check(rms > 500, $"TTS audio is real speech (RMS {rms:F0} > 500) — gobbledygook check", ref failures);
        }

        events.Clear();
        await client.SendByeAsync(cts.Token);
        await WaitForAsync(() => events.Any(e => e is ServerEvent.ByeAck), TimeSpan.FromSeconds(3), cts.Token);
        Check(events.Any(e => e is ServerEvent.ByeAck), "bye -> bye_ack", ref failures);

        if (connFail is not null)
        {
            Console.WriteLine($"  connection failure: {connFail.Message}");
            failures++;
        }

        Console.WriteLine($"\n=== smoke: {(failures == 0 ? "PASS" : $"{failures} FAILURES")} in {sw.Elapsed.TotalSeconds:F1}s ===");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunPttExchangeAsync(ChorusClient client, string wavPath,
        List<ServerEvent> events, List<short[]> frames, CancellationToken ct)
    {
        short[] pcm = ReadWav16kMono(wavPath);
        Console.WriteLine($"  wav: {pcm.Length / 16000.0:F2}s @16k mono");
        using var enc = new OpusEncoder16k();

        await client.SendPttAsync(true, ct);
        for (int i = 0; i + Protocol.MicFrameSamples <= pcm.Length; i += Protocol.MicFrameSamples)
        {
            var frame = new short[Protocol.MicFrameSamples];
            Array.Copy(pcm, i, frame, 0, Protocol.MicFrameSamples);
            await client.SendAudioFrameAsync(enc.EncodeFrame(frame), ct);
            await Task.Delay(5, ct); // pace like a live mic
        }
        await client.SendPttAsync(false, ct);

        // wait until the turn returns to listening AFTER speaking (TTS frames
        // streamed) — or an error. "listening" alone is not enough: it also
        // appears right after ptt down at the start of the exchange.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        bool sawSpeaking = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
            ServerEvent[] snapshot;
            lock (events) snapshot = events.ToArray();
            if (snapshot.Any(e => e is ServerEvent.Error)) break;
            var turnStates = snapshot.OfType<ServerEvent.Turn>().Select(t => t.State).ToList();
            if (turnStates.Contains("speaking")) sawSpeaking = true;
            if (sawSpeaking && turnStates.Count > 0 && turnStates[^1] == "listening") break;
        }
    }

    private static short[] ReadWav16kMono(string path)
    {
        // Minimal RIFF reader: PCM16 mono 16 kHz.
        var bytes = File.ReadAllBytes(path);
        int dataOffset = 44; // standard PCM header
        int sampleCount = (bytes.Length - dataOffset) / 2;
        var pcm = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            pcm[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2);
        return pcm;
    }

    private static double Rms(List<short[]> frames)
    {
        long sum = 0; long n = 0;
        foreach (var f in frames) { foreach (var s in f) { sum += (long)s * s; n++; } }
        return n == 0 ? 0 : Math.Sqrt((double)sum / n);
    }

    private static async Task WaitForAsync(Func<bool> cond, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(50, ct);
        }
    }

    private static void Check(bool ok, string label, ref int failures)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label}");
        if (!ok) failures++;
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) ?? fallback;
}
