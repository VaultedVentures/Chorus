using Chorus.Core;

namespace Chorus.Core.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x20, "Ctrl+Shift+Space")]
    [InlineData("Win+Shift+T", HotkeyBinding.ModWin | HotkeyBinding.ModShift, 0x54, "Win+Shift+T")]
    [InlineData("Ctrl+Alt+F8", HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x77, "Ctrl+Alt+F8")]
    [InlineData("Alt+5", HotkeyBinding.ModAlt, 0x35, "Alt+5")]
    [InlineData("Ctrl+F1", HotkeyBinding.ModControl, 0x70, "Ctrl+F1")]
    [InlineData("Ctrl+Shift+F24", HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x87, "Ctrl+Shift+F24")]
    [InlineData("Shift+Enter", HotkeyBinding.ModShift, 0x0D, "Shift+Enter")]
    [InlineData("Ctrl+Tab", HotkeyBinding.ModControl, 0x09, "Ctrl+Tab")]
    [InlineData("Win+Esc", HotkeyBinding.ModWin, 0x1B, "Win+Esc")]
    [InlineData("Ctrl+Home", HotkeyBinding.ModControl, 0x24, "Ctrl+Home")]
    [InlineData("Ctrl+PageUp", HotkeyBinding.ModControl, 0x21, "Ctrl+PageUp")]
    [InlineData("Ctrl+Left", HotkeyBinding.ModControl, 0x25, "Ctrl+Left")]
    [InlineData("Ctrl+Backspace", HotkeyBinding.ModControl, 0x08, "Ctrl+Backspace")]
    public void Parse_ValidSpecs(string spec, uint expectedMods, uint expectedVk, string expectedDisplay)
    {
        Assert.True(HotkeyBinding.TryParse(spec, out var b));
        Assert.True(b.IsValid);
        Assert.Equal(expectedMods, b.Modifiers);
        Assert.Equal(expectedVk, b.VirtualKey);
        Assert.Equal(expectedDisplay, b.Display);
    }

    [Theory]
    [InlineData("ctrl+shift+space", "Ctrl+Shift+Space")]  // case-insensitive
    [InlineData("CONTROL+SHIFT+SPACE", "Ctrl+Shift+Space")]
    [InlineData("  Ctrl + Shift + Space  ", "Ctrl+Shift+Space")] // whitespace-tolerant
    [InlineData("shift+ctrl+space", "Ctrl+Shift+Space")]    // modifier order canonicalized
    [InlineData("Ctrl+Shift+Enter", "Ctrl+Shift+Enter")]
    public void Parse_Canonicalizes(string spec, string expectedDisplay)
    {
        Assert.True(HotkeyBinding.TryParse(spec, out var b));
        Assert.Equal(expectedDisplay, b.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Space")]                 // no modifier — would hijack the key globally
    [InlineData("Ctrl+")]                 // missing key
    [InlineData("Ctrl+Space+Enter")]      // two keys
    [InlineData("Ctrl+Banana")]           // unknown key
    [InlineData("F1")]                    // no modifier
    [InlineData("Ctrl+Shift+Space+Extra")]
    [InlineData("+Ctrl+Space")]
    public void Parse_InvalidSpecs_AreInvalid(string? spec)
    {
        Assert.False(HotkeyBinding.TryParse(spec, out var b));
        Assert.False(b.IsValid);
    }

    [Fact]
    public void Parse_Never_Throws()
    {
        foreach (var spec in new[] { null, "", "garbage", "Ctrl+", "+", "Win+Banana", "  " })
            Assert.False(HotkeyBinding.Parse(spec).IsValid);
    }

    [Fact]
    public void Default_Ptt_Binding_Is_CtrlShiftSpace()
    {
        var b = HotkeyBinding.Parse("Ctrl+Shift+Space");
        Assert.True(b.IsValid);
        Assert.Equal(HotkeyBinding.ModControl | HotkeyBinding.ModShift, b.Modifiers);
        Assert.Equal(0x20u, b.VirtualKey);
    }

    [Fact]
    public void ModNoRepeat_Is_Not_Part_Of_Spec_Modifiers()
    {
        var b = HotkeyBinding.Parse("Ctrl+Shift+Space");
        Assert.Equal(0u, b.Modifiers & HotkeyBinding.ModNoRepeat);
    }
}
