using System.Runtime.InteropServices;
using Chorus.Core;

namespace Chorus.App;

/// <summary>
/// Global hotkeys. Hotkey specs are configurable (chorus.json
/// PttHotkey/WakeHotkey/TextSelectHotkey, e.g. "Ctrl+Shift+Space",
/// "Win+Shift+W", "Win+Shift+R"); parsing/validation lives in
/// Chorus.Core.HotkeyBinding so it is unit-testable. Works from ANY app, even
/// when the console window is hidden to the tray.
///
/// Key+modifier combos use RegisterHotKey (consumed at the OS level).
/// MODIFIER-ONLY CHORDS (e.g. Win+Alt) cannot be registered by RegisterHotKey
/// — they are detected by polling the physical modifier keys on the hold
/// timer (Handy-style two-key push-to-talk).
///
/// Hold-to-talk: the press starts the hold; the poll timer owns release
/// detection (25 ms) — when the key/chord goes up, no matter which app has
/// focus, we fire the release. A 60 s watchdog force-releases if wedged.
/// </summary>
public sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = HotkeyBinding.ModNoRepeat;
    private const int PttId = 1;
    private const int WakeId = 2;
    private const int TextSelectId = 3;

    // Modifier virtual-key codes for chord polling (RegisterHotKey can't do
    // modifier-only combos; Handy-style chords are detected by key state).
    private const int VkLWin = 0x5B, VkRWin = 0x5C;
    private const int VkControl = 0x11, VkMenu = 0x12, VkShift = 0x10;

    /// <summary>Force-release if a hold exceeds this (belt-and-braces).</summary>
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);

    /// <summary>Poll cadence for release detection (snappy, cheap).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly HotkeyBinding _ptt;
    private readonly HotkeyBinding _wake;
    private readonly HotkeyBinding _textSelect;
    private readonly System.Windows.Forms.Timer _holdTimer;
    private DateTime _holdStart;
    private bool _pttDown;

    public event Action? PttPressed;
    public event Action? PttReleased;
    public event Action? WakePressed;
    public event Action? TextSelectPressed;

    /// <summary>Raised when a combo could not be registered (owned by another app).</summary>
    public event Action<string>? RegistrationFailed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public GlobalHotkeys(HotkeyBinding ptt, HotkeyBinding wake, HotkeyBinding textSelect)
    {
        _ptt = ptt;
        _wake = wake;
        _textSelect = textSelect;
        _holdTimer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _holdTimer.Tick += (_, _) => PollHold();
    }

    public string PttDisplay => _ptt.Display;
    public string WakeDisplay => _wake.Display;
    public string TextSelectDisplay => _textSelect.Display;

    public void Register(IntPtr hwnd)
    {
        AssignHandle(hwnd);
        if (!TryRegister(hwnd, PttId, _ptt))
            RegistrationFailed?.Invoke($"PTT hotkey {_ptt.Display} is already in use by another app");
        if (!TryRegister(hwnd, WakeId, _wake))
            RegistrationFailed?.Invoke($"Wake hotkey {_wake.Display} is already in use by another app");
        if (!TryRegister(hwnd, TextSelectId, _textSelect))
            RegistrationFailed?.Invoke($"Text-select hotkey {_textSelect.Display} is already in use by another app");

        // Modifier-only chord PTT: RegisterHotKey can't hold it — the poll
        // timer becomes the detector (starts immediately, runs forever).
        if (_ptt.IsChord)
            _holdTimer.Start();
    }

    private static bool TryRegister(IntPtr hwnd, int id, HotkeyBinding binding)
    {
        // Chords (modifier-only) are hook/poll-driven; skip RegisterHotKey.
        if (binding.IsChord) return true;
        return binding.IsValid && RegisterHotKey(hwnd, id, binding.Modifiers | ModNoRepeat, binding.VirtualKey);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            int id = m.WParam.ToInt32();
            if (id == PttId)
            {
                // WM_HOTKEY with MOD_NOREPEAT arrives exactly once per press.
                // Start the hold; the poll timer owns release detection.
                BeginHold();
            }
            else if (id == WakeId)
            {
                WakePressed?.Invoke();
            }
            else if (id == TextSelectId)
            {
                TextSelectPressed?.Invoke();
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>True while ALL modifiers of a chord are physically down.</summary>
    private bool ChordHeld(HotkeyBinding chord)
    {
        uint mods = chord.Modifiers;
        if ((mods & HotkeyBinding.ModWin) != 0)
        {
            bool win = (GetAsyncKeyState(VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;
            if (!win) return false;
        }
        if ((mods & HotkeyBinding.ModControl) != 0 && (GetAsyncKeyState(VkControl) & 0x8000) == 0) return false;
        if ((mods & HotkeyBinding.ModAlt) != 0 && (GetAsyncKeyState(VkMenu) & 0x8000) == 0) return false;
        if ((mods & HotkeyBinding.ModShift) != 0 && (GetAsyncKeyState(VkShift) & 0x8000) == 0) return false;
        return true;
    }

    private void BeginHold()
    {
        if (_pttDown) return; // double-press guard
        _pttDown = true;
        _holdStart = DateTime.UtcNow;
        PttPressed?.Invoke();
        _holdTimer.Start();
    }

    private void PollHold()
    {
        bool overMax = DateTime.UtcNow - _holdStart > MaxHold;

        if (_ptt.IsChord)
        {
            // Chord detector: press when the chord goes down, release on up.
            bool held = ChordHeld(_ptt);
            if (held && !_pttDown)
                BeginHold();
            else if (!held && _pttDown)
            {
                _pttDown = false;
                PttReleased?.Invoke();
            }
            else if (_pttDown && overMax)
            {
                _pttDown = false;
                PttReleased?.Invoke();
            }
            return;
        }

        if (!_pttDown)
        {
            _holdTimer.Stop();
            return;
        }

        // Physical key state — works even when another app has focus, and even
        // if the key was released while a different window was active.
        bool keyDown = (GetAsyncKeyState((int)_ptt.VirtualKey) & 0x8000) != 0;

        if (!keyDown || overMax)
        {
            _pttDown = false;
            _holdTimer.Stop();
            PttReleased?.Invoke();
        }
    }

    public void Dispose()
    {
        _holdTimer.Stop();
        _holdTimer.Dispose();
        if (Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, PttId);
            UnregisterHotKey(Handle, WakeId);
            UnregisterHotKey(Handle, TextSelectId);
        }
        ReleaseHandle();
    }
}
