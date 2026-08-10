namespace Chorus.Core;

/// <summary>
/// A parsed global-hotkey spec such as "Ctrl+Shift+Space" or "Win+Shift+T".
///
/// Grammar: modifiers joined with '+', then a key, e.g.
///   Ctrl+Shift+Space, Win+Shift+T, Alt+F8, Ctrl+Shift+F12
/// Modifiers (any order, case-insensitive): Ctrl, Alt, Shift, Win.
/// Keys: a letter (A-Z), a digit (0-9), F1-F24, or a named key
/// (Space, Tab, Enter, Esc, Home, End, PageUp, PageDown, Insert, Delete,
/// Backspace, Up, Down, Left, Right).
///
/// At least one modifier is required — a bare key as a GLOBAL hotkey would
/// hijack that key for every application, which is almost never intended.
///
/// The numeric values match the Win32 RegisterHotKey contract (MOD_* flags
/// and virtual-key codes) so the portable core can own parsing/validation
/// while the WinForms layer only does the P/Invoke.
/// </summary>
public sealed record HotkeyBinding(uint Modifiers, uint VirtualKey, string Display)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000; // RegisterHotKey modifier, not part of a spec

    public bool IsValid => Modifiers != 0 && VirtualKey != 0;

    /// <summary>Parse a spec. Invalid/unknown specs yield an invalid binding (never throws).</summary>
    public static HotkeyBinding Parse(string? spec)
    {
        if (TryParse(spec, out var binding)) return binding;
        return new HotkeyBinding(0, 0, spec ?? "");
    }

    public static bool TryParse(string? spec, out HotkeyBinding binding)
    {
        binding = new HotkeyBinding(0, 0, spec ?? "");
        if (string.IsNullOrWhiteSpace(spec)) return false;
        if (spec.Contains("++") || spec.TrimStart().StartsWith('+') || spec.TrimEnd().EndsWith('+'))
            return false; // empty modifier/key slot

        var parts = spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false; // need at least one modifier + a key

        uint mods = 0;
        string? keyToken = null;
        foreach (var part in parts)
        {
            string token = part.Trim().ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win" or "windows" or "super": mods |= ModWin; break;
                default:
                    if (keyToken is not null) return false; // two keys in one spec
                    keyToken = part.Trim();
                    break;
            }
        }

        if (mods == 0 || keyToken is null) return false;
        if (!TryMapKey(keyToken, out uint vk, out string keyDisplay)) return false;

        binding = new HotkeyBinding(mods, vk, BuildDisplay(mods, keyDisplay));
        return true;
    }

    /// <summary>Canonical display string, e.g. "Win+Shift+W" or "Ctrl+Shift+Space".</summary>
    public static string BuildDisplay(uint mods, string keyDisplay)
    {
        var parts = new List<string>(4);
        if ((mods & ModWin) != 0) parts.Add("Win");
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        parts.Add(keyDisplay);
        return string.Join("+", parts);
    }

    private static bool TryMapKey(string token, out uint vk, out string display)
    {
        vk = 0;
        display = token;
        string t = token.ToLowerInvariant();

        // single letter A-Z -> VK 0x41..0x5A
        if (t.Length == 1 && t[0] is >= 'a' and <= 'z')
        {
            vk = (uint)(t[0] - 'a' + 0x41);
            display = t.ToUpperInvariant();
            return true;
        }

        // single digit 0-9 -> VK 0x30..0x39
        if (t.Length == 1 && t[0] is >= '0' and <= '9')
        {
            vk = (uint)(t[0] - '0' + 0x30);
            display = t;
            return true;
        }

        // F1-F24 -> VK 0x70..0x87
        if (t.Length >= 2 && t[0] == 'f' && int.TryParse(t[1..], out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            display = $"F{fn}";
            return true;
        }

        display = t switch
        {
            "space" => "Space",
            "tab" => "Tab",
            "enter" or "return" => "Enter",
            "esc" or "escape" => "Esc",
            "home" => "Home",
            "end" => "End",
            "pageup" or "pgup" => "PageUp",
            "pagedown" or "pgdn" => "PageDown",
            "insert" or "ins" => "Insert",
            "delete" or "del" => "Delete",
            "backspace" => "Backspace",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            _ => "",
        };
        if (display.Length == 0) return false;

        vk = t switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "backspace" => 0x08,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ => 0,
        };
        return vk != 0;
    }
}
