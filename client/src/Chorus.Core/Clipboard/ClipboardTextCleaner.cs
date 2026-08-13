using System.Text;
using System.Text.RegularExpressions;

namespace Chorus.Core.Clipboard;

/// <summary>
/// Turns raw clipboard text into clean, speakable prose for the ClipReader
/// feature. Windows Terminal copies are the canonical hard case: every line
/// ends with CRLF, so a long Hermes reply wrapped at the console width is
/// "chocker block full of line breaks". SAPI stutters or hangs on a wall of
/// newlines, so single line breaks (terminal wraps) become SPACES — the text
/// flows as prose — while runs of 2+ newlines (real paragraph breaks)
/// collapse to a single '\n' pause point that TtsChunker treats as a chunk
/// boundary. ANSI escape sequences (color codes / OSC hyperlinks that some
/// apps copy into the clipboard) and stray control characters are stripped
/// so nothing textual is ever spoken. Pure and deterministic — fully
/// unit-testable without Windows.
/// </summary>
public static class ClipboardTextCleaner
{
    /// <summary>CSI sequences: ESC [ params? intermediates? final-byte (@-~).</summary>
    private static readonly Regex AnsiCsi = new(
        "\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>OSC sequences (hyperlinks etc.): ESC ] ... (BEL|ST).</summary>
    private static readonly Regex AnsiOsc = new(
        "\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)?", RegexOptions.Compiled);

    /// <summary>
    /// Clean raw clipboard text for TTS. Returns an empty string for
    /// null/blank input or when nothing speakable survives cleaning.
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 1. Strip ANSI escape sequences (colors, hyperlinks, cursor moves).
        string text = AnsiOsc.Replace(raw, string.Empty);
        text = AnsiCsi.Replace(text, string.Empty);
        text = text.Replace("\u001b", string.Empty);

        // 2. Normalize line endings.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 3. Line-break semantics: a single '\n' between content lines is a
        //    terminal WRAP (prose continues -> space); one or more blank
        //    lines is a PARAGRAPH BREAK (pause -> single '\n'). Leading and
        //    trailing newlines are dropped.
        var sb = new StringBuilder(text.Length);
        int blankRun = 0;   // consecutive blank lines since the last content line
        bool anyContent = false;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = CleanLine(rawLine);
            if (line.Length == 0)
            {
                blankRun++;
                continue;
            }

            if (anyContent)
            {
                sb.Append(blankRun == 0 ? ' ' : '\n');
            }
            sb.Append(line);
            anyContent = true;
            blankRun = 0;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Clean one physical line: strip control chars, collapse whitespace
    /// runs (NBSP/tab count as spaces), trim. Never returns null.
    /// </summary>
    private static string CleanLine(string rawLine)
    {
        var sb = new StringBuilder(rawLine.Length);
        bool lastSpace = false;
        foreach (var ch in rawLine)
        {
            // Whitespace check FIRST: IsControl('\t') is true, and tabs must
            // become spaces (Windows Terminal copies indent with tabs).
            if (char.IsWhiteSpace(ch)) // NBSP, tab, thin spaces all count
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
            lastSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// A short single-line preview of the cleaned text for the tray balloon /
    /// console caption. Never throws on weird input.
    /// </summary>
    public static string Preview(string? text, int maxChars = 96)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var single = text.Replace('\n', ' ').Replace('\r', ' ');
        var sb = new StringBuilder(single.Length);
        bool lastSpace = false;
        foreach (var ch in single)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(ch);
            lastSpace = false;
        }
        var collapsed = sb.ToString().Trim();
        if (collapsed.Length <= maxChars) return collapsed;
        return collapsed[..Math.Max(1, maxChars - 1)] + "…";
    }
}
