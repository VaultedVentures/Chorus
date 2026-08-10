using System.Text;

namespace Chorus.Core.ScreenText;

/// <summary>
/// Turns raw OCR output into clean, speakable text: strips control
/// characters, collapses whitespace runs, trims each line and drops empty
/// lines, and normalizes NBSP to plain spaces so the TTS doesn't stall on
/// them. Pure and deterministic — fully unit-testable without Windows.
/// </summary>
public static class OcrTextCleaner
{
    /// <summary>
    /// Clean raw OCR text for TTS. Returns an empty string for null/blank
    /// input or when nothing recognizable survives cleaning.
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            switch (ch)
            {
                case '\u00A0': // NBSP -> plain space (SAPI can stall on it)
                    sb.Append(' ');
                    break;
                case '\r':
                    break; // normalize CRLF -> LF
                case '\n':
                    sb.Append('\n');
                    break;
                case '\t':
                    sb.Append(' ');
                    break;
                default:
                    if (char.IsControl(ch)) break; // drop other control chars
                    sb.Append(ch);
                    break;
            }
        }

        var lines = sb.ToString()
            .Split('\n')
            .Select(CollapseSpaces)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        return string.Join("\n", lines);
    }

    private static string CollapseSpaces(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool lastWasSpace = false;
        foreach (var ch in line)
        {
            if (ch == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A short single-line preview of the recognized text for the tray
    /// balloon / console caption. Never throws on weird input.
    /// </summary>
    public static string Preview(string? text, int maxChars = 96)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var single = text.Replace('\n', ' ').Replace('\r', ' ');
        single = CollapseSpaces(single).Trim();
        if (single.Length <= maxChars) return single;
        return single[..Math.Max(1, maxChars - 1)] + "…";
    }
}
