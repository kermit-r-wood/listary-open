using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Infrastructure.Windows;
using System.Windows.Input;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class TaskManagerSearchViewModelTests
{
    [Fact]
    public void TaskManagerResultsBindResolvedExecutableIcons()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "TaskManagerSearchWindow.xaml"));

        Assert.Contains("local:FileIcon.Path=\"{Binding IconPath}\"", xaml);
        Assert.Contains("local:FileIcon.IsDirectory=\"False\"", xaml);
    }

    [Fact]
    public void TaskManagerOverlayExposesStableBlackBoxAutomationIds()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "TaskManagerSearchWindow.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"TaskManagerSearchQuery\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"TaskManagerSearchResults\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"{Binding Id}\"", xaml);
        Assert.Contains(
            "<Setter Property=\"AutomationProperties.AutomationId\" Value=\"{Binding Id}\" />",
            xaml);
    }

    [Fact]
    public void FiltersAndRanksVisibleTaskManagerRows()
    {
        var viewModel = new TaskManagerSearchViewModel(TimeSpan.Zero);
        viewModel.SetItems(
        [
            new TaskManagerItem("1", "Windows Explorer", "explorer.exe"),
            new TaskManagerItem("2", "Visual Studio Code", "code.exe"),
            new TaskManagerItem("3", "Service Host", "svchost.exe")
        ]);

        viewModel.QueryText = "vis";

        Assert.Equal("Visual Studio Code", viewModel.Results[0].Name);
        Assert.Same(viewModel.Results[0], viewModel.SelectedItem);
    }

    [Fact]
    public void SelectionMovesToTheNextOverlayResult()
    {
        var viewModel = new TaskManagerSearchViewModel(TimeSpan.Zero);
        viewModel.SetItems(
        [
            new TaskManagerItem("1", "Alpha", ""),
            new TaskManagerItem("2", "Alpine", "")
        ]);
        viewModel.QueryText = "al";

        var first = viewModel.SelectedItem;
        viewModel.MoveSelection(1);

        Assert.NotNull(first);
        Assert.NotSame(first, viewModel.SelectedItem);
    }

    [Theory]
    [InlineData(Key.Up, 2)]
    [InlineData(Key.Down, 3)]
    [InlineData(Key.Enter, 4)]
    [InlineData(Key.Escape, 1)]
    public void OverlayCapturesKeyboardSelectionAndConfirmation(
        Key key,
        int expected)
    {
        Assert.Equal(expected, (int)TaskManagerSearchWindow.GetPreviewKeyAction(key));
    }

    [Theory]
    [InlineData(true, false, 0, 42, 100, false)]
    [InlineData(true, false, 42, 42, 100, false)]
    [InlineData(true, false, 100, 42, 100, false)]
    [InlineData(true, false, 200, 42, 100, true)]
    [InlineData(true, true, 200, 42, 100, false)]
    [InlineData(false, false, 200, 42, 100, false)]
    public void OverlayStaysOpenWhenTaskManagerTemporarilyTakesFocus(
        bool isVisible,
        bool isActive,
        int foregroundWindow,
        int taskManagerWindow,
        int overlayWindow,
        bool expected)
    {
        Assert.Equal(
            expected,
            TaskManagerSearchWindow.ShouldDismissAfterDeactivation(
                isVisible,
                isActive,
                new IntPtr(foregroundWindow),
                new IntPtr(taskManagerWindow),
                new IntPtr(overlayWindow)));
    }

    [Fact]
    public void EmptyQueryDoesNotPublishTaskRows()
    {
        var viewModel = new TaskManagerSearchViewModel(TimeSpan.Zero);
        viewModel.SetItems([new TaskManagerItem("1", "Explorer", "")]);

        Assert.Empty(viewModel.Results);
        Assert.Null(viewModel.SelectedItem);
    }

    [Fact]
    public void NoMatchDoesNotDiscardTheTaskManagerSnapshotOrBlockALaterSelection()
    {
        var viewModel = new TaskManagerSearchViewModel(TimeSpan.Zero);
        viewModel.SetItems(
        [
            new TaskManagerItem("1", "Windows Explorer", "explorer.exe"),
            new TaskManagerItem("2", "Windows Terminal", "terminal.exe"),
            new TaskManagerItem("3", "Firefox", "firefox.exe"),
            new TaskManagerItem("4", "Settings", "SystemSettings.exe")
        ]);

        viewModel.QueryText = "program-that-does-not-exist";

        Assert.Empty(viewModel.Results);
        Assert.Null(viewModel.SelectedItem);

        viewModel.QueryText = "terminal";

        Assert.Equal("Windows Terminal", Assert.Single(viewModel.Results).Name);
        Assert.Same(viewModel.Results[0], viewModel.SelectedItem);
    }

    [Fact]
    public void CanonicalProcessSearchIgnoresUnrelatedWindowTitlesContainingListary()
    {
        var viewModel = new TaskManagerSearchViewModel(TimeSpan.Zero);
        viewModel.SetItems(
        [
            new TaskManagerItem(
                "listary",
                "Process: ListaryOpen",
                "ListaryOpen",
                SearchText: "ListaryOpen.App ListaryOpen",
                ProcessIdentity: "ListaryOpen.App"),
            new TaskManagerItem(
                "powershell-repo",
                "Process: ⠇ listary_open",
                "pwsh.exe",
                SearchText: "pwsh PowerShell",
                ProcessIdentity: "pwsh"),
            new TaskManagerItem(
                "powershell-grok",
                "Process: Optimize Listary Index Performance Match... - grok",
                "pwsh.exe",
                SearchText: "pwsh PowerShell",
                ProcessIdentity: "pwsh")
        ]);

        viewModel.QueryText = "listary";

        Assert.Equal("Process: ListaryOpen", Assert.Single(viewModel.Results).Name);
    }

    [Fact]
    public void CanonicalProcessSearchDoesNotFuzzyMatchAcrossMetadataAliases()
    {
        var viewModel = new TaskManagerSearchViewModel(TimeSpan.Zero);
        viewModel.SetItems(
        [
            new TaskManagerItem(
                "listary",
                "Process: ListaryOpen",
                "ListaryOpen",
                SearchText: "ListaryOpen.App\nListaryOpen",
                ProcessIdentity: "ListaryOpen.App"),
            new TaskManagerItem(
                "application-frame-host",
                "Process: Application Frame Host",
                "ApplicationFrameHost.exe",
                SearchText: "ApplicationFrameHost\nApplication Frame Host\nMicrosoft Windows Operating System",
                ProcessIdentity: "ApplicationFrameHost"),
            new TaskManagerItem(
                "security-health-systray",
                "Process: Windows Security notification icon",
                "SecurityHealthSystray.exe",
                SearchText: "SecurityHealthSystray\nWindows Security notification icon\nMicrosoft Windows Operating System",
                ProcessIdentity: "SecurityHealthSystray"),
            new TaskManagerItem(
                "wlanext",
                "Process: Windows Wireless LAN 802.11 Extensibility Framework",
                "wlanext.exe",
                SearchText: "wlanext\nWindows Wireless LAN 802.11 Extensibility Framework\nMicrosoft Windows Operating System",
                ProcessIdentity: "wlanext")
        ]);

        viewModel.QueryText = "listary";

        Assert.Equal("Process: ListaryOpen", Assert.Single(viewModel.Results).Name);
    }

    [Theory]
    [InlineData("ListaryOpen.HookHost")]
    [InlineData("ListaryOpen.PreviewHost.exe")]
    [InlineData("ListaryOpen.Indexer.Elevated")]
    public void InternalListaryHelpersAreExcludedFromTaskManagerResults(string processIdentity)
    {
        Assert.True(UiAutomationTaskManagerProvider.IsInternalListaryProcess(processIdentity));
        Assert.False(UiAutomationTaskManagerProvider.IsInternalListaryProcess("ListaryOpen.App"));
    }

    [Fact]
    public async Task QueryRefreshIsDebouncedAndKeepsPreviousFrameUntilReady()
    {
        using var viewModel = new TaskManagerSearchViewModel(TimeSpan.FromMilliseconds(60));
        viewModel.SetItems(
        [
            new TaskManagerItem("1", "Firefox", "firefox.exe"),
            new TaskManagerItem("2", "Explorer", "explorer.exe")
        ]);
        viewModel.QueryText = "fire";

        Assert.Empty(viewModel.Results);
        await viewModel.WaitForPendingRefreshAsync();
        Assert.Equal("Firefox", Assert.Single(viewModel.Results).Name);

        viewModel.QueryText = "expl";
        Assert.Equal("Firefox", Assert.Single(viewModel.Results).Name);
        await viewModel.WaitForPendingRefreshAsync();
        Assert.Equal("Explorer", Assert.Single(viewModel.Results).Name);
    }

    [Fact]
    public void ModernTaskManagerRowUsesDescendantTextWhenAccessibleNameIsEmpty()
    {
        var item = UiAutomationTaskManagerProvider.CreateProjectedItem(
            "row-1",
            string.Empty,
            ["Windows Explorer", "1.2%", "84.3 MB"]);

        Assert.NotNull(item);
        Assert.Equal("Windows Explorer", item!.Name);
        Assert.Equal("1.2% · 84.3 MB", item.Details);
    }

    [Fact]
    public void TaskManagerRowKeepsSearchableRowNameAsFallbackDetails()
    {
        var item = UiAutomationTaskManagerProvider.CreateProjectedItem(
            "row-2",
            "Visual Studio Code (8)",
            ["Visual Studio Code", "code.exe"]);

        Assert.NotNull(item);
        Assert.Equal("Visual Studio Code (8)", item!.Name);
        Assert.Contains("Visual Studio Code", item.Details);
        Assert.Contains("code.exe", item.Details);
    }

    [Fact]
    public void DuplicateChildProcessesCollapseToOneVisibleResult()
    {
        var items = UiAutomationTaskManagerProvider.DeduplicateItems(
        [
            new TaskManagerItem("1", "Process: Firefox", "", ""),
            new TaskManagerItem("2", "Process: Firefox", "", "C:\\Apps\\firefox.exe"),
            new TaskManagerItem("3", "Process: Firefox (12)", "", "")
        ]);

        var item = Assert.Single(items);
        Assert.Equal("2", item.Id);
        Assert.Equal("C:\\Apps\\firefox.exe", item.IconPath);
    }

    [Fact]
    public void ProcessDisplayNameResolvesExecutableIconPath()
    {
        var iconPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["firefox"] = "C:\\Program Files\\Mozilla Firefox\\firefox.exe"
        };

        var path = UiAutomationTaskManagerProvider.ResolveIconPath(
            "Process: Firefox (12)",
            ["Firefox", "0%", "84 MB"],
            iconPaths);

        Assert.Equal("C:\\Program Files\\Mozilla Firefox\\firefox.exe", path);
    }

    [Fact]
    public void LegacyListaryOpenWindowTitleResolvesApplicationIconPath()
    {
        var iconPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ListaryOpen.App"] = "C:\\Apps\\ListaryOpen.App.exe"
        };
        UiAutomationTaskManagerProvider.AddListaryOpenIconAliases(iconPaths);

        var path = UiAutomationTaskManagerProvider.ResolveIconPath(
            "Process: ListaryOpen Options",
            ["ListaryOpen Options", "0%", "155 MB"],
            iconPaths);

        Assert.Equal("C:\\Apps\\ListaryOpen.App.exe", path);
    }

    [Theory]
    [InlineData("Process: ListaryOpen.HookHost.exe (32 bit)")]
    [InlineData("Process: ListaryOpen.HookHost.exe (32 位)")]
    [InlineData("Process: ListaryOpen.HookHost.exe (4)")]
    public void DecoratedExecutableDisplayNameResolvesExecutableIconPath(string displayName)
    {
        var iconPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ListaryOpen.HookHost"] = "C:\\Apps\\hooks\\x86\\ListaryOpen.HookHost.exe"
        };

        var path = UiAutomationTaskManagerProvider.ResolveIconPath(
            displayName,
            Array.Empty<string>(),
            iconPaths);

        Assert.Equal("C:\\Apps\\hooks\\x86\\ListaryOpen.HookHost.exe", path);
    }

    [Theory]
    [InlineData("same-id", "anything", "Process: Other", "same-id", "Process: 1Password", 3)]
    [InlineData("new-id", "Process: 1Password", "", "old-id", "Process: 1Password", 2)]
    [InlineData("new-id", "Process: 1Password (4)", "", "old-id", "Process: 1Password", 1)]
    [InlineData("new-id", "", "Process: 1Password", "old-id", "Process: 1Password", 2)]
    [InlineData("new-id", "Process: Firefox", "", "old-id", "Process: 1Password", 0)]
    public void SelectionRematchesRowsWhenTaskManagerRuntimeIdsChange(
        string candidateId,
        string candidateName,
        string descendantName,
        string itemId,
        string itemName,
        int expected)
    {
        Assert.Equal(
            expected,
            UiAutomationTaskManagerProvider.GetCandidateMatchScore(
                candidateId,
                candidateName,
                [descendantName],
                new TaskManagerItem(itemId, itemName, string.Empty)));
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments)));
    }
}
