using Chorus.Core.ScreenText;

namespace Chorus.TextPipelineCheck;

/// <summary>
/// Linux-side verification of the ScreenToTextToSpeech text pipeline: reads
/// RAW OCR output on stdin (e.g. from `tesseract`) and prints the cleaned
/// text plus the SAPI-safe chunks — exercising the EXACT same
/// OcrTextCleaner/TtsChunker code the Windows app uses. WinRT OCR itself only
/// runs on Windows; this proves the text side of the pipeline handles real
/// OCR output.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var raw = Console.In.ReadToEnd();
        var cleaned = OcrTextCleaner.Clean(raw);
        var chunks = TtsChunker.Chunk(cleaned);

        Console.WriteLine($"CLEANED ({cleaned.Length} chars):");
        Console.WriteLine(cleaned);
        Console.WriteLine();
        Console.WriteLine($"CHUNKS ({chunks.Count}):");
        for (int i = 0; i < chunks.Count; i++)
        {
            Console.WriteLine($"[{i + 1}/{chunks.Count}] ({chunks[i].Length} chars) {chunks[i]}");
        }
        return 0;
    }
}
