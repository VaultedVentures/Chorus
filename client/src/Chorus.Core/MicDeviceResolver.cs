namespace Chorus.Core;

/// <summary>
/// Resolves a configured mic device spec to an input-device index.
///
/// The spec is a free-form string from chorus.json / CHORUS_DEVICE:
///   ""            → system default input device (index 0)
///   "3"           → literal device index (clamped to valid range)
///   "USB Audio"   → first device whose name CONTAINS the spec
///                   (case-insensitive); falls back to default if no match.
///
/// Pure logic — no NAudio dependency — so it is unit-testable on any OS.
/// The app feeds it the device-name list from NAudio's WaveIn enumeration.
/// </summary>
public static class MicDeviceResolver
{
    /// <summary>
    /// Resolve <paramref name="spec"/> against <paramref name="deviceNames"/>
    /// (device i is described by deviceNames[i]).
    /// </summary>
    /// <returns>A valid device index, or -1 if the list is empty.</returns>
    public static int ResolveIndex(string spec, IReadOnlyList<string> deviceNames)
    {
        if (deviceNames.Count == 0) return -1;

        spec = spec?.Trim() ?? "";

        // Empty spec = system default (index 0).
        if (spec.Length == 0) return 0;

        // Pure numeric spec = literal index (clamped).
        if (int.TryParse(spec, out int idx))
            return idx >= 0 && idx < deviceNames.Count ? idx : 0;

        // Otherwise match by name substring, case-insensitive.
        for (int i = 0; i < deviceNames.Count; i++)
        {
            if (deviceNames[i].Contains(spec, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0; // no name match → default device
    }

    /// <summary>Human-readable description of what a spec resolves to (for logs/UI).</summary>
    public static string Describe(string spec, IReadOnlyList<string> deviceNames, int index)
    {
        if (index < 0) return "no audio input devices available";
        var name = index < deviceNames.Count ? deviceNames[index] : $"device {index}";
        return string.IsNullOrWhiteSpace(spec)
            ? $"default input device ({name})"
            : $"device {index} ({name})";
    }
}
