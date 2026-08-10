using Chorus.Core;

namespace Chorus.App;

/// <summary>
/// App configuration. Env overrides: CHORUS_URL (gateway endpoint),
/// CHORUS_AGENT (default agent id). Session id is persisted next to the EXE
/// so reconnects/restarts resume the same gateway session.
/// </summary>
public sealed record AppSettings(
    string GatewayUrl,
    string Agent,
    string Device,
    string SessionIdFile)
{
    public static AppSettings Load()
    {
        var url = Environment.GetEnvironmentVariable("CHORUS_URL") ?? Protocol.DefaultUrl;
        var agent = Environment.GetEnvironmentVariable("CHORUS_AGENT") ?? "hermes";
        var sessionFile = Path.Combine(AppContext.BaseDirectory, "session.id");
        return new AppSettings(url, agent, "desktop-win", sessionFile);
    }

    public string? LoadSessionId() =>
        File.Exists(SessionIdFile) ? File.ReadAllText(SessionIdFile).Trim() : null;

    public void SaveSessionId(string? id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) { if (File.Exists(SessionIdFile)) File.Delete(SessionIdFile); }
            else File.WriteAllText(SessionIdFile, id);
        }
        catch { /* non-fatal */ }
    }
}
