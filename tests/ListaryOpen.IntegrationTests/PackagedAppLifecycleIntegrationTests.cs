using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class PackagedAppLifecycleIntegrationTests
{
    private const string SingleInstanceMutexName = @"Local\ListaryOpen.SingleInstance";
    private const int GwlExStyle = -20;
    private const long WsExDlgModalFrame = 0x00000001L;
    private const uint TrayCallbackMessage = 0x0400;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const uint WmClose = 0x0010;

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public void PublishedAppHasAHealthyBackgroundLifecycleAndSilentSingleInstanceExit()
    {
        Assert.True(
            IsCurrentProcessElevated(),
            "ElevatedPackagedBlackboxE2E must run in an elevated test process; running it non-elevated is a test failure.");

        var packageDirectoryValue = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
        Assert.False(
            string.IsNullOrWhiteSpace(packageDirectoryValue),
            "LISTARYOPEN_PACKAGE_DIR must identify the published package under test.");

        var packageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectoryValue));
        var packageExePath = Path.GetFullPath(Path.Combine(packageDirectory, "ListaryOpen.App.exe"));
        Assert.Equal(packageDirectory, Path.GetDirectoryName(packageExePath), ignoreCase: true);
        Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
        Assert.True(
            IsSingleInstanceMutexAvailable(),
            "ListaryOpen is already running. Close the existing instance before ElevatedPackagedBlackboxE2E; the test will not terminate a user-owned instance.");

        var packageExeSha256 = Sha256(packageExePath);
        var firstNonce = CreateNonce();
        var secondNonce = CreateNonce();
        Assert.NotEqual(firstNonce, secondNonce);

        var data = PackageDataScope.RequireInitiallyAbsent(packageDirectory);
        Process? firstProcess = null;
        Process? secondProcess = null;
        E2ePipeClient? control = null;
        int? firstProcessId = null;
        int? secondProcessId = null;
        var firstShutdownSucceeded = false;
        var dataCreatedWhileRunning = false;
        var firstVisibleWindows = Array.Empty<WindowSnapshot>();
        var secondVisibleWindows = new List<WindowSnapshot>();
        var secondElapsed = TimeSpan.Zero;
        int? secondExitCode = null;
        TrayIconSnapshot? trayIcon = null;
        try
        {
            firstProcess = StartPackagedApp(packageExePath, packageDirectory, firstNonce);
            firstProcessId = firstProcess.Id;
            control = E2ePipeClient.Connect(
                "ListaryOpen.E2E." + firstNonce,
                firstProcess,
                TimeSpan.FromSeconds(45));

            Assert.Equal($"Ready {firstProcess.Id}", control.Exchange(firstNonce + " Ready"));
            firstProcess.Refresh();
            Assert.False(firstProcess.HasExited, "The packaged app exited immediately after reporting Ready.");
            Assert.Equal(
                packageExePath,
                Path.GetFullPath(firstProcess.MainModule?.FileName ?? string.Empty),
                ignoreCase: true);
            Assert.True(firstProcess.Threads.Count > 0, "The ready background process did not have any live threads.");
            Assert.True(firstProcess.WorkingSet64 > 0, "The ready background process did not have a live working set.");
            Assert.False(
                IsSingleInstanceMutexAvailable(),
                "The ready packaged app did not retain the production single-instance mutex.");

            trayIcon = FindSingleRegisteredTrayIcon(firstProcess.Id);

            dataCreatedWhileRunning = WaitUntil(
                () => Directory.Exists(data.DataDirectory),
                TimeSpan.FromSeconds(10));
            Assert.True(
                dataCreatedWhileRunning,
                "The ready packaged app did not initialize its package-local runtime data directory.");

            firstVisibleWindows = EnumerateVisibleTopLevelWindows(firstProcess.Id).ToArray();
            Assert.Empty(firstVisibleWindows);

            using (var windowMonitor = VisibleWindowEventMonitor.Start())
            {
                var secondStopwatch = Stopwatch.StartNew();
                secondProcess = StartPackagedApp(packageExePath, packageDirectory, secondNonce);
                secondProcessId = secondProcess.Id;
                Assert.NotEqual(firstProcess.Id, secondProcess.Id);
                while (!secondProcess.WaitForExit(5))
                {
                    secondVisibleWindows.AddRange(EnumerateVisibleTopLevelWindows(secondProcess.Id));
                    Assert.True(
                        secondStopwatch.Elapsed < TimeSpan.FromSeconds(10),
                        "A duplicate launch did not exit within 10 seconds.");
                }

                secondVisibleWindows.AddRange(EnumerateVisibleTopLevelWindows(secondProcess.Id));
                secondElapsed = secondStopwatch.Elapsed;
                secondExitCode = secondProcess.ExitCode;
                windowMonitor.Stop();
                secondVisibleWindows.AddRange(windowMonitor.GetObservedWindows(secondProcess.Id));
            }

            Assert.True(secondElapsed < TimeSpan.FromSeconds(10));
            Assert.Equal(0, secondExitCode);
            Assert.Empty(
                secondVisibleWindows.DistinctBy(window => window.Handle));

            Assert.Equal($"Ready {firstProcess.Id}", control.Exchange(firstNonce + " Ready"));
            firstProcess.Refresh();
            Assert.False(firstProcess.HasExited, "The original packaged app was terminated by the duplicate launch.");
            Assert.False(
                IsSingleInstanceMutexAvailable(),
                "The original packaged app released its single-instance mutex after the duplicate launch.");
            Assert.Empty(EnumerateVisibleTopLevelWindows(firstProcess.Id));
            AssertRegisteredTrayIcon(trayIcon);

            Assert.Equal("Ok", control.Exchange(firstNonce + " Shutdown"));
            firstShutdownSucceeded = firstProcess.WaitForExit(15_000);
            Assert.True(firstShutdownSucceeded, "The packaged app did not exit after its authenticated Shutdown request.");
            Assert.Equal(0, firstProcess.ExitCode);
            Assert.True(
                WaitUntil(IsSingleInstanceMutexAvailable, TimeSpan.FromSeconds(5)),
                "The packaged app did not release its single-instance mutex after normal shutdown.");
            Assert.False(IsWindow(new IntPtr(trayIcon.Handle)), "The tray callback owner survived normal shutdown.");
            Assert.False(
                TryReadRegisteredTrayIcon(new IntPtr(trayIcon.Handle), out _),
                "The Windows Shell still reported the packaged app's notification icon after normal shutdown.");
            Assert.Equal(packageExeSha256, Sha256(packageExePath));
        }
        finally
        {
            if (secondProcess is { HasExited: false })
            {
                secondProcess.Kill(entireProcessTree: true);
                _ = secondProcess.WaitForExit(10_000);
            }

            if (firstProcess is { HasExited: false })
            {
                if (!firstShutdownSucceeded && control is not null)
                {
                    try
                    {
                        _ = control.Exchange(firstNonce + " Shutdown");
                        firstShutdownSucceeded = firstProcess.WaitForExit(10_000);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException)
                    {
                    }
                }

                if (!firstProcess.HasExited)
                {
                    firstProcess.Kill(entireProcessTree: true);
                    _ = firstProcess.WaitForExit(10_000);
                }
            }

            control?.Dispose();
            secondProcess?.Dispose();
            firstProcess?.Dispose();
            data.Dispose();
        }

        Assert.False(
            Directory.Exists(data.DataDirectory),
            $"Packaged app runtime data was not removed after the lifecycle test: {data.DataDirectory}");
        WriteEvidenceMetadata(
            packageExePath,
            packageExeSha256,
            firstProcessId,
            secondProcessId,
            secondElapsed,
            secondExitCode,
            dataCreatedWhileRunning,
            firstVisibleWindows,
            secondVisibleWindows,
            trayIcon);
    }

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public void PublishedTrayOpensSettingsAndAppearancePersistsAcrossARealRestart()
    {
        RunInSta(() =>
        {
            Assert.True(
                IsCurrentProcessElevated(),
                "ElevatedPackagedBlackboxE2E must run in an elevated test process; running it non-elevated is a test failure.");

            var packageDirectoryValue = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
            Assert.False(
                string.IsNullOrWhiteSpace(packageDirectoryValue),
                "LISTARYOPEN_PACKAGE_DIR must identify the published package under test.");
            var packageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectoryValue));
            var packageExePath = Path.GetFullPath(Path.Combine(packageDirectory, "ListaryOpen.App.exe"));
            Assert.Equal(packageDirectory, Path.GetDirectoryName(packageExePath), ignoreCase: true);
            Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
            Assert.True(
                IsSingleInstanceMutexAvailable(),
                "ListaryOpen is already running. Close the existing instance before ElevatedPackagedBlackboxE2E; the test will not terminate a user-owned instance.");

            var packageExeSha256 = Sha256(packageExePath);
            var firstNonce = CreateNonce();
            var restartedNonce = CreateNonce();
            var data = PackageDataScope.RequireInitiallyAbsent(packageDirectory);
            var settingsPath = Path.Combine(data.DataDirectory, "settings.json");
            Process? firstProcess = null;
            Process? restartedProcess = null;
            E2ePipeClient? firstControl = null;
            E2ePipeClient? restartedControl = null;
            var firstShutdownSucceeded = false;
            var restartedShutdownSucceeded = false;
            TrayIconSnapshot? firstTrayIcon = null;
            TrayIconSnapshot? restartedTrayIcon = null;
            string? settingsSha256 = null;
            string? firstSelectedTheme = null;
            string? restartedSelectedTheme = null;
            string? firstScreenshotPath = null;
            string? restartedScreenshotPath = null;
            int? firstProcessId = null;
            int? restartedProcessId = null;
            try
            {
                firstProcess = StartPackagedApp(packageExePath, packageDirectory, firstNonce);
                firstProcessId = firstProcess.Id;
                firstControl = E2ePipeClient.Connect(
                    "ListaryOpen.E2E." + firstNonce,
                    firstProcess,
                    TimeSpan.FromSeconds(45));
                Assert.Equal($"Ready {firstProcess.Id}", firstControl.Exchange(firstNonce + " Ready"));

                firstTrayIcon = FindSingleRegisteredTrayIcon(firstProcess.Id);
                var firstSettingsWindow = OpenSettingsThroughTrayCallback(firstProcess.Id, firstTrayIcon);
                firstSelectedTheme = SelectGeekThemeAndSave(firstProcess.Id, firstSettingsWindow);
                Assert.Equal("Geek", firstSelectedTheme);
                Assert.True(
                    WaitUntil(
                        () => TryReadPersistedTheme(settingsPath, out var version, out var theme) &&
                            version == 4 && theme == 3 && !File.Exists(settingsPath + ".tmp"),
                        TimeSpan.FromSeconds(10)),
                    "The settings UI did not atomically persist version 4 with AppTheme.Geek.");
                settingsSha256 = Sha256(settingsPath);
                firstScreenshotPath = CaptureWindowEvidence(
                    firstSettingsWindow,
                    "30a-packaged-settings-geek-saved");

                Assert.True(PostMessage(firstSettingsWindow, WmClose, IntPtr.Zero, IntPtr.Zero));
                Assert.True(
                    WaitUntil(() => !IsWindowVisible(firstSettingsWindow), TimeSpan.FromSeconds(5)),
                    "Closing the settings window did not hide it back to the tray.");
                Assert.Equal($"Ready {firstProcess.Id}", firstControl.Exchange(firstNonce + " Ready"));
                AssertRegisteredTrayIcon(firstTrayIcon);

                Assert.Equal("Ok", firstControl.Exchange(firstNonce + " Shutdown"));
                firstShutdownSucceeded = firstProcess.WaitForExit(15_000);
                Assert.True(firstShutdownSucceeded, "The first settings process did not shut down normally.");
                Assert.Equal(0, firstProcess.ExitCode);
                Assert.True(WaitUntil(IsSingleInstanceMutexAvailable, TimeSpan.FromSeconds(5)));
                Assert.False(IsWindow(new IntPtr(firstTrayIcon.Handle)));
                Assert.False(TryReadRegisteredTrayIcon(new IntPtr(firstTrayIcon.Handle), out _));
                firstControl.Dispose();
                firstControl = null;

                restartedProcess = StartPackagedApp(packageExePath, packageDirectory, restartedNonce);
                restartedProcessId = restartedProcess.Id;
                restartedControl = E2ePipeClient.Connect(
                    "ListaryOpen.E2E." + restartedNonce,
                    restartedProcess,
                    TimeSpan.FromSeconds(45));
                Assert.Equal($"Ready {restartedProcess.Id}", restartedControl.Exchange(restartedNonce + " Ready"));
                Assert.Equal(settingsSha256, Sha256(settingsPath));

                restartedTrayIcon = FindSingleRegisteredTrayIcon(restartedProcess.Id);
                var restartedSettingsWindow = OpenSettingsThroughTrayCallback(restartedProcess.Id, restartedTrayIcon);
                restartedSelectedTheme = ReadSelectedAppearanceTheme(restartedSettingsWindow);
                Assert.Equal("Geek", restartedSelectedTheme);
                Assert.True(TryReadPersistedTheme(settingsPath, out var restartedVersion, out var restartedTheme));
                Assert.Equal(4, restartedVersion);
                Assert.Equal(3, restartedTheme);
                restartedScreenshotPath = CaptureWindowEvidence(
                    restartedSettingsWindow,
                    "30b-packaged-settings-geek-restarted");

                Assert.Equal("Ok", restartedControl.Exchange(restartedNonce + " Shutdown"));
                restartedShutdownSucceeded = restartedProcess.WaitForExit(15_000);
                Assert.True(restartedShutdownSucceeded, "The restarted settings process did not shut down normally.");
                Assert.Equal(0, restartedProcess.ExitCode);
                Assert.False(IsWindow(new IntPtr(restartedTrayIcon.Handle)));
                Assert.False(TryReadRegisteredTrayIcon(new IntPtr(restartedTrayIcon.Handle), out _));
                Assert.Equal(packageExeSha256, Sha256(packageExePath));
            }
            finally
            {
                ShutdownOrKillOwnedProcess(restartedProcess, restartedControl, restartedNonce, restartedShutdownSucceeded);
                ShutdownOrKillOwnedProcess(firstProcess, firstControl, firstNonce, firstShutdownSucceeded);
                restartedControl?.Dispose();
                firstControl?.Dispose();
                restartedProcess?.Dispose();
                firstProcess?.Dispose();
                data.Dispose();
            }

            Assert.False(Directory.Exists(data.DataDirectory));
            WriteSettingsEvidenceMetadata(
                packageExePath,
                packageExeSha256,
                firstProcessId,
                restartedProcessId,
                firstTrayIcon,
                restartedTrayIcon,
                settingsSha256,
                firstSelectedTheme,
                restartedSelectedTheme,
                firstScreenshotPath,
                restartedScreenshotPath);
        });
    }

    private static Process StartPackagedApp(string executablePath, string workingDirectory, string nonce)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--listary-e2e-control=" + nonce,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        return process;
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "ListaryOpen packaged settings E2E"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "The packaged settings E2E did not finish within three minutes.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void ShutdownOrKillOwnedProcess(
        Process? process,
        E2ePipeClient? control,
        string nonce,
        bool shutdownSucceeded)
    {
        if (process is not { HasExited: false })
        {
            return;
        }

        if (!shutdownSucceeded && control is not null)
        {
            try
            {
                _ = control.Exchange(nonce + " Shutdown");
                shutdownSucceeded = process.WaitForExit(10_000);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
            }
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(10_000);
        }
    }

    private static IntPtr OpenSettingsThroughTrayCallback(int processId, TrayIconSnapshot trayIcon)
    {
        AssertRegisteredTrayIcon(trayIcon);
        Assert.True(
            PostMessage(
                new IntPtr(trayIcon.Handle),
                TrayCallbackMessage,
                IntPtr.Zero,
                new IntPtr(WmLeftButtonDoubleClick)),
            "The native notification callback could not be posted to the registered tray icon owner.");

        IntPtr settingsWindow = IntPtr.Zero;
        Assert.True(
            WaitUntil(
                () =>
                {
                    settingsWindow = FindSettingsWindow(processId);
                    return settingsWindow != IntPtr.Zero && IsWindowVisible(settingsWindow);
                },
                TimeSpan.FromSeconds(10)),
            "The registered tray icon's native double-click callback did not open the packaged settings window.");
        return settingsWindow;
    }

    private static IntPtr FindSettingsWindow(int processId)
    {
        var result = IntPtr.Zero;
        _ = EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != unchecked((uint)processId) || !IsWindowVisible(window))
            {
                return true;
            }

            try
            {
                var root = AutomationElement.FromHandle(window);
                var navigation = root?.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavigation"));
                if (navigation is not null)
                {
                    result = window;
                    return false;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string SelectGeekThemeAndSave(int processId, IntPtr settingsWindow)
    {
        var root = AutomationElement.FromHandle(settingsWindow);
        Assert.NotNull(root);
        var appearanceTab = SelectAppearanceTab(root);
        var themeSelector = WaitForAutomationElement(
            root,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsThemeSelector"),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(themeSelector);
        Assert.True(themeSelector.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern));
        ((ExpandCollapsePattern)expandPattern).Expand();

        var geekItem = WaitForOwnedAutomationElement(
            processId,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, "Geek"),
                new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(geekItem);
        Assert.True(geekItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionItemPattern));
        ((SelectionItemPattern)selectionItemPattern).Select();
        ((ExpandCollapsePattern)expandPattern).Collapse();

        var saveButton = WaitForAutomationElement(
            root,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsSaveAppearance"),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(saveButton);
        Assert.True(saveButton.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern));
        ((InvokePattern)invokePattern).Invoke();
        GC.KeepAlive(appearanceTab);
        return ReadSelectedTheme(themeSelector);
    }

    private static string ReadSelectedAppearanceTheme(IntPtr settingsWindow)
    {
        var root = AutomationElement.FromHandle(settingsWindow);
        Assert.NotNull(root);
        _ = SelectAppearanceTab(root);
        var themeSelector = WaitForAutomationElement(
            root,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsThemeSelector"),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(themeSelector);
        return ReadSelectedTheme(themeSelector);
    }

    private static AutomationElement SelectAppearanceTab(AutomationElement settingsRoot)
    {
        var navigation = settingsRoot.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavigation"));
        Assert.NotNull(navigation);
        var appearanceTab = navigation.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsAppearanceTab"));
        if (appearanceTab is null)
        {
            var tabs = navigation.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            Assert.True(tabs.Count >= 2, "The settings navigation did not expose its Appearance tab.");
            appearanceTab = tabs[1];
        }
        Assert.True(appearanceTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var tabSelectionPattern));
        ((SelectionItemPattern)tabSelectionPattern).Select();
        return appearanceTab;
    }

    private static string ReadSelectedTheme(AutomationElement themeSelector)
    {
        Assert.True(themeSelector.TryGetCurrentPattern(SelectionPattern.Pattern, out var selectionPattern));
        var selected = ((SelectionPattern)selectionPattern).Current.GetSelection();
        return Assert.Single(selected).Current.Name;
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

    private static AutomationElement? WaitForOwnedAutomationElement(
        int processId,
        System.Windows.Automation.Condition condition,
        TimeSpan timeout)
    {
        AutomationElement? result = null;
        return WaitUntil(
            () =>
            {
                _ = EnumWindows((window, parameter) =>
                {
                    _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                    if (ownerProcessId != unchecked((uint)processId))
                    {
                        return true;
                    }

                    try
                    {
                        var root = AutomationElement.FromHandle(window);
                        result = root?.FindFirst(TreeScope.Descendants, condition);
                        return result is null;
                    }
                    catch (ElementNotAvailableException)
                    {
                        return true;
                    }
                }, IntPtr.Zero);
                return result is not null;
            },
            timeout)
            ? result
            : null;
    }

    private static bool TryReadPersistedTheme(string settingsPath, out int version, out int theme)
    {
        version = default;
        theme = default;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.TryGetProperty("version", out var versionElement) &&
                versionElement.TryGetInt32(out version) &&
                document.RootElement.TryGetProperty("theme", out var themeElement) &&
                themeElement.TryGetInt32(out theme);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private static string CreateNonce() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSingleInstanceMutexAvailable()
    {
        using var mutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
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

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static IEnumerable<WindowSnapshot> EnumerateVisibleTopLevelWindows(int processId)
    {
        var windows = new List<WindowSnapshot>();
        _ = EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != unchecked((uint)processId) || !IsWindowVisible(window))
            {
                return true;
            }

            var className = ReadWindowClass(window);
            var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            windows.Add(new WindowSnapshot(
                window.ToInt64(),
                ReadWindowText(window),
                className,
                IsWindowEnabled(window),
                (extendedStyle & WsExDlgModalFrame) != 0 || string.Equals(className, "#32770", StringComparison.Ordinal)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string ReadWindowText(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WriteEvidenceMetadata(
        string packageExePath,
        string packageExeSha256,
        int? firstProcessId,
        int? secondProcessId,
        TimeSpan secondElapsed,
        int? secondExitCode,
        bool dataCreatedWhileRunning,
        IReadOnlyList<WindowSnapshot> firstVisibleWindows,
        IReadOnlyList<WindowSnapshot> secondVisibleWindows,
        TrayIconSnapshot? trayIcon)
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(evidenceDirectory);
        var metadataPath = Path.Combine(evidenceDirectory, "29-packaged-app-lifecycle.json");
        var metadata = new
        {
            schemaVersion = 1,
            scenario = "packaged-elevated-app-background-lifecycle-and-single-instance",
            passed = true,
            elevatedTestProcess = true,
            packageExePath,
            packageExeSha256,
            firstProcessId,
            secondProcessId,
            e2eControlCommands = new[] { "Ready", "Ready", "Shutdown" },
            backgroundHealthOracle = new
            {
                readyBeforeDuplicate = true,
                readyAfterDuplicate = true,
                singleInstanceMutexHeld = true,
                firstVisibleTopLevelWindows = firstVisibleWindows,
                registeredWindowsShellTrayIcon = trayIcon
            },
            duplicateLaunchOracle = new
            {
                exitCode = secondExitCode,
                elapsedMilliseconds = secondElapsed.TotalMilliseconds,
                visibleOrModalTopLevelWindows = secondVisibleWindows
            },
            dataDirectoryOracle = new
            {
                initiallyAbsent = true,
                createdWhileRunning = dataCreatedWhileRunning,
                absentAfterCleanup = true
            },
            executableHashUnchangedAfterShutdown = true,
            normalShutdownExitCode = 0
        };
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 512);
    }

    private static string? CaptureWindowEvidence(IntPtr window, string name)
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            return null;
        }

        Directory.CreateDirectory(evidenceDirectory);
        Assert.True(IsWindowVisible(window));
        Assert.True(GetWindowRect(window, out var bounds));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, 400, 4_000);
        Assert.InRange(height, 300, 3_000);

        var desktopDc = GetDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, desktopDc);
        var memoryDc = CreateCompatibleDC(desktopDc);
        var bitmap = CreateCompatibleBitmap(desktopDc, width, height);
        Assert.NotEqual(IntPtr.Zero, memoryDc);
        Assert.NotEqual(IntPtr.Zero, bitmap);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!PrintWindow(window, memoryDc, 0x00000002))
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
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            var path = Path.Combine(evidenceDirectory, name + ".png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            stream.Flush();
            Assert.True(stream.Length > 8_192, $"Packaged settings screenshot was unexpectedly small: {path}");
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

    private static void WriteSettingsEvidenceMetadata(
        string packageExePath,
        string packageExeSha256,
        int? firstProcessId,
        int? restartedProcessId,
        TrayIconSnapshot? firstTrayIcon,
        TrayIconSnapshot? restartedTrayIcon,
        string? settingsSha256,
        string? firstSelectedTheme,
        string? restartedSelectedTheme,
        string? firstScreenshotPath,
        string? restartedScreenshotPath)
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(evidenceDirectory);
        var metadata = new
        {
            schemaVersion = 1,
            scenario = "packaged-tray-settings-persistence-across-restart",
            passed = true,
            elevatedTestProcess = true,
            packageExePath,
            packageExeSha256,
            firstProcessId,
            restartedProcessId,
            trayOracle = new
            {
                discovery = "top-level-and-HWND_MESSAGE-owner-pid plus successful Shell_NotifyIconGetRect(hwnd,uID=0)",
                firstInstance = firstTrayIcon,
                restartedInstance = restartedTrayIcon,
                settingsOpenInteraction = "registered-notification-owner WM_USER+0 / WM_LBUTTONDBLCLK callback"
            },
            persistenceOracle = new
            {
                settingsFile = "data/settings.json",
                settingsSha256,
                version = 4,
                serializedThemeValue = 3,
                firstSelectedTheme,
                restartedSelectedTheme,
                temporaryFileAbsent = true,
                dataDirectoryRemovedAfterTest = true
            },
            screenshots = new[]
            {
                ScreenshotEvidence(firstScreenshotPath),
                ScreenshotEvidence(restartedScreenshotPath)
            }.Where(item => item is not null)
        };
        var metadataPath = Path.Combine(evidenceDirectory, "30-packaged-settings-persistence.json");
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 768);
    }

    private static object? ScreenshotEvidence(string? path) => path is null
        ? null
        : new
        {
            file = Path.GetFileName(path),
            sha256 = Sha256(path)
        };

    private static TrayIconSnapshot FindSingleRegisteredTrayIcon(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<TrayIconSnapshot> matches;
        IReadOnlyList<string> observations = Array.Empty<string>();
        do
        {
            var current = new List<TrayIconSnapshot>();
            var currentObservations = new List<string>();
            void ObserveWindow(IntPtr window)
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != unchecked((uint)processId))
                {
                    return;
                }

                var ownerClass = ReadWindowClass(window);
                var registered = TryReadRegisteredTrayIcon(window, out var bounds);
                currentObservations.Add(
                    $"0x{window.ToInt64():X}:{ownerClass}:{ReadWindowText(window)}:registered={registered}");
                if (registered)
                {
                    current.Add(new TrayIconSnapshot(
                        window.ToInt64(),
                        IconId: 0,
                        ReadWindowText(window),
                        ownerClass,
                        new EvidenceRect(
                            bounds.Left,
                            bounds.Top,
                            bounds.Right - bounds.Left,
                            bounds.Bottom - bounds.Top)));
                }
            }

            _ = EnumWindows((window, parameter) =>
            {
                ObserveWindow(window);

                return true;
            }, IntPtr.Zero);

            // Hardcodet uses an HWND_MESSAGE callback sink on current Windows
            // builds. EnumWindows intentionally omits message-only windows.
            var messageOnlyParent = new IntPtr(-3);
            var messageWindow = IntPtr.Zero;
            while ((messageWindow = FindWindowEx(
                       messageOnlyParent,
                       messageWindow,
                       lpClassName: null,
                       lpWindowName: null)) != IntPtr.Zero)
            {
                ObserveWindow(messageWindow);
            }

            matches = current;
            observations = currentObservations;
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.True(
            matches.Count == 1,
            $"Expected one registered tray icon for process {processId}; found {matches.Count}. " +
            $"Owned windows: {string.Join(" | ", observations)}");
        return matches[0];
    }

    private static void AssertRegisteredTrayIcon(TrayIconSnapshot trayIcon)
    {
        Assert.True(IsWindow(new IntPtr(trayIcon.Handle)), "The tray callback owner window no longer exists.");
        Assert.StartsWith("WPFTaskbarIcon_", ReadWindowClass(new IntPtr(trayIcon.Handle)), StringComparison.Ordinal);
        Assert.True(
            TryReadRegisteredTrayIcon(new IntPtr(trayIcon.Handle), out var bounds),
            "Shell_NotifyIconGetRect no longer recognized the packaged app's notification icon.");
        Assert.True(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top);
    }

    private static bool TryReadRegisteredTrayIcon(IntPtr ownerWindow, out NativeRect bounds)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconIdentifier>()),
            WindowHandle = ownerWindow,
            IconId = 0,
            IconGuid = Guid.Empty
        };
        var result = Shell_NotifyIconGetRect(ref identifier, out bounds);
        // Shell_NotifyIconGetRect returns an HRESULT. S_FALSE (1) is still a
        // successful result and is commonly returned for an icon in the overflow
        // area; Windows supplies its real non-empty bounds in that case.
        return result >= 0 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
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

    private sealed class PackageDataScope : IDisposable
    {
        private readonly string _packageDirectory;
        private bool _disposed;

        private PackageDataScope(string packageDirectory, string dataDirectory)
        {
            _packageDirectory = packageDirectory;
            DataDirectory = dataDirectory;
        }

        public string DataDirectory { get; }

        public static PackageDataScope RequireInitiallyAbsent(string packageDirectory)
        {
            var normalizedPackage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
            var dataDirectory = Path.GetFullPath(Path.Combine(normalizedPackage, "data"));
            Assert.Equal(normalizedPackage, Path.GetDirectoryName(dataDirectory), ignoreCase: true);
            Assert.False(string.Equals(normalizedPackage, dataDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.False(
                Directory.Exists(dataDirectory),
                $"The published package already contains runtime data: {dataDirectory}. " +
                "Use a clean publish directory so the black-box test is deterministic.");
            return new PackageDataScope(normalizedPackage, dataDirectory);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!Directory.Exists(DataDirectory))
            {
                return;
            }

            var resolvedParent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetDirectoryName(DataDirectory)!));
            if (!string.Equals(resolvedParent, _packageDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(DataDirectory), "data", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to remove package data outside the verified package directory: {DataDirectory}");
            }

            var attributes = File.GetAttributes(DataDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to recursively remove a reparse-point package data directory: {DataDirectory}");
            }

            Directory.Delete(DataDirectory, recursive: true);
            if (Directory.Exists(DataDirectory))
            {
                throw new IOException($"Packaged app runtime data was not removed: {DataDirectory}");
            }
        }
    }

    private sealed class VisibleWindowEventMonitor : IDisposable
    {
        private const uint EventObjectShow = 0x8002;
        private const int ObjectIdWindow = 0;
        private const uint WineventOutOfContext = 0x0000;
        private const uint WineventSkipOwnProcess = 0x0002;
        private const uint GetAncestorRoot = 2;
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _ready = new();
        private readonly WinEventCallback _callback;
        private readonly Thread _thread;
        private readonly List<ObservedWindow> _observed = new();
        private Dispatcher? _dispatcher;
        private Exception? _startupException;
        private IntPtr _hook;
        private bool _stopped;

        private VisibleWindowEventMonitor()
        {
            _callback = OnWindowEvent;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ListaryOpen duplicate-window WinEvent monitor"
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public static VisibleWindowEventMonitor Start()
        {
            var monitor = new VisibleWindowEventMonitor();
            monitor._thread.Start();
            if (!monitor._ready.Wait(TimeSpan.FromSeconds(5)))
            {
                monitor.Stop();
                throw new TimeoutException("The duplicate-window WinEvent monitor did not start.");
            }

            if (monitor._startupException is not null)
            {
                monitor.Stop();
                throw new InvalidOperationException(
                    "The duplicate-window WinEvent monitor could not be installed.",
                    monitor._startupException);
            }

            return monitor;
        }

        public IReadOnlyList<WindowSnapshot> GetObservedWindows(int processId)
        {
            lock (_gate)
            {
                return _observed
                    .Where(item => item.ProcessId == processId)
                    .Select(item => item.Window)
                    .ToArray();
            }
        }

        public void Stop()
        {
            Dispatcher? dispatcher;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                dispatcher = _dispatcher;
            }

            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            if (_thread.IsAlive && Thread.CurrentThread != _thread)
            {
                _ = _thread.Join(TimeSpan.FromSeconds(5));
            }
        }

        public void Dispose()
        {
            Stop();
            _ready.Dispose();
        }

        private void Run()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _hook = SetWinEventHook(
                    EventObjectShow,
                    EventObjectShow,
                    IntPtr.Zero,
                    _callback,
                    0,
                    0,
                    WineventOutOfContext | WineventSkipOwnProcess);
                if (_hook == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"SetWinEventHook(EVENT_OBJECT_SHOW) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }
            }
            catch (Exception exception)
            {
                _startupException = exception;
            }
            finally
            {
                _ready.Set();
            }

            if (_hook == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Dispatcher.Run();
            }
            finally
            {
                _ = UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private void OnWindowEvent(
            IntPtr hook,
            uint eventType,
            IntPtr window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (eventType != EventObjectShow ||
                objectId != ObjectIdWindow ||
                childId != 0 ||
                window == IntPtr.Zero ||
                GetAncestor(window, GetAncestorRoot) != window ||
                !IsWindowVisible(window))
            {
                return;
            }

            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return;
            }

            var className = ReadWindowClass(window);
            var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            var snapshot = new WindowSnapshot(
                window.ToInt64(),
                ReadWindowText(window),
                className,
                IsWindowEnabled(window),
                (extendedStyle & WsExDlgModalFrame) != 0 || string.Equals(className, "#32770", StringComparison.Ordinal));
            lock (_gate)
            {
                _observed.Add(new ObservedWindow(unchecked((int)processId), snapshot));
            }

            GC.KeepAlive(hook);
            GC.KeepAlive(eventThread);
            GC.KeepAlive(eventTime);
        }

        private sealed record ObservedWindow(int ProcessId, WindowSnapshot Window);
    }

    private sealed record WindowSnapshot(
        long Handle,
        string Title,
        string ClassName,
        bool IsEnabled,
        bool IsModal);

    private sealed record TrayIconSnapshot(
        long Handle,
        uint IconId,
        string OwnerWindowTitle,
        string OwnerWindowClass,
        EvidenceRect Bounds);

    private sealed record EvidenceRect(int Left, int Top, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint IconId;
        public Guid IconGuid;
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

    private delegate void WinEventCallback(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr parentWindow,
        IntPtr childAfter,
        string? lpClassName,
        string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr destination, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder buffer, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder buffer, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        IntPtr eventHookModule,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(
        ref NotifyIconIdentifier identifier,
        out NativeRect iconLocation);
}
