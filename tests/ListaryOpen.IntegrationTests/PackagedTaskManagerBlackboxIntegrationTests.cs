using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class PackagedTaskManagerBlackboxIntegrationTests
{
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToVsc = 0;
    private const ushort VkDown = 0x28;
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public async Task PackagedAppCapturesRealTaskManagerInputAndSynchronizesTwoRows()
    {
        await RunInStaAsync(() =>
        {
            Assert.True(
                IsCurrentProcessElevated(),
                "ElevatedPackagedBlackboxE2E must run in an elevated test process. " +
                "This mandatory real-system test fails instead of skipping or using a fake Task Manager.");

            var packageDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
            Assert.False(
                string.IsNullOrWhiteSpace(packageDirectory),
                "LISTARYOPEN_PACKAGE_DIR must identify the published package under test.");
            packageDirectory = Path.GetFullPath(packageDirectory);
            var packageExePath = Path.Combine(packageDirectory, "ListaryOpen.App.exe");
            var packageHookDllPath = Path.Combine(packageDirectory, "hooks", "x64", "ListaryOpen.Hook.dll");
            var packageHookRuntimePath = Path.Combine(packageDirectory, "hooks", "x64", "libunwind.dll");
            Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
            Assert.True(File.Exists(packageHookDllPath));
            Assert.True(File.Exists(packageHookRuntimePath));
            Assert.True(
                IsSingleInstanceMutexAvailable(),
                "ListaryOpen is already running. Close the existing instance before the packaged Task Manager E2E; " +
                "the test will not terminate a user-owned instance.");

            var packageExeSha256 = Sha256(packageExePath);
            var packageSha256 = ComputePackageHash(packageDirectory);
            using var packageData = PackageDataCleanup.RequireInitiallyAbsent(packageDirectory);
            var taskManager = AcquireTaskManagerWindow();
            using var ownedTaskManager = taskManager.StartedByTest
                ? new OwnedTaskManagerWindow(taskManager.Handle)
                : null;
            _ = ShowWindow(taskManager.Handle, SwRestore);
            Assert.True(IsWindowVisible(taskManager.Handle), "The real Task Manager window is not visible.");
            Assert.Equal("TaskManagerWindow", GetWindowClass(taskManager.Handle));
            Assert.Equal("Taskmgr", GetWindowProcessName(taskManager.Handle), ignoreCase: true);

            IReadOnlyList<ProjectedRow> projectedRows = Array.Empty<ProjectedRow>();
            ProjectedRow[] realRows = [];
            HashSet<string> realRowIds = new(StringComparer.Ordinal);
            var query = string.Empty;
            Assert.True(
                TaskManagerPageTestSupport.PrepareRowPage(
                    taskManager.Handle,
                    () =>
                    {
                        projectedRows = ReadProjectedRows(taskManager.Handle);
                        realRows = projectedRows.Where(IsRealContentRow).ToArray();
                        realRowIds = realRows.Select(row => row.RuntimeId).ToHashSet(StringComparer.Ordinal);
                        query = FindQueryWhoseFirstTwoResultsAreRealRows(projectedRows, realRowIds);
                        return realRows.Length >= 2 && !string.IsNullOrWhiteSpace(query);
                    },
                    out var pagePreparation),
                $"Task Manager did not expose two queryable, independently selectable content rows after '{pagePreparation}'.");
            Assert.True(
                realRows.Length >= 2,
                "The prepared Task Manager row page did not retain two selectable content rows.");
            Assert.False(
                string.IsNullOrWhiteSpace(query),
                "No short query could produce two leading selectable real Task Manager rows.");

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Process? appProcess = null;
            E2ePipeClient? control = null;
            var shutdownSucceeded = false;
            try
            {
                appProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = packageExePath,
                    Arguments = "--listary-e2e-control=" + nonce,
                    WorkingDirectory = packageDirectory,
                    UseShellExecute = false
                });
                Assert.NotNull(appProcess);

                control = E2ePipeClient.Connect(
                    "ListaryOpen.E2E." + nonce,
                    appProcess,
                    TimeSpan.FromSeconds(45));
                Assert.Equal($"Ready {appProcess.Id}", control.Exchange(nonce + " Ready"));
                Assert.Equal("Ok", control.Exchange(nonce + " AllowInjectedInput"));

                // Task Manager refreshes its UIA rows continuously. Rebuild the
                // oracle with the same production provider and matching rules as
                // the packaged app. Raw UIA titles can differ from canonical
                // process aliases and previously produced queries with no results.
                IReadOnlyList<TaskManagerItem> productItems = Array.Empty<TaskManagerItem>();
                IReadOnlyList<TaskManagerItem> productResults = Array.Empty<TaskManagerItem>();
                using var oracleService = new TaskManagerAutomationService();
                Assert.True(
                    WaitUntil(
                        () =>
                        {
                            projectedRows = ReadProjectedRows(taskManager.Handle);
                            realRows = projectedRows.Where(IsRealContentRow).ToArray();
                            realRowIds = realRows.Select(row => row.RuntimeId).ToHashSet(StringComparer.Ordinal);
                            productItems = oracleService.GetItemsAsync(taskManager.Handle, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            query = FindProductionQuery(productItems, realRowIds, out productResults);
                            return realRows.Length >= 2 && productResults.Count >= 2;
                        },
                        TimeSpan.FromSeconds(5)),
                    "Task Manager rows did not remain queryable after the packaged app completed startup.");
                var focusRow = realRows.First(row => row.RuntimeId == productResults[0].Id);

                Assert.True(
                    DesktopWindowActivator.TryActivate(taskManager.Handle, TimeSpan.FromSeconds(5)),
                    "Task Manager could not be activated before selecting its initial row.");
                Assert.True(
                    WaitUntil(
                        () => TrySelectTaskManagerRow(taskManager.Handle, focusRow.RuntimeId),
                        TimeSpan.FromSeconds(5)),
                    $"Task Manager row '{focusRow.RuntimeId}' never became enabled and selectable.");
                var focusedTaskManagerRow = WaitForSelectedTaskManagerRow(
                    taskManager.Handle,
                    focusRow.RuntimeId,
                    TimeSpan.FromSeconds(5));
                Assert.NotNull(focusedTaskManagerRow);
                Assert.True(
                    GetForegroundWindow() == taskManager.Handle &&
                    !TaskManagerAutomationService.IsNativeTextInputFocused(taskManager.Handle),
                    "Task Manager did not retain foreground with a selected content row and no native text input before SendInput.");

                SendVirtualKeyText(query);
                var overlayHandle = WaitForTopLevelWindow(
                    appProcess.Id,
                    "Task Manager Search",
                    TimeSpan.FromSeconds(12));
                Assert.NotEqual(IntPtr.Zero, overlayHandle);
                Assert.True(IsWindowVisible(overlayHandle));
                var overlayRoot = AutomationElement.FromHandle(overlayHandle);
                Assert.NotNull(overlayRoot);
                var queryElement = WaitForAutomationElement(
                    overlayHandle,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "TaskManagerSearchQuery"),
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(queryElement);
                Assert.True(queryElement.TryGetCurrentPattern(ValuePattern.Pattern, out var queryPatternObject));
                Assert.Equal(query, ((ValuePattern)queryPatternObject).Current.Value);
                var resultsElement = WaitForAutomationElement(
                    overlayHandle,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "TaskManagerSearchResults"),
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(resultsElement);

                var initialOverlaySelection = WaitForOverlaySelection(
                    overlayHandle,
                    previousRuntimeId: null,
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(initialOverlaySelection);
                var initialTaskManagerSelection = WaitForSelectedTaskManagerRow(
                    taskManager.Handle,
                    focusRow.RuntimeId,
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(initialTaskManagerSelection);
                Assert.Equal(focusRow.RuntimeId, initialTaskManagerSelection.RuntimeId);
                Assert.True(IsRealContentRow(initialTaskManagerSelection));

                SendVirtualKey(VkDown);
                var movedOverlaySelection = WaitForOverlaySelection(
                    overlayHandle,
                    initialOverlaySelection.RuntimeId,
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(movedOverlaySelection);
                Assert.NotEqual(initialOverlaySelection.RuntimeId, movedOverlaySelection.RuntimeId);
                var hostSelectionAfterPreview = WaitForSelectedTaskManagerRow(
                    taskManager.Handle,
                    focusRow.RuntimeId,
                    TimeSpan.FromSeconds(3));
                Assert.NotNull(hostSelectionAfterPreview);
                Assert.Null(WaitForSelectedTaskManagerRow(
                    taskManager.Handle,
                    movedOverlaySelection.RuntimeId,
                    TimeSpan.FromMilliseconds(300)));
                Assert.True(IsRealContentRow(hostSelectionAfterPreview));

                var screenshotPath = CaptureComposedEvidence(
                    taskManager.Handle,
                    overlayHandle,
                    "29-packaged-real-task-manager-overlay");
                SendVirtualKey(VkReturn);
                Assert.True(
                    WaitUntil(() => !IsWindowVisible(overlayHandle), TimeSpan.FromSeconds(8)),
                    "Confirming the Task Manager result did not dismiss the overlay.");
                var confirmedTaskManagerSelection = WaitForSelectedTaskManagerRow(
                    taskManager.Handle,
                    movedOverlaySelection.RuntimeId,
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(confirmedTaskManagerSelection);

                WriteEvidenceMetadata(
                    screenshotPath,
                    packageDirectory,
                    packageExePath,
                    packageExeSha256,
                    packageSha256,
                    appProcess.Id,
                    taskManager,
                    overlayHandle,
                    query,
                    pagePreparation,
                    queryElement,
                    initialOverlaySelection,
                    initialTaskManagerSelection,
                    movedOverlaySelection,
                    hostSelectionAfterPreview,
                    confirmedTaskManagerSelection);

                Assert.True(IsWindow(taskManager.Handle), "Dismissing the overlay unexpectedly closed Task Manager.");

                var shutdownStopwatch = Stopwatch.StartNew();
                Assert.Equal("Ok", control.Exchange(nonce + " Shutdown"));
                shutdownSucceeded = appProcess.WaitForExit(15_000);
                Assert.True(shutdownSucceeded, "The packaged app did not exit after its authenticated Shutdown request.");
                Assert.Equal(0, appProcess.ExitCode);
                var nativeModulesUnloaded = WaitUntil(
                    () =>
                        !IsModuleLoadedInWindowProcess(taskManager.Handle, packageHookDllPath) &&
                        !IsModuleLoadedInWindowProcess(taskManager.Handle, packageHookRuntimePath),
                    TimeSpan.FromSeconds(10));
                Assert.True(
                    nativeModulesUnloaded,
                    "The published app exited but its native hook DLL/runtime remained loaded in Task Manager. " +
                    $"Elapsed since Shutdown={shutdownStopwatch.Elapsed}; " +
                    $"HookDllLoaded={IsModuleLoadedInWindowProcess(taskManager.Handle, packageHookDllPath)}; " +
                    $"RuntimeLoaded={IsModuleLoadedInWindowProcess(taskManager.Handle, packageHookRuntimePath)}; " +
                    $"package HookHost processes={DescribePackageHookHosts(packageDirectory)}.");
                AppendNativeUnloadEvidence(
                    Path.ChangeExtension(screenshotPath, ".json"),
                    "Taskmgr",
                    GetWindowProcessId(taskManager.Handle),
                    packageHookDllPath,
                    packageHookRuntimePath,
                    shutdownStopwatch.Elapsed);
            }
            finally
            {
                if (appProcess is { HasExited: false })
                {
                    if (!shutdownSucceeded && control is not null)
                    {
                        try
                        {
                            _ = control.Exchange(nonce + " Shutdown");
                            shutdownSucceeded = appProcess.WaitForExit(10_000);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidOperationException)
                        {
                        }
                    }

                    if (!appProcess.HasExited)
                    {
                        appProcess.Kill(entireProcessTree: true);
                        _ = appProcess.WaitForExit(10_000);
                    }
                }

                control?.Dispose();
                appProcess?.Dispose();
            }

            Assert.Equal(packageExeSha256, Sha256(packageExePath));
        });
    }

    private static Task RunInStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "ListaryOpen packaged real Task Manager E2E"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSingleInstanceMutexAvailable()
    {
        using var mutex = new Mutex(initiallyOwned: false, @"Local\ListaryOpen.SingleInstance");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            return acquired;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
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
                "An existing Taskmgr process had no attachable window; the test refuses to launch or close another instance.");
            return new TaskManagerAcquisition(existing, StartedByTest: false);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "taskmgr.exe",
            UseShellExecute = true
        });
        Assert.NotNull(process);
        var launched = IntPtr.Zero;
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

    private static IReadOnlyList<ProjectedRow> ReadProjectedRows(IntPtr taskManagerWindow)
    {
        var root = AutomationElement.FromHandle(taskManagerWindow);
        Assert.NotNull(root);
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));
        var candidates = root.FindAll(TreeScope.Descendants, condition);
        var rows = new List<ProjectedRow>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            try
            {
                var element = candidates[index];
                if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
                {
                    continue;
                }

                var text = ReadDescendantText(element);
                if (element.Current.ControlType == ControlType.Custom && text.Count < 2)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(element.Current.Name)
                    ? text.FirstOrDefault() ?? string.Empty
                    : element.Current.Name.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var runtimeId = RuntimeId(element);
                var details = string.Join(
                    " · ",
                    text.Where(value => !string.Equals(value, name, StringComparison.OrdinalIgnoreCase)));
                rows.Add(new ProjectedRow(
                    runtimeId,
                    name,
                    details,
                    element.Current.ControlType.ProgrammaticName,
                    text));
            }
            catch (Exception exception) when (IsExpectedAutomationException(exception))
            {
            }
        }

        return rows
            .GroupBy(row => NormalizeProcessName(row.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsRealContentRow(ProjectedRow row) =>
        row.ControlType.EndsWith(".DataItem", StringComparison.Ordinal) ||
        row.ControlType.EndsWith(".ListItem", StringComparison.Ordinal) ||
        row.DescendantText.Count >= 2;

    private static bool IsRealContentRow(SelectionIdentity row) =>
        row.ControlType.EndsWith(".DataItem", StringComparison.Ordinal) ||
        row.ControlType.EndsWith(".ListItem", StringComparison.Ordinal) ||
        row.ControlType.EndsWith(".TreeItem", StringComparison.Ordinal) ||
        row.DescendantText.Count >= 2;

    private static string FindQueryWhoseFirstTwoResultsAreRealRows(
        IReadOnlyList<ProjectedRow> rows,
        IReadOnlySet<string> realRowIds)
    {
        var candidates = rows
            .Where(row => realRowIds.Contains(row.RuntimeId))
            .SelectMany(row => CandidateQueries(row.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Length)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var results = ProjectResults(rows, candidate);
            if (results.Count >= 2 &&
                realRowIds.Contains(results[0].RuntimeId) &&
                realRowIds.Contains(results[1].RuntimeId))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<ProjectedRow> ProjectResults(IReadOnlyList<ProjectedRow> rows, string query) =>
        rows.Select(row => (Row: row, Score: MatchScore(row, query)))
            .Where(candidate => !double.IsNegativeInfinity(candidate.Score))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Row.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select(candidate => candidate.Row)
            .ToArray();

    private static string FindProductionQuery(
        IReadOnlyList<TaskManagerItem> items,
        IReadOnlySet<string> realRowIds,
        out IReadOnlyList<TaskManagerItem> results)
    {
        var candidates = items
            .Where(item => realRowIds.Contains(item.Id))
            .SelectMany(item => CandidateQueries(item.Name)
                .Concat(item.SearchText.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .SelectMany(CandidateQueries)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Length)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var matches = items
                .Select(item => (Item: item, Score: TaskManagerSearchViewModel.MatchScore(item, candidate)))
                .Where(value => !double.IsNegativeInfinity(value.Score))
                .OrderByDescending(value => value.Score)
                .ThenBy(value => value.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .Select(value => value.Item)
                .ToArray();
            if (matches.Length >= 2 &&
                realRowIds.Contains(matches[0].Id) &&
                realRowIds.Contains(matches[1].Id))
            {
                results = matches;
                return candidate;
            }
        }

        results = Array.Empty<TaskManagerItem>();
        return string.Empty;
    }

    private static double MatchScore(ProjectedRow row, string query)
    {
        if (string.Equals(row.Name, query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (row.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 800;
        var nameIndex = row.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (nameIndex >= 0) return 600 - nameIndex;
        var detailsIndex = row.Details.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (detailsIndex >= 0) return 300 - detailsIndex;
        var queryIndex = 0;
        for (var index = 0; index < row.Name.Length && queryIndex < query.Length; index++)
        {
            if (char.ToUpperInvariant(row.Name[index]) == char.ToUpperInvariant(query[queryIndex]))
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length ? 100 - row.Name.Length : double.NegativeInfinity;
    }

    private static IEnumerable<string> CandidateQueries(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            // Validate name search with stable text. CPU/memory digits in Task
            // Manager accessibility labels can change between the oracle and app
            // snapshots and leave an otherwise healthy overlay with no matches.
            if (char.IsLetter(value[index]))
            {
                yield return char.ToLowerInvariant(value[index]).ToString();
            }
        }
    }

    private static AutomationElement FindTaskManagerRow(IntPtr taskManagerWindow, string runtimeId)
    {
        var root = AutomationElement.FromHandle(taskManagerWindow);
        Assert.NotNull(root);
        var rows = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true));
        for (var index = 0; index < rows.Count; index++)
        {
            try
            {
                if (string.Equals(RuntimeId(rows[index]), runtimeId, StringComparison.Ordinal))
                {
                    return rows[index];
                }
            }
            catch (Exception exception) when (IsExpectedAutomationException(exception))
            {
            }
        }

        throw new InvalidOperationException($"Real Task Manager row '{runtimeId}' was no longer available.");
    }

    private static SelectionIdentity? WaitForSelectedTaskManagerRow(
        IntPtr taskManagerWindow,
        string expectedRuntimeId,
        TimeSpan timeout)
    {
        SelectionIdentity? result = null;
        return WaitUntil(
            () =>
            {
                try
                {
                    var row = FindTaskManagerRow(taskManagerWindow, expectedRuntimeId);
                    if (!row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject) ||
                        patternObject is not SelectionItemPattern pattern ||
                        !pattern.Current.IsSelected)
                    {
                        return false;
                    }

                    result = ReadSelectionIdentity(row);
                    return true;
                }
                catch (Exception exception) when (IsExpectedAutomationException(exception) || exception is InvalidOperationException)
                {
                    return false;
                }
            },
            timeout)
            ? result
            : null;
    }

    private static SelectionIdentity? WaitForOverlaySelection(
        IntPtr overlayWindow,
        string? previousRuntimeId,
        TimeSpan timeout)
    {
        SelectionIdentity? result = null;
        return WaitUntil(
            () =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(overlayWindow);
                    var results = root?.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.AutomationIdProperty,
                            "TaskManagerSearchResults"));
                    if (results is null)
                    {
                        return false;
                    }

                    if (!results.TryGetCurrentPattern(SelectionPattern.Pattern, out var patternObject))
                    {
                        return false;
                    }

                    var selected = ((SelectionPattern)patternObject).Current.GetSelection();
                    if (selected.Length != 1)
                    {
                        return false;
                    }

                    var runtimeId = selected[0].Current.AutomationId;
                    if (string.IsNullOrWhiteSpace(runtimeId))
                    {
                        return false;
                    }

                    if (string.Equals(runtimeId, previousRuntimeId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    result = new SelectionIdentity(
                        runtimeId,
                        selected[0].Current.Name,
                        runtimeId,
                        selected[0].Current.ControlType.ProgrammaticName,
                        IsSelected: true,
                        ReadDescendantText(selected[0]));
                    return true;
                }
                catch (Exception exception) when (IsExpectedAutomationException(exception))
                {
                    return false;
                }
            },
            timeout)
            ? result
            : null;
    }

    private static SelectionIdentity ReadSelectionIdentity(AutomationElement element) => new(
        RuntimeId(element),
        element.Current.Name,
        element.Current.AutomationId,
        element.Current.ControlType.ProgrammaticName,
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
        ReadDescendantText(element));

    private static IReadOnlyList<string> ReadDescendantText(AutomationElement element)
    {
        var elements = element.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var result = new List<string>(elements.Count);
        for (var index = 0; index < elements.Count; index++)
        {
            try
            {
                var value = elements[index].Current.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
            catch (Exception exception) when (IsExpectedAutomationException(exception))
            {
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string RuntimeId(AutomationElement element)
    {
        var runtimeId = element.GetRuntimeId();
        return runtimeId is { Length: > 0 }
            ? string.Join(".", runtimeId)
            : $"{element.Current.AutomationId}|{element.Current.Name}";
    }

    private static string NormalizeProcessName(string name)
    {
        var normalized = name.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4].TrimEnd();
        }

        var countStart = normalized.LastIndexOf(" (", StringComparison.Ordinal);
        if (countStart >= 0 && normalized.EndsWith(')') &&
            int.TryParse(normalized.AsSpan(countStart + 2, normalized.Length - countStart - 3), out _))
        {
            normalized = normalized[..countStart].TrimEnd();
        }

        return normalized;
    }

    private static bool IsExpectedAutomationException(Exception exception) => exception is
        ArgumentException or ElementNotAvailableException or ElementNotEnabledException or
        InvalidOperationException or COMException or UnauthorizedAccessException;

    private static bool TrySelectTaskManagerRow(IntPtr taskManagerWindow, string runtimeId)
    {
        try
        {
            var row = FindTaskManagerRow(taskManagerWindow, runtimeId);
            if (!row.Current.IsEnabled ||
                !row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPatternObject))
            {
                return false;
            }

            if (row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollPatternObject) &&
                scrollPatternObject is ScrollItemPattern scrollPattern)
            {
                scrollPattern.ScrollIntoView();
            }
            ((SelectionItemPattern)selectionPatternObject).Select();
            ClickAutomationElement(row);
            try { row.SetFocus(); }
            catch (Exception exception) when (IsExpectedAutomationException(exception)) { }
            return true;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static void ClickAutomationElement(AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        Assert.False(bounds.IsEmpty, $"Automation element '{element.Current.Name}' has no clickable bounds.");
        Assert.True(SetCursorPos(
            checked((int)Math.Round(bounds.Left + bounds.Width / 2)),
            checked((int)Math.Round(bounds.Top + bounds.Height / 2))));
        var inputs = new[]
        {
            new NativeInput { Type = InputMouse, Union = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = MouseEventLeftDown } } },
            new NativeInput { Type = InputMouse, Union = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = MouseEventLeftUp } } }
        };
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static void SendVirtualKeyText(string text)
    {
        foreach (var character in text)
        {
            var key = VkKeyScan(character);
            Assert.NotEqual(-1, key);
            Assert.Equal(0, (key >> 8) & 0xFF);
            SendVirtualKey(unchecked((ushort)(key & 0xFF)));
        }
    }

    private static void SendVirtualKey(ushort virtualKey)
    {
        var scanCode = unchecked((ushort)MapVirtualKey(virtualKey, MapVkToVsc));
        var inputs = new[]
        {
            new NativeInput
            {
                Type = InputKeyboard,
                Union = new NativeInputUnion { Keyboard = new NativeKeyboardInput { VirtualKey = virtualKey, ScanCode = scanCode } }
            },
            new NativeInput
            {
                Type = InputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput { VirtualKey = virtualKey, ScanCode = scanCode, Flags = KeyEventKeyUp }
                }
            }
        };
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static IntPtr WaitForTopLevelWindow(int processId, string title, TimeSpan timeout)
    {
        var result = IntPtr.Zero;
        return WaitUntil(
            () =>
            {
                result = IntPtr.Zero;
                _ = EnumWindows(
                    (window, parameter) =>
                    {
                        _ = GetWindowThreadProcessId(window, out var windowProcessId);
                        if (windowProcessId == unchecked((uint)processId) &&
                            IsWindowVisible(window) &&
                            string.Equals(GetWindowText(window), title, StringComparison.Ordinal))
                        {
                            result = window;
                            return false;
                        }

                        return true;
                    },
                    IntPtr.Zero);
                return result != IntPtr.Zero;
            },
            timeout)
            ? result
            : IntPtr.Zero;
    }

    private static AutomationElement? WaitForAutomationElement(
        IntPtr rootWindow,
        System.Windows.Automation.Condition condition,
        TimeSpan timeout)
    {
        AutomationElement? result = null;
        return WaitUntil(
            () =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(rootWindow);
                    if (root is null)
                    {
                        return false;
                    }

                    result = root.FindFirst(TreeScope.Descendants, condition);
                    return result is not null;
                }
                catch (Exception exception) when (IsExpectedAutomationException(exception))
                {
                    return false;
                }
            },
            timeout)
            ? result
            : null;
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

    private static string CaptureComposedEvidence(IntPtr taskManager, IntPtr overlay, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Assert.True(IsWindowVisible(taskManager));
        Assert.True(IsWindowVisible(overlay));
        Assert.True(GetWindowRect(taskManager, out var first));
        Assert.True(GetWindowRect(overlay, out var second));
        var bounds = new NativeRect
        {
            Left = Math.Min(first.Left, second.Left),
            Top = Math.Min(first.Top, second.Top),
            Right = Math.Max(first.Right, second.Right),
            Bottom = Math.Max(first.Bottom, second.Bottom)
        };
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, 500, 8_000);
        Assert.InRange(height, 300, 4_500);
        _ = DwmFlush();
        Thread.Sleep(250);
        var desktop = GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktop);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        Assert.NotEqual(IntPtr.Zero, memory);
        Assert.NotEqual(IntPtr.Zero, bitmap);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(memory, 0, 0, width, height, desktop, bounds.Left, bounds.Top, 0x00CC0020));
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            var path = Path.Combine(directory, name + ".png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            stream.Flush();
            Assert.True(stream.Length > 10_000, $"Packaged Task Manager screenshot was unexpectedly small: {path}");
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
        string packageDirectory,
        string packageExePath,
        string packageExeSha256,
        string packageSha256,
        int appProcessId,
        TaskManagerAcquisition taskManager,
        IntPtr overlay,
        string query,
        string pagePreparation,
        AutomationElement queryElement,
        SelectionIdentity initialOverlay,
        SelectionIdentity initialTaskManager,
        SelectionIdentity movedOverlay,
        SelectionIdentity hostAfterPreview,
        SelectionIdentity confirmedTaskManager)
    {
        Assert.True(GetWindowRect(taskManager.Handle, out var taskBounds));
        Assert.True(GetWindowRect(overlay, out var overlayBounds));
        var metadata = new
        {
            schemaVersion = 1,
            passed = true,
            category = "ElevatedPackagedBlackboxE2E",
            scenario = "packaged-elevated-app-real-task-manager-type-search",
            elevatedTestProcess = true,
            packageDirectory,
            packageExePath,
            packageExeSha256,
            packageSha256,
            appProcessId,
            taskManager = new
            {
                realSystemTaskManager = true,
                startedByTest = taskManager.StartedByTest,
                processName = GetWindowProcessName(taskManager.Handle),
                className = GetWindowClass(taskManager.Handle),
                handle = taskManager.Handle.ToInt64(),
                bounds = EvidenceBounds(taskBounds)
            },
            overlay = new
            {
                handle = overlay.ToInt64(),
                title = GetWindowText(overlay),
                bounds = EvidenceBounds(overlayBounds)
            },
            interactionPath = "TaskManagerContentRow-WindowsSendInput-GlobalLowLevelHook",
            e2eControlCommands = new[] { "Ready", "AllowInjectedInput", "Shutdown" },
            query,
            pagePreparation,
            queryOracle = new
            {
                queryElement.Current.AutomationId,
                queryElement.Current.Name,
                value = ((ValuePattern)queryElement.GetCurrentPattern(ValuePattern.Pattern)).Current.Value
            },
            transitions = new[]
            {
                new { action = "initial", overlaySelectedIdentity = initialOverlay, taskManagerSelectedIdentity = initialTaskManager },
                new { action = "SendInput(Down)", overlaySelectedIdentity = movedOverlay, taskManagerSelectedIdentity = hostAfterPreview },
                new { action = "SendInput(Enter)", overlaySelectedIdentity = movedOverlay, taskManagerSelectedIdentity = confirmedTaskManager }
            },
            enterConfirmedAndDismissedOverlay = true,
            fakeServiceUsed = false,
            skipped = false,
            screenshot = Path.GetFileName(screenshotPath),
            screenshotSha256 = Sha256(screenshotPath)
        };
        var metadataPath = Path.ChangeExtension(screenshotPath, ".json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 1_024);
    }

    private static object EvidenceBounds(NativeRect bounds) => new
    {
        bounds.Left,
        bounds.Top,
        bounds.Right,
        bounds.Bottom,
        width = bounds.Right - bounds.Left,
        height = bounds.Bottom - bounds.Top
    };

    private static string ComputePackageHash(string packageDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
                     .Where(file => !Path.GetRelativePath(packageDirectory, file)
                         .StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => Path.GetRelativePath(packageDirectory, file), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            using var stream = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, count));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void AppendNativeUnloadEvidence(
        string metadataPath,
        string targetProcess,
        long targetProcessId,
        string hookDllPath,
        string hookRuntimePath,
        TimeSpan elapsed)
    {
        var root = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject()
            ?? throw new InvalidDataException($"Evidence metadata is invalid: {metadataPath}");
        root["nativeUnloadOracle"] = new JsonObject
        {
            ["targetProcess"] = targetProcess,
            ["targetProcessId"] = targetProcessId,
            ["hookDllPath"] = Path.GetFullPath(hookDllPath),
            ["hookRuntimePath"] = Path.GetFullPath(hookRuntimePath),
            ["authenticatedShutdownCompleted"] = true,
            ["unloadedAfterAuthenticatedShutdown"] = true,
            ["waitTimeoutMs"] = 10_000,
            ["observedElapsedMs"] = Math.Ceiling(elapsed.TotalMilliseconds)
        };
        File.WriteAllText(metadataPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static int GetWindowProcessId(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return unchecked((int)processId);
    }

    private static bool IsModuleLoadedInWindowProcess(IntPtr window, string expectedPath)
    {
        var processId = GetWindowProcessId(window);
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(
                    Path.GetFullPath(module.FileName),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The long-lived real Task Manager is expected to remain present.
            // An unreadable module list cannot prove unload and must fail closed.
            return IsWindow(window);
        }
    }

    private static string DescribePackageHookHosts(string packageDirectory)
    {
        var descriptions = new List<string>();
        foreach (var process in Process.GetProcessesByName("ListaryOpen.HookHost"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName ?? "<unknown>";
                    if (Path.GetFullPath(path).StartsWith(
                            Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        descriptions.Add($"{process.Id}:{path}");
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    descriptions.Add($"{process.Id}:<unreadable>");
                }
            }
        }

        return descriptions.Count == 0 ? "none" : string.Join(", ", descriptions);
    }

    private static string GetWindowProcessName(IntPtr window)
    {
        var processId = GetWindowProcessId(window);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string GetWindowClass(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetWindowText(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private sealed class E2ePipeClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private E2ePipeClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 256, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
        }

        public static E2ePipeClient Connect(string pipeName, Process appProcess, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            Exception? lastError = null;
            while (stopwatch.Elapsed < timeout)
            {
                appProcess.Refresh();
                if (appProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The packaged app exited with code {appProcess.ExitCode} before its E2E endpoint became ready. " +
                        "An existing ListaryOpen instance or startup failure is not accepted.",
                        lastError);
                }

                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                try
                {
                    pipe.Connect(750);
                    return new E2ePipeClient(pipe);
                }
                catch (Exception exception) when (exception is TimeoutException or IOException)
                {
                    lastError = exception;
                    pipe.Dispose();
                }
            }

            throw new TimeoutException($"The packaged app did not create its E2E endpoint within {timeout}.", lastError);
        }

        public string Exchange(string request)
        {
            Assert.DoesNotContain('\r', request);
            Assert.DoesNotContain('\n', request);
            _writer.WriteLine(request);
            return _reader.ReadLine() ?? throw new IOException("The packaged app closed its E2E pipe without a response.");
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _pipe.Dispose();
        }
    }

    private sealed class PackageDataCleanup : IDisposable
    {
        private readonly string _packageDirectory;
        private readonly string _dataDirectory;

        private PackageDataCleanup(string packageDirectory, string dataDirectory)
        {
            _packageDirectory = packageDirectory;
            _dataDirectory = dataDirectory;
        }

        public static PackageDataCleanup RequireInitiallyAbsent(string packageDirectory)
        {
            var normalizedPackage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
            var dataDirectory = Path.GetFullPath(Path.Combine(normalizedPackage, "data"));
            Assert.Equal(normalizedPackage, Path.GetDirectoryName(dataDirectory), ignoreCase: true);
            Assert.False(
                Directory.Exists(dataDirectory),
                $"The published package already contains runtime data: {dataDirectory}. Use a clean package directory.");
            return new PackageDataCleanup(normalizedPackage, dataDirectory);
        }

        public void Dispose()
        {
            if (!Directory.Exists(_dataDirectory))
            {
                return;
            }

            var resolvedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(_dataDirectory)!));
            if (!string.Equals(resolvedParent, _packageDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to remove package data outside the package: {_dataDirectory}");
            }

            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private sealed class OwnedTaskManagerWindow : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly int _processId;

        public OwnedTaskManagerWindow(IntPtr handle)
        {
            _handle = handle;
            _processId = GetWindowProcessId(handle);
        }

        public void Dispose()
        {
            if (IsWindow(_handle))
            {
                _ = PostMessage(_handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                if (WaitUntil(() => !IsWindow(_handle), TimeSpan.FromSeconds(5)))
                {
                    return;
                }
            }

            // This wrapper is constructed only when AcquireTaskManagerWindow
            // started the process for this test. Never apply this cleanup to a
            // pre-existing user Task Manager instance.
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

    private sealed record ProjectedRow(
        string RuntimeId,
        string Name,
        string Details,
        string ControlType,
        IReadOnlyList<string> DescendantText);

    private sealed record SelectionIdentity(
        string RuntimeId,
        string Name,
        string AutomationId,
        string ControlType,
        bool IsSelected,
        IReadOnlyList<string> DescendantText);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScan(char character);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int xDestination, int yDestination, int width, int height, IntPtr source, int xSource, int ySource, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
