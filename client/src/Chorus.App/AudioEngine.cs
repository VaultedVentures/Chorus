using Chorus.Core;
using NAudio;
using NAudio.Wave;

namespace Chorus.App;

/// <summary>Why mic capture failed (surfaced to the user, never swallowed).</summary>
public enum MicFailureKind
{
    NoMicrophone,
    PermissionDenied,
    DeviceInUse,
    Unknown,
}

/// <summary>A capture failure with a human-readable explanation.</summary>
public sealed record MicFailure(MicFailureKind Kind, string Message, Exception? Inner);

/// <summary>Current capture state, observable by the UI/tray.</summary>
public enum MicState { Stopped, Capturing, Failed }

/// <summary>
/// NAudio audio surface: mic capture (16 kHz mono 16-bit PCM, 20 ms frames)
/// and TTS playback (24 kHz mono). Mic frames are raised as short[] (320
/// samples) to every registered consumer via <see cref="MicFrameCaptured"/>;
/// playback is fed from OPUS-decoded 24 kHz PCM via a BufferedWaveProvider.
///
/// Device selection: the configured mic spec ("" = default, "3" = index, or a
/// name substring) is resolved against the enumerated input devices by
/// <see cref="MicDeviceResolver"/>. Capture failures — including missing mic
/// permission — are surfaced via <see cref="MicStateChanged"/> /
/// <see cref="MicFailed"/> instead of throwing, so the shell can tell the
/// user exactly what is wrong and keep running.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _micLock = new();
    private readonly ChorusConfig _config;
    private WaveInEvent? _mic;
    private WaveOutEvent? _speaker;
    private BufferedWaveProvider? _speakerBuffer;

    /// <summary>16 kHz mono 16-bit PCM frame (320 samples = 20 ms) delivered to all consumers.</summary>
    public event Action<short[]>? MicFrameCaptured;

    /// <summary>Raised on every capture-state transition (UI/tray reflect this).</summary>
    public event Action<MicState, string?>? MicStateChanged;

    /// <summary>Raised when a capture attempt fails, with a clear reason.</summary>
    public event Action<MicFailure>? MicFailed;

    public MicState State { get; private set; } = MicState.Stopped;
    public bool MicActive => State == MicState.Capturing;

    public AudioEngine(ChorusConfig config)
    {
        _config = config;
    }

    /// <summary>Names of all input devices (for config/debug display).</summary>
    public static IReadOnlyList<string> ListInputDevices()
    {
        var names = new List<string>();
        int count = WaveInEvent.DeviceCount;
        for (int i = 0; i < count; i++)
        {
            try { names.Add(WaveInEvent.GetCapabilities(i).ProductName); }
            catch (Exception) { names.Add($"(device {i})"); }
        }
        return names;
    }

    /// <summary>
    /// Start capturing from the configured device. Non-throwing: on failure
    /// the state moves to <see cref="MicState.Failed"/> and the reason is
    /// raised via <see cref="MicFailed"/> / <see cref="MicStateChanged"/>.
    /// </summary>
    public bool StartMic()
    {
        lock (_micLock)
        {
            if (State == MicState.Capturing) return true;

            var devices = ListInputDevices();
            if (devices.Count == 0)
            {
                Fail(new MicFailure(MicFailureKind.NoMicrophone,
                    "No microphone input device was found. Plug in a mic and try again.", null));
                return false;
            }

            int deviceNumber = MicDeviceResolver.ResolveIndex(_config.MicDevice, devices);
            try
            {
                _mic = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(Protocol.MicSampleRate, 16, 1),
                    BufferMilliseconds = _config.MicBufferMs,
                    DeviceNumber = deviceNumber,
                };
                _mic.DataAvailable += OnMicData;
                _mic.RecordingStopped += (_, _) => SetState(MicState.Stopped, null);
                _mic.StartRecording();
                SetState(MicState.Capturing,
                    MicDeviceResolver.Describe(_config.MicDevice, devices, deviceNumber));
                return true;
            }
            catch (Exception ex)
            {
                try { _mic?.Dispose(); } catch { /* best effort */ }
                _mic = null;
                Fail(ClassifyFailure(ex, devices, deviceNumber));
                return false;
            }
        }
    }

    public void StopMic()
    {
        lock (_micLock)
        {
            if (_mic is null) return;
            var mic = _mic;
            _mic = null;
            try
            {
                mic.StopRecording();
                mic.Dispose();
            }
            catch (Exception) { /* device already gone */ }
            SetState(MicState.Stopped, null);
        }
    }

    /// <summary>Start playback (TTS out). No-op if already running.</summary>
    public void StartPlayback()
    {
        if (_speaker is not null) return;
        _speakerBuffer = new BufferedWaveProvider(new WaveFormat(Protocol.TtsSampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true,
        };
        _speaker = new WaveOutEvent();
        _speaker.Init(_speakerBuffer);
        _speaker.Play();
    }

    public void EnqueuePlayback(short[] pcm24k)
    {
        if (_speakerBuffer is null || pcm24k.Length == 0) return;
        var bytes = new byte[pcm24k.Length * 2];
        Buffer.BlockCopy(pcm24k, 0, bytes, 0, bytes.Length);
        _speakerBuffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void ClearPlayback() => _speakerBuffer?.ClearBuffer();

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        var frame = new short[Protocol.MicFrameSamples];
        int n = Math.Min(e.BytesRecorded / 2, frame.Length);
        for (int i = 0; i < n; i++)
            frame[i] = BitConverter.ToInt16(e.Buffer, i * 2);
        MicFrameCaptured?.Invoke(frame);
    }

    private void SetState(MicState state, string? detail)
    {
        State = state;
        MicStateChanged?.Invoke(state, detail);
    }

    private void Fail(MicFailure failure)
    {
        SetState(MicState.Failed, failure.Message);
        MicFailed?.Invoke(failure);
    }

    /// <summary>
    /// Map an NAudio/OS capture exception to a user-facing reason. Windows
    /// privacy-blocked mics typically surface as MMSYSERR_ALLOCATED (device
    /// appears busy to the app) or UnauthorizedAccessException.
    /// </summary>
    private static MicFailure ClassifyFailure(Exception ex, IReadOnlyList<string> devices, int deviceNumber)
    {
        string device = deviceNumber < devices.Count ? devices[deviceNumber] : $"device {deviceNumber}";

        if (ex is UnauthorizedAccessException)
        {
            return new MicFailure(MicFailureKind.PermissionDenied,
                "Microphone permission is blocked. Enable mic access for CHORUS " +
                "in Windows Settings → Privacy & security → Microphone, then retry.", ex);
        }

        if (ex is MmException mm)
        {
            switch (mm.Result)
            {
                case MmResult.NotEnabled:
                case MmResult.AlreadyAllocated:
                    return new MicFailure(MicFailureKind.PermissionDenied,
                        $"Microphone \"{device}\" is unavailable — Windows may be blocking mic access " +
                        "(Settings → Privacy & security → Microphone) or another app is using it exclusively. " +
                        "Check both, then retry.", ex);
                case MmResult.BadDeviceId:
                case MmResult.NoDriver:
                    return new MicFailure(MicFailureKind.NoMicrophone,
                        $"Microphone \"{device}\" no longer exists. Re-select the device in chorus.json.", ex);
                case MmResult.HandleBusy:
                    return new MicFailure(MicFailureKind.DeviceInUse,
                        $"Microphone \"{device}\" is in use by another application.", ex);
                default:
                    return new MicFailure(MicFailureKind.Unknown,
                        $"Microphone \"{device}\" failed to start: {mm.Result} ({mm.Message}).", ex);
            }
        }

        return new MicFailure(MicFailureKind.Unknown,
            $"Microphone \"{device}\" failed to start: {ex.Message}", ex);
    }

    public void Dispose()
    {
        StopMic();
        _speaker?.Stop();
        _speaker?.Dispose();
        _speaker = null;
        _speakerBuffer = null;
    }
}
