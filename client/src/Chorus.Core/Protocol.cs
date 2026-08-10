namespace Chorus.Core;

/// <summary>
/// Constants from docs/chorus-protocol-v1.md — the wire contract.
/// The ONE rule: binary frames are OPUS audio only; text frames are JSON
/// events only. Never mixed, never decoded as the other kind.
/// </summary>
public static class Protocol
{
    public const string Proto = "1.0";

    /// <summary>Production gateway (dev endpoint). Override with CHORUS_URL.</summary>
    public const string DefaultUrl = "ws://2.28.14.119:8765/v1/session";

    /// <summary>Mic in: 16 kHz mono, 20 ms frames (320 samples), OPUS/VOIP.</summary>
    public const int MicSampleRate = 16000;
    public const int MicFrameSamples = 320; // 20 ms @ 16 kHz

    /// <summary>TTS out: 24 kHz mono, 20 ms frames (480 samples), OPUS/VOIP.</summary>
    public const int TtsSampleRate = 24000;
    public const int TtsFrameSamples = 480; // 20 ms @ 24 kHz

    /// <summary>Keepalive cadence (server sends nothing while idle).</summary>
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(25);
}
