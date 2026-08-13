using Chorus.Core.Clipboard;

namespace Chorus.Core.Tests;

public class SapiVoicePickerTests
{
    [Fact]
    public void PickBest_EmptyList_ReturnsNull()
    {
        Assert.Null(SapiVoicePicker.PickBest(Array.Empty<string>()));
        Assert.Null(SapiVoicePicker.PickBest(null!));
    }

    [Fact]
    public void PickBest_NaturalVoice_WinsOverLegacy()
    {
        // Windows 11 neural voice beats the legacy desktop voices.
        var voices = new[] { "Microsoft David Desktop", "Microsoft Hazel Desktop", "Microsoft Zira Desktop" };
        Assert.Equal("Microsoft Hazel Desktop", SapiVoicePicker.PickBest(voices));
    }

    [Fact]
    public void PickBest_AnyNaturalSuffixedVoice_IsChosen()
    {
        var voices = new[] { "Microsoft David Desktop", "Microsoft Aria Online (Natural)" };
        Assert.Equal("Microsoft Aria Online (Natural)", SapiVoicePicker.PickBest(voices));
    }

    [Fact]
    public void PickBest_PreferredExactMatch_Wins()
    {
        var voices = new[] { "Microsoft David Desktop", "Microsoft Zira Desktop" };
        Assert.Equal("Microsoft Zira Desktop", SapiVoicePicker.PickBest(voices, "Microsoft Zira Desktop"));
    }

    [Fact]
    public void PickBest_PreferredSubstring_Wins()
    {
        var voices = new[] { "Microsoft David Desktop", "Microsoft Zira Desktop" };
        Assert.Equal("Microsoft Zira Desktop", SapiVoicePicker.PickBest(voices, "Zira"));
    }

    [Fact]
    public void PickBest_Preferred_IsCaseInsensitive()
    {
        var voices = new[] { "Microsoft David Desktop", "Microsoft Zira Desktop" };
        Assert.Equal("Microsoft Zira Desktop", SapiVoicePicker.PickBest(voices, "zira"));
    }

    [Fact]
    public void PickBest_PreferredMissing_FallsBackToNaturalPriority()
    {
        // Preferred voice not installed → natural-name priority still applies.
        var voices = new[] { "Microsoft David Desktop", "Microsoft Guy 24k" };
        Assert.Equal("Microsoft Guy 24k", SapiVoicePicker.PickBest(voices, "Some Voice Not Installed"));
    }

    [Fact]
    public void PickBest_UnknownVoices_ReturnsFirst()
    {
        var voices = new[] { "Custom Voice A", "Custom Voice B" };
        Assert.Equal("Custom Voice A", SapiVoicePicker.PickBest(voices));
    }

    [Fact]
    public void PickBest_PreferredBlank_IgnoresPreference()
    {
        var voices = new[] { "Microsoft David Desktop", "Microsoft Hazel Desktop" };
        Assert.Equal("Microsoft Hazel Desktop", SapiVoicePicker.PickBest(voices, "  "));
        Assert.Equal("Microsoft Hazel Desktop", SapiVoicePicker.PickBest(voices, null));
    }
}
