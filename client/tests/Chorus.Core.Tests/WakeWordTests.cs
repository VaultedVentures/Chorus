using System.Reflection;
using System.Text.Json;
using Chorus.Core.WakeWord;

namespace Chorus.Core.Tests;

/// <summary>
/// Wake-word engine tests. The MFCC contract test pins the C# DSP against
/// the Python reference (tests/wakeword_lib.py) via the fixture dumped by
/// tests/wakeword_templates.py; the pipeline tests drive the engine with the
/// packaged canonical "hey chorus" PCM (an embedded resource, byte-identical
/// every run — deterministic).
/// </summary>
public class WakeWordTests
{
    private static short[] CanonicalPcm()
    {
        string name = WakeWordEngine.EmbeddedResourceNames()
            .Single(n => n.EndsWith("canonical_hey_chorus.pcm", StringComparison.Ordinal));
        return WakeWordEngine.LoadEmbeddedPcm(name);
    }

    private static MfccFixture LoadFixture()
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("canonical_mfcc.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<MfccFixture>(reader.ReadToEnd(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed class MfccFixture
    {
        public int Dim { get; set; }
        public int Frames { get; set; }
        public double[][] Matrix { get; set; } = [];
    }

    private static WakeWordEngine NewEngine(WakeWordSettings? settings = null) =>
        WakeWordEngine.LoadDefault(settings);

    private static void FeedAll(WakeWordEngine engine, short[] pcm, int frameSamples = 320)
    {
        for (int i = 0; i + frameSamples <= pcm.Length; i += frameSamples)
            engine.FeedFrame(pcm.AsSpan(i, frameSamples));
        // the mic pipeline delivers exactly 320-sample frames; a trailing
        // partial frame is dropped in real life too
    }

    // -- packaged model ----------------------------------------------------

    [Fact]
    public void EmbeddedTemplates_LoadsAll_WithExpectedDim()
    {
        var templates = WakeWordEngine.LoadEmbeddedTemplates();
        Assert.Equal(21, templates.Count); // 3 voices x 7-9 speaking rates
        foreach (var t in templates)
        {
            Assert.True(t.Length >= 20, $"template has {t.Length} frames");
            foreach (var row in t)
                Assert.Equal(WakeWordParams.NMfcc, row.Length);
        }
    }

    [Fact]
    public void SensitivityToThreshold_MapsCalibratedRange()
    {
        Assert.Equal(0.30, WakeWordParams.SensitivityToThreshold(0f), 3);
        Assert.Equal(0.40, WakeWordParams.SensitivityToThreshold(1f), 3);
        Assert.Equal(0.34, WakeWordParams.SensitivityToThreshold(0.4f), 3);
        Assert.Equal(0.35, WakeWordParams.SensitivityToThreshold(0.5f), 3);
    }

    // -- MFCC cross-language contract --------------------------------------

    [Fact]
    public void Mfcc_MatchesPythonReferenceFixture()
    {
        var fixture = LoadFixture();
        var extractor = new MfccExtractor();
        var pcm = CanonicalPcm();

        // replicate the hop framing: window 400, hop 160
        int nHops = (pcm.Length - WakeWordParams.WinLen) / WakeWordParams.Hop + 1;
        Assert.Equal(fixture.Frames, nHops);
        Assert.Equal(WakeWordParams.NMfcc, fixture.Dim);

        double maxAbs = 0, maxRel = 0;
        for (int h = 0; h < nHops; h++)
        {
            var mfcc = extractor.Compute(pcm.AsSpan(h * WakeWordParams.Hop, WakeWordParams.WinLen));
            for (int k = 0; k < WakeWordParams.NMfcc; k++)
            {
                double expected = fixture.Matrix[h][k];
                double diff = Math.Abs(mfcc[k] - expected);
                maxAbs = Math.Max(maxAbs, diff);
                maxRel = Math.Max(maxRel, diff / Math.Max(Math.Abs(expected), 1e-6));
            }
        }
        // float32 templates + float64 reference: ~1e-3 expected; allow 5e-3
        Assert.True(maxAbs < 5e-3, $"MFCC contract broken: max abs diff {maxAbs:F5}");
        Assert.True(maxRel < 5e-3, $"MFCC contract broken: max rel diff {maxRel:F5}");
    }

    // -- full pipeline (deterministic, packaged audio) ----------------------

    [Fact]
    public void Engine_DetectsCanonicalPcm()
    {
        var engine = NewEngine();
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        FeedAll(engine, CanonicalPcm());

        var t = Assert.Single(triggers);
        Assert.Equal("hey chorus", t.Phrase);
        Assert.True(t.Score < 0.34, $"canonical score {t.Score} should clear the default threshold 0.34");
        Assert.Equal(21, t.TemplateCount);
    }

    [Fact]
    public void Engine_NoTrigger_OnSilence()
    {
        var engine = NewEngine();
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        FeedAll(engine, new short[16000 * 2]); // 2 s of silence
        Assert.Empty(triggers);
    }

    [Fact]
    public void Engine_NoTrigger_OnNoise()
    {
        var engine = NewEngine();
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        var rng = new Random(42);
        var noise = new short[16000 * 2];
        for (int i = 0; i < noise.Length; i++) noise[i] = (short)rng.Next(-400, 400);
        FeedAll(engine, noise);
        Assert.Empty(triggers);
    }

    [Fact]
    public void Engine_Muted_EmitsNoWakeEvents()
    {
        var engine = NewEngine();
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        engine.Muted = true;
        FeedAll(engine, CanonicalPcm());
        Assert.Empty(triggers); // acceptance: no wake events while muted

        engine.Muted = false;
        FeedAll(engine, CanonicalPcm());
        Assert.Single(triggers); // unmuting restores detection
    }

    [Fact]
    public void Engine_Disabled_EmitsNoWakeEvents()
    {
        var engine = NewEngine(new WakeWordSettings("hey chorus", Enabled: false, 0.4f, 2000, 45000));
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        FeedAll(engine, CanonicalPcm());
        Assert.Empty(triggers);
    }

    [Fact]
    public void Engine_Cooldown_PreventsDoubleTrigger()
    {
        var engine = NewEngine(new WakeWordSettings("hey chorus", true, 0.4f, CooldownMs: 2000, 45000));
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        var pcm = CanonicalPcm();

        FeedAll(engine, pcm);                    // 1st phrase
        FeedAll(engine, new short[16000]);       // 1 s gap (< cooldown)
        FeedAll(engine, pcm);                    // 2nd phrase — suppressed
        Assert.Single(triggers);

        FeedAll(engine, new short[16000 * 3]);   // 3 s gap (> cooldown)
        FeedAll(engine, pcm);                    // 3rd phrase — allowed
        Assert.Equal(2, triggers.Count);
    }

    [Fact]
    public void Engine_CooldownMs_IsConfigurable()
    {
        var engine = NewEngine(new WakeWordSettings("hey chorus", true, 0.4f, CooldownMs: 500, 45000));
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        var pcm = CanonicalPcm();

        FeedAll(engine, pcm);
        FeedAll(engine, new short[16000]);       // 1 s > 500 ms cooldown
        FeedAll(engine, pcm);
        Assert.Equal(2, triggers.Count);
    }

    [Fact]
    public void Engine_HighSensitivity_StillRejectsNoise()
    {
        var engine = NewEngine(new WakeWordSettings("hey chorus", true, Sensitivity: 1f, 2000, 45000));
        var triggers = new List<WakeWordTrigger>();
        engine.WakeDetected += triggers.Add;
        var rng = new Random(7);
        var noise = new short[16000 * 3];
        for (int i = 0; i < noise.Length; i++) noise[i] = (short)rng.Next(-800, 800);
        FeedAll(engine, noise);
        Assert.Empty(triggers); // even max sensitivity must reject noise
    }

    [Fact]
    public void Engine_FrameByFrame_MatchesBulkFeed()
    {
        var a = NewEngine();
        var b = NewEngine();
        var ta = new List<WakeWordTrigger>();
        var tb = new List<WakeWordTrigger>();
        a.WakeDetected += ta.Add;
        b.WakeDetected += tb.Add;
        var pcm = CanonicalPcm();

        FeedAll(a, pcm);            // 320-sample frames (real mic cadence)
        b.FeedFrame(pcm);           // whole buffer at once (same hop framing)
        Assert.Equal(ta.Count, tb.Count);
    }

    [Fact]
    public void Engine_Stats_ReflectProcessing()
    {
        var engine = NewEngine();
        FeedAll(engine, CanonicalPcm());
        var stats = engine.Stats;
        Assert.True(stats.HopsProcessed > 50);
        Assert.Equal(1, stats.Triggers);
        Assert.True(stats.LastScore < 0.34);
    }
}
