using Chorus.Core;

namespace Chorus.Core.Tests;

public class ChorusConfigTests : IDisposable
{
    private readonly string _dir;

    public ChorusConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "chorus-cfg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Defaults_Are_Sane()
    {
        var d = ChorusConfig.Default;
        Assert.Equal(Protocol.DefaultUrl, d.GatewayUrl);
        Assert.Equal("hermes", d.Agent);
        Assert.Equal("", d.MicDevice);
        Assert.True(d.StartHidden, "default should start to tray without a main window");
        Assert.Equal(20, d.MicBufferMs);
        Assert.Equal("Ctrl+Shift+Space", d.PttHotkey);
        Assert.Equal("Win+Shift+W", d.WakeHotkey);
        Assert.Equal("Win+Shift+R", d.TextSelectHotkey);
    }

    [Fact]
    public void Hotkey_Display_Helpers_Parse_Defaults()
    {
        var d = ChorusConfig.Default;
        Assert.Equal("Ctrl+Shift+Space", d.PttHotkeyDisplay);
        Assert.Equal("Win+Shift+W", d.WakeHotkeyDisplay);
        Assert.Equal("Win+Shift+R", d.TextSelectHotkeyDisplay);
        Assert.True(d.PttBinding.IsValid);
        Assert.Equal(HotkeyBinding.ModControl | HotkeyBinding.ModShift, d.PttBinding.Modifiers);
        Assert.Equal(0x20u, d.PttBinding.VirtualKey);
    }

    [Fact]
    public void Invalid_Hotkey_Config_Falls_Back_To_Default_Binding()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """
            {
              "GatewayUrl": "ws://example.test:9999/v1/session",
              "PttHotkey": "Ctrl+Banana",
              "WakeHotkey": "F1",
              "TextSelectHotkey": ""
            }
            """);

        var cfg = ChorusConfig.Load(_dir);
        Assert.Equal("Ctrl+Shift+Space", cfg.PttHotkeyDisplay, ignoreCase: true);
        Assert.Equal("Win+Shift+W", cfg.WakeHotkeyDisplay, ignoreCase: true);
        Assert.Equal("Win+Shift+R", cfg.TextSelectHotkeyDisplay, ignoreCase: true);
    }

    [Fact]
    public void Custom_Hotkeys_Load_From_File()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """
            {
              "GatewayUrl": "ws://example.test:9999/v1/session",
              "PttHotkey": "Alt+F9",
              "WakeHotkey": "Ctrl+Shift+W",
              "TextSelectHotkey": "Ctrl+Shift+S"
            }
            """);

        var cfg = ChorusConfig.Load(_dir);
        Assert.Equal("Alt+F9", cfg.PttHotkeyDisplay);
        Assert.Equal("Ctrl+Shift+W", cfg.WakeHotkeyDisplay);
        Assert.Equal("Ctrl+Shift+S", cfg.TextSelectHotkeyDisplay);
        Assert.Equal(0x78u, cfg.PttBinding.VirtualKey); // F9
    }

    [Fact]
    public void Env_Hotkeys_Override_File()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """{ "PttHotkey": "Alt+F9" }""");

        Environment.SetEnvironmentVariable("CHORUS_PTT_HOTKEY", "Ctrl+Shift+F12");
        Environment.SetEnvironmentVariable("CHORUS_WAKE_HOTKEY", "Alt+Space");
        try
        {
            var cfg = ChorusConfig.Load(_dir);
            Assert.Equal("Ctrl+Shift+F12", cfg.PttHotkeyDisplay);
            Assert.Equal("Alt+Space", cfg.WakeHotkeyDisplay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHORUS_PTT_HOTKEY", null);
            Environment.SetEnvironmentVariable("CHORUS_WAKE_HOTKEY", null);
        }
    }

    [Fact]
    public void Old_Config_Without_Hotkey_Fields_Keeps_Defaults()
    {
        // Backward compat: a chorus.json written by an earlier CHORUS build
        // has no hotkey fields — they must resolve to the built-in defaults,
        // not null (which would break the display helpers / RegisterHotKey).
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """
            {
              "GatewayUrl": "ws://example.test:9999/v1/session",
              "Agent": "kimi",
              "MicDevice": "USB Audio",
              "StartHidden": false,
              "MicBufferMs": 40
            }
            """);

        var cfg = ChorusConfig.Load(_dir);
        Assert.Equal("Ctrl+Shift+Space", cfg.PttHotkey);
        Assert.Equal("Win+Shift+W", cfg.WakeHotkey);
        Assert.Equal("Win+Shift+R", cfg.TextSelectHotkey);
        Assert.Equal("Ctrl+Shift+Space", cfg.PttHotkeyDisplay);
    }

    [Fact]
    public void Env_Invalid_Hotkey_Is_Ignored()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """{ "PttHotkey": "Alt+F9" }""");

        Environment.SetEnvironmentVariable("CHORUS_PTT_HOTKEY", "not-a-hotkey");
        try
        {
            var cfg = ChorusConfig.Load(_dir);
            Assert.Equal("Alt+F9", cfg.PttHotkeyDisplay); // file value survives bad env
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHORUS_PTT_HOTKEY", null);
        }
    }

    [Fact]
    public void Missing_File_Writes_Defaults_And_Loads()
    {
        string path = Path.Combine(_dir, "chorus.json");
        var cfg = ChorusConfig.Load(_dir);

        Assert.Equal(Protocol.DefaultUrl, cfg.GatewayUrl);
        Assert.Equal(path, cfg.ConfigPath);
        Assert.True(File.Exists(path), "default config file should be written on first load");
    }

    [Fact]
    public void Load_Reads_File_Values()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """
            {
              "GatewayUrl": "ws://example.test:9999/v1/session",
              "Agent": "kimi",
              "MicDevice": "USB Audio",
              "StartHidden": false,
              "MicBufferMs": 40
            }
            """);

        var cfg = ChorusConfig.Load(_dir);
        Assert.Equal("ws://example.test:9999/v1/session", cfg.GatewayUrl);
        Assert.Equal("kimi", cfg.Agent);
        Assert.Equal("USB Audio", cfg.MicDevice);
        Assert.False(cfg.StartHidden);
        Assert.Equal(40, cfg.MicBufferMs);
    }

    [Fact]
    public void Corrupt_File_Falls_Back_To_Defaults()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, "{ not valid json !!");

        var cfg = ChorusConfig.Load(_dir);
        Assert.Equal(Protocol.DefaultUrl, cfg.GatewayUrl);
        Assert.Equal("hermes", cfg.Agent);
    }

    [Fact]
    public void Env_Overrides_File()
    {
        string path = Path.Combine(_dir, "chorus.json");
        File.WriteAllText(path, """{ "GatewayUrl": "ws://file:1", "Agent": "file-agent" }""");

        Environment.SetEnvironmentVariable("CHORUS_URL", "ws://env:2");
        Environment.SetEnvironmentVariable("CHORUS_AGENT", "env-agent");
        try
        {
            var cfg = ChorusConfig.Load(_dir);
            Assert.Equal("ws://env:2", cfg.GatewayUrl);
            Assert.Equal("env-agent", cfg.Agent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHORUS_URL", null);
            Environment.SetEnvironmentVariable("CHORUS_AGENT", null);
        }
    }

    [Fact]
    public void Env_StartHidden_Parses_Bool()
    {
        File.WriteAllText(Path.Combine(_dir, "chorus.json"), """{ "StartHidden": false }""");

        Environment.SetEnvironmentVariable("CHORUS_START_HIDDEN", "true");
        try
        {
            var cfg = ChorusConfig.Load(_dir);
            Assert.True(cfg.StartHidden);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHORUS_START_HIDDEN", null);
        }
    }

    [Fact]
    public void RoundTrip_Json_Preserves_Values()
    {
        var cfg = new ChorusConfig("ws://rt:1", "a2", "Mic (Realtek)", false, 30, "desktop-win", "/tmp/x.json");
        var back = ChorusConfig.FromJson(cfg.ToJson());

        Assert.Equal(cfg.GatewayUrl, back.GatewayUrl);
        Assert.Equal(cfg.Agent, back.Agent);
        Assert.Equal(cfg.MicDevice, back.MicDevice);
        Assert.Equal(cfg.StartHidden, back.StartHidden);
        Assert.Equal(cfg.MicBufferMs, back.MicBufferMs);
        Assert.Equal(cfg.ClientDevice, back.ClientDevice);
        Assert.Null(back.ConfigPath); // never serialized
    }

    [Fact]
    public void SessionId_File_RoundTrip()
    {
        var cfg = new ChorusConfig(Protocol.DefaultUrl, "hermes", "", true, 20, "desktop-win", null);
        // SessionIdFile is computed from AppContext.BaseDirectory — point it at our temp dir via a custom config path is not enough,
        // so just verify the methods behave on a real temp path:
        string sid = Path.Combine(_dir, "session.id");
        cfg.SaveSessionId("abc-123");
        // (the default SessionIdFile is next to the exe; simulate by reading what we wrote)
        Assert.Equal("abc-123", File.ReadAllText(cfg.SessionIdFile).Trim());
        Assert.Equal("abc-123", cfg.LoadSessionId());
        cfg.SaveSessionId(null);
        Assert.Null(cfg.LoadSessionId());
    }
}
