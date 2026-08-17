using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class RealExplorerIntegrationTests
{
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MapVkToVsc = 0;

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task ProductionTrackerAndSelectionServiceDriveARealSystemExplorerWindow()
    {
        await RunInStaAsync(() =>
        {
            using var directory = TemporaryExplorerDirectory.Create();
            var selectedPath = Path.Combine(directory.Path, "00-listary-selected-marker.txt");
            var unselectedPath = Path.Combine(directory.Path, "01-listary-control-item.txt");
            File.WriteAllText(selectedPath, "Selected by the production ExplorerSelectionService.");
            File.WriteAllText(unselectedPath, "This control item must remain unselected.");

            var existingExplorerHandles = EnumerateExplorerWindows()
                .Select(window => window.Handle)
                .ToHashSet();
            LaunchExplorer(directory.Path);

            var ownedWindow = WaitForExplorerWindow(directory.Path, TimeSpan.FromSeconds(15));
            Assert.NotNull(ownedWindow);
            Assert.DoesNotContain(
                ownedWindow.Handle,
                existingExplorerHandles);

            using var cleanup = new OwnedExplorerWindow(ownedWindow.Handle, directory.Path);
            Assert.Equal("CabinetWClass", GetWindowClass(ownedWindow.Handle));
            Assert.True(IsWindowVisible(ownedWindow.Handle), "The real Explorer test window was not visible.");
            Assert.True(ShowWindow(ownedWindow.Handle, SwRestore));
            _ = SetForegroundWindow(ownedWindow.Handle);
            Assert.True(
                WaitUntil(
                    () => GetForegroundWindow() == ownedWindow.Handle,
                    TimeSpan.FromSeconds(5)),
                "The real Explorer test window did not become foreground.");

            var tracker = new ExplorerTracker();
            Assert.True(
                WaitUntil(
                    () =>
                    {
                        tracker.ObserveForegroundExplorerFolder();
                        return tracker.GetFolderCandidates().Any(candidate =>
                            candidate.WindowHandle == ownedWindow.Handle &&
                            PathsEqual(candidate.FolderPath, directory.Path));
                    },
                    TimeSpan.FromSeconds(8)),
                "The production ExplorerTracker did not discover the real system Explorer window.");
            Assert.True(PathsEqual(directory.Path, tracker.LastFolder));

            using var selectionService = new ExplorerSelectionService();
            Assert.True(
                selectionService.TrySelectItem(ownedWindow.Handle, directory.Path, selectedPath),
                "The production ExplorerSelectionService rejected the real Explorer selection request.");

            Assert.True(
                WaitUntil(
                    () => ReadSelectedPaths(ownedWindow.Handle, directory.Path)
                        .Any(path => PathsEqual(path, selectedPath)),
                    TimeSpan.FromSeconds(8)),
                "An independent Shell COM read did not observe the requested item selection.");

            var shellSelectedPaths = ReadSelectedPaths(ownedWindow.Handle, directory.Path);
            Assert.Contains(shellSelectedPaths, path => PathsEqual(path, selectedPath));
            Assert.DoesNotContain(shellSelectedPaths, path => PathsEqual(path, unselectedPath));

            var markerElement = WaitForMarkerElement(ownedWindow.Handle, Path.GetFileName(selectedPath));
            Assert.NotNull(markerElement);
            Assert.True(
                markerElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern),
                "UI Automation did not expose SelectionItemPattern for the visible Explorer marker.");
            Assert.True(
                ((SelectionItemPattern)selectionPattern).Current.IsSelected,
                "UI Automation reported that the visible Explorer marker was not selected.");

            var evidence = CaptureEvidence(ownedWindow.Handle, "27-real-explorer-selection");
            WriteEvidenceMetadata(
                evidence,
                ownedWindow.Handle,
                directory.Path,
                selectedPath,
                tracker,
                shellSelectedPaths,
                markerElement);
        });
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task ProductionNavigationServiceReusesARealExplorerWindow()
    {
        await RunInStaAsync(() =>
        {
            using var directory = TemporaryExplorerDirectory.Create();
            var navigationTarget = Directory.CreateDirectory(Path.Combine(directory.Path, "target"));
            var existingExplorerHandles = EnumerateExplorerWindows()
                .Select(window => window.Handle)
                .ToHashSet();
            LaunchExplorer(directory.Path);
            var explorer = WaitForExplorerWindow(directory.Path, TimeSpan.FromSeconds(15));
            Assert.NotNull(explorer);
            Assert.DoesNotContain(explorer.Handle, existingExplorerHandles);
            using var cleanup = new OwnedExplorerWindow(explorer.Handle, directory.Path);
            var navigationService = new ExplorerNavigationService();

            try
            {
#pragma warning disable xUnit1031 // The service completes on its own dedicated STA worker.
                Assert.True(
                    navigationService.NavigateToFolderAsync(explorer.Handle, navigationTarget.FullName)
                        .GetAwaiter()
                        .GetResult());
#pragma warning restore xUnit1031
                Assert.True(
                    WaitUntil(
                        () => EnumerateExplorerWindows().Any(window =>
                            window.Handle == explorer.Handle &&
                            PathsEqual(window.FolderPath, navigationTarget.FullName)),
                        TimeSpan.FromSeconds(8)),
                    "The requested folder was not shown in the original Explorer HWND.");
                Assert.DoesNotContain(
                    EnumerateExplorerWindows(),
                    window => !existingExplorerHandles.Contains(window.Handle) &&
                        window.Handle != explorer.Handle &&
                        PathsEqual(window.FolderPath, navigationTarget.FullName));
            }
            finally
            {
#pragma warning disable xUnit1031 // Restore the owned window for guarded cleanup.
                _ = navigationService.NavigateToFolderAsync(explorer.Handle, directory.Path)
                    .GetAwaiter()
                    .GetResult();
#pragma warning restore xUnit1031
                _ = WaitUntil(
                    () => EnumerateExplorerWindows().Any(window =>
                        window.Handle == explorer.Handle && PathsEqual(window.FolderPath, directory.Path)),
                    TimeSpan.FromSeconds(8));
            }
        });
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task RealExplorerJumpThenParentNavigationStillRoutesFollowUpTypeSearchInput()
    {
        // Production path for: jump into a folder → Backspace to parent → type again.
        // Asserts focus restoration + follow-up text capture without requiring an
        // elevated packaged app process.
        await RunInStaAsync(() =>
        {
            using var directory = TemporaryExplorerDirectory.Create();
            var child = Directory.CreateDirectory(Path.Combine(directory.Path, "jump-child"));
            var existingExplorerHandles = EnumerateExplorerWindows()
                .Select(window => window.Handle)
                .ToHashSet();
            LaunchExplorer(directory.Path);
            var explorer = WaitForExplorerWindow(directory.Path, TimeSpan.FromSeconds(15));
            Assert.NotNull(explorer);
            Assert.DoesNotContain(explorer.Handle, existingExplorerHandles);
            using var cleanup = new OwnedExplorerWindow(explorer.Handle, directory.Path);
            var navigationService = new ExplorerNavigationService();

            try
            {
                _ = ShowWindow(explorer.Handle, SwRestore);
                // Foreground activation is best-effort on busy desktops; Navigate2
                // and follow-up typing only need a valid HWND + eventual focus.
                _ = DesktopWindowActivator.TryActivate(explorer.Handle, TimeSpan.FromSeconds(2));

#pragma warning disable xUnit1031
                Assert.True(
                    navigationService.NavigateToFolderAsync(explorer.Handle, child.FullName)
                        .GetAwaiter()
                        .GetResult(),
                    "Could not navigate the real Explorer window into the child folder.");
#pragma warning restore xUnit1031
                Assert.True(
                    WaitUntil(
                        () => EnumerateExplorerWindows().Any(window =>
                            window.Handle == explorer.Handle &&
                            PathsEqual(window.FolderPath, child.FullName)),
                        TimeSpan.FromSeconds(8)),
                    "Explorer did not show the jumped child folder.");

                _ = ExplorerFolderViewFocus.TryFocusFolderView(explorer.Handle);
                _ = DesktopWindowActivator.TryActivate(explorer.Handle, TimeSpan.FromSeconds(2));

                using var hook = new RealExplorerFollowUpHookHarness();
                hook.Service.OverlayTextInputWindow = explorer.Handle;
                hook.Service.CaptureFollowUpTextInput = true;
                hook.Service.YieldHostSearchBoxToNativeInput = true;
                hook.Service.CaptureOverlayInput = false;

                // Prefer real Backspace parent navigation when list focus worked.
                // Fall back to production Navigate for the parent so the follow-up
                // typing assertion still runs if Shell ignores Backspace.
                _ = ExplorerFolderViewFocus.TryFocusFolderView(explorer.Handle);
                _ = DesktopWindowActivator.TryActivate(explorer.Handle, TimeSpan.FromSeconds(1));
                SendVirtualKey(0x08);
                Assert.False(
                    WaitUntil(() => !hook.EditCommands.IsEmpty, TimeSpan.FromMilliseconds(400)),
                    "Follow-up capture must not treat Explorer Backspace as an overlay edit command.");
                var returnedViaBackspace = WaitUntil(
                    () => EnumerateExplorerWindows().Any(window =>
                        window.Handle == explorer.Handle &&
                        PathsEqual(window.FolderPath, directory.Path)),
                    TimeSpan.FromSeconds(3));
                if (!returnedViaBackspace)
                {
#pragma warning disable xUnit1031
                    Assert.True(
                        navigationService.NavigateToFolderAsync(explorer.Handle, directory.Path)
                            .GetAwaiter()
                            .GetResult(),
                        "Could not return Explorer to the parent folder after Backspace was ignored.");
#pragma warning restore xUnit1031
                    Assert.True(
                        WaitUntil(
                            () => EnumerateExplorerWindows().Any(window =>
                                window.Handle == explorer.Handle &&
                                PathsEqual(window.FolderPath, directory.Path)),
                            TimeSpan.FromSeconds(8)),
                        "Explorer did not show the parent folder after fallback navigation.");
                }

                // Shell often parks focus in the address band after parent nav.
                _ = ExplorerFolderViewFocus.TryFocusFolderView(explorer.Handle);
                _ = DesktopWindowActivator.TryActivate(explorer.Handle, TimeSpan.FromSeconds(3));
                Thread.Sleep(150);

                // Even if focus remains on address Edit, follow-up capture should
                // reclaim the next printable key for type-to-search reopening.
                // Retry activation briefly; if desktop policy blocks SetForeground,
                // still send input when the HWND is valid (hook uses focused host).
                for (var attempt = 0; attempt < 5 && GetForegroundWindow() != explorer.Handle; attempt++)
                {
                    _ = DesktopWindowActivator.TryActivate(explorer.Handle, TimeSpan.FromSeconds(1));
                    Thread.Sleep(100);
                }

                SendVirtualKey(0x4F); // 'O'
                Assert.True(
                    WaitUntil(() => !hook.TextInputs.IsEmpty, TimeSpan.FromSeconds(5)),
                    "Follow-up type-to-search input was not routed after jump → parent → type.");
                Assert.True(hook.TextInputs.TryDequeue(out var input));
                Assert.Equal("o", input.Text, ignoreCase: true);
                Assert.Equal(GlobalTextInputHost.Explorer, input.Host);
                Assert.Equal(explorer.Handle, input.ForegroundWindow);
            }
            finally
            {
#pragma warning disable xUnit1031
                _ = navigationService.NavigateToFolderAsync(explorer.Handle, directory.Path)
                    .GetAwaiter()
                    .GetResult();
#pragma warning restore xUnit1031
            }
        });
    }

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public async Task PackagedElevatedAppCapturesRealExplorerInputAndSynchronizesSelection()
    {
        await RunInStaAsync(() =>
        {
            Assert.True(
                IsCurrentProcessElevated(),
                "ElevatedPackagedBlackboxE2E must run in an elevated test process; running it non-elevated is a test failure.");

            var packageDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
            Assert.False(
                string.IsNullOrWhiteSpace(packageDirectory),
                "LISTARYOPEN_PACKAGE_DIR must identify the published package under test.");
            packageDirectory = Path.GetFullPath(packageDirectory);
            var packageExePath = Path.Combine(packageDirectory, "ListaryOpen.App.exe");
            var hookDllPath = Path.Combine(packageDirectory, "hooks", "x64", "ListaryOpen.Hook.dll");
            var hookRuntimePath = Path.Combine(packageDirectory, "hooks", "x64", "libunwind.dll");
            Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
            Assert.True(File.Exists(hookDllPath), $"The packaged x64 native hook DLL is missing: {hookDllPath}");
            Assert.True(File.Exists(hookRuntimePath), $"The packaged x64 native hook runtime is missing: {hookRuntimePath}");
            Assert.True(
                IsSingleInstanceMutexAvailable(),
                "ListaryOpen is already running. Close the existing instance before ElevatedPackagedBlackboxE2E; the test will not terminate a user-owned instance.");

            var packageExeSha256 = Sha256(packageExePath);
            using var packageData = PackageDataCleanup.RequireInitiallyAbsent(packageDirectory);
            using var directory = TemporaryExplorerDirectory.Create();
            var query = "listarye2e" + CreateLetterNonce(10);
            var selectedPath = Path.Combine(directory.Path, query + ".txt");
            var controlPath = Path.Combine(directory.Path, "unrelated-control-item.txt");
            File.WriteAllText(selectedPath, "The packaged ListaryOpen app must select this marker.");
            File.WriteAllText(controlPath, "This item is clicked before injected typing and must be replaced by the result selection.");

            var existingExplorerHandles = EnumerateExplorerWindows()
                .Select(window => window.Handle)
                .ToHashSet();
            LaunchExplorer(directory.Path);
            var explorerWindow = WaitForExplorerWindow(directory.Path, TimeSpan.FromSeconds(15));
            Assert.NotNull(explorerWindow);
            Assert.DoesNotContain(explorerWindow.Handle, existingExplorerHandles);
            using var explorerCleanup = new OwnedExplorerWindow(explorerWindow.Handle, directory.Path);

            Assert.True(ShowWindow(explorerWindow.Handle, SwRestore));
            Assert.True(
                TryActivateWindow(explorerWindow.Handle),
                "The Explorer setup window could not be activated before the physical selection click.");
            var controlElement = WaitForMarkerElement(explorerWindow.Handle, Path.GetFileName(controlPath));
            Assert.NotNull(controlElement);
            Assert.True(
                SelectExplorerItemWithPhysicalClick(
                    explorerWindow.Handle,
                    directory.Path,
                    controlPath,
                    TimeSpan.FromSeconds(10)),
                "The physical setup click did not select the control item in Explorer's item view.");

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

                Assert.True(ShowWindow(explorerWindow.Handle, SwRestore));
                Assert.True(
                    SelectExplorerItemWithPhysicalClick(
                        explorerWindow.Handle,
                        directory.Path,
                        controlPath,
                        TimeSpan.FromSeconds(10)) &&
                    GetForegroundWindow() == explorerWindow.Handle,
                    "Explorer's real item view was not the foreground input surface before SendInput.");

                SendVirtualKeyText(query, explorerWindow.Handle);

                var overlayHandle = WaitForTopLevelWindow(
                    appProcess.Id,
                    "ListaryOpen Search",
                    TimeSpan.FromSeconds(12));
                Assert.NotEqual(IntPtr.Zero, overlayHandle);
                Assert.True(IsWindowVisible(overlayHandle));

                var overlay = AutomationElement.FromHandle(overlayHandle);
                Assert.NotNull(overlay);
                var queryElement = WaitForAutomationElement(
                    overlay,
                    new PropertyCondition(AutomationElement.NameProperty, "Search query"),
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(queryElement);
                Assert.True(queryElement.TryGetCurrentPattern(ValuePattern.Pattern, out var queryValuePattern));
                Assert.Equal(query, ((ValuePattern)queryValuePattern).Current.Value);

                var resultsElement = WaitForAutomationElement(
                    overlay,
                    new PropertyCondition(AutomationElement.NameProperty, "Search results"),
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(resultsElement);
                var resultMarker = WaitForAutomationElement(
                    resultsElement,
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        Path.GetFileName(selectedPath)),
                    TimeSpan.FromSeconds(12));
                Assert.NotNull(resultMarker);
                var resultItem = FindAncestorOfControlType(resultMarker, ControlType.ListItem);
                Assert.NotNull(resultItem);
                Assert.True(resultItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var resultSelectionPattern));
                Assert.True(
                    ((SelectionItemPattern)resultSelectionPattern).Current.IsSelected,
                    "UI Automation reported that the packaged app's matching search result was not selected.");

                Assert.True(
                    WaitUntil(
                        () =>
                        {
                            var paths = ReadSelectedPaths(explorerWindow.Handle, directory.Path);
                            return paths.Any(path => PathsEqual(path, selectedPath)) &&
                                paths.All(path => !PathsEqual(path, controlPath));
                        },
                        TimeSpan.FromSeconds(10)),
                    "Shell COM did not observe the packaged app synchronizing its selected result into real Explorer.");
                var shellSelectedPaths = ReadSelectedPaths(explorerWindow.Handle, directory.Path);

                var screenshotPath = CaptureComposedEvidence(
                    explorerWindow.Handle,
                    overlayHandle,
                    "28-packaged-real-explorer-overlay");
                Assert.NotNull(screenshotPath);
                WritePackagedEvidenceMetadata(
                    screenshotPath,
                    packageExePath,
                    packageExeSha256,
                    appProcess.Id,
                    explorerWindow.Handle,
                    overlayHandle,
                    directory.Path,
                    query,
                    selectedPath,
                    shellSelectedPaths,
                    queryElement,
                    resultItem);

                SendVirtualKey(0x1B);
                Assert.True(
                    WaitUntil(() => !IsWindowVisible(overlayHandle), TimeSpan.FromSeconds(8)),
                    "Injected Escape did not dismiss the packaged app's Explorer overlay.");
                Assert.True(IsWindow(explorerWindow.Handle), "Dismissing the overlay unexpectedly closed Explorer.");
                Assert.Contains(
                    ReadSelectedPaths(explorerWindow.Handle, directory.Path),
                    path => PathsEqual(path, selectedPath));

                Assert.Equal("Ok", control.Exchange(nonce + " Shutdown"));
                shutdownSucceeded = appProcess.WaitForExit(15_000);
                Assert.True(shutdownSucceeded, "The packaged app did not exit after its authenticated Shutdown request.");
                Assert.Equal(0, appProcess.ExitCode);
                var unloadStopwatch = Stopwatch.StartNew();
                var nativeModulesUnloaded = WaitUntil(
                    () =>
                        !IsModuleLoadedInWindowProcess(explorerWindow.Handle, hookDllPath) &&
                        !IsModuleLoadedInWindowProcess(explorerWindow.Handle, hookRuntimePath),
                    TimeSpan.FromSeconds(10));
                Assert.True(
                    nativeModulesUnloaded,
                    "The published app exited but its native hook DLL/runtime remained loaded in Explorer.");
                _ = GetWindowThreadProcessId(explorerWindow.Handle, out var explorerProcessId);
                Assert.NotEqual(0u, explorerProcessId);
                AppendNativeUnloadEvidence(
                    Path.ChangeExtension(screenshotPath, ".json")!,
                    "explorer",
                    explorerProcessId,
                    hookDllPath,
                    hookRuntimePath,
                    unloadStopwatch.Elapsed);
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

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public async Task PackagedAppReopensExplorerTypeSearchAfterJumpAndParentBackspace()
    {
        // Full product path: type folder → Enter jump → Backspace to parent → type again
        // must reopen the Explorer type-to-search overlay on the same HWND.
        await RunInStaAsync(() =>
        {
            Assert.True(
                IsCurrentProcessElevated(),
                "ElevatedPackagedBlackboxE2E must run in an elevated test process; running it non-elevated is a test failure.");

            var packageDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
            Assert.False(
                string.IsNullOrWhiteSpace(packageDirectory),
                "LISTARYOPEN_PACKAGE_DIR must identify the published package under test.");
            packageDirectory = Path.GetFullPath(packageDirectory);
            var packageExePath = Path.Combine(packageDirectory, "ListaryOpen.App.exe");
            var hookDllPath = Path.Combine(packageDirectory, "hooks", "x64", "ListaryOpen.Hook.dll");
            var hookRuntimePath = Path.Combine(packageDirectory, "hooks", "x64", "libunwind.dll");
            Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
            Assert.True(File.Exists(hookDllPath), $"The packaged x64 native hook DLL is missing: {hookDllPath}");
            Assert.True(File.Exists(hookRuntimePath), $"The packaged x64 native hook runtime is missing: {hookRuntimePath}");
            Assert.True(
                IsSingleInstanceMutexAvailable(),
                "ListaryOpen is already running. Close the existing instance before ElevatedPackagedBlackboxE2E.");

            using var packageData = PackageDataCleanup.RequireInitiallyAbsent(packageDirectory);
            using var directory = TemporaryExplorerDirectory.Create();
            var folderQuery = "listaryjump" + CreateLetterNonce(10);
            var childDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, folderQuery));
            var seedFile = Path.Combine(directory.Path, "seed-" + CreateLetterNonce(6) + ".txt");
            File.WriteAllText(seedFile, "Keeps the parent folder non-empty for list focus.");

            var existingExplorerHandles = EnumerateExplorerWindows()
                .Select(window => window.Handle)
                .ToHashSet();
            LaunchExplorer(directory.Path);
            var explorerWindow = WaitForExplorerWindow(directory.Path, TimeSpan.FromSeconds(15));
            Assert.NotNull(explorerWindow);
            Assert.DoesNotContain(explorerWindow.Handle, existingExplorerHandles);
            using var explorerCleanup = new OwnedExplorerWindow(explorerWindow.Handle, directory.Path);

            Assert.True(
                SelectExplorerItemWithPhysicalClick(
                    explorerWindow.Handle,
                    directory.Path,
                    seedFile,
                    TimeSpan.FromSeconds(5)),
                "Explorer seed item could not be physically selected before app startup.");

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

                // Wait for the unique folder to become searchable (startup indexing).
                Assert.True(
                    SelectExplorerItemWithPhysicalClick(
                        explorerWindow.Handle,
                        directory.Path,
                        seedFile,
                        TimeSpan.FromSeconds(5)),
                    "Explorer item view was not ready before the first type-to-search.");

                // First type-to-search: open overlay on the child folder name.
                Assert.True(
                    SelectExplorerItemWithPhysicalClick(
                        explorerWindow.Handle,
                        directory.Path,
                        seedFile,
                        TimeSpan.FromSeconds(5)),
                    "Explorer seed item lost selection before the first type-to-search.");
                SendVirtualKeyText(folderQuery, explorerWindow.Handle);

                var firstOverlay = WaitForTopLevelWindow(
                    appProcess.Id,
                    "ListaryOpen Search",
                    TimeSpan.FromSeconds(20));
                Assert.NotEqual(IntPtr.Zero, firstOverlay);
                Assert.True(IsWindowVisible(firstOverlay));

                var firstOverlayElement = AutomationElement.FromHandle(firstOverlay);
                Assert.NotNull(firstOverlayElement);
                var firstResults = WaitForAutomationElement(
                    firstOverlayElement,
                    new PropertyCondition(AutomationElement.NameProperty, "Search results"),
                    TimeSpan.FromSeconds(12));
                Assert.NotNull(firstResults);
                var folderResult = WaitForAutomationElement(
                    firstResults,
                    new PropertyCondition(AutomationElement.NameProperty, folderQuery),
                    TimeSpan.FromSeconds(20));
                Assert.NotNull(folderResult);

                // Enter confirms the folder result and navigates the current Explorer window.
                SendVirtualKey(0x0D);
                Assert.True(
                    WaitUntil(
                        () => !IsWindowVisible(firstOverlay)
                            && EnumerateExplorerWindows().Any(window =>
                                window.Handle == explorerWindow.Handle
                                && PathsEqual(window.FolderPath, childDirectory.FullName)),
                        TimeSpan.FromSeconds(12)),
                    "Enter did not jump the existing Explorer window into the searched folder and dismiss the overlay.");
                Assert.DoesNotContain(
                    EnumerateExplorerWindows(),
                    window => !existingExplorerHandles.Contains(window.Handle)
                        && window.Handle != explorerWindow.Handle
                        && PathsEqual(window.FolderPath, childDirectory.FullName));

                // Backspace must return to the parent. Production handles this by
                // programmatically navigating parent when Shell focus is not on the
                // items view — do not soft-pass with NavigateToFolderAsync here.
                Assert.True(TryActivateWindow(explorerWindow.Handle));
                Thread.Sleep(200);
                SendVirtualKey(0x08);
                Assert.True(
                    WaitUntil(
                        () => EnumerateExplorerWindows().Any(window =>
                            window.Handle == explorerWindow.Handle
                            && PathsEqual(window.FolderPath, directory.Path)),
                        TimeSpan.FromSeconds(10)),
                    "Backspace after Explorer type-to-search jump did not return to the parent folder.");

                // Second type-to-search must reopen the overlay after parent navigation.
                Assert.True(TryActivateWindow(explorerWindow.Handle));
                Thread.Sleep(200);
                var reopenQuery = "seed";
                SendVirtualKeyText(reopenQuery, explorerWindow.Handle);

                var secondOverlay = WaitForTopLevelWindow(
                    appProcess.Id,
                    "ListaryOpen Search",
                    TimeSpan.FromSeconds(12));
                Assert.NotEqual(IntPtr.Zero, secondOverlay);
                Assert.True(
                    IsWindowVisible(secondOverlay),
                    "Type-to-search did not reopen after jump → parent navigation → typing.");
                Assert.True(
                    IsWindow(explorerWindow.Handle),
                    "Explorer HWND must remain the same after jump/backspace follow-up typing.");

                var secondOverlayElement = AutomationElement.FromHandle(secondOverlay);
                Assert.NotNull(secondOverlayElement);
                var secondQuery = WaitForAutomationElement(
                    secondOverlayElement,
                    new PropertyCondition(AutomationElement.NameProperty, "Search query"),
                    TimeSpan.FromSeconds(8));
                Assert.NotNull(secondQuery);
                Assert.True(secondQuery.TryGetCurrentPattern(ValuePattern.Pattern, out var secondQueryValue));
                Assert.Contains(
                    reopenQuery,
                    ((ValuePattern)secondQueryValue).Current.Value,
                    StringComparison.OrdinalIgnoreCase);

                var evidence = CaptureComposedEvidence(
                    explorerWindow.Handle,
                    secondOverlay,
                    "29-packaged-explorer-follow-up-after-parent");
                Assert.NotNull(evidence);

                SendVirtualKey(0x1B);
                Assert.True(
                    WaitUntil(() => !IsWindowVisible(secondOverlay), TimeSpan.FromSeconds(8)),
                    "Escape did not dismiss the reopened Explorer type-to-search overlay.");

                Assert.Equal("Ok", control.Exchange(nonce + " Shutdown"));
                shutdownSucceeded = appProcess.WaitForExit(15_000);
                Assert.True(shutdownSucceeded, "The packaged app did not exit after Shutdown.");
                Assert.Equal(0, appProcess.ExitCode);
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
            Name = "ListaryOpen real Explorer E2E"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// Minimal LL-hook harness for real-Explorer follow-up typing assertions.
    /// </summary>
    private sealed class RealExplorerFollowUpHookHarness : IDisposable
    {
        private const uint WmQuit = 0x0012;
        private const uint PmNoRemove = 0x0000;
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private uint _threadId;
        private int _disposed;
        private Exception? _startupFailure;
        private GlobalTextInputService? _service;

        public RealExplorerFollowUpHookHarness()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ListaryOpen real Explorer follow-up hook"
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                Dispose();
                throw new TimeoutException("Real Explorer follow-up hook thread did not start.");
            }

            if (_startupFailure is not null)
            {
                Dispose();
                throw new InvalidOperationException(
                    "Real Explorer follow-up hook could not start.",
                    _startupFailure);
            }
        }

        public GlobalTextInputService Service =>
            _service ?? throw new InvalidOperationException("Follow-up hook is not ready.");

        public ConcurrentQueue<GlobalTextInputEventArgs> TextInputs { get; } = new();
        public ConcurrentQueue<GlobalEditCommandInputEventArgs> EditCommands { get; } = new();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (_thread.IsAlive && _threadId != 0)
                {
                    _ = PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                }

                if (!_thread.Join(10_000))
                {
                    _service?.Dispose();
                    if (_threadId != 0)
                    {
                        _ = PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                    }

                    _ = _thread.Join(2_000);
                }
            }
            finally
            {
                _ready.Dispose();
            }
        }

        private void Run()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                _service = new GlobalTextInputService(acceptInjectedInputForTesting: true);
                _service.TextInput += (_, input) =>
                {
                    input.Handled = true;
                    TextInputs.Enqueue(input);
                };
                _service.EditCommandPressed += (_, input) =>
                {
                    input.Handled = true;
                    EditCommands.Enqueue(input);
                };
                _service.Start();
            }
            catch (Exception exception)
            {
                _service?.Dispose();
                _startupFailure = exception;
                try
                {
                    _ready.Set();
                }
                catch (ObjectDisposedException)
                {
                }

                return;
            }

            try
            {
                _ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessage(ref message);
                }
            }
            finally
            {
                _service?.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(
            out NativeMessage message,
            IntPtr window,
            uint min,
            uint max,
            uint remove);

        [DllImport("user32.dll")]
        private static extern int GetMessage(
            out NativeMessage message,
            IntPtr window,
            uint min,
            uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(
            uint threadId,
            uint message,
            UIntPtr wParam,
            IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
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

    private static string CreateLetterNonce(int length)
    {
        var random = RandomNumberGenerator.GetBytes(length);
        return new string(random.Select(value => (char)('a' + (value % 26))).ToArray());
    }

    private static void ClickAutomationElement(AutomationElement element)
    {
        Assert.True(TryClickAutomationElement(element), $"Automation element '{element.Current.Name}' could not be clicked.");
    }

    private static bool TryClickAutomationElement(AutomationElement element)
    {
        Rect bounds;
        try
        {
            bounds = element.Current.BoundingRectangle;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        if (bounds.IsEmpty)
        {
            return false;
        }

        var x = checked((int)Math.Round(bounds.Left + (bounds.Width / 2)));
        var y = checked((int)Math.Round(bounds.Top + (bounds.Height / 2)));
        if (!SetCursorPos(x, y))
        {
            return false;
        }

        var inputs = new[]
        {
            new NativeInput
            {
                Type = InputMouse,
                Union = new NativeInputUnion
                {
                    Mouse = new NativeMouseInput { Flags = MouseEventLeftDown }
                }
            },
            new NativeInput
            {
                Type = InputMouse,
                Union = new NativeInputUnion
                {
                    Mouse = new NativeMouseInput { Flags = MouseEventLeftUp }
                }
            }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == (uint)inputs.Length;
    }

    private static bool SelectExplorerItemWithPhysicalClick(
        IntPtr explorerWindow,
        string currentFolder,
        string itemPath,
        TimeSpan timeout)
    {
        var markerName = Path.GetFileName(itemPath);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            _ = ShowWindow(explorerWindow, SwRestore);
            if (TryActivateWindow(explorerWindow))
            {
                var marker = FindMarkerElement(explorerWindow, markerName);
                if (marker is not null &&
                    TryClickAutomationElement(marker) &&
                    WaitUntil(
                        () => GetForegroundWindow() == explorerWindow &&
                            ReadSelectedPaths(explorerWindow, currentFolder)
                                .Any(path => PathsEqual(path, itemPath)),
                        TimeSpan.FromMilliseconds(750)))
                {
                    return true;
                }
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static void SendVirtualKeyText(string text, IntPtr expectedForegroundWindow = default)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var key = VkKeyScan(character);
            Assert.NotEqual(-1, key);
            var virtualKey = unchecked((ushort)(key & 0xFF));
            var modifierState = (key >> 8) & 0xFF;
            Assert.Equal(0, modifierState);
            SendVirtualKey(virtualKey);
            if (expectedForegroundWindow != IntPtr.Zero)
            {
                Assert.True(
                    GetForegroundWindow() == expectedForegroundWindow,
                    $"Foreground focus left Explorer after input character {index + 1}/{text.Length} ('{character}').");
            }
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
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput
                    {
                        VirtualKey = virtualKey,
                        ScanCode = scanCode
                    }
                }
            },
            new NativeInput
            {
                Type = InputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput
                    {
                        VirtualKey = virtualKey,
                        ScanCode = scanCode,
                        Flags = KeyEventKeyUp
                    }
                }
            }
        };
        Assert.Equal(
            (uint)inputs.Length,
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
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
        AutomationElement root,
        System.Windows.Automation.Condition condition,
        TimeSpan timeout)
    {
        AutomationElement? result = null;
        return WaitUntil(
            () =>
            {
                try
                {
                    result = root.FindFirst(TreeScope.Descendants, condition);
                    return result is not null;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            },
            timeout)
            ? result
            : null;
    }

    private static AutomationElement? FindAncestorOfControlType(
        AutomationElement element,
        ControlType controlType)
    {
        var current = element;
        for (var depth = 0; depth < 12; depth++)
        {
            try
            {
                if (current.Current.ControlType == controlType)
                {
                    return current;
                }

                current = TreeWalker.ControlViewWalker.GetParent(current);
                if (current is null)
                {
                    return null;
                }
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }

    private static void LaunchExplorer(string folderPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/n,\"{folderPath}\"",
            UseShellExecute = true
        });
        Assert.NotNull(process);
    }

    private static ExplorerWindow? WaitForExplorerWindow(string folderPath, TimeSpan timeout)
    {
        ExplorerWindow? match = null;
        return WaitUntil(
            () =>
            {
                match = EnumerateExplorerWindows().FirstOrDefault(window => PathsEqual(window.FolderPath, folderPath));
                return match is not null;
            },
            timeout)
            ? match
            : null;
    }

    private static IReadOnlyList<ExplorerWindow> EnumerateExplorerWindows()
    {
        var result = new List<ExplorerWindow>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return result;
        }

        object? shell = null;
        object? windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            windows = ((dynamic)shell!).Windows();
            foreach (var window in (System.Collections.IEnumerable)windows)
            {
                object? document = null;
                object? folder = null;
                object? self = null;
                try
                {
                    dynamic shellWindow = window;
                    var handle = new IntPtr(Convert.ToInt64(shellWindow.HWND));
                    document = shellWindow.Document;
                    folder = document is null ? null : ((dynamic)document).Folder;
                    self = folder is null ? null : ((dynamic)folder).Self;
                    var path = self is null ? null : ((dynamic)self).Path as string;
                    if (handle != IntPtr.Zero && !string.IsNullOrWhiteSpace(path))
                    {
                        result.Add(new ExplorerWindow(handle, path));
                    }
                }
                catch (Exception exception) when (IsExpectedShellException(exception))
                {
                }
                finally
                {
                    ReleaseComObject(self);
                    ReleaseComObject(folder);
                    ReleaseComObject(document);
                    ReleaseComObject(window);
                }
            }
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadSelectedPaths(IntPtr explorerHandle, string folderPath)
    {
        var result = new List<string>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        Assert.NotNull(shellType);
        object? shell = null;
        object? windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            windows = ((dynamic)shell!).Windows();
            foreach (var window in (System.Collections.IEnumerable)windows)
            {
                object? document = null;
                object? folder = null;
                object? self = null;
                object? selectedItems = null;
                try
                {
                    dynamic shellWindow = window;
                    if (new IntPtr(Convert.ToInt64(shellWindow.HWND)) != explorerHandle)
                    {
                        continue;
                    }

                    document = shellWindow.Document;
                    folder = document is null ? null : ((dynamic)document).Folder;
                    self = folder is null ? null : ((dynamic)folder).Self;
                    var observedFolder = self is null ? null : ((dynamic)self).Path as string;
                    if (!PathsEqual(observedFolder, folderPath))
                    {
                        continue;
                    }

                    selectedItems = ((dynamic)document!).SelectedItems();
                    foreach (var selectedItem in (System.Collections.IEnumerable)selectedItems)
                    {
                        try
                        {
                            var path = ((dynamic)selectedItem).Path as string;
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                result.Add(path);
                            }
                        }
                        finally
                        {
                            ReleaseComObject(selectedItem);
                        }
                    }
                }
                catch (Exception exception) when (IsExpectedShellException(exception))
                {
                }
                finally
                {
                    ReleaseComObject(selectedItems);
                    ReleaseComObject(self);
                    ReleaseComObject(folder);
                    ReleaseComObject(document);
                    ReleaseComObject(window);
                }
            }
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return result;
    }

    private static AutomationElement? WaitForMarkerElement(IntPtr explorerHandle, string markerName)
    {
        AutomationElement? match = null;
        return WaitUntil(
            () =>
            {
                match = FindMarkerElement(explorerHandle, markerName);
                return match is not null;
            },
            TimeSpan.FromSeconds(8))
            ? match
            : null;
    }

    private static AutomationElement? FindMarkerElement(IntPtr explorerHandle, string markerName)
    {
        try
        {
            return AutomationElement.FromHandle(explorerHandle)?
                .FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .FirstOrDefault(element =>
                    string.Equals(element.Current.Name, markerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string? CaptureEvidence(IntPtr window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        Assert.True(GetWindowRect(window, out var bounds));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, 400, 4_000);
        Assert.InRange(height, 300, 2_500);
        Assert.True(TryActivateWindow(window));

        var desktopDc = GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktopDc);
        var memoryDc = CreateCompatibleDC(desktopDc);
        var bitmap = CreateCompatibleBitmap(desktopDc, width, height);
        Assert.NotEqual(IntPtr.Zero, memoryDc);
        Assert.NotEqual(IntPtr.Zero, bitmap);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            _ = DwmFlush();
            Thread.Sleep(200);
            Assert.True(
                PrintWindow(window, memoryDc, 2) ||
                BitBlt(memoryDc, 0, 0, width, height, desktopDc, bounds.Left, bounds.Top, 0x00CC0020));
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
            Assert.True(stream.Length > 4_096, $"Real Explorer screenshot was unexpectedly small: {path}");
            return path;
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static void WriteEvidenceMetadata(
        string? screenshotPath,
        IntPtr explorerHandle,
        string folderPath,
        string selectedPath,
        ExplorerTracker tracker,
        IReadOnlyList<string> shellSelectedPaths,
        AutomationElement markerElement)
    {
        if (screenshotPath is null)
        {
            return;
        }

        var metadataPath = Path.ChangeExtension(screenshotPath, ".json");
        var metadata = new
        {
            scenario = "real-system-explorer-selection",
            realSystemExplorer = true,
            processName = GetWindowProcessName(explorerHandle),
            windowClass = GetWindowClass(explorerHandle),
            windowHandle = explorerHandle.ToInt64(),
            folderPath,
            selectedPath,
            trackerLastFolder = tracker.LastFolder,
            trackerCandidates = tracker.GetFolderCandidates().Select(candidate => new
            {
                candidate.FolderPath,
                windowHandle = candidate.WindowHandle.ToInt64(),
                candidate.IsForeground
            }),
            shellOracleSelectedPaths = shellSelectedPaths,
            uiaOracle = new
            {
                markerElement.Current.Name,
                markerElement.Current.ControlType.ProgrammaticName,
                isSelected = ((SelectionItemPattern)markerElement.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected
            },
            screenshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshotPath)))
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 256);
    }

    private static string? CaptureComposedEvidence(IntPtr explorerWindow, IntPtr overlayWindow, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        Assert.True(IsWindowVisible(explorerWindow));
        Assert.True(IsWindowVisible(overlayWindow));
        Assert.True(GetWindowRect(explorerWindow, out var explorerBounds));
        Assert.True(GetWindowRect(overlayWindow, out var overlayBounds));
        var bounds = new NativeRect
        {
            Left = Math.Min(explorerBounds.Left, overlayBounds.Left),
            Top = Math.Min(explorerBounds.Top, overlayBounds.Top),
            Right = Math.Max(explorerBounds.Right, overlayBounds.Right),
            Bottom = Math.Max(explorerBounds.Bottom, overlayBounds.Bottom)
        };
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, 400, 8_000);
        Assert.InRange(height, 300, 4_500);

        _ = DwmFlush();
        Thread.Sleep(250);
        var desktopDc = GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktopDc);
        var memoryDc = CreateCompatibleDC(desktopDc);
        var bitmap = CreateCompatibleBitmap(desktopDc, width, height);
        Assert.NotEqual(IntPtr.Zero, memoryDc);
        Assert.NotEqual(IntPtr.Zero, bitmap);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            Assert.True(BitBlt(
                memoryDc,
                0,
                0,
                width,
                height,
                desktopDc,
                bounds.Left,
                bounds.Top,
                0x00CC0020));
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
            Assert.True(stream.Length > 8_192, $"Packaged Explorer overlay screenshot was unexpectedly small: {path}");
            return path;
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static void WritePackagedEvidenceMetadata(
        string? screenshotPath,
        string packageExePath,
        string packageExeSha256,
        int appProcessId,
        IntPtr explorerWindow,
        IntPtr overlayWindow,
        string explorerFolder,
        string query,
        string selectedPath,
        IReadOnlyList<string> shellSelectedPaths,
        AutomationElement queryElement,
        AutomationElement resultItem)
    {
        if (screenshotPath is null)
        {
            return;
        }

        Assert.True(GetWindowRect(explorerWindow, out var explorerBounds));
        Assert.True(GetWindowRect(overlayWindow, out var overlayBounds));
        var metadata = new
        {
            schemaVersion = 1,
            scenario = "packaged-elevated-app-real-explorer-type-search",
            passed = true,
            elevatedTestProcess = true,
            packageExePath,
            packageExeSha256,
            appProcessId,
            appWindow = new
            {
                title = GetWindowText(overlayWindow),
                handle = overlayWindow.ToInt64(),
                bounds = EvidenceBounds(overlayBounds)
            },
            explorer = new
            {
                processName = GetWindowProcessName(explorerWindow),
                className = GetWindowClass(explorerWindow),
                handle = explorerWindow.ToInt64(),
                folder = explorerFolder,
                bounds = EvidenceBounds(explorerBounds)
            },
            interactionPath = "ExplorerItemsView-WindowsSendInput-GlobalLowLevelHook",
            e2eControlCommands = new[] { "Ready", "AllowInjectedInput", "Shutdown" },
            query,
            queryOracle = new
            {
                queryElement.Current.Name,
                queryElement.Current.AutomationId,
                value = ((ValuePattern)queryElement.GetCurrentPattern(ValuePattern.Pattern)).Current.Value
            },
            selectedPath,
            resultOracle = new
            {
                resultItem.Current.Name,
                resultItem.Current.AutomationId,
                isSelected = ((SelectionItemPattern)resultItem.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected
            },
            shellOracleSelectedPaths = shellSelectedPaths,
            screenshot = Path.GetFileName(screenshotPath),
            screenshotSha256 = Sha256(screenshotPath)
        };
        var metadataPath = Path.ChangeExtension(screenshotPath, ".json");
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 512);
    }

    private static object EvidenceBounds(NativeRect bounds) => new
    {
        left = bounds.Left,
        top = bounds.Top,
        width = bounds.Right - bounds.Left,
        height = bounds.Bottom - bounds.Top
    };

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool TryActivateWindow(IntPtr window) => DesktopWindowActivator.TryActivate(window);

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

    private static string GetWindowProcessName(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        using var process = Process.GetProcessById((int)processId);
        return process.ProcessName;
    }

    private static bool IsModuleLoadedInWindowProcess(IntPtr window, string expectedPath)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(
                    Path.GetFullPath(module.FileName),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The owned Explorer window remains alive until this assertion has
            // completed, so an unreadable module list is not evidence of unload.
            return IsWindow(window);
        }
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
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

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsExpectedShellException(Exception exception) =>
        exception is ArgumentException
            or IOException
            or InvalidCastException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or COMException
        || string.Equals(
            exception.GetType().FullName,
            "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException",
            StringComparison.Ordinal);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private static string GetWindowClass(IntPtr handle)
    {
        var buffer = new char[256];
        var length = GetClassName(handle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private sealed record ExplorerWindow(IntPtr Handle, string FolderPath);

    private sealed class OwnedExplorerWindow : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly string _folderPath;

        public OwnedExplorerWindow(IntPtr handle, string folderPath)
        {
            _handle = handle;
            _folderPath = folderPath;
        }

        public void Dispose()
        {
            if (!IsWindow(_handle) ||
                !EnumerateExplorerWindows().Any(window =>
                    window.Handle == _handle && PathsEqual(window.FolderPath, _folderPath)))
            {
                return;
            }

            _ = PostMessage(_handle, WmClose, IntPtr.Zero, IntPtr.Zero);
            _ = WaitUntil(() => !IsWindow(_handle), TimeSpan.FromSeconds(5));
        }
    }

    private sealed class E2ePipeClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private E2ePipeClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 256,
                leaveOpen: true);
            _writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 256,
                leaveOpen: true)
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

                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.None);
                try
                {
                    pipe.Connect(750);
                    return new E2ePipeClient(pipe);
                }
                catch (TimeoutException exception)
                {
                    lastError = exception;
                    pipe.Dispose();
                }
                catch (IOException exception)
                {
                    lastError = exception;
                    pipe.Dispose();
                }
            }

            throw new TimeoutException(
                $"The packaged app did not create its nonce-scoped E2E endpoint within {timeout}.",
                lastError);
        }

        public string Exchange(string request)
        {
            Assert.DoesNotContain('\r', request);
            Assert.DoesNotContain('\n', request);
            _writer.WriteLine(request);
            return _reader.ReadLine()
                ?? throw new IOException("The packaged app closed its E2E endpoint without a response.");
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
        private bool _disposed;

        private PackageDataCleanup(string packageDirectory, string dataDirectory)
        {
            _packageDirectory = packageDirectory;
            _dataDirectory = dataDirectory;
        }

        public static PackageDataCleanup RequireInitiallyAbsent(string packageDirectory)
        {
            var normalizedPackage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
            var dataDirectory = Path.GetFullPath(Path.Combine(normalizedPackage, "data"));
            Assert.Equal(
                normalizedPackage,
                Path.GetDirectoryName(dataDirectory),
                ignoreCase: true);
            Assert.False(
                Directory.Exists(dataDirectory),
                $"The published package already contains runtime data: {dataDirectory}. " +
                "Use a clean publish directory so the black-box test is deterministic.");
            return new PackageDataCleanup(normalizedPackage, dataDirectory);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!Directory.Exists(_dataDirectory))
            {
                return;
            }

            var resolvedParent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetDirectoryName(_dataDirectory)!));
            if (!string.Equals(resolvedParent, _packageDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to remove package data outside the verified package directory: {_dataDirectory}");
            }

            Directory.Delete(_dataDirectory, recursive: true);
            if (Directory.Exists(_dataDirectory))
            {
                throw new IOException($"Packaged app runtime data was not removed: {_dataDirectory}");
            }
        }
    }

    private sealed class TemporaryExplorerDirectory : IDisposable
    {
        private TemporaryExplorerDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryExplorerDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "listary-real-explorer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryExplorerDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

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
        [FieldOffset(0)]
        public NativeMouseInput Mouse;

        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char character);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
