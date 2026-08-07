using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class RecentAndPreviewFeatureTests
{
    [Fact]
    public async Task SqliteIndexReturnsExistingItemsInMostRecentUsageOrder()
    {
        var directory = Directory.CreateTempSubdirectory("ListaryOpenRecent");
        var databasePath = Path.Combine(directory.FullName, "index.db");
        var firstPath = Path.Combine(directory.FullName, "first.txt");
        var secondPath = Path.Combine(directory.FullName, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(databasePath, CancellationToken.None);
            await index.UpsertAsync(FileRecord.Create(firstPath, false, 5, DateTimeOffset.UtcNow), CancellationToken.None);
            await index.UpsertAsync(FileRecord.Create(secondPath, false, 6, DateTimeOffset.UtcNow), CancellationToken.None);
            await index.RecordUsageAsync(firstPath, CancellationToken.None);
            await Task.Delay(10);
            await index.RecordUsageAsync(secondPath, CancellationToken.None);

            var recent = await index.GetRecentAsync(10, CancellationToken.None);

            Assert.Equal([secondPath, firstPath], recent.Select(item => item.Record.FullPath));
            Assert.All(recent, item => Assert.Equal("recent", item.MatchReason));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EmptyGlobalQueryShowsRecentItems()
    {
        var record = FileRecord.Create(
            Path.Combine(Environment.CurrentDirectory, "recent.txt"),
            false,
            10,
            DateTimeOffset.UtcNow);
        var viewModel = new SearchPanelViewModel(new RecentSearchIndex(
            [new SearchResult(record, 1, "recent")]));

        await viewModel.ActivateFilesAndFoldersSearchAsync();

        Assert.Equal(record.FullPath, Assert.Single(viewModel.Results).Record.FullPath);
        Assert.Contains("Recent", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreviewSizeFormattingUsesReadableUnits()
    {
        Assert.Equal("0 B", FilePreviewPane.FormatSize(0));
        Assert.Equal("1.5 KB", FilePreviewPane.FormatSize(1536));
        Assert.Equal("2.0 MB", FilePreviewPane.FormatSize(2 * 1024 * 1024));
    }

    [Fact]
    public void ExplorerTrackerReturnsOnlyFreshRecommendedFolder()
    {
        var directory = Directory.CreateTempSubdirectory("ListaryOpenExplorerRecent");
        try
        {
            var tracker = new ExplorerTracker();
            tracker.ObserveFolderForTests(directory.FullName);

            Assert.Equal(directory.FullName, tracker.GetRecentlyObservedFolder(TimeSpan.FromSeconds(10)));
            Assert.Null(tracker.GetRecentlyObservedFolder(TimeSpan.Zero, DateTimeOffset.UtcNow.AddSeconds(1)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SearchPanelContainsCollapsiblePreviewPane()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml"));

        Assert.Contains("FilePreviewPane", xaml);
        Assert.Contains("PreviewSplitter", xaml);
        Assert.Contains("PreviewColumn", xaml);
        Assert.Contains("SearchFilterBar", xaml);
        Assert.Contains("SelectedItemTypeFilter", xaml);
        Assert.Contains("SelectedDateFilter", xaml);
        Assert.Contains("x:Name=\"SearchFilterBar\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
    }

    [Fact]
    public void ImagePreviewUsesAFiniteUniformSurfaceInsteadOfANativeSizeScroller()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "FilePreviewPane.xaml"));

        Assert.Contains("x:Name=\"ImagePreviewSurface\"", xaml);
        Assert.Contains("x:Name=\"PreviewImage\"", xaml);
        Assert.Contains("Stretch=\"Uniform\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml);
        Assert.DoesNotContain("x:Name=\"ImageScroller\"", xaml);
    }

    [Fact]
    public void CompactGlobalSearchHeightReservesRoomForImagePreview()
    {
        // Without preview, a single hit stays compact.
        var withoutPreview = SearchPanel.CalculateCompactGlobalSearchOuterHeight(
            resultCount: 1,
            filtersVisible: true,
            previewVisible: false);
        Assert.True(withoutPreview < 220, $"Expected slim compact height, got {withoutPreview}.");

        // With preview, body must be tall enough for the image surface (not just the file name row).
        var withPreview = SearchPanel.CalculateCompactGlobalSearchOuterHeight(
            resultCount: 1,
            filtersVisible: true,
            previewVisible: true);
        Assert.True(withPreview >= 520, $"Expected preview-capable height, got {withPreview}.");
        Assert.True(withPreview > withoutPreview);

        // Empty query stays short even if the preview column is open.
        var empty = SearchPanel.CalculateCompactGlobalSearchOuterHeight(
            resultCount: 0,
            filtersVisible: true,
            previewVisible: true);
        Assert.True(empty < 160, $"Expected empty compact height, got {empty}.");
    }

    [Fact]
    public void CompactGlobalSearchWidthUsesAComfortableDesktopMaximumAndFitsSmallScreens()
    {
        var desktop = SearchPanel.CalculateCompactGlobalSearchOuterWidth(1920);
        var narrow = SearchPanel.CalculateCompactGlobalSearchOuterWidth(800);

        Assert.Equal(860, desktop);
        Assert.Equal(772, narrow);
        Assert.True(desktop < 1920 * 0.6);
        Assert.True(narrow < 800);
    }

    [Fact]
    public async Task SqliteSearchAppliesTypeAndDateFiltersBeforePaging()
    {
        var directory = Directory.CreateTempSubdirectory("ListaryOpenFilter");
        var databasePath = Path.Combine(directory.FullName, "index.db");
        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(databasePath, CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            await index.UpsertAsync(FileRecord.Create(Path.Combine(directory.FullName, "report-new.pdf"), false, 1, now), CancellationToken.None);
            await index.UpsertAsync(FileRecord.Create(Path.Combine(directory.FullName, "report-old.pdf"), false, 1, now.AddYears(-2)), CancellationToken.None);
            await index.UpsertAsync(FileRecord.Create(Path.Combine(directory.FullName, "report-new.png"), false, 1, now), CancellationToken.None);

            var results = await index.SearchAsync(
                new SearchQuery(
                    "report",
                    SearchMode.FilesAndFolders,
                    requiredExtensions: ["pdf"],
                    isDirectory: false,
                    modifiedAfter: now.AddDays(-30)),
                CancellationToken.None);

            Assert.Equal("report-new.pdf", Assert.Single(results).Record.Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string GetRepositoryPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            Path.Combine(segments)));

    private sealed class RecentSearchIndex(IReadOnlyList<SearchResult> recent) : ISearchIndex
    {
        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(recent.Take(limit).ToArray());
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }
}
