using System.Collections.Concurrent;
using System.Speech.Synthesis;
using Chorus.Core.Clipboard;

namespace Chorus.App;

/// <summary>
/// Local text-to-speech for the Text Select and Clipboard Reader features
/// via Windows SAPI (System.Speech). No gateway involvement — the read-aloud
/// path is fully local, matching the card's "SAPI/Piper TTS" tech and the
/// CHORUS "no gateway involvement" scope.
///
/// A single dedicated STA thread owns the SpeechSynthesizer (SAPI is
/// thread-affine); long selections are spoken in bounded chunks (see
/// TtsChunker) because SAPI Speak() becomes unreliable past a few hundred
/// chars (the ClipReader v1.1 incident).
///
/// Voice: when <paramref name="voiceName"/> is given (chorus.json
/// VoiceName) that installed voice is selected; otherwise the best
/// installed voice is auto-picked (SapiVoicePicker — prefers the Windows 11
/// neural "Natural" voices, e.g. Hazel, over legacy desktop voices).
/// </summary>
public sealed class SapiSpeechSynthesizer : IDisposable
{
    private readonly Thread _worker;
    private readonly BlockingCollection<ChunkJob> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly string? _voiceName;
    private volatile bool _speaking;

    public SapiSpeechSynthesizer(string? voiceName = null)
    {
        _voiceName = voiceName;
        _worker = new Thread(WorkerLoop)
        {
            Name = "Chorus.SapiSpeech",
            IsBackground = true,
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>True when a read-aloud is currently in flight.</summary>
    public bool IsSpeaking => _speaking;

    /// <summary>
    /// Speak the given chunks in order. Any in-flight reading is stopped
    /// first. Returns when the last chunk finishes (or is cancelled).
    /// </summary>
    public Task SpeakAsync(IReadOnlyList<string> chunks, CancellationToken ct = default)
    {
        var job = new ChunkJob(chunks, ct);
        _queue.Add(job);
        return job.Completion.Task;
    }

    /// <summary>Stop any in-flight reading immediately (also drops queued reads).</summary>
    public void Stop()
    {
        ChunkJob? current;
        lock (_gate)
        {
            current = _currentJob;
            try { _synth?.SpeakAsyncCancelAll(); } catch { /* already stopped */ }
        }
        current?.CtSource.Cancel();
        while (_queue.TryTake(out var job))
        {
            job.Completion.TrySetCanceled();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.CompleteAdding();
        try { _worker.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _synth?.Dispose();
        _queue.Dispose();
        _shutdown.Dispose();
    }

    // -- worker (owns the synthesizer) -------------------------------------

    private SpeechSynthesizer? _synth;
    private ChunkJob? _currentJob;

    private void WorkerLoop()
    {
        _synth = new SpeechSynthesizer();
        try
        {
            SelectVoice();
        }
        catch (Exception)
        {
            // no usable voice — Speak() will still work with the default
        }
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable(_shutdown.Token))
            {
                lock (_gate) _currentJob = job;
                _speaking = true;
                try
                {
                    foreach (var chunk in job.Chunks)
                    {
                        job.Ct.ThrowIfCancellationRequested();
                        _synth.Speak(chunk); // synchronous SAPI call — reliable per-chunk
                    }
                    job.Completion.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    job.Completion.TrySetCanceled(job.Ct);
                }
                catch (Exception ex)
                {
                    job.Completion.TrySetException(ex);
                }
                finally
                {
                    _speaking = false;
                    lock (_gate) _currentJob = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    /// <summary>
    /// Select the configured voice (exact/substring match) or auto-pick the
    /// best installed voice. Runs on the worker thread once at startup.
    /// </summary>
    private void SelectVoice()
    {
        var installed = _synth!.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name)
            .ToArray();
        var pick = SapiVoicePicker.PickBest(installed, _voiceName);
        if (pick is not null) _synth.SelectVoice(pick);
    }

    private sealed class ChunkJob
    {
        public ChunkJob(IReadOnlyList<string> chunks, CancellationToken ct)
        {
            Chunks = chunks;
            CtSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        public IReadOnlyList<string> Chunks { get; }
        public CancellationTokenSource CtSource { get; }
        public CancellationToken Ct => CtSource.Token;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
