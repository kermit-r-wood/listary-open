using ListaryOpen.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class VisibleWindowBoundsTests
{
    [Fact]
    public void SelectBestPrefersDwmVisibleBoundsOverInvisibleResizeBorder()
    {
        var windowRectangle = new NativeRectangle(460, 105, 1420, 645);
        var visibleRectangle = new NativeRectangle(467, 105, 1413, 638);

        var selected = VisibleWindowBounds.SelectBest(windowRectangle, 0, visibleRectangle);

        Assert.Equal(467, selected.Left);
        Assert.Equal(105, selected.Top);
        Assert.Equal(1413, selected.Right);
        Assert.Equal(638, selected.Bottom);
    }

    [Fact]
    public void SelectBestFallsBackWhenDwmBoundsAreUnavailableOrEmpty()
    {
        var windowRectangle = new NativeRectangle(10, 20, 300, 400);

        var unavailable = VisibleWindowBounds.SelectBest(windowRectangle, -1, default);
        var empty = VisibleWindowBounds.SelectBest(windowRectangle, 0, new NativeRectangle(10, 20, 10, 20));

        Assert.Equal(windowRectangle.Bottom, unavailable.Bottom);
        Assert.Equal(windowRectangle.Bottom, empty.Bottom);
    }
}
