using System.Text;

namespace Chorus.Core.ScreenText;

/// <summary>
/// Splits long recognized text into SAPI-safe spoken chunks. The ClipReader
/// lesson (v1.1): SAPI's Speak() becomes unreliable on long strings (a few
/// hundred chars is the practical ceiling), so a long screen selection must be
/// read in bounded pieces. Chunks prefer sentence/paragraph boundaries and
/// never split a word unless a single token is longer than the cap.
/// </summary>
public static class TtsChunker
{
    /// <summary>Practical per-Speak ceiling from the ClipReader incident.</summary>
    public const int DefaultMaxChars = 400;

    /// <summary>Sentence terminators that make good chunk boundaries.</summary>
    private static readonly char[] SentenceEnds = { '.', '!', '?', ';', ':', '\n' };

    /// <summary>
    /// Split <paramref name="text"/> into speakable chunks, each at most
    /// <paramref name="maxChars"/> long. Empty/blank input yields no chunks.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || maxChars <= 0) return Array.Empty<string>();

        var cleaned = OcrTextCleaner.Clean(text);
        if (cleaned.Length == 0) return Array.Empty<string>();

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var sentence in SplitSentences(cleaned))
        {
            if (sentence.Length > maxChars)
            {
                // A single sentence longer than the cap: flush what we have,
                // then hard-split this sentence on word boundaries.
                Flush(current, chunks);
                SplitLongSentence(sentence, maxChars, chunks);
                continue;
            }

            if (current.Length > 0 && current.Length + sentence.Length > maxChars)
            {
                Flush(current, chunks);
            }
            current.Append(sentence);
        }
        Flush(current, chunks);

        return chunks;
    }

    /// <summary>Split on sentence ends (keeping the terminator) — the natural TTS pause points.</summary>
    private static IEnumerable<string> SplitSentences(string text)
    {
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            current.Append(ch);
            if (SentenceEnds.Contains(ch))
            {
                yield return current.ToString();
                current.Clear();
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static void SplitLongSentence(string sentence, int maxChars, List<string> chunks)
    {
        var current = new StringBuilder();
        foreach (var word in sentence.Split(' '))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                Flush(current, chunks);
            }
            if (word.Length > maxChars)
            {
                // One gigantic token (URL, blob of digits): hard-split it.
                Flush(current, chunks);
                for (int i = 0; i < word.Length; i += maxChars)
                {
                    chunks.Add(word.Substring(i, Math.Min(maxChars, word.Length - i)));
                }
            }
            else
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
        }
        Flush(current, chunks);
    }

    private static void Flush(StringBuilder current, List<string> chunks)
    {
        var text = current.ToString().Trim();
        if (text.Length > 0) chunks.Add(text);
        current.Clear();
    }
}
