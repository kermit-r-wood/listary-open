using ListaryOpen.Infrastructure.Windows;
using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerTrackerTests
{
    [Fact]
    public void ObserveForegroundExplorerFolderStoresMatchingExistingFolder()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var foregroundHandle = new IntPtr(1234);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(5678), "C:\\Other"),
                    new ExplorerShellWindow(foregroundHandle, folder.FullName)
                }));

            tracker.ObserveForegroundExplorerFolder();

            Assert.Equal(folder.FullName, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveForegroundExplorerFolderWithoutMatchingWindowLeavesLastFolderUnchanged()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var existingPath = folder.FullName + Path.DirectorySeparatorChar;
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(5678), folder.FullName)
                }));
            tracker.ObserveFolderForTests(existingPath);

            tracker.ObserveForegroundExplorerFolder();

            Assert.Equal(existingPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveForegroundExplorerFolderWhenShellEnumerationFailsLeavesLastFolderUnchanged()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var existingPath = folder.FullName + Path.DirectorySeparatorChar;
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new ThrowingExplorerShellWindowsProvider(new COMException("Shell unavailable.")));
            tracker.ObserveFolderForTests(existingPath);

            tracker.ObserveForegroundExplorerFolder();

            Assert.Equal(existingPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveFolderForTestsStoresExistingFolderPathExactly()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var tracker = new ExplorerTracker();

            var observedPath = folder.FullName + Path.DirectorySeparatorChar;

            tracker.ObserveFolderForTests(observedPath);

            Assert.Equal(observedPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveFolderForTestsIgnoresMissingFolderAndKeepsLastFolder()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");
        var missingFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var tracker = new ExplorerTracker();
            var observedPath = folder.FullName + Path.DirectorySeparatorChar;
            tracker.ObserveFolderForTests(observedPath);

            tracker.ObserveFolderForTests(missingFolder);

            Assert.Equal(observedPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesReturnsForegroundExplorerFolderFirstAndDeduplicates()
    {
        var first = Directory.CreateTempSubdirectory("listary-open-first-");
        var second = Directory.CreateTempSubdirectory("listary-open-second-");

        try
        {
            var foregroundHandle = new IntPtr(2222);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), first.FullName),
                    new ExplorerShellWindow(foregroundHandle, second.FullName),
                    new ExplorerShellWindow(new IntPtr(3333), first.FullName + Path.DirectorySeparatorChar)
                }));

            var candidates = tracker.GetFolderCandidates();

            Assert.Equal(new[] { second.FullName, first.FullName }, candidates.Select(candidate => candidate.FolderPath));
            Assert.True(candidates[0].IsForeground);
            Assert.Equal("Explorer", candidates[0].SourceName);
            Assert.Equal(foregroundHandle, candidates[0].WindowHandle);
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesPrefersLaterForegroundDuplicateFolder()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-duplicate-");

        try
        {
            var foregroundHandle = new IntPtr(2222);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), folder.FullName),
                    new ExplorerShellWindow(foregroundHandle, folder.FullName + Path.DirectorySeparatorChar)
                }));

            var candidate = Assert.Single(tracker.GetFolderCandidates());

            Assert.True(candidate.IsForeground);
            Assert.Equal(foregroundHandle, candidate.WindowHandle);
            Assert.Equal(folder.FullName, candidate.FolderPath);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesSkipsMissingFolders()
    {
        var existing = Directory.CreateTempSubdirectory("listary-open-existing-");
        var missing = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());

        try
        {
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1234), missing),
                    new ExplorerShellWindow(new IntPtr(5678), existing.FullName)
                }));

            var candidate = Assert.Single(tracker.GetFolderCandidates());

            Assert.Equal(existing.FullName, candidate.FolderPath);
            Assert.False(candidate.IsForeground);
        }
        finally
        {
            existing.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesPrefersRememberedFolderWhenNoExplorerWindowIsForeground()
    {
        var remembered = Directory.CreateTempSubdirectory("listary-open-remembered-");
        var firstEnumerated = Directory.CreateTempSubdirectory("listary-open-first-");

        try
        {
            var tracker = new ExplorerTracker(
                () => new IntPtr(9999),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), firstEnumerated.FullName),
                    new ExplorerShellWindow(new IntPtr(2222), remembered.FullName)
                }));
            tracker.ObserveFolderForTests(remembered.FullName);

            var candidates = tracker.GetFolderCandidates();

            Assert.Equal(remembered.FullName, candidates[0].FolderPath);
            Assert.False(candidates[0].IsForeground);
        }
        finally
        {
            remembered.Delete(recursive: true);
            firstEnumerated.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompositeQuickSwitchWindowProviderRefreshesProvidersDeduplicatesAndKeepsForegroundFirst()
    {
        var foreground = Directory.CreateTempSubdirectory("listary-open-foreground-");
        var duplicate = Directory.CreateTempSubdirectory("listary-open-duplicate-");
        var thirdParty = Directory.CreateTempSubdirectory("listary-open-dopus-");

        try
        {
            var explorerProvider = new RecordingRefreshableQuickSwitchProvider(new[]
            {
                new QuickSwitchFolderCandidate(duplicate.FullName, "Explorer", new IntPtr(1), false),
                new QuickSwitchFolderCandidate(foreground.FullName, "Explorer", new IntPtr(2), true)
            });
            var directoryOpusProvider = new RecordingRefreshableQuickSwitchProvider(new[]
            {
                new QuickSwitchFolderCandidate(duplicate.FullName + Path.DirectorySeparatorChar, "Directory Opus", new IntPtr(3), false),
                new QuickSwitchFolderCandidate(thirdParty.FullName, "Directory Opus", new IntPtr(4), false)
            });
            var composite = new CompositeQuickSwitchWindowProvider(new IQuickSwitchWindowProvider[]
            {
                explorerProvider,
                directoryOpusProvider
            });

            composite.Refresh();
            var candidates = composite.GetFolderCandidates();

            Assert.True(explorerProvider.Refreshed);
            Assert.True(directoryOpusProvider.Refreshed);
            Assert.Equal(
                new[] { foreground.FullName, duplicate.FullName, thirdParty.FullName },
                candidates.Select(candidate => candidate.FolderPath));
            Assert.Equal("Explorer", candidates[1].SourceName);
        }
        finally
        {
            foreground.Delete(recursive: true);
            duplicate.Delete(recursive: true);
            thirdParty.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompositeQuickSwitchWindowProviderKeepsFallbackCandidatesWhenProvidersReturnNoFolders()
    {
        var fallback = Directory.CreateTempSubdirectory("listary-open-fallback-");

        try
        {
            var composite = new CompositeQuickSwitchWindowProvider(
                new IQuickSwitchWindowProvider[]
                {
                    new RecordingRefreshableQuickSwitchProvider(Array.Empty<QuickSwitchFolderCandidate>())
                },
                new[]
                {
                    new QuickSwitchFolderCandidate(fallback.FullName, "Explorer", IntPtr.Zero, false)
                });

            var candidate = Assert.Single(composite.GetFolderCandidates());

            Assert.Equal(fallback.FullName, candidate.FolderPath);
        }
        finally
        {
            fallback.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompositeQuickSwitchWindowProviderSkipsThrowingProviders()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-safe-provider-");

        try
        {
            var composite = new CompositeQuickSwitchWindowProvider(new IQuickSwitchWindowProvider[]
            {
                new ThrowingQuickSwitchWindowProvider(new IOException("Provider failed.")),
                new RecordingRefreshableQuickSwitchProvider(new[]
                {
                    new QuickSwitchFolderCandidate(folder.FullName, "Explorer", IntPtr.Zero, false)
                })
            });

            var candidate = Assert.Single(composite.GetFolderCandidates());

            Assert.Equal(folder.FullName, candidate.FolderPath);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void DirectoryOpusQuickSwitchProviderParsesActiveListerActiveTabBeforeOtherTabs()
    {
        var active = Directory.CreateTempSubdirectory("listary-open-dopus-active-");
        var dual = Directory.CreateTempSubdirectory("listary-open-dopus-dual-");
        var inactive = Directory.CreateTempSubdirectory("listary-open-dopus-inactive-");
        var otherListerSource = Directory.CreateTempSubdirectory("listary-open-dopus-other-source-");

        try
        {
            var candidates = DirectoryOpusQuickSwitchProvider.ParseInfoPaths(
                $"""
                <results>
                  <path lister="2" tab="1" active_lister="0" active_tab="1" tab_state="1">{otherListerSource.FullName}</path>
                  <path lister="1" tab="3" active_lister="1" active_tab="0" tab_state="0">{inactive.FullName}</path>
                  <path lister="1" tab="2" active_lister="1" active_tab="0" tab_state="2">{dual.FullName}</path>
                  <path lister="1" tab="1" active_lister="1" active_tab="1" tab_state="1">{active.FullName}</path>
                  <path tab_state="0">C:\Missing\Folder</path>
                </results>
                """);

            Assert.Equal(
                new[] { active.FullName, dual.FullName, inactive.FullName, otherListerSource.FullName },
                candidates.Select(candidate => candidate.FolderPath));
            Assert.Equal("Directory Opus", candidates[0].SourceName);
            Assert.True(candidates[0].IsForeground);
            Assert.False(candidates[1].IsForeground);
            Assert.False(candidates[3].IsForeground);
        }
        finally
        {
            active.Delete(recursive: true);
            dual.Delete(recursive: true);
            inactive.Delete(recursive: true);
            otherListerSource.Delete(recursive: true);
        }
    }

    [Fact]
    public void DirectoryOpusQuickSwitchProviderKeepsBestDuplicatePath()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-dopus-duplicate-");

        try
        {
            var candidates = DirectoryOpusQuickSwitchProvider.ParseInfoPaths(
                $"""
                <results>
                  <path active_lister="0" active_tab="0" tab_state="0">{folder.FullName}</path>
                  <path active_lister="1" active_tab="1" tab_state="1">{folder.FullName}</path>
                </results>
                """);

            var candidate = Assert.Single(candidates);

            Assert.True(candidate.IsForeground);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void DirectoryOpusQuickSwitchProviderSkipsInfoReadWhenDirectoryOpusIsNotForeground()
    {
        var read = false;
        var provider = new DirectoryOpusQuickSwitchProvider(
            () =>
            {
                read = true;
                return "<path active_lister=\"1\" active_tab=\"1\">C:\\Users</path>";
            },
            foregroundProvider: () => false);

        var candidates = provider.GetFolderCandidates();

        Assert.Empty(candidates);
        Assert.False(read);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(100, 100, true)]
    [InlineData(100, 200, false)]
    public void ExplorerShellWindowActiveTabFilterMatchesOnlyCurrentTab(int activeTabHandle, int shellBrowserHandle, bool expected)
    {
        Assert.Equal(
            expected,
            ComExplorerShellWindowsProvider.ShouldIncludeShellWindowForActiveTab(
                new IntPtr(activeTabHandle),
                new IntPtr(shellBrowserHandle)));
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

    private sealed class ThrowingExplorerShellWindowsProvider : IExplorerShellWindowsProvider
    {
        private readonly Exception _exception;

        public ThrowingExplorerShellWindowsProvider(Exception exception)
        {
            _exception = exception;
        }

        public IEnumerable<ExplorerShellWindow> EnumerateWindows()
        {
            throw _exception;
        }
    }

    private sealed class RecordingRefreshableQuickSwitchProvider : IRefreshableQuickSwitchWindowProvider
    {
        private readonly IReadOnlyList<QuickSwitchFolderCandidate> _candidates;

        public RecordingRefreshableQuickSwitchProvider(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
        {
            _candidates = candidates;
        }

        public bool Refreshed { get; private set; }

        public void Refresh()
        {
            Refreshed = true;
        }

        public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
        {
            return _candidates;
        }
    }

    private sealed class ThrowingQuickSwitchWindowProvider : IQuickSwitchWindowProvider
    {
        private readonly Exception _exception;

        public ThrowingQuickSwitchWindowProvider(Exception exception)
        {
            _exception = exception;
        }

        public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
        {
            throw _exception;
        }
    }
}
