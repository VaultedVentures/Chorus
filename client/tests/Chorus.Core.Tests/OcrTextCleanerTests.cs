using Chorus.Core.ScreenText;

namespace Chorus.Core.Tests;

public class OcrTextCleanerTests
{
    [Fact]
    public void Clean_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OcrTextCleaner.Clean(null));
        Assert.Equal(string.Empty, OcrTextCleaner.Clean(""));
        Assert.Equal(string.Empty, OcrTextCleaner.Clean("   \n \t "));
    }

    [Fact]
    public void Clean_TrimsLinesAndDropsEmptyLines()
    {
        var raw = "  Hello world  \n\n\n   \nSecond line  ";
        Assert.Equal("Hello world\nSecond line", OcrTextCleaner.Clean(raw));
    }

    [Fact]
    public void Clean_CollapsesWhitespaceRuns()
    {
        Assert.Equal("a b c", OcrTextCleaner.Clean("a   b\t\tc"));
    }

    [Fact]
    public void Clean_ReplacesNbspWithPlainSpace()
    {
        Assert.Equal("a b", OcrTextCleaner.Clean("a\u00A0b"));
    }

    [Fact]
    public void Clean_StripsControlCharacters()
    {
        // \u0002 (STX) and \u0007 (BEL) are common OCR junk; \r normalizes away.
        Assert.Equal("hello\nworld", OcrTextCleaner.Clean("hel\u0002lo\r\nwor\u0007ld"));
    }

    [Fact]
    public void Clean_KeepsPunctuationIntact()
    {
        Assert.Equal("Hello, world! How are you?", OcrTextCleaner.Clean("Hello, world! How are you?"));
    }

    [Fact]
    public void Preview_SingleLine_IsTruncatedWithEllipsis()
    {
        var longText = new string('x', 200);
        var preview = OcrTextCleaner.Preview(longText, 96);
        Assert.True(preview.Length <= 96);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Preview_ShortText_IsUnchanged()
    {
        Assert.Equal("short", OcrTextCleaner.Preview("short"));
    }

    [Fact]
    public void Preview_CollapsesNewlinesToSpaces()
    {
        Assert.Equal("a b", OcrTextCleaner.Preview("a\nb"));
    }
}
