using System.Runtime.InteropServices;

namespace Chorus.App;

/// <summary>
/// Global hotkeys via RegisterHotKey: Win+Shift+T = push-to-talk (hold),
/// Win+Shift+W = wake word window, Win+Shift+R = read screen text
/// (ScreenToTextToSpeech selection). Works from ANY app, even when the
/// console window is hidden to the tray.
/// </summary>
public sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModWin = 0x0008;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int PttId = 1;
    private const int WakeId = 2;
    private const int TextSelectId = 3;
    private const char PttKey = 'T';
    private const char WakeKey = 'W';
    private const char TextSelectKey = 'R';

    public event Action? PttPressed;
    public event Action? PttReleased;
    public event Action? WakePressed;
    public event Action? TextSelectPressed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Register(IntPtr hwnd)
    {
        AssignHandle(hwnd);
        RegisterHotKey(hwnd, PttId, ModWin | ModShift | ModNoRepeat, (uint)char.ToUpperInvariant(PttKey));
        RegisterHotKey(hwnd, WakeId, ModWin | ModShift | ModNoRepeat, (uint)char.ToUpperInvariant(WakeKey));
        RegisterHotKey(hwnd, TextSelectId, ModWin | ModShift | ModNoRepeat, (uint)char.ToUpperInvariant(TextSelectKey));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            int id = m.WParam.ToInt32();
            if (id == PttId)
            {
                // RegisterHotKey fires once per press; poll the key state to
                // distinguish hold (down) from release (up) for hold-to-talk.
                bool down = (GetAsyncKeyState(char.ToUpperInvariant(PttKey)) & 0x8000) != 0;
                if (down) PttPressed?.Invoke(); else PttReleased?.Invoke();
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

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, PttId);
            UnregisterHotKey(Handle, WakeId);
            UnregisterHotKey(Handle, TextSelectId);
        }
        ReleaseHandle();
    }
}
