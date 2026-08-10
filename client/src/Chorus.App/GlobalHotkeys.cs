using System.Runtime.InteropServices;
using Chorus.Core;

namespace Chorus.App;

/// <summary>
/// Global hotkeys via RegisterHotKey. Hotkey specs are configurable
/// (chorus.json PttHotkey/WakeHotkey/TextSelectHotkey, e.g. "Ctrl+Shift+Space",
/// "Win+Shift+W", "Win+Shift+R"); parsing/validation lives in
/// Chorus.Core.HotkeyBinding so it is unit-testable. Works from ANY app, even
/// when the console window is hidden to the tray, and the combo never reaches
/// the focused application (RegisterHotKey consumes it at the OS level).
///
/// Hold-to-talk: RegisterHotKey fires once per press (MOD_NOREPEAT). On the
/// press we start the hold and poll the physical key state on a 25 ms timer;
/// when the key goes up — no matter which app has focus — we fire the
/// release. A 60 s watchdog force-releases if the key state is ever wedged,
/// so the stream can never be left open.
/// </summary>
public sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = HotkeyBinding.ModNoRepeat;
    private const int PttId = 1;
    private const int WakeId = 2;
    private const int TextSelectId = 3;

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
    }

    private static bool TryRegister(IntPtr hwnd, int id, HotkeyBinding binding) =>
        binding.IsValid && RegisterHotKey(hwnd, id, binding.Modifiers | ModNoRepeat, binding.VirtualKey);

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
        if (!_pttDown)
        {
            _holdTimer.Stop();
            return;
        }

        // Physical key state — works even when another app has focus, and even
        // if the key was released while a different window was active.
        bool keyDown = (GetAsyncKeyState((int)_ptt.VirtualKey) & 0x8000) != 0;
        bool overMax = DateTime.UtcNow - _holdStart > MaxHold;

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
