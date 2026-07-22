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
            tracker.ObserveForegroundExplorerFolder();

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
            tracker.ObserveForegroundExplorerFolder();

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
            tracker.ObserveForegroundExplorerFolder();

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
            tracker.ObserveForegroundExplorerFolder();

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
    public void CompositeQuickSwitchWindowProviderPrefersMostRecentlyForegroundSourceWhenNoneIsForeground()
    {
        var explorerFolder = Directory.CreateTempSubdirectory("listary-open-explorer-");
        var directoryOpusFolder = Directory.CreateTempSubdirectory("listary-open-dopus-");

        try
        {
            var explorerProvider = new RecordingRefreshableQuickSwitchProvider(new[]
            {
                new QuickSwitchFolderCandidate(explorerFolder.FullName, "Explorer", new IntPtr(1), false)
            });
            var directoryOpusProvider = new RecordingRefreshableQuickSwitchProvider(new[]
            {
                new QuickSwitchFolderCandidate(directoryOpusFolder.FullName, "Directory Opus", new IntPtr(2), true)
            });
            var composite = new CompositeQuickSwitchWindowProvider(new IQuickSwitchWindowProvider[]
            {
                explorerProvider,
                directoryOpusProvider
            });

            _ = composite.GetFolderCandidates();
            directoryOpusProvider.Candidates = new[]
            {
                new QuickSwitchFolderCandidate(directoryOpusFolder.FullName, "Directory Opus", new IntPtr(2), false)
            };

            var candidates = composite.GetFolderCandidates();

            Assert.Equal(directoryOpusFolder.FullName, candidates[0].FolderPath);
        }
        finally
        {
            explorerFolder.Delete(recursive: true);
            directoryOpusFolder.Delete(recursive: true);
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
    public void CompositeQuickSwitchWindowProviderSnapshotsStaticFallbackCandidates()
    {
        var fallback = Directory.CreateTempSubdirectory("listary-open-fallback-snapshot-");
        var candidates = new List<QuickSwitchFolderCandidate>
        {
            new(fallback.FullName, "Explorer", IntPtr.Zero, false)
        };

        try
        {
            var composite = new CompositeQuickSwitchWindowProvider(
                new IQuickSwitchWindowProvider[]
                {
                    new RecordingRefreshableQuickSwitchProvider(Array.Empty<QuickSwitchFolderCandidate>())
                },
                candidates);
            candidates.Clear();

            var candidate = Assert.Single(composite.GetFolderCandidates());

            Assert.Equal(fallback.FullName, candidate.FolderPath);
        }
        finally
        {
            fallback.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompositeQuickSwitchWindowProviderSkipsThrowingFallbackCandidates()
    {
        var composite = new CompositeQuickSwitchWindowProvider(
            new IQuickSwitchWindowProvider[]
            {
                new RecordingRefreshableQuickSwitchProvider(Array.Empty<QuickSwitchFolderCandidate>())
            },
            () => throw new IOException("Fallback failed."));

        Assert.Empty(composite.GetFolderCandidates());
    }

    [Fact]
    public void CompositeQuickSwitchWindowProviderDoesNotLetRememberedExplorerFolderOutrankLiveProviders()
    {
        var remembered = Directory.CreateTempSubdirectory("listary-open-remembered-");
        var liveFolder = Directory.CreateTempSubdirectory("listary-open-live-provider-");

        try
        {
            var tracker = new ExplorerTracker(
                () => IntPtr.Zero,
                new RecordingExplorerShellWindowsProvider(Array.Empty<ExplorerShellWindow>()));
            tracker.ObserveFolderForTests(remembered.FullName);
            var composite = new CompositeQuickSwitchWindowProvider(
                new IQuickSwitchWindowProvider[]
                {
                    tracker,
                    new RecordingRefreshableQuickSwitchProvider(new[]
                    {
                        new QuickSwitchFolderCandidate(liveFolder.FullName, "Directory Opus", new IntPtr(1), false)
                    })
                },
                new[]
                {
                    new QuickSwitchFolderCandidate(remembered.FullName, "Explorer", IntPtr.Zero, false)
                });

            var candidate = Assert.Single(composite.GetFolderCandidates());

            Assert.Equal(liveFolder.FullName, candidate.FolderPath);
        }
        finally
        {
            remembered.Delete(recursive: true);
            liveFolder.Delete(recursive: true);
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
    public void DirectoryOpusQuickSwitchProviderReadsActiveListerWhenDirectoryOpusIsNotForeground()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-dopus-background-");
        var read = false;

        try
        {
            var provider = new DirectoryOpusQuickSwitchProvider(
                () =>
                {
                    read = true;
                    return $"<path active_lister=\"1\" active_tab=\"1\">{folder.FullName}</path>";
                },
                foregroundProvider: () => false);

            Assert.True(SpinWait.SpinUntil(() => provider.GetFolderCandidates().Count == 1, TimeSpan.FromSeconds(1)));
            var candidate = Assert.Single(provider.GetFolderCandidates());

            Assert.True(read);
            Assert.Equal(folder.FullName, candidate.FolderPath);
            Assert.Equal("Directory Opus", candidate.SourceName);
            Assert.False(candidate.IsForeground);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void DirectoryOpusQuickSwitchProviderReturnsCachedSnapshotWhileInfoReadIsInFlight()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-dopus-cache-");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        try
        {
            var provider = new DirectoryOpusQuickSwitchProvider(
                () =>
                {
                    started.Set();
                    release.Wait();
                    return $"<path active_lister=\"1\" active_tab=\"1\">{folder.FullName}</path>";
                },
                foregroundProvider: () => true);
            var refresh = typeof(DirectoryOpusQuickSwitchProvider).GetMethod(nameof(IRefreshableQuickSwitchWindowProvider.Refresh));

            Assert.NotNull(refresh);
            refresh!.Invoke(provider, null);
            Assert.True(started.Wait(TimeSpan.FromSeconds(1)));

            Assert.Empty(provider.GetFolderCandidates());

            release.Set();
            Assert.True(SpinWait.SpinUntil(() => provider.GetFolderCandidates().Count == 1, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            release.Set();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void TotalCommanderQuickSwitchProviderParsesPanelPathText()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-tc-parse-");

        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory)!;
            var lowerRoot = char.ToLowerInvariant(root[0]) + root[1..];

            Assert.Equal(root, TotalCommanderQuickSwitchProvider.ParsePanelPathText(root + "*.*"));
            Assert.Equal(root, TotalCommanderQuickSwitchProvider.ParsePanelPathText(lowerRoot + "*.*"));
            Assert.Equal(root, TotalCommanderQuickSwitchProvider.ParsePanelPathText(lowerRoot + ">"));
            Assert.Equal(
                folder.FullName,
                TotalCommanderQuickSwitchProvider.ParsePanelPathText(folder.FullName + Path.DirectorySeparatorChar + "*.*"));
            Assert.Equal(
                folder.FullName,
                TotalCommanderQuickSwitchProvider.ParsePanelPathText(folder.FullName + Path.DirectorySeparatorChar + "*.*>"));
            Assert.Null(TotalCommanderQuickSwitchProvider.ParsePanelPathText(folder.FullName + Path.DirectorySeparatorChar));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void TotalCommanderQuickSwitchProviderReturnsPanelPathsForegroundFirst()
    {
        var backgroundFolder = Directory.CreateTempSubdirectory("listary-open-tc-background-");
        var foregroundFolder = Directory.CreateTempSubdirectory("listary-open-tc-foreground-");

        try
        {
            var foregroundHandle = new IntPtr(200);
            var provider = new TotalCommanderQuickSwitchProvider(() => new[]
            {
                new TotalCommanderWindowSnapshot(
                    new IntPtr(100),
                    new[] { backgroundFolder.FullName + Path.DirectorySeparatorChar + "*.*" },
                    false),
                new TotalCommanderWindowSnapshot(
                    foregroundHandle,
                    new[] { foregroundFolder.FullName + Path.DirectorySeparatorChar + "*.*" },
                    true)
            });

            var candidates = provider.GetFolderCandidates();

            Assert.Equal(
                new[] { foregroundFolder.FullName, backgroundFolder.FullName },
                candidates.Select(candidate => candidate.FolderPath));
            Assert.True(candidates[0].IsForeground);
            Assert.Equal(foregroundHandle, candidates[0].WindowHandle);
            Assert.Equal("Total Commander", candidates[0].SourceName);
        }
        finally
        {
            backgroundFolder.Delete(recursive: true);
            foregroundFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public void TotalCommanderQuickSwitchProviderPrefersActivePanelBeforeInactivePanel()
    {
        var inactiveFolder = Directory.CreateTempSubdirectory("listary-open-tc-inactive-");
        var activeFolder = Directory.CreateTempSubdirectory("listary-open-tc-active-");

        try
        {
            var foregroundHandle = new IntPtr(200);
            var provider = new TotalCommanderQuickSwitchProvider(() => new[]
            {
                new TotalCommanderWindowSnapshot(
                    foregroundHandle,
                    new[]
                    {
                        inactiveFolder.FullName + Path.DirectorySeparatorChar + "*.*",
                        activeFolder.FullName + Path.DirectorySeparatorChar + ">"
                    },
                    true)
            });

            var candidates = provider.GetFolderCandidates();

            Assert.Equal(
                new[] { activeFolder.FullName, inactiveFolder.FullName },
                candidates.Select(candidate => candidate.FolderPath));
        }
        finally
        {
            inactiveFolder.Delete(recursive: true);
            activeFolder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0 k / 0 k in 0 / 2 file(s), 0 / 12 dir(s)")]
    [InlineData("[_none_]  2,589,780,744 k of 4,000,105,676 k free")]
    [InlineData("ftp://example.com/*.*")]
    [InlineData("C:\\Windows")]
    public void TotalCommanderQuickSwitchProviderSkipsNonFilesystemPanelText(string text)
    {
        Assert.Null(TotalCommanderQuickSwitchProvider.ParsePanelPathText(text));
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
        public RecordingRefreshableQuickSwitchProvider(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
        {
            Candidates = candidates;
        }

        public IReadOnlyList<QuickSwitchFolderCandidate> Candidates { get; set; }

        public bool Refreshed { get; private set; }

        public void Refresh()
        {
            Refreshed = true;
        }

        public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
        {
            return Candidates;
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
