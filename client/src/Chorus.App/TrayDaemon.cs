using System.Drawing.Drawing2D;

namespace Chorus.App;

/// <summary>
/// SysTray daemon surface — stays alive when the console is hidden, owns the
/// tray tooltip status and the quit path. NOT the primary UI.
/// </summary>
public sealed class TrayDaemon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _iconIdle;
    private readonly Icon _iconTransmitting;
    private string _baseStatus = "CHORUS — connecting…";
    private bool _transmitting;

    public event Action? ShowConsoleRequested;
    public event Action? ReconnectRequested;
    public event Action? TextSelectRequested;
    public event Action? QuitRequested;

    public TrayDaemon()
    {
        _iconIdle = MakeIcon(Color.FromArgb(0, 180, 136));      // teal = idle/connected
        _iconTransmitting = MakeIcon(Color.FromArgb(235, 120, 20)); // amber = transmitting
        _icon = new NotifyIcon
        {
            Text = _baseStatus,
            Icon = _iconIdle,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Console", null, (_, _) => ShowConsoleRequested?.Invoke());
        menu.Items.Add("Read Screen Text", null, (_, _) => TextSelectRequested?.Invoke());
        menu.Items.Add("Reconnect", null, (_, _) => ReconnectRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowConsoleRequested?.Invoke();
    }

    /// <summary>Tooltip is 63 chars max; keep it short and current.</summary>
    public void SetStatus(string text)
    {
        _baseStatus = text;
        Refresh();
    }

    /// <summary>While transmitting, the tooltip and icon color show it (green → amber).</summary>
    public void SetTransmitting(bool on)
    {
        _transmitting = on;
        _icon.Icon = on ? _iconTransmitting : _iconIdle;
        Refresh();
    }

    private void Refresh()
    {
        string text = _transmitting ? "CHORUS — transmitting… (release to stop)" : _baseStatus;
        if (text.Length > 60) text = text[..60];
        _icon.Text = text;
    }

    public void ShowBalloon(string title, string body)
    {
        _icon.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
    }

    private static Icon MakeIcon(Color color)
    {
        // 16x16 filled circle — no external asset needed.
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
        }
        IntPtr hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        NativeMethods.DestroyIcon(hIcon);
        return icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _iconIdle.Dispose();
        _iconTransmitting.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
