using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppDialogHotkeyTests
{
    [Fact]
    public void ObserveQuickSwitchFolderCandidatesReturnsMultipleExplorerFoldersForegroundFirst()
    {
        var firstFolder = Directory.CreateTempSubdirectory("listary-open-first-");
        var foregroundFolder = Directory.CreateTempSubdirectory("listary-open-foreground-");
        var thirdFolder = Directory.CreateTempSubdirectory("listary-open-third-");

        try
        {
            var foregroundHandle = new IntPtr(2222);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), firstFolder.FullName),
                    new ExplorerShellWindow(foregroundHandle, foregroundFolder.FullName),
                    new ExplorerShellWindow(new IntPtr(3333), thirdFolder.FullName)
                }));

            var candidates = ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(tracker);

            Assert.Equal(
                new[] { foregroundFolder.FullName, firstFolder.FullName, thirdFolder.FullName },
                candidates.Select(candidate => candidate.FolderPath));
            Assert.True(candidates[0].IsForeground);
            Assert.Equal(foregroundFolder.FullName, tracker.LastFolder);
        }
        finally
        {
            firstFolder.Delete(recursive: true);
            foregroundFolder.Delete(recursive: true);
            thirdFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveQuickSwitchFolderCandidatesWithoutExplorerTrackerReturnsEmpty()
    {
        var candidates = ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ObserveQuickSwitchFolderCandidatesFallsBackToRememberedExplorerFolder()
    {
        var rememberedFolder = Directory.CreateTempSubdirectory("listary-open-remembered-");

        try
        {
            var tracker = new ExplorerTracker(
                () => IntPtr.Zero,
                new RecordingExplorerShellWindowsProvider(Array.Empty<ExplorerShellWindow>()));
            tracker.ObserveFolderForTests(rememberedFolder.FullName);

            var candidate = Assert.Single(ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(tracker));

            Assert.Equal(rememberedFolder.FullName, candidate.FolderPath);
            Assert.Equal("Explorer", candidate.SourceName);
            Assert.Equal(IntPtr.Zero, candidate.WindowHandle);
            Assert.False(candidate.IsForeground);
        }
        finally
        {
            rememberedFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveQuickSwitchFolderCandidatesKeepsCompositeFallbackCandidate()
    {
        var rememberedFolder = Directory.CreateTempSubdirectory("listary-open-composite-remembered-");

        try
        {
            var composite = new CompositeQuickSwitchWindowProvider(
                new IQuickSwitchWindowProvider[]
                {
                    new EmptyQuickSwitchWindowProvider()
                },
                new[]
                {
                    new QuickSwitchFolderCandidate(rememberedFolder.FullName, "Explorer", IntPtr.Zero, false)
                });

            var candidate = Assert.Single(ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(composite));

            Assert.Equal(rememberedFolder.FullName, candidate.FolderPath);
        }
        finally
        {
            rememberedFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateDefaultQuickSwitchWindowProviderIncludesThirdPartyFileManagers()
    {
        var composite = ListaryOpen.App.App.CreateDefaultQuickSwitchWindowProvider(new ExplorerTracker());
        var providers = composite
            .GetType()
            .GetField("_providers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composite) as IReadOnlyList<IQuickSwitchWindowProvider>;

        Assert.NotNull(providers);
        Assert.Equal(
            new[] { "ExplorerTracker", "DirectoryOpusQuickSwitchProvider", "TotalCommanderQuickSwitchProvider" },
            providers!.Select(provider => provider.GetType().Name));
    }

    [Fact]
    public void ObserveAndGetExistingTrackedFolderRefreshesExplorerTrackerBeforeReadingLastFolder()
    {
        var staleFolder = Directory.CreateTempSubdirectory("listary-open-stale-");
        var foregroundFolder = Directory.CreateTempSubdirectory("listary-open-foreground-");

        try
        {
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1234), foregroundFolder.FullName)
                }));
            var stalePath = staleFolder.FullName + Path.DirectorySeparatorChar;
            tracker.ObserveFolderForTests(stalePath);

            var trackedFolder = ListaryOpen.App.App.ObserveAndGetExistingTrackedFolder(tracker);

            Assert.Equal(foregroundFolder.FullName, trackedFolder);
        }
        finally
        {
            staleFolder.Delete(recursive: true);
            foregroundFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TryJumpToFirstQuickSwitchFolderAsyncJumpsFirstCandidateDirectly()
    {
        var firstFolder = Directory.CreateTempSubdirectory("listary-open-first-");
        var secondFolder = Directory.CreateTempSubdirectory("listary-open-second-");
        var jumpedFolders = new List<string>();

        try
        {
            var result = await ListaryOpen.App.App.TryJumpToFirstQuickSwitchFolderAsync(
                new[]
                {
                    new QuickSwitchFolderCandidate(firstFolder.FullName, "Explorer", new IntPtr(1), true),
                    new QuickSwitchFolderCandidate(secondFolder.FullName, "Explorer", new IntPtr(2), false)
                },
                (folderPath, _) =>
                {
                    jumpedFolders.Add(folderPath);
                    return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed."));
                },
                CancellationToken.None);

            Assert.True(result);
            Assert.Equal(new[] { firstFolder.FullName }, jumpedFolders);
        }
        finally
        {
            firstFolder.Delete(recursive: true);
            secondFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TryJumpToFirstQuickSwitchFolderAsyncSkipsMissingCandidates()
    {
        var missingFolder = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());
        var existingFolder = Directory.CreateTempSubdirectory("listary-open-existing-jump-");
        var jumpedFolders = new List<string>();

        try
        {
            var result = await ListaryOpen.App.App.TryJumpToFirstQuickSwitchFolderAsync(
                new[]
                {
                    new QuickSwitchFolderCandidate(missingFolder, "Explorer", new IntPtr(1), true),
                    new QuickSwitchFolderCandidate(existingFolder.FullName, "Directory Opus", new IntPtr(2), false)
                },
                (folderPath, _) =>
                {
                    jumpedFolders.Add(folderPath);
                    return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed."));
                },
                CancellationToken.None);

            Assert.True(result);
            Assert.Equal(new[] { existingFolder.FullName }, jumpedFolders);
        }
        finally
        {
            existingFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TryJumpToFirstQuickSwitchFolderAsyncReportsSuccessfulDialogJumpResult()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-reported-jump-");
        var reportedResults = new List<DialogJumpResult>();
        var fallbackMessage = "Dialog folder changed via fallback automation after hook Failed: Hook could not jump.";

        try
        {
            var result = await ListaryOpen.App.App.TryJumpToFirstQuickSwitchFolderAsync(
                new[] { new QuickSwitchFolderCandidate(folder.FullName, "Explorer", IntPtr.Zero, true) },
                (_, _) => Task.FromResult(new DialogJumpResult(DialogJumpStatus.Success, fallbackMessage)),
                reportedResults.Add,
                CancellationToken.None);

            Assert.True(result);
            var reported = Assert.Single(reportedResults);
            Assert.Equal(DialogJumpStatus.Success, reported.Status);
            Assert.Equal(fallbackMessage, reported.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TryJumpToFirstQuickSwitchFolderAsyncFallsBackWhenNoCandidateExists()
    {
        var jumped = false;

        var result = await ListaryOpen.App.App.TryJumpToFirstQuickSwitchFolderAsync(
            Array.Empty<QuickSwitchFolderCandidate>(),
            (_, _) =>
            {
                jumped = true;
                return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed."));
            },
            CancellationToken.None);

        Assert.False(result);
        Assert.False(jumped);
    }

    [Fact]
    public async Task TryJumpToFirstQuickSwitchFolderAsyncFallsBackWhenDialogJumpFails()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-failed-jump-");
        var reportedResults = new List<DialogJumpResult>();

        try
        {
            var result = await ListaryOpen.App.App.TryJumpToFirstQuickSwitchFolderAsync(
                new[] { new QuickSwitchFolderCandidate(folder.FullName, "Explorer", IntPtr.Zero, false) },
                (_, _) => Task.FromResult(new DialogJumpResult(DialogJumpStatus.UnsupportedDialog, "No standard dialog.")),
                reportedResults.Add,
                CancellationToken.None);

            Assert.False(result);
            var reported = Assert.Single(reportedResults);
            Assert.Equal(DialogJumpStatus.UnsupportedDialog, reported.Status);
            Assert.Equal("No standard dialog.", reported.Message);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReportDirectDialogJumpStatusKeepsOrdinarySuccessSilent()
    {
        var panelResults = new List<DialogJumpResult>();
        var trayMessages = new List<string>();

        ListaryOpen.App.App.ReportDirectDialogJumpStatus(
            new DialogJumpResult(DialogJumpStatus.Success, "Hook changed folder."),
            panelResults.Add,
            trayMessages.Add);

        Assert.Empty(panelResults);
        Assert.Empty(trayMessages);
    }

    [Fact]
    public void ReportDirectDialogJumpStatusShowsDegradedSuccessInTrayStatus()
    {
        var panelResults = new List<DialogJumpResult>();
        var trayMessages = new List<string>();
        var message = "Dialog folder changed via fallback automation after hook Timeout: Hook host timed out.";

        ListaryOpen.App.App.ReportDirectDialogJumpStatus(
            new DialogJumpResult(DialogJumpStatus.Success, message, isDegradedSuccess: true),
            panelResults.Add,
            trayMessages.Add);

        Assert.Empty(panelResults);
        Assert.Equal(new[] { message }, trayMessages);
    }

    [Fact]
    public async Task ActivateQuickSwitchFolderSearchWithDirectJumpStatusAsyncReportsFailureAfterActivationCompletes()
    {
        var events = new List<string>();
        var activationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new[]
        {
            new QuickSwitchFolderCandidate("C:\\Projects", "Explorer", IntPtr.Zero, true)
        };

        var activationTask = ListaryOpen.App.App.ActivateQuickSwitchFolderSearchWithDirectJumpStatusAsync(
            candidates,
            new DialogJumpResult(DialogJumpStatus.UnsupportedDialog, "No standard dialog."),
            async activatedCandidates =>
            {
                events.Add("activate:" + activatedCandidates.Count);
                await activationCompletion.Task;
                events.Add("activation-complete");
            },
            result => events.Add("report:" + result.Status + ":" + result.Message),
            message => events.Add("tray:" + message));

        await Task.Yield();

        Assert.Equal(new[] { "activate:1" }, events);
        Assert.False(activationTask.IsCompleted);

        activationCompletion.SetResult();
        await activationTask;

        Assert.Equal(
            new[]
            {
                "activate:1",
                "activation-complete",
                "report:UnsupportedDialog:No standard dialog."
            },
            events);
    }

    private sealed class RecordingExplorerShellWindowsProvider : IExplorerShellWindowsProvider
    {
        private readonly IReadOnlyList<ExplorerShellWindow> _windows;

        public RecordingExplorerShellWindowsProvider(IReadOnlyList<ExplorerShellWindow> windows)
        {
            _windows = windows;
        }

        public IEnumerable<ExplorerShellWindow> EnumerateWindows()
        {
            return _windows;
        }
    }

    private sealed class EmptyQuickSwitchWindowProvider : IQuickSwitchWindowProvider
    {
        public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }
    }
}
