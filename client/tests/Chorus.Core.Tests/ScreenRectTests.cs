using Chorus.Core.ScreenText;

namespace Chorus.Core.Tests;

public class ScreenRectTests
{
    [Theory]
    [InlineData(10, 20, 30, 40, 10, 20, 20, 20)]   // normal top-left -> bottom-right
    [InlineData(30, 40, 10, 20, 10, 20, 20, 20)]   // reversed
    [InlineData(30, 20, 10, 40, 10, 20, 20, 20)]   // x reversed, y normal
    [InlineData(10, 40, 30, 20, 10, 20, 20, 20)]   // x normal, y reversed
    public void NormalizeDrag_AnyDragDirection_GivesTopLeftAnchoredRect(
        int x1, int y1, int x2, int y2, int ex, int ey, int ew, int eh)
    {
        var r = ScreenRect.NormalizeDrag(x1, y1, x2, y2);
        Assert.Equal(new ScreenRect(ex, ey, ew, eh), r);
    }

    [Fact]
    public void NormalizeDrag_ZeroSizeDrag_StillYieldsRect()
    {
        var r = ScreenRect.NormalizeDrag(5, 5, 5, 5);
        Assert.Equal(new ScreenRect(5, 5, 0, 0), r);
    }

    [Fact]
    public void ClampTo_InsideBounds_IsIdentity()
    {
        var bounds = new ScreenRect(0, 0, 1920, 1080);
        var r = new ScreenRect(100, 100, 500, 300);
        Assert.Equal(r, r.ClampTo(bounds));
    }

    [Fact]
    public void ClampTo_OverflowingRect_IsPushedInside()
    {
        var bounds = new ScreenRect(0, 0, 1920, 1080);
        var r = new ScreenRect(1800, 1000, 500, 300).ClampTo(bounds);
        Assert.Equal(1920, r.Right);
        Assert.Equal(1080, r.Bottom);
    }

    [Fact]
    public void ClampTo_NegativeOriginBounds_Works()
    {
        // Virtual screen can start at negative coords on multi-monitor setups.
        var bounds = new ScreenRect(-1920, 0, 1920, 1080);
        var r = new ScreenRect(-2000, 10, 100, 100).ClampTo(bounds);
        Assert.Equal(-1920, r.X);
        Assert.Equal(100, r.Width);
    }

    [Fact]
    public void ScaleBy_Dpi150_RoundsUpToOnePixelSelections()
    {
        var r = new ScreenRect(10, 10, 1, 1).ScaleBy(1.5);
        Assert.Equal(new ScreenRect(15, 15, 2, 2), r);
    }

    [Fact]
    public void ScaleBy_Dpi100_IsIdentity()
    {
        var r = new ScreenRect(10, 20, 30, 40).ScaleBy(1.0);
        Assert.Equal(new ScreenRect(10, 20, 30, 40), r);
    }

    [Fact]
    public void FitWithin_SmallerThanMax_IsUnchanged()
    {
        Assert.Equal((800, 600), ScreenRect.FitWithin(800, 600, 2600));
    }

    [Fact]
    public void FitWithin_LargerThanMax_ScalesAspectPreserving()
    {
        var (w, h) = ScreenRect.FitWithin(4000, 2000, 2600);
        Assert.True(w <= 2600 && h <= 2600);
        // aspect ratio preserved within rounding tolerance
        Assert.InRange((double)w / h, 1.9, 2.1);
    }

    [Fact]
    public void FitWithin_NonPositive_ReturnsZero()
    {
        Assert.Equal((0, 0), ScreenRect.FitWithin(0, 100, 2600));
        Assert.Equal((0, 0), ScreenRect.FitWithin(-5, 100, 2600));
    }
}
