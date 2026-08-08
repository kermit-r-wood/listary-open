using ListaryOpen.App;
using ListaryOpen.App.Search;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelViewModelTests
{
    [Fact]
    public async Task ExactQuickLaunchRunsAfterInputStabilizes()
    {
        var executor = new RecordingQuickLaunchExecutor();
        var entry = new QuickLaunchEntry(Guid.NewGuid().ToString("N"), "note", "Notepad", "notepad.exe");
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            _ => null,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            quickLaunchEntries: () => new[] { entry },
            quickLaunchExecutor: executor);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.QuickLaunchExecuted += (_, _) => executed.TrySetResult();

        viewModel.QueryText = "NoTe";
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(entry, Assert.Single(executor.Executed));
    }

    [Fact]
    public async Task QuickLaunchKeywordDoesNotRunWhileUserContinuesTyping()
    {
        var executor = new RecordingQuickLaunchExecutor();
        var entry = new QuickLaunchEntry(Guid.NewGuid().ToString("N"), "op", "Open tool", "tool.exe");
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            _ => null,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            quickLaunchEntries: () => new[] { entry },
            quickLaunchExecutor: executor);

        viewModel.QueryText = "op";
        await Task.Delay(50);
        viewModel.QueryText = "open";
        await Task.Delay(400);

        Assert.Empty(executor.Executed);
    }

    [Fact]
    public async Task SearchLoadsAdditionalResultPagesInFiftyItemSteps()
    {
        var results = Enumerable.Range(0, 120)
            .Select(index => CreateResult($"C:\\Results\\Report-{index:D3}.txt", isDirectory: false))
            .ToArray();
        var searchIndex = new RecordingSearchIndex(results);
        var viewModel = new SearchPanelViewModel(searchIndex)
        {
            QueryText = "report"
        };

        await viewModel.ActivateFilesAndFoldersSearchAsync();

        Assert.Equal(50, viewModel.Results.Count);
        Assert.True(viewModel.CanLoadMore);
        Assert.Equal(50, searchIndex.ObservedQueries.Last().Limit);

        await viewModel.LoadMoreAsync();

        Assert.Equal(100, viewModel.Results.Count);
        Assert.True(viewModel.CanLoadMore);
        Assert.Equal(100, searchIndex.ObservedQueries.Last().Limit);

        await viewModel.LoadMoreAsync();

        Assert.Equal(120, viewModel.Results.Count);
        Assert.False(viewModel.CanLoadMore);
        Assert.Equal(150, searchIndex.ObservedQueries.Last().Limit);
    }

    [Fact]
    public async Task ExplorerSearchPrioritizesCurrentFolderAndSelectsFirstResult()
    {
        var currentFolder = Directory.CreateTempSubdirectory("listary-open-type-search-");
        var childFolder = Directory.CreateDirectory(Path.Combine(currentFolder.FullName, "Child"));
        try
        {
            var global = CreateResult("C:\\Elsewhere\\Report.txt", isDirectory: false);
            var descendant = CreateResult(Path.Combine(childFolder.FullName, "Report-child.txt"), isDirectory: false);
            var direct = CreateResult(Path.Combine(currentFolder.FullName, "Report-current.txt"), isDirectory: false);
            var index = new RecordingSearchIndex(new[] { global, descendant, direct });
            var viewModel = new SearchPanelViewModel(
                index,
                _ => currentFolder.FullName,
                _ => true,
                _ => DateTimeOffset.UtcNow,
                new RecordingActivationService(),
                DialogJumpNotConfiguredAsync,
                TimeSpan.Zero,
                (_, _) => new[] { direct.Record });

            await viewModel.ActivateExplorerSearchAsync("report", currentFolder.FullName);

            Assert.Equal("report", viewModel.QueryText);
            Assert.True(viewModel.IsExplorerTypeSearchMode);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(currentFolder.FullName),
                Assert.Single(index.ObservedQueries).PreferredRoot);
            Assert.Equal(
                new[] { direct.Record.FullPath, descendant.Record.FullPath, global.Record.FullPath },
                viewModel.Results.Select(result => result.Record.FullPath));
            Assert.Equal(direct.Record.FullPath, viewModel.SelectedResult?.Record.FullPath);
            Assert.Contains("current folder first", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            currentFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExplorerOverlayQueryDoesNotReplaceRegularSearchQuery()
    {
        var currentFolder = Directory.CreateTempSubdirectory("listary-open-query-isolation-");
        try
        {
            var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
            var viewModel = new SearchPanelViewModel(index)
            {
                QueryText = "regular query"
            };

            await viewModel.ActivateExplorerSearchAsync("overlay query", currentFolder.FullName);
            Assert.Equal("overlay query", viewModel.QueryText);

            await viewModel.ActivateFilesAndFoldersSearchAsync();

            Assert.False(viewModel.IsExplorerTypeSearchMode);
            Assert.Equal("regular query", viewModel.QueryText);
            Assert.Equal("regular query", index.ObservedQueries.Last().NormalizedText);
        }
        finally
        {
            currentFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExplorerOverlayFiltersCurrentFolderBeforeIndexResultsOnlyInOverlayMode()
    {
        var currentFolder = Directory.CreateTempSubdirectory("listary-open-current-page-");
        try
        {
            var currentFile = Path.Combine(currentFolder.FullName, "Needle-current.txt");
            File.WriteAllText(currentFile, "test");
            var currentDirectory = Directory.CreateDirectory(
                Path.Combine(currentFolder.FullName, "Needle-folder"));
            var global = CreateResult("C:\\Elsewhere\\Needle-global.txt", isDirectory: false);
            var index = new RecordingSearchIndex(new[] { global });
            var viewModel = new SearchPanelViewModel(index)
            {
                QueryText = "needle"
            };

            await viewModel.ActivateFilesAndFoldersSearchAsync();
            Assert.Equal(new[] { global.Record.FullPath }, viewModel.Results.Select(result => result.Record.FullPath));

            await viewModel.ActivateExplorerSearchAsync("needle", currentFolder.FullName);

            Assert.Equal(3, viewModel.Results.Count);
            Assert.All(
                viewModel.Results.Take(2),
                result => Assert.Equal(
                    Path.TrimEndingDirectorySeparator(currentFolder.FullName),
                    Path.TrimEndingDirectorySeparator(result.Record.ParentPath),
                    ignoreCase: true));
            Assert.Contains(viewModel.Results.Take(2), result => result.Record.FullPath == currentFile);
            Assert.Contains(viewModel.Results.Take(2), result => result.Record.FullPath == currentDirectory.FullName);
            Assert.Equal(global.Record.FullPath, viewModel.Results[2].Record.FullPath);
        }
        finally
        {
            currentFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExplorerOverlayPublishesCurrentFolderWhileIndexSearchIsStillPending()
    {
        var currentFolder = Directory.CreateTempSubdirectory("listary-open-current-preview-");
        try
        {
            var currentFile = Path.Combine(currentFolder.FullName, "Needle-preview.txt");
            File.WriteAllText(currentFile, "test");
            var index = new BlockingSearchIndex();
            var viewModel = new SearchPanelViewModel(index);

            var activationTask = viewModel.ActivateExplorerSearchAsync("needle", currentFolder.FullName);
            await index.WaitForSearchCountAsync(1);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (viewModel.Results.All(result => result.Record.FullPath != currentFile))
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.False(activationTask.IsCompleted);
            Assert.Equal(currentFile, viewModel.SelectedResult?.Record.FullPath);

            index.Complete(Array.Empty<SearchResult>());
            await activationTask;
        }
        finally
        {
            currentFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExplorerOverlayClearsStaleCurrentFolderResultsWhileIndexSearchIsPending()
    {
        var currentFolder = Path.GetFullPath("C:\\Current");
        var currentRecord = FileRecord.Create(
            Path.Combine(currentFolder, "Needle.txt"),
            isDirectory: false,
            sizeBytes: 0,
            DateTimeOffset.UtcNow);
        var index = new BlockingSearchIndex();
        var viewModel = new SearchPanelViewModel(
            index,
            _ => currentFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            (_, _) => new[] { currentRecord });

        var activationTask = viewModel.ActivateExplorerSearchAsync("needle", currentFolder);
        await index.WaitForSearchCountAsync(1);
        await WaitUntilAsync(() => viewModel.Results.Count == 1);

        viewModel.QueryText = "missing";
        await index.WaitForSearchCountAsync(2);
        await WaitUntilAsync(() => viewModel.Results.Count == 0);

        Assert.Null(viewModel.SelectedResult);
        Assert.Contains("No current-folder results", viewModel.StatusText, StringComparison.Ordinal);

        index.Complete(Array.Empty<SearchResult>());
        await activationTask;
    }

    [Fact]
    public async Task ExplorerOverlayAppliesStructuredFiltersToCurrentFolderResults()
    {
        var currentFolder = Path.GetFullPath("C:\\Current");
        var expected = FileRecord.Create(
            Path.Combine(currentFolder, "Report.pdf"),
            isDirectory: false,
            sizeBytes: 0,
            DateTimeOffset.UtcNow);
        var records = new[]
        {
            expected,
            FileRecord.Create(Path.Combine(currentFolder, "Report.txt"), false, 0, DateTimeOffset.UtcNow),
            FileRecord.Create(Path.Combine(currentFolder, "Draft.pdf"), false, 0, DateTimeOffset.UtcNow),
            FileRecord.Create("C:\\Elsewhere\\Report.pdf", false, 0, DateTimeOffset.UtcNow)
        };
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            _ => currentFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            (_, _) => records);

        await viewModel.ActivateExplorerSearchAsync("ext:pdf path:current !draft", currentFolder);

        Assert.Equal(expected.FullPath, Assert.Single(viewModel.Results).Record.FullPath);
    }

    [Fact]
    public async Task ExplorerOverlayReusesCurrentFolderSnapshotAcrossQueryChanges()
    {
        var currentFolder = Path.GetFullPath("C:\\Current");
        var currentRecord = FileRecord.Create(
            Path.Combine(currentFolder, "Needle.txt"),
            isDirectory: false,
            sizeBytes: 0,
            DateTimeOffset.UtcNow);
        var enumerationCount = 0;
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            _ => currentFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            (_, _) =>
            {
                Interlocked.Increment(ref enumerationCount);
                return new[] { currentRecord };
            });

        await viewModel.ActivateExplorerSearchAsync("n", currentFolder);
        viewModel.QueryText = "ne";
        await index.WaitForSearchCountAsync(2);
        await WaitUntilAsync(() => viewModel.QueryText == "ne" && viewModel.Results.Count == 1);

        Assert.Equal(1, Volatile.Read(ref enumerationCount));
    }

    [Fact]
    public async Task ExplorerOverlayRefreshesExpiredCurrentFolderSnapshot()
    {
        var currentFolder = Path.GetFullPath("C:\\Current");
        var enumerationCount = 0;
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            _ => currentFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            (_, _) =>
            {
                Interlocked.Increment(ref enumerationCount);
                return Array.Empty<FileRecord>();
            },
            currentFolderSnapshotLifetime: TimeSpan.FromMilliseconds(20));

        await viewModel.ActivateExplorerSearchAsync("a", currentFolder);
        await Task.Delay(50);
        viewModel.QueryText = "ab";
        await index.WaitForSearchCountAsync(2);
        await WaitUntilAsync(() => Volatile.Read(ref enumerationCount) == 2);

        Assert.Equal(2, Volatile.Read(ref enumerationCount));
    }

    [Fact]
    public async Task DeactivateExplorerSearchCancelsSnapshotAndRestoresRegularQuery()
    {
        var currentFolder = Path.GetFullPath("C:\\Current");
        var enumerationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            _ => currentFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero,
            (_, cancellationToken) =>
            {
                enumerationStarted.TrySetResult();
                try
                {
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Array.Empty<FileRecord>();
                }
                catch (OperationCanceledException)
                {
                    enumerationCancelled.TrySetResult();
                    throw;
                }
            });

        viewModel.QueryText = "regular";
        await index.WaitForSearchCountAsync(1);
        var activationTask = viewModel.ActivateExplorerSearchAsync("overlay", currentFolder);
        await enumerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.DeactivateExplorerSearch();

        await enumerationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await activationTask;
        Assert.False(viewModel.IsExplorerTypeSearchMode);
        Assert.Equal("regular", viewModel.QueryText);
        Assert.Empty(viewModel.Results);
        Assert.Null(viewModel.SelectedResult);
    }

    [Fact]
    public void CurrentFolderFilterChecksCancellationWhenAllEntriesAreExcluded()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeratedCount = 0;
        var query = new SearchQuery("ext:pdf invoice", SearchMode.FilesAndFolders);

        Assert.Throws<OperationCanceledException>(() => SearchPanelViewModel
            .FilterCurrentFolderRecords(
                query,
                CancelCurrentFolderFiltering(cancellation, () => enumeratedCount++),
                cancellation.Token)
            .ToArray());

        Assert.InRange(enumeratedCount, 8, 72);
    }

    [Fact]
    public void RootPriorityUsesPathBoundariesAndPreservesRankingWithinGroups()
    {
        var root = Path.GetFullPath("C:\\Projects");
        var firstGlobal = CreateResult("C:\\Projects-archive\\First.txt", isDirectory: false);
        var secondDirect = CreateResult("C:\\Projects\\Second.txt", isDirectory: false);
        var thirdDirect = CreateResult("C:\\Projects\\Third.txt", isDirectory: false);

        var prioritized = SearchPanelViewModel.PrioritizeResultsForRoot(
            new[] { firstGlobal, secondDirect, thirdDirect },
            root);

        Assert.Equal(
            new[] { secondDirect, thirdDirect, firstGlobal },
            prioritized);
    }

    [Fact]
    public async Task ActivateSelectedAsyncOpensSelectedFileInFilesAndFoldersModeAndSetsStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.ActivateSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
        Assert.Empty(activation.CopiedPaths);
        Assert.Equal(new[] { result.Record.FullPath }, index.RecordedUsagePaths);
        Assert.Contains(result.Record.FullPath, viewModel.StatusText);
    }

    [Fact]
    public async Task RevealSelectedAsyncRevealsSelectedPathAndSetsStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.RevealSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, activation.RevealedPaths);
        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.CopiedPaths);
        Assert.Empty(index.RecordedUsagePaths);
        Assert.Contains(result.Record.FullPath, viewModel.StatusText);
    }

    [Fact]
    public void CopySelectedPathCopiesSelectedPathAndSetsStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        viewModel.CopySelectedPath();

        Assert.Equal(new[] { result.Record.FullPath }, activation.CopiedPaths);
        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
        Assert.Empty(index.RecordedUsagePaths);
        Assert.Contains(result.Record.FullPath, viewModel.StatusText);
    }

    [Fact]
    public async Task ActivateSelectedAsyncDoesNotRecordUsageWhenOpenFails()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            new RecordingActivationService(new IOException("Open failed.")))
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.ActivateSelectedAsync();

        Assert.Empty(index.RecordedUsagePaths);
        Assert.Contains("Could not open", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsageWriteFailureDoesNotReplaceSuccessfulOpenStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var index = new RecordingSearchIndex(
            Array.Empty<SearchResult>(),
            usageException: new IOException("Usage failed."));
        var viewModel = new SearchPanelViewModel(index, new RecordingActivationService())
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.ActivateSelectedAsync();

        Assert.Contains("Opened", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Record.FullPath, viewModel.StatusText);
    }

    [Fact]
    public async Task ActivationCommandsWithoutSelectionSetStatusAndDoNotThrow()
    {
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation);

        await viewModel.ActivateSelectedAsync();
        await viewModel.RevealSelectedAsync();
        viewModel.CopySelectedPath();

        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
        Assert.Empty(activation.CopiedPaths);
        Assert.Contains("Select", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateSelectedAsyncInFolderSearchModeDoesNotOpenDirectory()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-dialog-");
        var result = CreateResult(folder.FullName, isDirectory: true);
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation)
        {
            SelectedResult = result
        };

        try
        {
            await viewModel.ActivateFolderSearchAsync(folder.FullName);
            viewModel.SelectedResult = result;

            await viewModel.ActivateSelectedAsync();

            Assert.Empty(activation.OpenedPaths);
            Assert.Contains("dialog", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActivateSelectedAsyncInFolderSearchModeJumpsDialogToSelectedDirectory()
    {
        var result = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
        var activation = new RecordingActivationService();
        var dialogActivation = new RecordingDialogFolderActivation(
            new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed."));
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation,
            dialogActivation.ActivateAsync)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.Results.Add(result);
        viewModel.SelectedResult = result;

        await viewModel.ActivateSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, dialogActivation.Paths);
        Assert.Empty(activation.OpenedPaths);
        Assert.Equal("Dialog folder changed.", viewModel.StatusText);
    }

    [Fact]
    public void ReportDialogJumpResultSurfacesNativeHookSuccessMessage()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var message = "Dialog folder changed through the captured native hook.";

        viewModel.ReportDialogJumpResult(new DialogJumpResult(DialogJumpStatus.Success, message));

        Assert.Equal(message, viewModel.StatusText);
    }

    [Theory]
    [InlineData(DialogJumpStatus.PermissionLimited, "Permission denied.")]
    [InlineData(DialogJumpStatus.UnsupportedDialog, "No standard file dialog is active.")]
    [InlineData(DialogJumpStatus.TargetGone, "The selected folder no longer exists.")]
    [InlineData(DialogJumpStatus.Failed, "Dialog folder could not be changed.")]
    public async Task ActivateSelectedAsyncInFolderSearchModeSurfacesDialogJumpFailure(
        DialogJumpStatus status,
        string message)
    {
        var result = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
        var dialogActivation = new RecordingDialogFolderActivation(new DialogJumpResult(status, message));
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            new RecordingActivationService(),
            dialogActivation.ActivateAsync);
        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.Results.Add(result);
        viewModel.SelectedResult = result;

        var activated = await viewModel.ActivateSelectedAsync();

        Assert.False(activated);
        Assert.Equal(new[] { result.Record.FullPath }, dialogActivation.Paths);
        Assert.Contains(status.ToString(), viewModel.StatusText);
        Assert.Contains(message, viewModel.StatusText);
    }

    [Fact]
    public async Task ActivateSelectedAsyncInFolderSearchModeSurfacesDialogJumpException()
    {
        var result = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
        var dialogActivation = new RecordingDialogFolderActivation(
            _ => throw new InvalidOperationException("Dialog automation failed."));
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            new RecordingActivationService(),
            dialogActivation.ActivateAsync);
        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.Results.Add(result);
        viewModel.SelectedResult = result;

        var activated = await viewModel.ActivateSelectedAsync();

        Assert.False(activated);
        Assert.Equal(new[] { result.Record.FullPath }, dialogActivation.Paths);
        Assert.Contains("Dialog jump failed", viewModel.StatusText);
        Assert.Contains("Dialog automation failed.", viewModel.StatusText);
    }

    [Fact]
    public async Task ActivateSelectedAsyncInFolderSearchModeIgnoresSelectedFile()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var dialogActivation = new RecordingDialogFolderActivation(
            new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed."));
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation,
            dialogActivation.ActivateAsync);
        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.Results.Add(result);
        viewModel.SelectedResult = result;

        await viewModel.ActivateSelectedAsync();

        Assert.Empty(dialogActivation.Paths);
        Assert.Empty(activation.OpenedPaths);
        Assert.Contains("folder", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateSelectedAsyncInFilesAndFoldersModeOpensDirectoryWithoutDialogJump()
    {
        var result = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
        var activation = new RecordingActivationService();
        var dialogActivation = new RecordingDialogFolderActivation(
            new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed."));
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation,
            dialogActivation.ActivateAsync)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.ActivateSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, activation.OpenedPaths);
        Assert.Empty(dialogActivation.Paths);
    }

    [Fact]
    public async Task ExplorerTypeSearchNavigatesDirectoryInCapturedWindowInsteadOfOpeningANewOne()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-navigation-");
        try
        {
            var result = CreateResult(folder.FullName, isDirectory: true);
            var activation = new RecordingActivationService();
            var index = new RecordingSearchIndex([result]);
            var navigatedPaths = new List<string>();
            var viewModel = new SearchPanelViewModel(index, activation);

            await viewModel.ActivateExplorerSearchAsync(folder.Name, folder.Parent!.FullName);
            viewModel.SetResultsForTesting([result]);
            viewModel.SelectedResult = result;

            var activated = await viewModel.ActivateSelectedAsync((path, _) =>
            {
                navigatedPaths.Add(path);
                return Task.FromResult(true);
            });

            Assert.True(activated);
            Assert.Equal([folder.FullName], navigatedPaths);
            Assert.Empty(activation.OpenedPaths);
            Assert.Equal([folder.FullName], index.RecordedUsagePaths);
            Assert.Contains("Navigated", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RefreshSelectsFirstResultWhenPreviousSelectionNoLongerExists()
    {
        var first = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var second = CreateResult("C:\\Docs\\Budget.xlsx", isDirectory: false);
        var replacement = CreateResult("C:\\Docs\\Receipt.xlsx", isDirectory: false);
        var index = new RecordingSearchIndex(new[] { first, second });
        var viewModel = new SearchPanelViewModel(index);

        viewModel.QueryText = "invoice";
        await index.WaitForSearchCountAsync(1);
        viewModel.SelectedResult = second;

        index.SetResults(new[] { replacement });
        viewModel.QueryText = "receipt";
        await index.WaitForSearchCountAsync(2);
        await WaitUntilAsync(() => viewModel.SelectedResult?.Record.FullPath == replacement.Record.FullPath);

        Assert.Equal(replacement.Record.FullPath, viewModel.SelectedResult?.Record.FullPath);
    }

    [Fact]
    public async Task RefreshPreservesSelectionUntilDelayedSearchPublishesReplacement()
    {
        var stale = CreateResult("C:\\Docs\\OldInvoice.xlsx", isDirectory: false);
        var index = new BlockingSearchIndex();
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(index, activation);
        viewModel.Results.Add(stale);
        viewModel.SelectedResult = stale;

        viewModel.QueryText = "new invoice";
        await index.WaitForSearchCountAsync(1);

        Assert.Equal(stale, Assert.Single(viewModel.Results));
        Assert.Equal(stale, viewModel.SelectedResult);

        index.Complete(Array.Empty<SearchResult>());
        await WaitUntilAsync(() => viewModel.StatusText == "0 results.");
        Assert.Empty(viewModel.Results);
        Assert.Null(viewModel.SelectedResult);
    }

    [Fact]
    public async Task QueryTextDelaysSearchWhileUserIsStillTyping()
    {
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.FromMilliseconds(150));

        viewModel.QueryText = "i";
        await Task.Delay(TimeSpan.FromMilliseconds(25));
        viewModel.QueryText = "in";
        await Task.Delay(TimeSpan.FromMilliseconds(25));
        viewModel.QueryText = "inv";

        await Task.Delay(TimeSpan.FromMilliseconds(75));
        Assert.Empty(index.ObservedQueries);

        await index.WaitForSearchCountAsync(1);
        var query = Assert.Single(index.ObservedQueries);
        Assert.Equal("inv", query.NormalizedText);
    }

    [Fact]
    public async Task QueryTextSearchesAfterEightyMillisecondDefaultDelay()
    {
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(index);

        viewModel.QueryText = "invoice";

        await Task.Delay(TimeSpan.FromMilliseconds(35));
        Assert.Empty(index.ObservedQueries);

        await index.WaitForSearchCountAsync(1);
        var query = Assert.Single(index.ObservedQueries);
        Assert.Equal("invoice", query.NormalizedText);
    }

    [Fact]
    public async Task QueryTextPreservesCurrentResultsWhileWaitingForIdleDelay()
    {
        var existing = CreateResult("C:\\Docs\\Existing.txt", isDirectory: false);
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.FromSeconds(1));
        viewModel.Results.Add(existing);
        viewModel.SelectedResult = existing;

        viewModel.QueryText = "replacement";
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(existing, Assert.Single(viewModel.Results));
        Assert.Equal(existing, viewModel.SelectedResult);
        Assert.Empty(index.ObservedQueries);
    }

    [Fact]
    public async Task QueryTextRunsSearchProviderAwayFromCallingThread()
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero);

        viewModel.QueryText = "invoice";
        await index.WaitForSearchCountAsync(1);

        Assert.DoesNotContain(callingThreadId, index.SearchThreadIds);
    }

    [Fact]
    public async Task QueryTextCancelsPreviousSearchWhenNewQueryStarts()
    {
        var firstResult = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var secondResult = CreateResult("C:\\Docs\\Receipt.xlsx", isDirectory: false);
        var index = new CancellableSearchIndex(
            new[]
            {
                firstResult
            },
            new[]
            {
                secondResult
            });
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero);

        viewModel.QueryText = "invoice";
        await index.WaitForSearchCountAsync(1);

        viewModel.QueryText = "receipt";
        await index.WaitForCancellationCountAsync(1);
        await index.WaitForSearchCountAsync(2);

        index.CompleteSearch(1);
        index.CompleteSearch(2);

        await WaitUntilAsync(() => viewModel.StatusText == "1 result.");
        Assert.Equal(secondResult.Record.FullPath, viewModel.SelectedResult?.Record.FullPath);
        Assert.Equal(new[] { "invoice", "receipt" }, index.ObservedQueries.Select(query => query.NormalizedText));
    }

    [Fact]
    public async Task SupersededEmptyQueryRefreshCannotOverwriteTheNextSearchResults()
    {
        var staleRecent = CreateResult("C:\\Old\\Recent", isDirectory: true);
        var expected = CreateResult("C:\\Current\\Other", isDirectory: true);
        var index = new BlockingRecentSearchIndex([expected]);
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero);

        var emptyRefresh = viewModel.ActivateQuickSwitchFolderSearchAsync(Array.Empty<QuickSwitchFolderCandidate>());
        await index.WaitForRecentAsync();

        viewModel.QueryText = "other";
        await index.WaitForSearchAsync();
        await WaitUntilAsync(() => viewModel.Results.Any(result => result.Record.FullPath == expected.Record.FullPath));

        index.CompleteRecent([staleRecent]);
        await emptyRefresh;

        var result = Assert.Single(viewModel.Results);
        Assert.Equal(expected.Record.FullPath, result.Record.FullPath);
        Assert.Equal("other", viewModel.QueryText);
    }

    [Fact]
    public async Task ActivationCommandsIgnoreSelectedResultThatIsNotInCurrentResults()
    {
        var current = CreateResult("C:\\Docs\\Current.xlsx", isDirectory: false);
        var stale = CreateResult("C:\\Docs\\OldInvoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation);
        viewModel.Results.Add(current);

        viewModel.SelectedResult = stale;
        await viewModel.ActivateSelectedAsync();

        viewModel.SelectedResult = stale;
        await viewModel.RevealSelectedAsync();

        viewModel.SelectedResult = stale;
        viewModel.CopySelectedPath();

        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
        Assert.Empty(activation.CopiedPaths);
        Assert.Contains("current", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ShouldCopySelectedResultPathSkipsQueryBoxAndTextInputFocus(
        bool focusIsQueryBox,
        bool focusIsTextInput,
        bool expected)
    {
        Assert.Equal(expected, SearchPanel.ShouldCopySelectedResultPath(focusIsQueryBox, focusIsTextInput));
    }

    [Fact]
    public async Task RunInteractionAsyncReportsUnexpectedExceptionsOnViewModelStatus()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));

        await SearchPanel.RunInteractionAsync(
            viewModel,
            _ => throw new InvalidOperationException("Injected failure."));

        Assert.Contains("Unexpected", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Injected failure.", viewModel.StatusText);
    }

    [Fact]
    public async Task RefreshWithBlankQueryReplacesPreviousResultCountStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var index = new RecordingSearchIndex(new[] { result });
        var viewModel = new SearchPanelViewModel(index);

        viewModel.QueryText = "invoice";
        await index.WaitForSearchCountAsync(1);
        await WaitUntilAsync(() => viewModel.StatusText == "1 result.");
        Assert.Equal("1 result.", viewModel.StatusText);

        viewModel.QueryText = string.Empty;

        Assert.Equal("Type to search.", viewModel.StatusText);
    }

    [Fact]
    public async Task RefreshWithBlankQueryInFolderModeRestoresFolderInstructionStatus()
    {
        var trackedFolder = Directory.CreateTempSubdirectory("listary-open-dialog-");
        var indexedFolder = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
        var index = new RecordingSearchIndex(new[] { indexedFolder });
        var viewModel = new SearchPanelViewModel(index);

        try
        {
            viewModel.QueryText = "invoice";
            await index.WaitForSearchCountAsync(1);

            await viewModel.ActivateFolderSearchAsync(trackedFolder.FullName);
            await index.WaitForSearchCountAsync(2);
            Assert.Contains("result", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

            viewModel.QueryText = string.Empty;

            Assert.Contains("dialog", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            trackedFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncShowsTrackedFolderForBlankQueryWithoutSearchingIndex()
    {
        var trackedFolder = Directory.CreateTempSubdirectory("listary-open-dialog-");
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(index);

        try
        {
            await viewModel.ActivateFolderSearchAsync(trackedFolder.FullName);

            var result = Assert.Single(viewModel.Results);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(trackedFolder.FullName)), result.Record.FullPath);
            Assert.True(result.Record.IsDirectory);
            Assert.Empty(index.ObservedQueries);
        }
        finally
        {
            trackedFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncPrependsTrackedFolderAndQueriesFoldersOnly()
    {
        var trackedFolder = Directory.CreateTempSubdirectory("listary-open-dialog-");
        var now = DateTimeOffset.UtcNow;
        var trackedRecord = FileRecord.Create(trackedFolder.FullName, true, 0, now);
        var indexedFolder = FileRecord.Create("C:\\Docs\\Invoices", true, 0, now);
        var indexedFile = FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, now);
        var index = new RecordingSearchIndex(new[]
        {
            new SearchResult(indexedFile, 150, "index"),
            new SearchResult(trackedRecord, 125, "index"),
            new SearchResult(indexedFolder, 100, "index")
        });
        var viewModel = new SearchPanelViewModel(index)
        {
            QueryText = "invoice"
        };

        try
        {
            await index.WaitForSearchCountAsync(1);
            index.ClearObservedQueries();

            await viewModel.ActivateFolderSearchAsync(trackedFolder.FullName);

            var query = Assert.Single(index.ObservedQueries);
            Assert.Equal(SearchMode.FoldersOnly, query.Mode);
            Assert.Equal("invoice", query.NormalizedText);
            Assert.Equal(
                new[] { trackedRecord.FullPath, indexedFolder.FullPath },
                viewModel.Results.Select(result => result.Record.FullPath));
        }
        finally
        {
            trackedFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncPinsQuickSwitchCandidatesAboveIndexResults()
    {
        var now = DateTimeOffset.UtcNow;
        var firstPinnedFolder = "C:\\Projects\\Beta";
        var secondPinnedFolder = "C:\\Projects\\Alpha";
        var duplicateIndexedFolder = FileRecord.Create(secondPinnedFolder, true, 0, now);
        var indexedFolder = FileRecord.Create("C:\\Projects\\Gamma", true, 0, now);
        var indexedFile = FileRecord.Create("C:\\Projects\\Report.txt", false, 10, now);
        var index = new RecordingSearchIndex(new[]
        {
            new SearchResult(duplicateIndexedFolder, 300, "index"),
            new SearchResult(indexedFile, 200, "index"),
            new SearchResult(indexedFolder, 100, "index")
        });
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => now)
        {
            QueryText = "project"
        };
        await index.WaitForSearchCountAsync(1);
        index.ClearObservedQueries();

        await viewModel.ActivateQuickSwitchFolderSearchAsync(new[]
        {
            CreateCandidate(firstPinnedFolder),
            CreateCandidate(secondPinnedFolder)
        });

        var query = Assert.Single(index.ObservedQueries);
        Assert.Equal(SearchMode.FoldersOnly, query.Mode);
        Assert.Equal("project", query.NormalizedText);
        Assert.Equal(
            new[] { firstPinnedFolder, secondPinnedFolder, indexedFolder.FullPath },
            viewModel.Results.Select(result => result.Record.FullPath));
        Assert.Equal(firstPinnedFolder, viewModel.SelectedResult?.Record.FullPath);
    }

    [Fact]
    public void FolderSearchQuickSwitchApisAvoidSameNameCandidateListOverloads()
    {
        Assert.DoesNotContain(
            typeof(SearchPanelViewModel).GetMethods(),
            method =>
                method.Name == nameof(SearchPanelViewModel.ActivateFolderSearchAsync) &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(IReadOnlyList<QuickSwitchFolderCandidate>));
        Assert.Contains(
            typeof(SearchPanelViewModel).GetMethods(),
            method =>
                method.Name == nameof(SearchPanelViewModel.ActivateQuickSwitchFolderSearchAsync) &&
                !method.IsGenericMethod &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(IReadOnlyList<QuickSwitchFolderCandidate>));
        Assert.DoesNotContain(
            typeof(SearchPanel).GetMethods(),
            method =>
                method.Name == nameof(SearchPanel.ActivateFolderSearch) &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(IReadOnlyList<QuickSwitchFolderCandidate>));
        Assert.Contains(
            typeof(SearchPanel).GetMethods(),
            method =>
                method.Name == nameof(SearchPanel.ActivateQuickSwitchFolderSearch) &&
                !method.IsGenericMethod &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(IReadOnlyList<QuickSwitchFolderCandidate>));
        Assert.Contains(
            typeof(SearchPanel).GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            method =>
                method.Name == nameof(SearchPanel.ActivateQuickSwitchFolderSearchAsync) &&
                method.GetParameters() is
                [
                    { ParameterType: var candidatesType },
                    { ParameterType: var activationType }
                ] &&
                candidatesType == typeof(IReadOnlyList<QuickSwitchFolderCandidate>) &&
                activationType == typeof(Func<string, CancellationToken, Task<DialogJumpResult>>));
    }

    [Fact]
    public async Task FolderSearchSkipsFileOnlyQueryAndKeepsPinnedFolders()
    {
        var now = DateTimeOffset.UtcNow;
        var pinnedFolder = "C:\\Projects\\Alpha";
        var index = new RecordingSearchIndex(new[] { CreateResult("C:\\Docs\\Report.txt", isDirectory: false) });
        var viewModel = new SearchPanelViewModel(
            index,
            NormalizeTestFolder,
            _ => true,
            _ => now,
            new RecordingActivationService(),
            DialogJumpNotConfiguredAsync,
            TimeSpan.Zero);

        await viewModel.ActivateQuickSwitchFolderSearchAsync(new[] { CreateCandidate(pinnedFolder) });
        viewModel.QueryText = "file: report";

        await WaitUntilAsync(() => viewModel.StatusText.Contains("File-only", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(index.ObservedQueries);
        var result = Assert.Single(viewModel.Results);
        Assert.Equal(pinnedFolder, result.Record.FullPath);
    }

    [Fact]
    public void MoveSelectionMovesWithinResultsAndClampsAtBothEnds()
    {
        var first = CreateResult("C:\\Docs\\Alpha.txt", isDirectory: false);
        var second = CreateResult("C:\\Docs\\Beta.txt", isDirectory: false);
        var third = CreateResult("C:\\Docs\\Gamma.txt", isDirectory: false);
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        viewModel.Results.Add(first);
        viewModel.Results.Add(second);
        viewModel.Results.Add(third);

        viewModel.MoveSelection(1);
        Assert.Equal(first, viewModel.SelectedResult);

        viewModel.MoveSelection(10);
        Assert.Equal(third, viewModel.SelectedResult);

        viewModel.MoveSelection(-10);
        Assert.Equal(first, viewModel.SelectedResult);

        var emptyViewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        emptyViewModel.MoveSelection(1);
        Assert.Null(emptyViewModel.SelectedResult);
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncSkipsMissingInvalidAndDuplicateQuickSwitchCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        var alphaFolder = "C:\\Projects\\Alpha";
        var betaFolder = "C:\\Projects\\Beta";
        var existingFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            alphaFolder,
            betaFolder
        };
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            folderPath =>
            {
                if (string.Equals(folderPath, "not-a-folder", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Folder path is invalid.", nameof(folderPath));
                }

                return NormalizeTestFolder(folderPath);
            },
            existingFolders.Contains,
            _ => now)
        {
            QueryText = "project"
        };
        await index.WaitForSearchCountAsync(1);
        index.ClearObservedQueries();

        await viewModel.ActivateQuickSwitchFolderSearchAsync(new[]
        {
            CreateCandidate("C:\\Projects\\Missing"),
            CreateCandidate("C:\\Projects\\Alpha\\"),
            CreateCandidate("C:\\PROJECTS\\ALPHA"),
            CreateCandidate("not-a-folder"),
            CreateCandidate(betaFolder)
        });

        Assert.Single(index.ObservedQueries);
        Assert.Equal(
            new[] { alphaFolder, betaFolder },
            viewModel.Results.Select(result => result.Record.FullPath));
        Assert.Equal(alphaFolder, viewModel.SelectedResult?.Record.FullPath);
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncUsesFallbackTimestampWhenTrackedFolderTimestampFails()
    {
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(
            index,
            _ => "C:\\Tracked",
            _ => true,
            _ => throw new UnauthorizedAccessException("Folder metadata is unavailable."));

        await viewModel.ActivateFolderSearchAsync("C:\\Tracked");

        var result = Assert.Single(viewModel.Results);
        Assert.Equal("C:\\Tracked", result.Record.FullPath);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.Record.LastWriteTime);
        Assert.Empty(index.ObservedQueries);
    }

    [Fact]
    public void ConstructorStartsWithSearchPresentation()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));

        Assert.Equal("Search", viewModel.ModeDisplayText);
        Assert.Equal("Search files and folders", viewModel.QueryPlaceholderText);
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncUsesDialogJumpPresentation()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Equal("Dialog Jump", viewModel.ModeDisplayText);
        Assert.Equal("Jump dialog to folder", viewModel.QueryPlaceholderText);
    }

    [Fact]
    public async Task ActivateQuickSwitchFolderSearchAsyncUsesQuickSwitchPresentation()
    {
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            NormalizeTestFolder,
            _ => true,
            _ => DateTimeOffset.UtcNow);

        await viewModel.ActivateQuickSwitchFolderSearchAsync(new[]
        {
            CreateCandidate("C:\\Projects")
        });

        Assert.Equal("Quick Switch", viewModel.ModeDisplayText);
        Assert.Equal("Quick switch to folder", viewModel.QueryPlaceholderText);
    }

    [Fact]
    public async Task QuickSwitchBarCanCollapseAndNormalSearchResetsIt()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        await viewModel.ActivateQuickSwitchFolderSearchAsync(Array.Empty<QuickSwitchFolderCandidate>());

        viewModel.SetQuickSwitchBarCollapsed(true);

        Assert.True(viewModel.IsQuickSwitchMode);
        Assert.True(viewModel.IsQuickSwitchBarCollapsed);

        await viewModel.ActivateFilesAndFoldersSearchAsync();

        Assert.False(viewModel.IsQuickSwitchMode);
        Assert.False(viewModel.IsQuickSwitchBarCollapsed);
    }

    [Fact]
    public async Task ResetQuickSwitchQueryStartsTheNextCollapsedSearchFromEmptyText()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        await viewModel.ActivateQuickSwitchFolderSearchAsync(Array.Empty<QuickSwitchFolderCandidate>());
        viewModel.QueryText = "onedrive";
        viewModel.SelectedResult = CreateResult("C:\\Users\\test\\OneDrive", isDirectory: true);

        viewModel.ResetQuickSwitchQuery();

        Assert.Equal(string.Empty, viewModel.QueryText);
        Assert.Null(viewModel.SelectedResult);
    }

    [Fact]
    public async Task QuickSwitchCandidateAreaIsVisibleOnlyWhenResultsExist()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        await viewModel.ActivateQuickSwitchFolderSearchAsync(Array.Empty<QuickSwitchFolderCandidate>());
        Assert.False(viewModel.AreResultsVisible);

        viewModel.Results.Add(CreateResult("C:\\Projects", isDirectory: true));
        Assert.True(viewModel.AreResultsVisible);

        viewModel.SetQuickSwitchBarCollapsed(true);
        Assert.False(viewModel.AreResultsVisible);
    }

    [Fact]
    public async Task QuickSwitchActivationUsesSessionDialogDelegateAndFolderModeRestoresDefault()
    {
        var result = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
        var defaultActivation = new RecordingDialogFolderActivation(
            new DialogJumpResult(DialogJumpStatus.Success, "Default dialog changed."));
        var sessionActivation = new RecordingDialogFolderActivation(
            new DialogJumpResult(DialogJumpStatus.Success, "Captured dialog changed."));
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            new RecordingActivationService(),
            defaultActivation.ActivateAsync);

        await viewModel.ActivateQuickSwitchFolderSearchAsync(
            Array.Empty<QuickSwitchFolderCandidate>(),
            sessionActivation.ActivateAsync);
        viewModel.Results.Add(result);
        viewModel.SelectedResult = result;
        var activated = await viewModel.ActivateSelectedAsync();

        Assert.True(activated);
        Assert.Equal(new[] { result.Record.FullPath }, sessionActivation.Paths);
        Assert.Empty(defaultActivation.Paths);

        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.Results.Add(result);
        viewModel.SelectedResult = result;
        await viewModel.ActivateSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, defaultActivation.Paths);
        Assert.Single(sessionActivation.Paths);
    }

    [Fact]
    public async Task PresentationModeChangesRaisePropertyChanged()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Equal(1, CountChanges(changedProperties, nameof(SearchPanelViewModel.ModeDisplayText)));
        Assert.Equal(1, CountChanges(changedProperties, nameof(SearchPanelViewModel.QueryPlaceholderText)));
    }

    [Fact]
    public async Task ReactivatingSamePresentationModeDoesNotRaisePresentationPropertiesAgain()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var changedProperties = new List<string?>();

        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Equal(0, CountChanges(changedProperties, nameof(SearchPanelViewModel.ModeDisplayText)));
        Assert.Equal(0, CountChanges(changedProperties, nameof(SearchPanelViewModel.QueryPlaceholderText)));
    }

    [Fact]
    public async Task ActivateFilesAndFoldersSearchAsyncReturnsToSearchPresentation()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var changedProperties = new List<string?>();

        await viewModel.ActivateFolderSearchAsync(null);
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await viewModel.ActivateFilesAndFoldersSearchAsync();

        Assert.Equal("Search", viewModel.ModeDisplayText);
        Assert.Equal("Search files and folders", viewModel.QueryPlaceholderText);
        Assert.Equal(1, CountChanges(changedProperties, nameof(SearchPanelViewModel.ModeDisplayText)));
        Assert.Equal(1, CountChanges(changedProperties, nameof(SearchPanelViewModel.QueryPlaceholderText)));
    }

    [Fact]
    public async Task PresentationModeNotificationsObserveUpdatedStatus()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var observedStatusText = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(SearchPanelViewModel.ModeDisplayText), StringComparison.Ordinal))
            {
                observedStatusText.Add(viewModel.StatusText);
            }
        };

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Equal(new[] { "Select a folder to jump the dialog." }, observedStatusText);
    }

    private static int CountChanges(IEnumerable<string?> changedProperties, string propertyName)
    {
        return changedProperties.Count(property => string.Equals(property, propertyName, StringComparison.Ordinal));
    }

    private static QuickSwitchFolderCandidate CreateCandidate(string folderPath)
    {
        return new QuickSwitchFolderCandidate(folderPath, "Explorer", IntPtr.Zero, false);
    }

    private static SearchResult CreateResult(string fullPath, bool isDirectory)
    {
        return new SearchResult(
            FileRecord.Create(fullPath, isDirectory, isDirectory ? 0 : 10, DateTimeOffset.UtcNow),
            100,
            "test");
    }

    private static string? NormalizeTestFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static IEnumerable<FileRecord> CancelCurrentFolderFiltering(
        CancellationTokenSource cancellation,
        Action onEnumerated)
    {
        for (var index = 0; index < 1_000; index++)
        {
            onEnumerated();
            if (index == 7)
            {
                cancellation.Cancel();
            }

            yield return FileRecord.Create(
                $"C:\\Current\\Invoice-{index:D4}.txt",
                false,
                1,
                DateTimeOffset.UnixEpoch);
        }
    }

    private static Task<DialogJumpResult> DialogJumpNotConfiguredAsync(string folderPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog jump is not configured."));
    }

    private sealed class RecordingActivationService : ISearchResultActivationService
    {
        private readonly Exception? _openException;

        public RecordingActivationService(Exception? openException = null)
        {
            _openException = openException;
        }

        public List<string> OpenedPaths { get; } = new();

        public List<string> RevealedPaths { get; } = new();

        public List<string> CopiedPaths { get; } = new();

        public Task OpenAsync(string path)
        {
            OpenedPaths.Add(path);
            return _openException is null
                ? Task.CompletedTask
                : Task.FromException(_openException);
        }

        public Task RevealAsync(string path, bool isDirectory)
        {
            RevealedPaths.Add(path);
            return Task.CompletedTask;
        }

        public void CopyPath(string path)
        {
            CopiedPaths.Add(path);
        }

        public List<string> DeletedPaths { get; } = new();

        public Task DeleteAsync(string path, bool isDirectory, bool allowUndo = true)
        {
            DeletedPaths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDialogFolderActivation
    {
        private readonly Func<string, DialogJumpResult> _activate;

        public RecordingDialogFolderActivation(DialogJumpResult result)
            : this(_ => result)
        {
        }

        public RecordingDialogFolderActivation(Func<string, DialogJumpResult> activate)
        {
            _activate = activate;
        }

        public List<string> Paths { get; } = new();

        public Task<DialogJumpResult> ActivateAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(_activate(path));
        }
    }

    private sealed class RecordingSearchIndex : ISearchIndex
    {
        private readonly object _lock = new();
        private IReadOnlyList<SearchResult> _results;
        private readonly List<SearchQuery> _queries = new();
        private readonly List<int> _searchThreadIds = new();
        private TaskCompletionSource _searchObserved = CreateCompletionSource();
        private readonly Exception? _usageException;

        public RecordingSearchIndex(IReadOnlyList<SearchResult> results, Exception? usageException = null)
        {
            _results = results;
            _usageException = usageException;
        }

        public List<string> RecordedUsagePaths { get; } = new();

        public IReadOnlyList<int> SearchThreadIds
        {
            get
            {
                lock (_lock)
                {
                    return _searchThreadIds.ToArray();
                }
            }
        }

        public IReadOnlyList<SearchQuery> ObservedQueries
        {
            get
            {
                lock (_lock)
                {
                    return _queries.ToArray();
                }
            }
        }

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken)
        {
            if (_usageException is not null)
            {
                return Task.FromException(_usageException);
            }

            RecordedUsagePaths.Add(fullPath);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _queries.Add(query);
                _searchThreadIds.Add(Environment.CurrentManagedThreadId);
                _searchObserved.TrySetResult();
                _searchObserved = CreateCompletionSource();
            }

            return Task.FromResult(_results);
        }

        public void ClearObservedQueries()
        {
            lock (_lock)
            {
                _queries.Clear();
                _searchObserved = CreateCompletionSource();
            }
        }

        public void SetResults(IReadOnlyList<SearchResult> results)
        {
            lock (_lock)
            {
                _results = results;
            }
        }

        public async Task WaitForSearchCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (true)
            {
                Task observedTask;
                lock (_lock)
                {
                    if (_queries.Count >= count)
                    {
                        return;
                    }

                    observedTask = _searchObserved.Task;
                }

                var completed = await Task.WhenAny(observedTask, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                if (completed != observedTask)
                {
                    throw new TimeoutException("The expected search was not observed.");
                }
            }
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class BlockingSearchIndex : ISearchIndex
    {
        private readonly object _lock = new();
        private readonly List<SearchQuery> _queries = new();
        private readonly TaskCompletionSource<IReadOnlyList<SearchResult>> _searchCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _searchObserved = CreateCompletionSource();

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _queries.Add(query);
                _searchObserved.TrySetResult();
                _searchObserved = CreateCompletionSource();
            }

            return _searchCompletion.Task;
        }

        public void Complete(IReadOnlyList<SearchResult> results)
        {
            _searchCompletion.SetResult(results);
        }

        public async Task WaitForSearchCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (true)
            {
                Task observedTask;
                lock (_lock)
                {
                    if (_queries.Count >= count)
                    {
                        return;
                    }

                    observedTask = _searchObserved.Task;
                }

                var completed = await Task.WhenAny(observedTask, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                if (completed != observedTask)
                {
                    throw new TimeoutException("The expected search was not observed.");
                }
            }
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class BlockingRecentSearchIndex : ISearchIndex
    {
        private readonly IReadOnlyList<SearchResult> _searchResults;
        private readonly TaskCompletionSource<IReadOnlyList<SearchResult>> _recentCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _recentObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _searchObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingRecentSearchIndex(IReadOnlyList<SearchResult> searchResults)
        {
            _searchResults = searchResults;
        }

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit, CancellationToken cancellationToken)
        {
            _recentObserved.TrySetResult();
            return _recentCompletion.Task;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            _searchObserved.TrySetResult();
            return Task.FromResult(_searchResults);
        }

        public void CompleteRecent(IReadOnlyList<SearchResult> results) => _recentCompletion.TrySetResult(results);

        public Task WaitForRecentAsync() => _recentObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForSearchAsync() => _searchObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class CancellableSearchIndex : ISearchIndex
    {
        private readonly object _lock = new();
        private readonly IReadOnlyList<SearchResult>[] _resultsByCall;
        private readonly List<SearchQuery> _queries = new();
        private readonly List<TaskCompletionSource<IReadOnlyList<SearchResult>>> _searchCompletions = new();
        private TaskCompletionSource _searchObserved = CreateCompletionSource();
        private TaskCompletionSource _cancellationObserved = CreateCompletionSource();
        private int _cancellationCount;

        public CancellableSearchIndex(params IReadOnlyList<SearchResult>[] resultsByCall)
        {
            _resultsByCall = resultsByCall;
        }

        public IReadOnlyList<SearchQuery> ObservedQueries
        {
            get
            {
                lock (_lock)
                {
                    return _queries.ToArray();
                }
            }
        }

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            TaskCompletionSource<IReadOnlyList<SearchResult>> completion;
            lock (_lock)
            {
                _queries.Add(query);
                completion = new TaskCompletionSource<IReadOnlyList<SearchResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _searchCompletions.Add(completion);
                _searchObserved.TrySetResult();
                _searchObserved = CreateCompletionSource();
            }

            cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    _cancellationCount++;
                    _cancellationObserved.TrySetResult();
                }

                completion.TrySetCanceled(cancellationToken);
            });

            return completion.Task;
        }

        public void CompleteSearch(int oneBasedCallIndex)
        {
            TaskCompletionSource<IReadOnlyList<SearchResult>> completion;
            IReadOnlyList<SearchResult> results;
            lock (_lock)
            {
                completion = _searchCompletions[oneBasedCallIndex - 1];
                results = _resultsByCall[Math.Min(oneBasedCallIndex - 1, _resultsByCall.Length - 1)];
            }

            completion.TrySetResult(results);
        }

        public async Task WaitForSearchCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (true)
            {
                Task observedTask;
                lock (_lock)
                {
                    if (_queries.Count >= count)
                    {
                        return;
                    }

                    observedTask = _searchObserved.Task;
                }

                var completed = await Task.WhenAny(observedTask, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                if (completed != observedTask)
                {
                    throw new TimeoutException("The expected search was not observed.");
                }
            }
        }

        public async Task WaitForCancellationCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (true)
            {
                Task observedTask;
                lock (_lock)
                {
                    if (_cancellationCount >= count)
                    {
                        return;
                    }

                    observedTask = _cancellationObserved.Task;
                }

                var completed = await Task.WhenAny(observedTask, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                if (completed != observedTask)
                {
                    throw new TimeoutException("The expected cancellation was not observed.");
                }
            }
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class RecordingQuickLaunchExecutor : IQuickLaunchExecutor
    {
        public List<QuickLaunchEntry> Executed { get; } = new();

        public Task ExecuteAsync(QuickLaunchEntry entry)
        {
            Executed.Add(entry);
            return Task.CompletedTask;
        }
    }
}
