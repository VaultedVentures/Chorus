namespace Chorus.App;

/// <summary>
/// Shared mutable session state, written on the UI thread, read from the
/// NAudio capture thread and the connection loop. Volatile fields only —
/// no locking needed for single-word reads/writes.
/// </summary>
public sealed class SessionState
{
    /// <summary>Last server turn state: idle|listening|pending|processing|speaking.</summary>
    public volatile string Turn = "idle";

    /// <summary>Selected agent id (persisted across reconnects).</summary>
    public volatile string Agent = "hermes";

    /// <summary>Mute: suppress mic frames + barge-in while true.</summary>
    public volatile bool Muted;

    /// <summary>Set by the form/tray when a reconnect is wanted.</summary>
    public volatile bool ReconnectRequested;

    public bool IsSpeakingOrPending => Turn is "speaking" or "pending";
}
