namespace Chorus.Core.ScreenText;

/// <summary>
/// Immutable screen rectangle in integer pixels (physical or logical — the
/// caller decides; the helpers here are unit-agnostic).
/// </summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static ScreenRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>
    /// Turn any click-drag (any direction, any corner order) into a
    /// top-left-anchored rectangle. This is the geometry behind the
    /// ScreenToTextToSpeech selection overlay.
    /// </summary>
    public static ScreenRect NormalizeDrag(int x1, int y1, int x2, int y2) =>
        FromLTRB(
            Math.Min(x1, x2),
            Math.Min(y1, y2),
            Math.Max(x1, x2),
            Math.Max(y1, y2));

    public bool Contains(int x, int y) =>
        x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>Clamp this rect so it lies fully inside <paramref name="bounds"/>.</summary>
    public ScreenRect ClampTo(ScreenRect bounds)
    {
        int x = Math.Clamp(X, bounds.X, Math.Max(bounds.X, bounds.Right - Width));
        int y = Math.Clamp(Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - Height));
        int w = Math.Min(Width, bounds.Right - x);
        int h = Math.Min(Height, bounds.Bottom - y);
        return new ScreenRect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    /// <summary>
    /// Scale by a DPI factor (1.0 = 96 dpi). Rounds half away from zero so a
    /// 1-px selection on a 150% display stays at least 1 physical px.
    /// </summary>
    public ScreenRect ScaleBy(double factor)
    {
        int Scale(int v) => (int)Math.Round(v * factor, MidpointRounding.AwayFromZero);
        return new ScreenRect(Scale(X), Scale(Y), Scale(Width), Scale(Height));
    }

    /// <summary>Shrink (negative) or grow (positive) by insets on every side.</summary>
    public ScreenRect Inflate(int dx, int dy) =>
        new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    /// <summary>
    /// Fit a size inside a maximum dimension, preserving aspect ratio.
    /// Returns the input unchanged when it already fits. Used to stay under
    /// the OCR engine's maximum image dimension.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxDim)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        if (width <= maxDim && height <= maxDim) return (width, height);
        double scale = (double)maxDim / Math.Max(width, height);
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
