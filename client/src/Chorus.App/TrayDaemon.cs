using System.Drawing.Drawing2D;

namespace Chorus.App;

/// <summary>
/// SysTray daemon surface — stays alive when the console is hidden, owns the
/// tray tooltip status and the quit path. NOT the primary UI.
/// </summary>
public sealed class TrayDaemon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _iconHandle;

    public event Action? ShowConsoleRequested;
    public event Action? ReconnectRequested;
    public event Action? QuitRequested;

    public TrayDaemon()
    {
        _iconHandle = MakeIcon();
        _icon = new NotifyIcon
        {
            Text = "CHORUS — connecting…",
            Icon = _iconHandle,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Console", null, (_, _) => ShowConsoleRequested?.Invoke());
        menu.Items.Add("Reconnect", null, (_, _) => ReconnectRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowConsoleRequested?.Invoke();
    }

    /// <summary>Tooltip is 63 chars max; keep it short and current.</summary>
    public void SetStatus(string text)
    {
        if (text.Length > 60) text = text[..60];
        _icon.Text = text;
    }

    public void ShowBalloon(string title, string body)
    {
        _icon.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
    }

    private static Icon MakeIcon()
    {
        // 16x16 filled circle — no external asset needed.
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(0, 180, 136));
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
        _iconHandle.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
