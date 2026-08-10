using System.Drawing.Drawing2D;
using Chorus.Core.ScreenText;

namespace Chorus.App;

/// <summary>
/// The ScreenToTextToSpeech selection overlay (card Option 1): a borderless,
/// always-on-top, full-screen form that dims the desktop with a semi-opaque
/// layer. The user click-drags a transparent rectangle over the text/image to
/// select; on release the form exposes the selected screen region (physical
/// pixels). Esc cancels. The captured desktop bitmap is frozen at open time so
/// the selection never races with live screen content.
/// </summary>
public sealed class TextSelectOverlayForm : Form
{
    private const int DimAlpha = 110;          // semi-opaque dark layer
    private const int MinSelectionPx = 4;      // ignore accidental clicks
    private static readonly Color Accent = Color.FromArgb(0, 180, 136);

    private readonly Bitmap _desktop;
    private Point _dragStart;
    private Point _dragCurrent;
    private bool _dragging;

    /// <summary>Selected region in PHYSICAL screen pixels, or null if cancelled.</summary>
    public ScreenRect? SelectedRegion { get; private set; }

    public TextSelectOverlayForm(Bitmap desktopSnapshot)
    {
        _desktop = desktopSnapshot;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen; // covers all monitors
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The controller captured the desktop BEFORE this form appeared, so the
        // snapshot is clean (no overlay in it). Just paint once.
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // Draw the frozen desktop full-bleed.
        g.DrawImage(_desktop, ClientRectangle);

        // Semi-opaque dim layer over everything...
        using (var dim = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0)))
        {
            g.FillRectangle(dim, ClientRectangle);
        }

        // ...except the selection rectangle stays transparent (the text under
        // it stays fully readable).
        if (_dragging)
        {
            var sel = SelectionClientRect();
            if (sel.Width > 0 && sel.Height > 0)
            {
                using var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
                g.FillRectangle(clear, sel);

                using var border = new Pen(Accent, 2f);
                g.DrawRectangle(border, sel);

                DrawSizeReadout(g, sel);
            }
        }
    }

    private Rectangle SelectionClientRect() =>
        Rectangle.FromLTRB(
            Math.Min(_dragStart.X, _dragCurrent.X),
            Math.Min(_dragStart.Y, _dragCurrent.Y),
            Math.Max(_dragStart.X, _dragCurrent.X),
            Math.Max(_dragStart.Y, _dragCurrent.Y));

    private void DrawSizeReadout(Graphics g, Rectangle sel)
    {
        var text = $"{sel.Width} × {sel.Height}";
        using var font = new Font("Segoe UI", 9f);
        var size = g.MeasureString(text, font);
        var x = sel.Right + 8;
        var y = sel.Bottom + 8;
        if (x + size.Width > ClientSize.Width) x = sel.Left - (int)size.Width - 8;
        if (y + size.Height > ClientSize.Height) y = sel.Top - (int)size.Height - 8;
        using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        using var fg = new SolidBrush(Color.White);
        g.FillRectangle(bg, x, y, size.Width + 8, size.Height + 4);
        g.DrawString(text, font, fg, x + 4, y + 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStart = e.Location;
        _dragCurrent = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _dragCurrent = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        _dragCurrent = e.Location;

        var client = SelectionClientRect();
        if (client.Width >= MinSelectionPx && client.Height >= MinSelectionPx)
        {
            // Map client coords -> physical screen pixels (handles the
            // negative-origin virtual screen and DPI scaling on Windows).
            var tl = PointToScreen(client.Location);
            var br = PointToScreen(new Point(client.Right, client.Bottom));
            SelectedRegion = ScreenRect.NormalizeDrag(tl.X, tl.Y, br.X, br.Y);
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            // Too small — treat as cancel (user clicked, didn't select).
            SelectedRegion = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    /// <summary>Programmatically cancel (used when a second trigger arrives while the overlay is up).</summary>
    public void Cancel()
    {
        if (IsDisposed) return;
        SelectedRegion = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Cancel();
        }
    }
}
