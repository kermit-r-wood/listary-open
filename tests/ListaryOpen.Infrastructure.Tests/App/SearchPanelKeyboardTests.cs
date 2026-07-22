using System.Windows.Input;
using ListaryOpen.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelKeyboardTests
{
    [Fact]
    public void QuickSwitchStaysExpandedWhenItsAttachedDialogRetainsForegroundFocus()
    {
        var dialogWindow = new IntPtr(123);

        Assert.True(QuickSwitchBarWindow.ShouldKeepDialogSearchExpandedAfterDeactivation(
            isAttached: true,
            isExpanded: true,
            foregroundWindow: dialogWindow,
            anchorWindow: dialogWindow));
        Assert.False(QuickSwitchBarWindow.ShouldKeepDialogSearchExpandedAfterDeactivation(
            isAttached: true,
            isExpanded: true,
            foregroundWindow: new IntPtr(456),
            anchorWindow: dialogWindow));
        Assert.False(QuickSwitchBarWindow.ShouldKeepDialogSearchExpandedAfterDeactivation(
            isAttached: true,
            isExpanded: false,
            foregroundWindow: dialogWindow,
            anchorWindow: dialogWindow));
    }

    [Fact]
    public void AttachedQuickSwitchStaysTopmostAfterFocusReturnsToDialog()
    {
        Assert.True(QuickSwitchBarWindow.ShouldKeepAttachedBarTopmostAfterDeactivation(isAttached: true));
        Assert.False(QuickSwitchBarWindow.ShouldKeepAttachedBarTopmostAfterDeactivation(isAttached: false));
    }

    [Theory]
    [InlineData(System.Windows.Input.Key.A, "A")]
    [InlineData(System.Windows.Input.Key.D7, "7")]
    [InlineData(System.Windows.Input.Key.F12, "F12")]
    [InlineData(System.Windows.Input.Key.Space, "Space")]
    public void SettingsHotkeyCaptureFormatsSupportedKeys(System.Windows.Input.Key key, string expected)
    {
        Assert.Equal(expected, MainWindow.FormatHotkeyKey(key));
    }

    [Theory]
    [InlineData(98, 100, true)]
    [InlineData(97.9, 100, false)]
    [InlineData(0, 0, false)]
    public void ResultPagingStartsOnlyNearTheBottom(
        double verticalOffset,
        double scrollableHeight,
        bool expected)
    {
        Assert.Equal(expected, SearchPanel.ShouldLoadMoreOnScroll(verticalOffset, scrollableHeight));
    }

    [Fact]
    public void QuickSwitchReusesPositionOnlyWhenAnchorDpiAndLayoutAreUnchanged()
    {
        var bounds = new NativeRectangle(100, 100, 900, 700);

        Assert.True(QuickSwitchBarWindow.CanReusePosition(
            hasCachedPosition: true,
            repositionInvalidated: false,
            bounds,
            bounds,
            previousDpiScale: 1.25,
            currentDpiScale: 1.25));
        Assert.False(QuickSwitchBarWindow.CanReusePosition(
            hasCachedPosition: true,
            repositionInvalidated: true,
            bounds,
            bounds,
            previousDpiScale: 1.25,
            currentDpiScale: 1.25));
        Assert.False(QuickSwitchBarWindow.CanReusePosition(
            hasCachedPosition: true,
            repositionInvalidated: false,
            bounds,
            new NativeRectangle(101, 100, 901, 700),
            previousDpiScale: 1.25,
            currentDpiScale: 1.25));
    }

    [Fact]
    public void EscapeKeyRequestsSearchPanelHide()
    {
        Assert.Equal(SearchPanelPreviewKeyAction.HidePanel, SearchPanel.GetPreviewKeyAction(Key.Escape));
    }

    [Fact]
    public void ArrowKeysRequestResultSelectionMovement()
    {
        Assert.Equal(SearchPanelPreviewKeyAction.MoveSelectionUp, SearchPanel.GetPreviewKeyAction(Key.Up));
        Assert.Equal(SearchPanelPreviewKeyAction.MoveSelectionDown, SearchPanel.GetPreviewKeyAction(Key.Down));
    }

    [Fact]
    public void RightArrowRequestsSelectedResultMenu()
    {
        Assert.Equal(SearchPanelPreviewKeyAction.OpenSelectedMenu, SearchPanel.GetPreviewKeyAction(Key.Right));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.C)]
    [InlineData(Key.Space)]
    public void NonEscapeKeysDoNotRequestSearchPanelHide(Key key)
    {
        Assert.Equal(SearchPanelPreviewKeyAction.None, SearchPanel.GetPreviewKeyAction(key));
    }

    [Theory]
    [InlineData(Key.D1, true, 0)]
    [InlineData(Key.D9, true, 8)]
    [InlineData(Key.NumPad3, true, 2)]
    [InlineData(Key.D1, false, -1)]
    public void QuickSwitchNumberShortcutMapsToCandidate(Key key, bool controlPressed, int expected)
    {
        Assert.Equal(expected, SearchPanel.GetQuickSwitchShortcutIndex(key, controlPressed));
    }

    [Theory]
    [InlineData(100, 800, 500, 0, 1920, 250)]
    [InlineData(-100, 400, 500, 0, 1920, 0)]
    [InlineData(1800, 400, 500, 0, 1920, 1420)]
    public void AttachedPanelIsCenteredAndClampedToWorkArea(
        double anchorLeft,
        double anchorWidth,
        double panelWidth,
        double workAreaLeft,
        double workAreaRight,
        double expected)
    {
        Assert.Equal(
            expected,
            SearchPanel.CalculateCenteredLeft(anchorLeft, anchorWidth, panelWidth, workAreaLeft, workAreaRight));
    }

    [Theory]
    [InlineData(1400, 800, 560, 360, 0, 0, 1920, 1080, 828, 428)]
    [InlineData(500, 400, 560, 360, 0, 0, 1920, 1080, 0, 28)]
    [InlineData(2200, 1100, 560, 360, 0, 0, 1920, 1080, 1360, 720)]
    public void ExplorerSearchPanelIsPlacedAtBottomRightAndClampedToWorkArea(
        double anchorRight,
        double anchorBottom,
        double panelWidth,
        double panelHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom,
        double expectedLeft,
        double expectedTop)
    {
        var position = SearchPanel.CalculateBottomRightPosition(
            anchorRight,
            anchorBottom,
            panelWidth,
            panelHeight,
            workAreaLeft,
            workAreaTop,
            workAreaRight,
            workAreaBottom);

        Assert.Equal(expectedLeft, position.X);
        Assert.Equal(expectedTop, position.Y);
    }

    [Theory]
    [InlineData(760, 460, 0, 0, 1920, 1080, 580, 310)]
    [InlineData(760, 460, -1920, 0, 0, 1080, -1340, 310)]
    [InlineData(2200, 1200, 0, 0, 1920, 1080, 0, 0)]
    public void RegularSearchPanelIsCenteredAndClampedToWorkArea(
        double panelWidth,
        double panelHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom,
        double expectedLeft,
        double expectedTop)
    {
        var position = SearchPanel.CalculateCenteredPosition(
            panelWidth,
            panelHeight,
            workAreaLeft,
            workAreaTop,
            workAreaRight,
            workAreaBottom);

        Assert.Equal(expectedLeft, position.X);
        Assert.Equal(expectedTop, position.Y);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(99, 100, false)]
    [InlineData(299, 249, true)]
    [InlineData(300, 249, false)]
    [InlineData(299, 250, false)]
    public void PointerHitTestUsesExclusiveRightAndBottomEdges(int x, int y, bool expected)
    {
        Assert.Equal(expected, SearchPanel.IsPointInsideBounds(x, y, 100, 100, 300, 250));
    }
}
