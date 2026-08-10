using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chorus.Core;

/// <summary>
/// File-backed app configuration for the desktop client.
///
/// Load precedence (highest wins):
///   1. Environment variables (CHORUS_URL, CHORUS_AGENT, CHORUS_DEVICE,
///      CHORUS_START_HIDDEN)
///   2. Config file (chorus.json next to the EXE, or --config path)
///   3. Built-in defaults (see <see cref="Default"/>)
///
/// The config file is written automatically on first load so users always
/// have a discoverable place to change the gateway URL and mic device.
/// </summary>
public sealed record ChorusConfig(
    string GatewayUrl,
    string Agent,
    string MicDevice,
    bool StartHidden,
    int MicBufferMs,
    string ClientDevice,
    string? ConfigPath)
{
    public const string FileName = "chorus.json";

    public static ChorusConfig Default { get; } = new(
        GatewayUrl: Protocol.DefaultUrl,
        Agent: "hermes",
        MicDevice: "",          // "" = system default input device
        StartHidden: true,      // start to the tray, console on demand
        MicBufferMs: 20,        // 20 ms frames @ 16 kHz = 320 samples
        ClientDevice: "desktop-win",
        ConfigPath: null);

    /// <summary>Env override names, in the order the loader applies them.</summary>
    public static readonly (string Env, string Field)[] EnvOverrides =
    {
        ("CHORUS_URL", nameof(GatewayUrl)),
        ("CHORUS_AGENT", nameof(Agent)),
        ("CHORUS_DEVICE", nameof(MicDevice)),
        ("CHORUS_START_HIDDEN", nameof(StartHidden)),
    };

    /// <summary>
    /// Load config: read the file at <paramref name="explicitPath"/> if given,
    /// otherwise <c>&lt;baseDir&gt;/chorus.json</c>. Missing file → defaults are
    /// used AND written back so the user can edit them. Env vars override the
    /// file. Never throws — a corrupt file degrades to defaults.
    /// </summary>
    public static ChorusConfig Load(string? baseDir = null, string? explicitPath = null)
    {
        string path = explicitPath
            ?? Path.Combine(baseDir ?? AppContext.BaseDirectory, FileName);

        ChorusConfig cfg = Default;
        string? raw = null;
        try
        {
            if (File.Exists(path)) raw = File.ReadAllText(path);
        }
        catch (Exception)
        {
            raw = null; // unreadable file → defaults
        }

        if (raw is not null)
        {
            try
            {
                cfg = FromJson(raw) with { ConfigPath = path };
            }
            catch (Exception)
            {
                cfg = Default with { ConfigPath = path };
            }
        }
        else
        {
            cfg = Default with { ConfigPath = path };
            TryWrite(path, cfg.ToJson());
        }

        return ApplyEnvOverrides(cfg);
    }

    public static ChorusConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ChorusConfig>(json, JsonOpts) ?? Default;

    public string ToJson() =>
        JsonSerializer.Serialize(this with { ConfigPath = null }, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ChorusConfig ApplyEnvOverrides(ChorusConfig cfg)
    {
        string? url = Environment.GetEnvironmentVariable("CHORUS_URL");
        string? agent = Environment.GetEnvironmentVariable("CHORUS_AGENT");
        string? device = Environment.GetEnvironmentVariable("CHORUS_DEVICE");
        string? hidden = Environment.GetEnvironmentVariable("CHORUS_START_HIDDEN");

        if (url is not null && url.Length > 0) cfg = cfg with { GatewayUrl = url };
        if (agent is not null && agent.Length > 0) cfg = cfg with { Agent = agent };
        if (device is not null) cfg = cfg with { MicDevice = device };
        if (hidden is not null && bool.TryParse(hidden, out bool h)) cfg = cfg with { StartHidden = h };
        return cfg;
    }

    private static void TryWrite(string path, string json)
    {
        try { File.WriteAllText(path, json); }
        catch (Exception) { /* config dir not writable — defaults still apply */ }
    }

    // -- session id persistence (resume the same gateway session across restarts) --

    public string SessionIdFile =>
        Path.Combine(AppContext.BaseDirectory, "session.id");

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
