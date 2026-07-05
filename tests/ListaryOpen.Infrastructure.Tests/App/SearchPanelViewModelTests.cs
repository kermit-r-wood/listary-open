using ListaryOpen.App;
using ListaryOpen.App.Search;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelViewModelTests
{
    [Fact]
    public async Task ActivateSelectedAsyncOpensSelectedFileInFilesAndFoldersModeAndSetsStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.ActivateSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
        Assert.Empty(activation.CopiedPaths);
        Assert.Contains(result.Record.FullPath, viewModel.StatusText);
    }

    [Fact]
    public async Task RevealSelectedAsyncRevealsSelectedPathAndSetsStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        await viewModel.RevealSelectedAsync();

        Assert.Equal(new[] { result.Record.FullPath }, activation.RevealedPaths);
        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.CopiedPaths);
        Assert.Contains(result.Record.FullPath, viewModel.StatusText);
    }

    [Fact]
    public void CopySelectedPathCopiesSelectedPathAndSetsStatus()
    {
        var result = CreateResult("C:\\Docs\\Invoice.xlsx", isDirectory: false);
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(
            new RecordingSearchIndex(Array.Empty<SearchResult>()),
            activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        viewModel.CopySelectedPath();

        Assert.Equal(new[] { result.Record.FullPath }, activation.CopiedPaths);
        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
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
    public void ReportDialogJumpResultSurfacesFallbackSuccessMessage()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var message = "Dialog folder changed via fallback automation after hook Failed: Hook could not jump.";

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

        await viewModel.ActivateSelectedAsync();

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

        await viewModel.ActivateSelectedAsync();

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
    public async Task RefreshClearsSelectionBeforeAwaitingDelayedSearchAndPreventsStaleActivation()
    {
        var stale = CreateResult("C:\\Docs\\OldInvoice.xlsx", isDirectory: false);
        var index = new BlockingSearchIndex();
        var activation = new RecordingActivationService();
        var viewModel = new SearchPanelViewModel(index, activation);
        viewModel.Results.Add(stale);
        viewModel.SelectedResult = stale;

        viewModel.QueryText = "new invoice";
        await index.WaitForSearchCountAsync(1);

        Assert.Empty(viewModel.Results);
        Assert.Null(viewModel.SelectedResult);

        await viewModel.ActivateSelectedAsync();
        await viewModel.RevealSelectedAsync();
        viewModel.CopySelectedPath();

        Assert.Empty(activation.OpenedPaths);
        Assert.Empty(activation.RevealedPaths);
        Assert.Empty(activation.CopiedPaths);

        index.Complete(Array.Empty<SearchResult>());
        await WaitUntilAsync(() => viewModel.StatusText == "0 results.");
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
    public async Task PresentationModeChangesRaisePropertyChanged()
    {
        var viewModel = new SearchPanelViewModel(new RecordingSearchIndex(Array.Empty<SearchResult>()));
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await viewModel.ActivateFolderSearchAsync(null);

        Assert.Contains(nameof(SearchPanelViewModel.ModeDisplayText), changedProperties);
        Assert.Contains(nameof(SearchPanelViewModel.QueryPlaceholderText), changedProperties);
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

    private static Task<DialogJumpResult> DialogJumpNotConfiguredAsync(string folderPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog jump is not configured."));
    }

    private sealed class RecordingActivationService : ISearchResultActivationService
    {
        public List<string> OpenedPaths { get; } = new();

        public List<string> RevealedPaths { get; } = new();

        public List<string> CopiedPaths { get; } = new();

        public Task OpenAsync(string path)
        {
            OpenedPaths.Add(path);
            return Task.CompletedTask;
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
        private TaskCompletionSource _searchObserved = CreateCompletionSource();

        public RecordingSearchIndex(IReadOnlyList<SearchResult> results)
        {
            _results = results;
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

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _queries.Add(query);
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
}
