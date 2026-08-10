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
