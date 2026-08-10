using Chorus.Core;
using Concentus.Enums;
using Concentus.Structs;

namespace Chorus.Core.Tests;

public class OpusCodecTests
{
    [Fact]
    public void Encode16k_Produces_VoipFrame()
    {
        using var enc = new OpusEncoder16k();
        var silence = new short[Protocol.MicFrameSamples];
        var frame = enc.EncodeFrame(silence);
        Assert.NotEmpty(frame);
        Assert.True(frame.Length <= 640); // VOIP 16k silence is small
    }

    [Fact]
    public void Encode16k_Decode16k_RoundTrip_Silence()
    {
        using var enc = new OpusEncoder16k();
        var dec = new OpusDecoder(16000, 1);
        var silence = new short[Protocol.MicFrameSamples];
        var opus = enc.EncodeFrame(silence);
        var pcm = new short[Protocol.MicFrameSamples];
        int n = dec.Decode(opus, pcm, Protocol.MicFrameSamples, false);
        Assert.Equal(Protocol.MicFrameSamples, n);
    }

    [Fact]
    public void Decode24k_GatewayShape_Silence()
    {
        // Gateway: 24 kHz VOIP, 480-sample frames, silence padded to full frame.
        using var enc = new OpusEncoder(24000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var dec = new OpusDecoder24k();
        var silence = new short[Protocol.TtsFrameSamples];
        var outBuf = new byte[4096];
        int n = enc.Encode(silence, Protocol.TtsFrameSamples, outBuf, outBuf.Length);
        var pcm = dec.DecodeFrame(outBuf.AsSpan(0, n));
        Assert.Equal(Protocol.TtsFrameSamples, pcm.Length);
    }

    [Fact]
    public void Decode24k_Tone_HasEnergy()
    {
        // NOTE: Concentus' default 24k VOIP bitrate decodes to silence (codec
        // quirk — see smoke/live-echo for the authoritative gateway path).
        // Pin a speech-grade bitrate here so the roundtrip is meaningful.
        using var enc = new OpusEncoder(24000, 1, OpusApplication.OPUS_APPLICATION_VOIP) { Bitrate = 64000 };
        var dec = new OpusDecoder24k();
        var tone = new short[Protocol.TtsFrameSamples];
        for (int i = 0; i < tone.Length; i++) tone[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 24000.0));
        var outBuf = new byte[4096];
        int n = enc.Encode(tone, Protocol.TtsFrameSamples, outBuf, outBuf.Length);
        var pcm = dec.DecodeFrame(outBuf.AsSpan(0, n));
        long sum = 0;
        foreach (var s in pcm) sum += (long)s * s;
        Assert.True(Math.Sqrt((double)sum / pcm.Length) > 1000, "decoded tone should have energy");
    }
}
