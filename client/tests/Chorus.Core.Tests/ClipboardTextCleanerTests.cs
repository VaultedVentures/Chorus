using Chorus.Core.Clipboard;

namespace Chorus.Core.Tests;

public class ClipboardTextCleanerTests
{
    [Fact]
    public void Clean_NullOrBlank_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClipboardTextCleaner.Clean(null));
        Assert.Equal(string.Empty, ClipboardTextCleaner.Clean(""));
        Assert.Equal(string.Empty, ClipboardTextCleaner.Clean("   \n \r\n  \t "));
    }

    [Fact]
    public void Clean_StripsAnsiColorCodes()
    {
        // Windows Terminal / CLI tools can copy ANSI SGR sequences with text.
        Assert.Equal("Hello world", ClipboardTextCleaner.Clean("\u001b[31mHello\u001b[0m world"));
    }

    [Fact]
    public void Clean_StripsAnsiCursorAndErasureSequences()
    {
        Assert.Equal("ab", ClipboardTextCleaner.Clean("a\u001b[2K\u001b[1;1Hb"));
    }

    [Fact]
    public void Clean_StripsOscHyperlinks()
    {
        // OSC 8 hyperlink sequences (some terminals copy them into text).
        Assert.Equal("click me", ClipboardTextCleaner.Clean("\u001b]8;;https://example.com\u001b\\click me\u001b]8;;\u001b\\"));
    }

    [Fact]
    public void Clean_StripsStrayEscapeChar()
    {
        // A bare ESC between letters is dropped (control-char convention),
        // joining the adjacent characters — same as the OCR cleaner.
        Assert.Equal("ab", ClipboardTextCleaner.Clean("a\u001bb"));
    }

    [Fact]
    public void Clean_CrLf_Normalizes_ToLf()
    {
        // Windows Terminal copies use CRLF at the end of every line.
        Assert.Equal("one two", ClipboardTextCleaner.Clean("one\r\ntwo"));
    }

    [Fact]
    public void Clean_BareCr_Normalizes_ToLf()
    {
        Assert.Equal("one two", ClipboardTextCleaner.Clean("one\rtwo"));
    }

    [Fact]
    public void Clean_SingleNewline_IsTerminalWrap_BecomesSpace()
    {
        // THE Scott case: a long Hermes reply wrapped at the console width is
        // "chocker block full of line breaks" — every line ends with a break.
        // A single break between content lines is a WRAP: prose continues.
        Assert.Equal(
            "The quick brown fox jumps over the lazy dog and keeps running",
            ClipboardTextCleaner.Clean("The quick brown fox jumps over\r\nthe lazy dog and keeps running"));
    }

    [Fact]
    public void Clean_BlankLine_IsParagraphBreak_StaysNewline()
    {
        // Two+ newlines in a row = a real paragraph break → keep a single \n
        // so TtsChunker treats it as a chunk boundary (a spoken pause).
        Assert.Equal("First paragraph\nSecond paragraph", ClipboardTextCleaner.Clean("First paragraph\n\nSecond paragraph"));
    }

    [Fact]
    public void Clean_MultipleBlankLines_CollapseToSingleParagraphBreak()
    {
        Assert.Equal("a\nb", ClipboardTextCleaner.Clean("a\n\n\n\n\nb"));
    }

    [Fact]
    public void Clean_LeadingAndTrailingNewlines_AreDropped()
    {
        Assert.Equal("hello", ClipboardTextCleaner.Clean("\n\nhello\n\n"));
        Assert.Equal("hello world", ClipboardTextCleaner.Clean("\nhello\nworld\n"));
    }

    [Fact]
    public void Clean_TrimsLines_And_CollapsesSpaceRuns()
    {
        Assert.Equal("a b c", ClipboardTextCleaner.Clean("  a   b\t\tc  "));
    }

    [Fact]
    public void Clean_ReplacesNbsp_WithPlainSpace()
    {
        Assert.Equal("a b", ClipboardTextCleaner.Clean("a\u00A0b"));
    }

    [Fact]
    public void Clean_StripsControlCharacters()
    {
        Assert.Equal("hello world", ClipboardTextCleaner.Clean("hel\u0002lo \u0007world"));
    }

    [Fact]
    public void Clean_KeepsPunctuationIntact()
    {
        Assert.Equal("Hello, world! How are you?", ClipboardTextCleaner.Clean("Hello, world! How are you?"));
    }

    [Fact]
    public void Clean_TerminalCopy_FlowsAsProse_WithParagraphPauses()
    {
        // A realistic Windows Terminal copy: wrapped prose lines + blank-line
        // paragraph breaks + a stray ANSI sequence. The result must read as
        // flowing prose with paragraph pauses — never a wall of newlines.
        var terminalCopy =
            "Here is a long reply from Hermes that wraps at the console width,\r\n" +
            "so every line ends with a break and the copy is chocker block full\r\n" +
            "of line breaks. It must not get hung up on that.\r\n" +
            "\r\n" +
            "\u001b[32mSecond paragraph starts here and also wraps\r\n" +
            "across several lines just like the first one does.\u001b[0m";

        var cleaned = ClipboardTextCleaner.Clean(terminalCopy);
        Assert.Equal(
            "Here is a long reply from Hermes that wraps at the console width, so every line ends with a break and the copy is chocker block full of line breaks. It must not get hung up on that.\n" +
            "Second paragraph starts here and also wraps across several lines just like the first one does.",
            cleaned);
    }

    [Fact]
    public void Clean_LongTerminalCopy_ChunksStayUnderCap()
    {
        // End-to-end with the chunker: even a long line-break-dense copy must
        // chunk into bounded speakable pieces (the ClipReader v1.1 lesson).
        var longLine = string.Join("\r\n", Enumerable.Repeat("lorem ipsum dolor sit amet consectetur adipiscing elit", 40));
        var cleaned = ClipboardTextCleaner.Clean(longLine);
        var chunks = Chorus.Core.ScreenText.TtsChunker.Chunk(cleaned);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Length, 1, Chorus.Core.ScreenText.TtsChunker.DefaultMaxChars);
        }
        // No chunk may contain an embedded newline (wraps are already spaces).
        Assert.All(chunks, c => Assert.DoesNotContain('\n', c));
    }

    [Fact]
    public void Preview_SingleLine_IsTruncatedWithEllipsis()
    {
        var longText = new string('x', 200);
        var preview = ClipboardTextCleaner.Preview(longText, 96);
        Assert.True(preview.Length <= 96);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Preview_ShortText_IsUnchanged()
    {
        Assert.Equal("short", ClipboardTextCleaner.Preview("short"));
    }

    [Fact]
    public void Preview_CollapsesNewlinesToSpaces()
    {
        Assert.Equal("a b", ClipboardTextCleaner.Preview("a\nb"));
    }

    [Fact]
    public void Preview_BlankOrNull_IsEmpty()
    {
        Assert.Equal(string.Empty, ClipboardTextCleaner.Preview(null));
        Assert.Equal(string.Empty, ClipboardTextCleaner.Preview("   "));
    }
}
