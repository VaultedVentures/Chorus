using Concentus;
using Concentus.Enums;

namespace Chorus.Core;

/// <summary>
/// Opus codec wrappers for the two fixed wire shapes (docs/chorus-protocol-v1.md):
/// mic in = 16 kHz mono VOIP 20 ms (320 samples), TTS out = 24 kHz mono VOIP
/// 20 ms (480 samples).
///
/// Uses OpusCodecFactory so decoding prefers the NATIVE libopus that ships in
/// the Concentus package (runtimes/win-x64/native/opus.dll is bundled into
/// the self-contained EXE by Concentus.targets). This matters: the managed
/// fallback decodes low-bitrate 24 kHz frames to near-silence, while native
/// opus reproduces the gateway's audio correctly.
///
/// Instances are NOT thread-safe. Hold one encoder on the audio-capture
/// thread and one decoder on the receive-loop thread; never share across
/// threads.
/// </summary>
public sealed class OpusEncoder16k : IDisposable
{
    private readonly IOpusEncoder _enc = OpusCodecFactory.CreateEncoder(
        Protocol.MicSampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
    private readonly byte[] _out = new byte[4096];

    /// <summary>Encode one 20 ms frame (320 samples) to an OPUS/VOIP packet.</summary>
    public byte[] EncodeFrame(short[] pcm)
    {
        int n = _enc.Encode(pcm.AsSpan(0, Protocol.MicFrameSamples),
            Protocol.MicFrameSamples, _out.AsSpan(), _out.Length);
        var result = new byte[n];
        Array.Copy(_out, result, n);
        return result;
    }

    public void Dispose() => _enc.Dispose();
}

/// <summary>Decodes gateway TTS frames (24 kHz mono). Returns the decoded PCM
/// samples (normally 480 per frame; the server pads partial frames).</summary>
public sealed class OpusDecoder24k : IDisposable
{
    private readonly IOpusDecoder _dec = OpusCodecFactory.CreateDecoder(
        Protocol.TtsSampleRate, 1);
    private readonly short[] _pcm = new short[Protocol.TtsFrameSamples + 64];

    public short[] DecodeFrame(ReadOnlySpan<byte> opus)
    {
        int samples = _dec.Decode(opus, _pcm.AsSpan(0, Protocol.TtsFrameSamples),
            Protocol.TtsFrameSamples, false);
        if (samples <= 0)
            return Array.Empty<short>();
        var result = new short[samples];
        Array.Copy(_pcm, result, samples);
        return result;
    }

    public void Dispose() => _dec.Dispose();
}
