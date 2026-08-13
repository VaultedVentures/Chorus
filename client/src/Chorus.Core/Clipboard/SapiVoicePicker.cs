namespace Chorus.Core.Clipboard;

/// <summary>
/// Picks the best installed SAPI voice for high-quality read-aloud.
/// Windows 11's neural "Natural" voices (Hazel, Aria, Guy, Jenny, …) sound
/// dramatically better than the legacy desktop voices (David, Zira), so the
/// picker prefers natural voices, then a known-good priority list, then any
/// enabled voice. An explicit configured name (chorus.json VoiceName) always
/// wins when installed (exact match first, then substring). Pure and
/// unit-testable — no System.Speech dependency in the core.
/// </summary>
public static class SapiVoicePicker
{
    // Windows 11 natural voices, best-first (en-US/en-GB/en-AU neural).
    private static readonly string[] NaturalNames =
    {
        "Hazel", "Aria", "Guy", "Jenny", "Michelle", "Natasha", "Libby",
        "Sonia", "Ryan", "Emma", "James", "George", "Susan",
    };

    /// <summary>
    /// Pick the best voice from an installed-voice name list.
    /// Returns null for an empty list.
    /// </summary>
    public static string? PickBest(IReadOnlyList<string> voices, string? preferred = null)
    {
        if (voices is null || voices.Count == 0) return null;

        // 1. Explicit configured voice wins when installed.
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var exact = voices.FirstOrDefault(v =>
                string.Equals(v, preferred, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var contains = voices.FirstOrDefault(v =>
                v.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (contains is not null) return contains;
        }

        // 2. Neural "Natural" voices first — the high-quality Win11 voices.
        foreach (var v in voices)
        {
            if (v.Contains("natural", StringComparison.OrdinalIgnoreCase)) return v;
        }

        // 3. Known-good natural voice names (Hazel etc.).
        foreach (var name in NaturalNames)
        {
            var hit = voices.FirstOrDefault(v =>
                v.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        // 4. Any enabled voice.
        return voices[0];
    }
}
