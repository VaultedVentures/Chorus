using System.Text.Json;
using System.Text.Json.Serialization;
using Chorus.Core.WakeWord;

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
    string? ConfigPath,
    string PttHotkey = "Ctrl+Shift+Space",
    string WakeHotkey = "Win+Shift+W",
    string TextSelectHotkey = "Win+Shift+R",
    string WakePhrase = "hey chorus",
    bool WakeEnabled = true,
    float WakeSensitivity = 0.4f,
    int WakeCooldownMs = 2000,
    int WakeSessionIdleMs = 45000,
    string ClipboardHotkey = "Win+Shift+C",
    string VoiceName = "")
{
    public const string FileName = "chorus.json";

    public static ChorusConfig Default { get; } = new(
        GatewayUrl: Protocol.DefaultUrl,
        Agent: "hermes",
        MicDevice: "",          // "" = system default input device
        StartHidden: true,      // start to the tray, console on demand
        MicBufferMs: 20,        // 20 ms frames @ 16 kHz = 320 samples
        ClientDevice: "desktop-win",
        ConfigPath: null,
        PttHotkey: "Ctrl+Shift+Space",   // hold-to-talk (global, works from any app)
        WakeHotkey: "Win+Shift+W",       // manual wake window (hotkey path)
        TextSelectHotkey: "Win+Shift+R", // read screen text
        WakePhrase: "hey chorus",        // wake-word phrase (acoustic model is fixed)
        WakeEnabled: true,               // continuous wake-word listening on startup
        WakeSensitivity: 0.4f,           // 0..1: higher triggers more easily
        WakeCooldownMs: 2000,            // min gap between wake triggers (ms)
        WakeSessionIdleMs: 45000,        // wake session auto-closes after this silence
        ClipboardHotkey: "Win+Shift+C",  // read the clipboard aloud
        VoiceName: "");                  // "" = auto-pick the best installed SAPI voice

    /// <summary>Parsed PTT binding; invalid config falls back to the default.</summary>
    public HotkeyBinding PttBinding => HotkeyBinding.Parse(PttHotkey).IsValid
        ? HotkeyBinding.Parse(PttHotkey)
        : HotkeyBinding.Parse("Ctrl+Shift+Space");

    /// <summary>Parsed wake binding; invalid config falls back to the default.</summary>
    public HotkeyBinding WakeBinding => HotkeyBinding.Parse(WakeHotkey).IsValid
        ? HotkeyBinding.Parse(WakeHotkey)
        : HotkeyBinding.Parse("Win+Shift+W");

    /// <summary>Parsed text-select binding; invalid config falls back to the default.</summary>
    public HotkeyBinding TextSelectBinding => HotkeyBinding.Parse(TextSelectHotkey).IsValid
        ? HotkeyBinding.Parse(TextSelectHotkey)
        : HotkeyBinding.Parse("Win+Shift+R");

    /// <summary>Parsed clipboard-read binding; invalid config falls back to the default.</summary>
    public HotkeyBinding ClipboardBinding => HotkeyBinding.Parse(ClipboardHotkey).IsValid
        ? HotkeyBinding.Parse(ClipboardHotkey)
        : HotkeyBinding.Parse("Win+Shift+C");

    /// <summary>Human-readable PTT combo, e.g. "Ctrl+Shift+Space".</summary>
    public string PttHotkeyDisplay => PttBinding.Display;

    /// <summary>Human-readable wake combo, e.g. "Win+Shift+W".</summary>
    public string WakeHotkeyDisplay => WakeBinding.Display;

    /// <summary>Human-readable text-select combo, e.g. "Win+Shift+R".</summary>
    public string TextSelectHotkeyDisplay => TextSelectBinding.Display;

    /// <summary>Human-readable clipboard-read combo, e.g. "Win+Shift+C".</summary>
    public string ClipboardHotkeyDisplay => ClipboardBinding.Display;

    /// <summary>Wake-word engine settings assembled from the config fields.</summary>
    public WakeWordSettings WakeSettings => new(
        Phrase: WakePhrase,
        Enabled: WakeEnabled,
        Sensitivity: WakeSensitivity,
        CooldownMs: WakeCooldownMs,
        SessionIdleMs: WakeSessionIdleMs);

    /// <summary>Env override names, in the order the loader applies them.</summary>
    public static readonly (string Env, string Field)[] EnvOverrides =
    {
        ("CHORUS_URL", nameof(GatewayUrl)),
        ("CHORUS_AGENT", nameof(Agent)),
        ("CHORUS_DEVICE", nameof(MicDevice)),
        ("CHORUS_START_HIDDEN", nameof(StartHidden)),
        ("CHORUS_PTT_HOTKEY", nameof(PttHotkey)),
        ("CHORUS_WAKE_HOTKEY", nameof(WakeHotkey)),
        ("CHORUS_TEXT_SELECT_HOTKEY", nameof(TextSelectHotkey)),
        ("CHORUS_WAKE_PHRASE", nameof(WakePhrase)),
        ("CHORUS_WAKE_ENABLED", nameof(WakeEnabled)),
        ("CHORUS_WAKE_SENSITIVITY", nameof(WakeSensitivity)),
        ("CHORUS_WAKE_COOLDOWN_MS", nameof(WakeCooldownMs)),
        ("CHORUS_WAKE_SESSION_IDLE_MS", nameof(WakeSessionIdleMs)),
        ("CHORUS_CLIPBOARD_HOTKEY", nameof(ClipboardHotkey)),
        ("CHORUS_VOICE", nameof(VoiceName)),
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
                cfg = Normalize(FromJson(raw)) with { ConfigPath = path };
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

    /// <summary>
    /// Clamp numeric settings read from the JSON file. The env override path
    /// clamps at parse time; the file path deserializes raw values, so an
    /// out-of-range chorus.json (e.g. "WakeSensitivity": 7.5) must be brought
    /// back in range here — the engine and UI both assume valid ranges.
    /// </summary>
    private static ChorusConfig Normalize(ChorusConfig cfg) => cfg with
    {
        WakeSensitivity = Math.Clamp(cfg.WakeSensitivity, 0f, 1f),
        WakeCooldownMs = Math.Max(0, cfg.WakeCooldownMs),
        WakeSessionIdleMs = Math.Max(0, cfg.WakeSessionIdleMs),
    };

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
        string? ptt = Environment.GetEnvironmentVariable("CHORUS_PTT_HOTKEY");
        string? wake = Environment.GetEnvironmentVariable("CHORUS_WAKE_HOTKEY");
        string? textSelect = Environment.GetEnvironmentVariable("CHORUS_TEXT_SELECT_HOTKEY");
        string? wakePhrase = Environment.GetEnvironmentVariable("CHORUS_WAKE_PHRASE");
        string? wakeEnabled = Environment.GetEnvironmentVariable("CHORUS_WAKE_ENABLED");
        string? wakeSensitivity = Environment.GetEnvironmentVariable("CHORUS_WAKE_SENSITIVITY");
        string? wakeCooldown = Environment.GetEnvironmentVariable("CHORUS_WAKE_COOLDOWN_MS");
        string? wakeSessionIdle = Environment.GetEnvironmentVariable("CHORUS_WAKE_SESSION_IDLE_MS");
        string? clipboardHotkey = Environment.GetEnvironmentVariable("CHORUS_CLIPBOARD_HOTKEY");
        string? voice = Environment.GetEnvironmentVariable("CHORUS_VOICE");

        if (url is not null && url.Length > 0) cfg = cfg with { GatewayUrl = url };
        if (agent is not null && agent.Length > 0) cfg = cfg with { Agent = agent };
        if (device is not null) cfg = cfg with { MicDevice = device };
        if (hidden is not null && bool.TryParse(hidden, out bool h)) cfg = cfg with { StartHidden = h };
        if (ptt is not null && ptt.Length > 0 && HotkeyBinding.TryParse(ptt, out _))
            cfg = cfg with { PttHotkey = ptt };
        if (wake is not null && wake.Length > 0 && HotkeyBinding.TryParse(wake, out _))
            cfg = cfg with { WakeHotkey = wake };
        if (textSelect is not null && textSelect.Length > 0 && HotkeyBinding.TryParse(textSelect, out _))
            cfg = cfg with { TextSelectHotkey = textSelect };
        if (wakePhrase is not null && wakePhrase.Length > 0)
            cfg = cfg with { WakePhrase = wakePhrase };
        if (wakeEnabled is not null && bool.TryParse(wakeEnabled, out bool we))
            cfg = cfg with { WakeEnabled = we };
        if (wakeSensitivity is not null && float.TryParse(wakeSensitivity, out float ws))
            cfg = cfg with { WakeSensitivity = Math.Clamp(ws, 0f, 1f) };
        if (wakeCooldown is not null && int.TryParse(wakeCooldown, out int wc))
            cfg = cfg with { WakeCooldownMs = Math.Max(0, wc) };
        if (wakeSessionIdle is not null && int.TryParse(wakeSessionIdle, out int wsi))
            cfg = cfg with { WakeSessionIdleMs = Math.Max(0, wsi) };
        if (clipboardHotkey is not null && clipboardHotkey.Length > 0 && HotkeyBinding.TryParse(clipboardHotkey, out _))
            cfg = cfg with { ClipboardHotkey = clipboardHotkey };
        if (voice is not null && voice.Length > 0)
            cfg = cfg with { VoiceName = voice };
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
