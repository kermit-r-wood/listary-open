using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerFolderViewFocusTests
{
    [Fact]
    public void PrefersShellDefViewContentOverAddressAndSearchEdits()
    {
        var addressEdit = new IntPtr(10);
        var searchBox = new IntPtr(11);
        var defView = new IntPtr(20);
        var listView = new IntPtr(21);
        var nodes = new[]
        {
            new ExplorerFolderViewFocus.WindowClassNode(addressEdit, "Edit", new IntPtr(1)),
            new ExplorerFolderViewFocus.WindowClassNode(searchBox, "SearchBoxControl", new IntPtr(1)),
            new ExplorerFolderViewFocus.WindowClassNode(defView, "SHELLDLL_DefView", new IntPtr(2)),
            new ExplorerFolderViewFocus.WindowClassNode(listView, "DirectUIHWND", defView),
        };

        Assert.Equal(listView, ExplorerFolderViewFocus.ChooseFocusTarget(nodes));
    }

    [Fact]
    public void NeverChoosesTextEntryControlsEvenWhenTheyAreTheOnlyCandidates()
    {
        var nodes = new[]
        {
            new ExplorerFolderViewFocus.WindowClassNode(new IntPtr(10), "Edit", new IntPtr(1)),
            new ExplorerFolderViewFocus.WindowClassNode(new IntPtr(11), "RichEditD2DPT", new IntPtr(1)),
            new ExplorerFolderViewFocus.WindowClassNode(new IntPtr(12), "SearchBoxControl", new IntPtr(1)),
        };

        Assert.Equal(IntPtr.Zero, ExplorerFolderViewFocus.ChooseFocusTarget(nodes));
    }

    [Theory]
    [InlineData("DirectUIHWND", "SHELLDLL_DefView", 100)]
    [InlineData("SysListView32", "SHELLDLL_DefView", 100)]
    [InlineData("UIItemsView", "CtrlNotifySink", 80)]
    [InlineData("SHELLDLL_DefView", "CtrlNotifySink", 60)]
    [InlineData("Edit", "Address Band Root", 0)]
    [InlineData("SearchBoxControl", "ToolbarWindow32", 0)]
    public void ScoresFolderViewCandidates(string className, string parentClassName, int expectedMinimum)
    {
        var score = ExplorerFolderViewFocus.ScoreFolderViewCandidate(className, parentClassName);
        if (expectedMinimum == 0)
        {
            Assert.Equal(0, score);
        }
        else
        {
            Assert.True(score >= expectedMinimum, $"Expected score >= {expectedMinimum} but was {score}");
        }
    }
}
