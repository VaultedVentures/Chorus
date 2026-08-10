using Chorus.Core.ScreenText;

namespace Chorus.Core.Tests;

public class TtsChunkerTests
{
    [Fact]
    public void Chunk_NullOrBlank_ReturnsNoChunks()
    {
        Assert.Empty(TtsChunker.Chunk(null));
        Assert.Empty(TtsChunker.Chunk(""));
        Assert.Empty(TtsChunker.Chunk("   \n "));
    }

    [Fact]
    public void Chunk_ShortText_SingleChunk()
    {
        var chunks = TtsChunker.Chunk("Hello world.");
        Assert.Single(chunks);
        Assert.Equal("Hello world.", chunks[0]);
    }

    [Fact]
    public void Chunk_LongText_SplitsAtSentenceBoundaries()
    {
        var longText = string.Join(" ",
            Enumerable.Range(0, 20).Select(i => $"Sentence number {i} has enough words to be a real spoken chunk."));
        var chunks = TtsChunker.Chunk(longText, maxChars: 400);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= 400, $"chunk too long: {chunk.Length}");
        }

        // Every sentence start should be preserved somewhere.
        Assert.Contains(chunks, c => c.Contains("Sentence number 0"));
        Assert.Contains(chunks, c => c.Contains("Sentence number 19"));
    }

    [Fact]
    public void Chunk_RespectsMaxChars_HardSplitsOnlyGiantTokens()
    {
        var text = "word ".Repeat(1000); // 5000 chars of small words
        var chunks = TtsChunker.Chunk(text, maxChars: 400);
        Assert.All(chunks, c => Assert.True(c.Length <= 400));
    }

    [Fact]
    public void Chunk_GiantSingleToken_IsHardSplit()
    {
        var token = new string('x', 1000);
        var chunks = TtsChunker.Chunk(token, maxChars: 400);
        Assert.All(chunks, c => Assert.True(c.Length <= 400));
        Assert.True(chunks.Count >= 3); // 1000 / 400 -> at least 3 pieces
        // Rejoining preserves the token.
        Assert.Equal(token, string.Concat(chunks));
    }

    [Fact]
    public void Chunk_NoTextLost_WhenRejoined()
    {
        var text = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"word{i}"));
        var cleaned = OcrTextCleaner.Clean(text);
        var chunks = TtsChunker.Chunk(cleaned, maxChars: 100);

        var rejoined = string.Join(" ", chunks.Select(c => c.Replace("\n", " ")));
        // Words survive in order (whitespace is normalized, so compare on words).
        var originalWords = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunkWords = rejoined.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(originalWords, chunkWords);
    }

    [Fact]
    public void Chunk_ParagraphBreaks_AreRespected()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var chunks = TtsChunker.Chunk(text, maxChars: 400);
        Assert.Single(chunks);
        Assert.Equal("First paragraph.\nSecond paragraph.", chunks[0]);
    }
}

internal static class TestStringExtensions
{
    public static string Repeat(this string s, int count) =>
        string.Concat(Enumerable.Repeat(s, count));
}
