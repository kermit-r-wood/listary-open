using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Infrastructure.Windows;
using WpfApplication = ListaryOpen.App.App;

namespace ListaryOpen.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ElevatedDesktopIntegrationCollection
{
    public const string Name = "Elevated desktop integration";
}

[Collection(ElevatedDesktopIntegrationCollection.Name)]
public sealed class RealTaskManagerElevatedIntegrationTests
{
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int TokenElevationInformation = 20;
    private const int TokenIntegrityLevelInformation = 25;
    private const int HighIntegrityRid = 0x3000;

    [Fact]
    [Trait("Category", "ElevatedDesktopIntegration")]
    public void ProductionOverlaySynchronizesTwoSelectionsWithRealSystemTaskManager()
    {
        Exception? failure = null;
        var completed = false;
        var stage = "not started";
        var thread = new Thread(() =>
        {
            WpfApplication? application = null;
            TaskManagerSearchWindow? overlay = null;
            OwnedTaskManagerWindow? ownedTaskManager = null;
            try
            {
                stage = "require an elevated test process";
                var testProcessSecurity = ReadProcessSecurity(GetCurrentProcess());
                Assert.True(
                    testProcessSecurity.IsElevated && testProcessSecurity.IntegrityRid >= HighIntegrityRid,
                    $"This is a mandatory elevated E2E test and cannot run at {testProcessSecurity.IntegrityName} " +
                    "integrity. Start the test runner as administrator and filter Category=ElevatedDesktopIntegration; " +
                    "the test intentionally fails instead of skipping or substituting a fake Task Manager.");

                stage = "attach to or start the real system Task Manager";
                var acquisition = AcquireTaskManagerWindow();
                ownedTaskManager = acquisition.StartedByTest
                    ? new OwnedTaskManagerWindow(acquisition.Handle)
                    : null;
                var taskManagerHandle = acquisition.Handle;
                Assert.NotEqual(IntPtr.Zero, taskManagerHandle);
                Assert.Equal("TaskManagerWindow", GetWindowClass(taskManagerHandle));
                Assert.Equal("Taskmgr", GetWindowProcessName(taskManagerHandle), ignoreCase: true);
                _ = ShowWindow(taskManagerHandle, SwRestore);
                Assert.True(IsWindowVisible(taskManagerHandle), "The real Task Manager window is not visible.");
                Assert.True(
                    DesktopWindowActivator.TryActivate(taskManagerHandle, TimeSpan.FromSeconds(5)),
                    "The real Task Manager window did not become foreground.");

                _ = GetWindowThreadProcessId(taskManagerHandle, out var taskManagerProcessId);
                using var taskManagerProcess = Process.GetProcessById((int)taskManagerProcessId);
                var taskManagerSecurity = ReadProcessSecurity(taskManagerProcess.Handle);
                Assert.True(
                    testProcessSecurity.IntegrityRid >= taskManagerSecurity.IntegrityRid,
                    $"The test process integrity ({testProcessSecurity.IntegrityName}) cannot inspect Task Manager " +
                    $"at {taskManagerSecurity.IntegrityName} integrity.");

                stage = "read real rows through the production Task Manager service";
                using var service = new TaskManagerAutomationService();
                IReadOnlyList<TaskManagerItem> items = Array.Empty<TaskManagerItem>();
                IReadOnlySet<string> selectableIds = new HashSet<string>(StringComparer.Ordinal);
                var query = string.Empty;
#pragma warning disable xUnit1031 // A dedicated STA owns the WPF dispatcher; no xUnit synchronization context is blocked.
                Assert.True(
                    TaskManagerPageTestSupport.PrepareRowPage(
                        taskManagerHandle,
                        () =>
                        {
                            items = service.GetItemsAsync(taskManagerHandle, CancellationToken.None)
                                .GetAwaiter()
                                .GetResult();
                            selectableIds = ReadSelectableRows(taskManagerHandle)
                                .Where(IsRealContentRow)
                                .Select(row => row.RuntimeId)
                                .ToHashSet(StringComparer.Ordinal);
                            query = FindQueryWhoseFirstTwoResultsAreSelectable(items, selectableIds);
                            return items.Count >= 2 && selectableIds.Count >= 2 && !string.IsNullOrWhiteSpace(query);
                        },
                        out var pagePreparation),
                    $"Task Manager did not expose two queryable, independently selectable content rows after '{pagePreparation}'.");
#pragma warning restore xUnit1031
                Assert.True(
                    items.Count >= 2,
                    $"Production TaskManagerAutomationService returned only {items.Count} real Task Manager rows.");
                Assert.True(
                    selectableIds.Count >= 2,
                    "The prepared Task Manager row page did not retain two independently selectable content rows.");
                Assert.False(
                    string.IsNullOrWhiteSpace(query),
                    "Could not find a real Task Manager query whose first two production results expose SelectionItemPattern.");

                stage = "show the production Task Manager search overlay";
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                application = new WpfApplication();
                application.InitializeComponent();
                ThemeManager.Apply(ListaryOpen.Core.Settings.AppTheme.Light);
                LocalizationManager.Apply(ListaryOpen.Core.Settings.AppLanguage.English);
                overlay = new TaskManagerSearchWindow(service);
                overlay.ActivateSearch(query, taskManagerHandle);
                var viewModel = Assert.IsType<TaskManagerSearchViewModel>(overlay.DataContext);
                Assert.True(
                    PumpUntil(() => viewModel.Results.Count >= 2 && viewModel.SelectedItem is not null, TimeSpan.FromSeconds(12)),
                    $"The production overlay did not publish two real results for query '{query}': " +
                    $"available={viewModel.AvailableItemCount}, matches={viewModel.Results.Count}, " +
                    $"active={overlay.IsTaskManagerSearchActive}, status='{viewModel.StatusText}'.");
                Assert.True(overlay.IsTaskManagerSearchActive);
                Assert.Equal(taskManagerHandle, overlay.TaskManagerWindow);

                stage = "establish a host selection before overlay preview";
                var firstOverlaySelection = Assert.IsType<TaskManagerItem>(viewModel.SelectedItem);
                Assert.Equal(viewModel.Results[0].Id, firstOverlaySelection.Id);
                Assert.True(TrySelectRow(taskManagerHandle, firstOverlaySelection.Id));
                var firstHostSelection = WaitForSelectedRow(
                    taskManagerHandle,
                    firstOverlaySelection.Id,
                    TimeSpan.FromSeconds(6));
                Assert.NotNull(firstHostSelection);
                var firstOverlayOracle = ReadOverlaySelection(overlay, firstOverlaySelection);
                Assert.Equal(firstOverlaySelection.Id, firstOverlayOracle.RuntimeId);

                stage = "move overlay preview without mutating Task Manager";
                overlay.MoveSelection(1);
                var secondOverlaySelection = Assert.IsType<TaskManagerItem>(viewModel.SelectedItem);
                Assert.Equal(viewModel.Results[1].Id, secondOverlaySelection.Id);
                Assert.NotEqual(firstOverlaySelection.Id, secondOverlaySelection.Id);
                var secondOverlayOracle = ReadOverlaySelection(overlay, secondOverlaySelection);
                Assert.Equal(secondOverlaySelection.Id, secondOverlayOracle.RuntimeId);
                Thread.Sleep(350);
                var hostSelectionAfterPreview = WaitForSelectedRow(
                    taskManagerHandle,
                    firstOverlaySelection.Id,
                    TimeSpan.FromSeconds(2));
                Assert.NotNull(hostSelectionAfterPreview);
                Assert.Null(WaitForSelectedRow(
                    taskManagerHandle,
                    secondOverlaySelection.Id,
                    TimeSpan.FromMilliseconds(250)));

                stage = "capture composed Task Manager and production overlay evidence";
                var overlayHandle = new WindowInteropHelper(overlay).Handle;
                Assert.NotEqual(IntPtr.Zero, overlayHandle);
                Assert.True(IsWindowVisible(overlayHandle));
                Assert.True(GetWindowRect(taskManagerHandle, out var taskManagerBounds));
                Assert.True(GetWindowRect(overlayHandle, out var overlayBounds));
                var screenshotPath = CaptureComposedEvidence(
                    taskManagerBounds,
                    overlayBounds,
                    "28-real-task-manager-overlay-selection");

                stage = "confirm the second overlay result explicitly";
                overlay.ConfirmSelection();
                var confirmedHostSelection = WaitForSelectedRow(
                    taskManagerHandle,
                    secondOverlaySelection.Id,
                    TimeSpan.FromSeconds(6));
                Assert.NotNull(confirmedHostSelection);

                WriteEvidenceMetadata(
                    screenshotPath,
                    acquisition.StartedByTest,
                    taskManagerHandle,
                    taskManagerProcessId,
                    taskManagerBounds,
                    overlayHandle,
                    overlayBounds,
                    query,
                    testProcessSecurity,
                    taskManagerSecurity,
                    pagePreparation,
                    firstOverlaySelection,
                    firstOverlayOracle,
                    firstHostSelection!,
                    secondOverlaySelection,
                    secondOverlayOracle,
                    hostSelectionAfterPreview!,
                    confirmedHostSelection!);
            }
            catch (Exception exception)
            {
                failure = new InvalidOperationException(
                    $"Real elevated Task Manager integration failed at '{stage}'.",
                    exception);
            }
            finally
            {
                overlay?.DismissSearch();
                if (application is not null)
                {
                    application.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    application.Shutdown();
                }

                ownedTaskManager?.Dispose();
                completed = true;
            }
        })
        {
            IsBackground = true,
            Name = "ListaryOpen real elevated Task Manager E2E"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Page preparation can legitimately consume about 41 seconds while it
        // probes every supported Task Manager page. Leave enough time for the
        // subsequent overlay, selection, screenshot, and confirmation checks.
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), $"Real Task Manager E2E timed out at '{stage}'.");
        Assert.True(completed);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static TaskManagerAcquisition AcquireTaskManagerWindow()
    {
        var existing = EnumerateTaskManagerWindows().FirstOrDefault();
        if (existing != IntPtr.Zero)
        {
            return new TaskManagerAcquisition(existing, StartedByTest: false);
        }

        var existingProcessIds = Process.GetProcessesByName("Taskmgr")
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .ToHashSet();
        if (existingProcessIds.Count > 0)
        {
            Assert.True(
                WaitUntil(
                    () =>
                    {
                        existing = EnumerateTaskManagerWindows().FirstOrDefault(window =>
                        {
                            _ = GetWindowThreadProcessId(window, out var processId);
                            return existingProcessIds.Contains((int)processId);
                        });
                        return existing != IntPtr.Zero;
                    },
                    TimeSpan.FromSeconds(5)),
                "An existing Taskmgr process had no attachable TaskManagerWindow. The test will not launch or " +
                "close another instance because it cannot prove ownership.");
            return new TaskManagerAcquisition(existing, StartedByTest: false);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "taskmgr.exe",
            UseShellExecute = true
        });
        Assert.NotNull(process);
        IntPtr launched = IntPtr.Zero;
        Assert.True(
            WaitUntil(
                () =>
                {
                    launched = EnumerateTaskManagerWindows().FirstOrDefault();
                    return launched != IntPtr.Zero;
                },
                TimeSpan.FromSeconds(12)),
            "The test launched taskmgr.exe but no real TaskManagerWindow appeared.");
        return new TaskManagerAcquisition(launched, StartedByTest: true);
    }

    private static IReadOnlyList<IntPtr> EnumerateTaskManagerWindows()
    {
        var result = new List<IntPtr>();
        _ = EnumWindows(
            (window, _) =>
            {
                if (string.Equals(GetWindowClass(window), "TaskManagerWindow", StringComparison.Ordinal) &&
                    string.Equals(GetWindowProcessName(window), "Taskmgr", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(window);
                }

                return true;
            },
            IntPtr.Zero);
        return result;
    }

    private static string FindQueryWhoseFirstTwoResultsAreSelectable(
        IReadOnlyList<TaskManagerItem> items,
        IReadOnlySet<string> selectableIds)
    {
        var candidateQueries = items
            .Where(item => selectableIds.Contains(item.Id))
            .SelectMany(item => CandidateQueries(item.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(query => query.Length)
            .ThenBy(query => query, StringComparer.OrdinalIgnoreCase);

        foreach (var query in candidateQueries)
        {
            var results = items
                .Select(item => (Item: item, Score: TaskManagerSearchViewModel.MatchScore(item, query)))
                .Where(candidate => !double.IsNegativeInfinity(candidate.Score))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .Select(candidate => candidate.Item)
                .ToArray();
            if (results.Length >= 2 &&
                selectableIds.Contains(results[0].Id) &&
                selectableIds.Contains(results[1].Id) &&
                !string.Equals(results[0].Id, results[1].Id, StringComparison.Ordinal))
            {
                return query;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> CandidateQueries(string value)
    {
        // Prefer real name text over volatile CPU/memory digits embedded in
        // accessibility labels. Numeric one-character queries can lose every
        // match between the setup snapshot and the overlay snapshot as usage
        // values refresh, which does not exercise Task Manager name search.
        var normalized = new string(value.Where(char.IsLetter).ToArray());
        for (var length = 1; length <= Math.Min(4, normalized.Length); length++)
        {
            for (var start = 0; start + length <= normalized.Length; start++)
            {
                yield return normalized.Substring(start, length);
            }
        }
    }

    private static IReadOnlyList<SelectionIdentity> ReadSelectableRows(IntPtr taskManagerHandle)
    {
        var root = AutomationElement.FromHandle(taskManagerHandle);
        Assert.NotNull(root);
        var rows = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true));
        var result = new List<SelectionIdentity>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            try
            {
                if (row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
                {
                    result.Add(ReadSelectionIdentity(row));
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return result;
    }

    private static bool IsRealContentRow(SelectionIdentity identity) =>
        identity.ControlType.EndsWith(".DataItem", StringComparison.Ordinal) ||
        identity.ControlType.EndsWith(".ListItem", StringComparison.Ordinal) ||
        identity.DescendantText.Count >= 2;

    private static SelectionIdentity? WaitForSelectedRow(
        IntPtr taskManagerHandle,
        string expectedRuntimeId,
        TimeSpan timeout)
    {
        SelectionIdentity? selected = null;
        return WaitUntil(
            () =>
            {
                selected = ReadSelectableRows(taskManagerHandle)
                    .FirstOrDefault(row =>
                        string.Equals(row.RuntimeId, expectedRuntimeId, StringComparison.Ordinal) && row.IsSelected);
                return selected is not null;
            },
            timeout)
            ? selected
            : null;
    }

    private static bool TrySelectRow(IntPtr taskManagerHandle, string runtimeId)
    {
        var root = AutomationElement.FromHandle(taskManagerHandle);
        if (root is null)
        {
            return false;
        }

        var rows = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true));
        for (var index = 0; index < rows.Count; index++)
        {
            try
            {
                var identity = ReadSelectionIdentity(rows[index]);
                if (!string.Equals(identity.RuntimeId, runtimeId, StringComparison.Ordinal) ||
                    !rows[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject) ||
                    patternObject is not SelectionItemPattern pattern)
                {
                    continue;
                }

                pattern.Select();
                return true;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
            {
            }
        }

        return false;
    }

    private static SelectionIdentity ReadOverlaySelection(
        TaskManagerSearchWindow overlay,
        TaskManagerItem expected)
    {
        overlay.UpdateLayout();
        PumpDispatcherOnce();
        var root = AutomationElement.FromHandle(new WindowInteropHelper(overlay).Handle);
        Assert.NotNull(root);
        var list = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskManagerSearchResults"));
        Assert.NotNull(list);
        Assert.True(list.TryGetCurrentPattern(SelectionPattern.Pattern, out var patternObject));
        var selected = ((SelectionPattern)patternObject).Current.GetSelection();
        Assert.Single(selected);
        Assert.Equal(expected.Id, selected[0].Current.AutomationId);
        return new SelectionIdentity(
            expected.Id,
            selected[0].Current.Name,
            selected[0].Current.AutomationId,
            selected[0].Current.ControlType.ProgrammaticName,
            IsSelected: true,
            DescendantText: ReadDescendantText(selected[0]));
    }

    private static SelectionIdentity ReadSelectionIdentity(AutomationElement row)
    {
        var runtimeId = row.GetRuntimeId();
        var id = runtimeId is { Length: > 0 }
            ? string.Join(".", runtimeId)
            : $"{row.Current.AutomationId}|{row.Current.Name}";
        var isSelected = row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject) &&
            patternObject is SelectionItemPattern selection &&
            selection.Current.IsSelected;
        return new SelectionIdentity(
            id,
            row.Current.Name,
            row.Current.AutomationId,
            row.Current.ControlType.ProgrammaticName,
            isSelected,
            ReadDescendantText(row));
    }

    private static IReadOnlyList<string> ReadDescendantText(AutomationElement element)
    {
        var text = element.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var values = new List<string>(text.Count);
        for (var index = 0; index < text.Count; index++)
        {
            try
            {
                var value = text[index].Current.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            PumpDispatcherOnce();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        PumpDispatcherOnce();
        return condition();
    }

    private static void PumpDispatcherOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }

    private static string CaptureComposedEvidence(NativeRect first, NativeRect second, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        _ = DwmFlush();
        Thread.Sleep(150);

        var left = Math.Min(first.Left, second.Left) - 12;
        var top = Math.Min(first.Top, second.Top) - 12;
        var width = Math.Max(first.Right, second.Right) - left + 12;
        var height = Math.Max(first.Bottom, second.Bottom) - top + 12;
        Assert.InRange(width, 500, 8_000);
        Assert.InRange(height, 300, 4_000);
        var desktop = GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktop);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        Assert.NotEqual(IntPtr.Zero, memory);
        Assert.NotEqual(IntPtr.Zero, bitmap);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(
                memory,
                0,
                0,
                width,
                height,
                desktop,
                left,
                top,
                SourceCopy | CaptureBlt));
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var path = Path.Combine(directory, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
            stream.Flush();
            Assert.True(stream.Length > 10_000, $"Task Manager evidence screenshot was unexpectedly small: {path}");
            return path;
        }
        finally
        {
            _ = SelectObject(memory, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    private static void WriteEvidenceMetadata(
        string screenshotPath,
        bool taskManagerStartedByTest,
        IntPtr taskManagerHandle,
        uint taskManagerProcessId,
        NativeRect taskManagerBounds,
        IntPtr overlayHandle,
        NativeRect overlayBounds,
        string query,
        ProcessSecurity testProcessSecurity,
        ProcessSecurity taskManagerSecurity,
        string pagePreparation,
        TaskManagerItem firstOverlay,
        SelectionIdentity firstOverlayOracle,
        SelectionIdentity firstHost,
        TaskManagerItem secondOverlay,
        SelectionIdentity secondOverlayOracle,
        SelectionIdentity hostAfterPreview,
        SelectionIdentity confirmedHost)
    {
        var metadataPath = Path.ChangeExtension(screenshotPath, ".json");
        var metadata = new
        {
            schemaVersion = 1,
            passed = true,
            scenario = "real-system-task-manager-production-overlay-selection-sync",
            category = "ElevatedDesktopIntegration",
            realSystemTaskManager = true,
            fakeServiceUsed = false,
            skipped = false,
            taskManagerStartedByTest,
            query,
            pagePreparation,
            testProcessSecurity,
            taskManager = new
            {
                processName = GetWindowProcessName(taskManagerHandle),
                processId = taskManagerProcessId,
                windowClass = GetWindowClass(taskManagerHandle),
                windowHandle = taskManagerHandle.ToInt64(),
                security = taskManagerSecurity,
                bounds = EvidenceBounds(taskManagerBounds)
            },
            overlay = new
            {
                processName = Process.GetCurrentProcess().ProcessName,
                windowHandle = overlayHandle.ToInt64(),
                bounds = EvidenceBounds(overlayBounds)
            },
            transitions = new object[]
            {
                new
                {
                    action = "initial-selection",
                    overlaySelectedIdentity = EvidenceItem(firstOverlay),
                    overlayUiaSelectedIdentity = firstOverlayOracle,
                    taskManagerUiaSelectedIdentity = firstHost
                },
                new
                {
                    action = "MoveSelection(1)",
                    overlaySelectedIdentity = EvidenceItem(secondOverlay),
                    overlayUiaSelectedIdentity = secondOverlayOracle,
                    taskManagerUiaSelectedIdentity = hostAfterPreview,
                    hostSelectionChanged = false
                },
                new
                {
                    action = "ConfirmSelection()",
                    overlaySelectedIdentity = EvidenceItem(secondOverlay),
                    taskManagerUiaSelectedIdentity = confirmedHost,
                    hostSelectionChanged = true
                }
            },
            screenshot = Path.GetFileName(screenshotPath),
            screenshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshotPath)))
        };
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 1_024);
    }

    private static object EvidenceItem(TaskManagerItem item) => new
    {
        runtimeId = item.Id,
        item.Name,
        item.Details,
        item.IconPath
    };

    private static object EvidenceBounds(NativeRect bounds) => new
    {
        bounds.Left,
        bounds.Top,
        bounds.Right,
        bounds.Bottom,
        width = bounds.Right - bounds.Left,
        height = bounds.Bottom - bounds.Top
    };

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static ProcessSecurity ReadProcessSecurity(IntPtr processHandle)
    {
        Assert.True(OpenProcessToken(processHandle, TokenQuery, out var token));
        try
        {
            var elevationSize = Marshal.SizeOf<TokenElevation>();
            var elevationBuffer = Marshal.AllocHGlobal(elevationSize);
            try
            {
                Assert.True(GetTokenInformation(
                    token,
                    TokenElevationInformation,
                    elevationBuffer,
                    elevationSize,
                    out _));
                var elevation = Marshal.PtrToStructure<TokenElevation>(elevationBuffer);

                _ = GetTokenInformation(
                    token,
                    TokenIntegrityLevelInformation,
                    IntPtr.Zero,
                    0,
                    out var integritySize);
                Assert.True(integritySize > 0);
                var integrityBuffer = Marshal.AllocHGlobal(integritySize);
                try
                {
                    Assert.True(GetTokenInformation(
                        token,
                        TokenIntegrityLevelInformation,
                        integrityBuffer,
                        integritySize,
                        out _));
                    var sid = Marshal.ReadIntPtr(integrityBuffer);
                    var countPointer = GetSidSubAuthorityCount(sid);
                    Assert.NotEqual(IntPtr.Zero, countPointer);
                    var count = Marshal.ReadByte(countPointer);
                    Assert.True(count > 0);
                    var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                    Assert.NotEqual(IntPtr.Zero, ridPointer);
                    var rid = Marshal.ReadInt32(ridPointer);
                    return new ProcessSecurity(
                        elevation.TokenIsElevated != 0,
                        rid,
                        GetIntegrityName(rid));
                }
                finally
                {
                    Marshal.FreeHGlobal(integrityBuffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(elevationBuffer);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static string GetIntegrityName(int rid) => rid switch
    {
        < 0x1000 => "Untrusted",
        < 0x2000 => "Low",
        < 0x3000 => "Medium",
        < 0x4000 => "High",
        < 0x5000 => "System",
        _ => "Protected"
    };

    private static string GetWindowClass(IntPtr handle)
    {
        var buffer = new char[256];
        var length = GetClassName(handle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetWindowProcessName(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private sealed class OwnedTaskManagerWindow : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly int _processId;

        public OwnedTaskManagerWindow(IntPtr handle)
        {
            _handle = handle;
            _ = GetWindowThreadProcessId(handle, out var processId);
            _processId = unchecked((int)processId);
        }

        public void Dispose()
        {
            if (!IsWindow(_handle))
            {
                return;
            }

            _ = PostMessage(_handle, WmClose, IntPtr.Zero, IntPtr.Zero);
            if (WaitUntil(() => !IsWindow(_handle), TimeSpan.FromSeconds(5)))
            {
                return;
            }

            // Constructed only for a Task Manager process launched by this test.
            try
            {
                using var process = Process.GetProcessById(_processId);
                if (!process.HasExited)
                {
                    process.Kill();
                    _ = process.WaitForExit(5_000);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
    }

    private sealed record TaskManagerAcquisition(IntPtr Handle, bool StartedByTest);

    private sealed record ProcessSecurity(bool IsElevated, int IntegrityRid, string IntegrityName);

    private sealed record SelectionIdentity(
        string RuntimeId,
        string Name,
        string AutomationId,
        string ControlType,
        bool IsSelected,
        IReadOnlyList<string> DescendantText);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination,
        int xDestination,
        int yDestination,
        int width,
        int height,
        IntPtr source,
        int xSource,
        int ySource,
        uint operation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
