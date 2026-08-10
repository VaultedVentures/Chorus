using Chorus.Core;
using NAudio.Wave;

namespace Chorus.App;

/// <summary>
/// NAudio audio surface: mic capture (16 kHz mono, 20 ms frames) and TTS
/// playback (24 kHz mono). Mic frames are raised as short[] (320 samples);
/// playback is fed from OPUS-decoded 24 kHz PCM via a BufferedWaveProvider.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _micLock = new();
    private WaveInEvent? _mic;
    private WaveOutEvent? _speaker;
    private BufferedWaveProvider? _speakerBuffer;

    public event Action<short[]>? MicFrameCaptured;

    public bool MicActive { get; private set; }

    public void StartMic()
    {
        lock (_micLock)
        {
            if (MicActive) return;
            _mic = new WaveInEvent
            {
                WaveFormat = new WaveFormat(Protocol.MicSampleRate, 16, 1),
                BufferMilliseconds = 20, // 640 bytes = 320 samples
                DeviceNumber = 0,
            };
            _mic.DataAvailable += OnMicData;
            _mic.RecordingStopped += (_, _) => MicActive = false;
            _mic.StartRecording();
            MicActive = true;
        }
    }

    public void StopMic()
    {
        lock (_micLock)
        {
            if (_mic is null) return;
            _mic.StopRecording();
            _mic.Dispose();
            _mic = null;
            MicActive = false;
        }
    }

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

    public void Dispose()
    {
        StopMic();
        _speaker?.Stop();
        _speaker?.Dispose();
        _speaker = null;
        _speakerBuffer = null;
    }
}
