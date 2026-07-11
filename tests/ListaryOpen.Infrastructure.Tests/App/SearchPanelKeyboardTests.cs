using System.Windows.Input;
using ListaryOpen.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelKeyboardTests
{
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

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.C)]
    [InlineData(Key.Space)]
    public void NonEscapeKeysDoNotRequestSearchPanelHide(Key key)
    {
        Assert.Equal(SearchPanelPreviewKeyAction.None, SearchPanel.GetPreviewKeyAction(key));
    }
}
