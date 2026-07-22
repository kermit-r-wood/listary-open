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

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class PackagedFirefoxBlackboxIntegrationTests
{
    private const byte VkControl = 0x11;
    private const byte VkG = 0x47;
    private const byte VkO = 0x4F;
    private const byte VkMenu = 0x12;
    private const byte VkReturn = 0x0D;
    private const byte VkEscape = 0x1B;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint WmClose = 0x0010;
    private const int SwRestore = 9;

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public async Task PublishedAppUsesCtrlGAndItsNativeHookToNavigateARealFirefoxPicker()
    {
        await RunInStaAsync(() =>
        {
            Assert.True(
                IsCurrentProcessElevated(),
                "ElevatedPackagedBlackboxE2E must run in an elevated test process; a non-elevated run is a test failure, not a skip or simulated pass.");

            var repository = FindRepositoryRoot();
            var packageValue = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
            Assert.False(
                string.IsNullOrWhiteSpace(packageValue),
                "LISTARYOPEN_PACKAGE_DIR must identify the final published package under test.");
            var packageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageValue));
            var packageExePath = Path.Combine(packageDirectory, "ListaryOpen.App.exe");
            var hookDllPath = Path.Combine(packageDirectory, "hooks", "x64", "ListaryOpen.Hook.dll");
            var hookRuntimePath = Path.Combine(packageDirectory, "hooks", "x64", "libunwind.dll");
            Assert.True(File.Exists(packageExePath), $"The packaged app executable is missing: {packageExePath}");
            Assert.True(File.Exists(hookDllPath), $"The packaged x64 native hook DLL is missing: {hookDllPath}");
            Assert.True(File.Exists(hookRuntimePath), $"The packaged x64 native hook runtime is missing: {hookRuntimePath}");
            Assert.True(
                IsSingleInstanceMutexAvailable(),
                "ListaryOpen is already running. The black-box test will not terminate a user-owned instance.");

            var packageExeSha256 = Sha256(packageExePath);
            var hookDllSha256 = Sha256(hookDllPath);
            using var packageData = PackageDataCleanup.RequireInitiallyAbsent(packageDirectory);
            using var target = TemporaryDirectory.Create("listary-firefox-packaged-target");
            var selectedFileName = $"selected-{Guid.NewGuid():N}.txt";
            File.WriteAllText(Path.Combine(target.Path, selectedFileName), "Packaged Firefox native-hook black-box acceptance marker.");

            var existingExplorerHandles = EnumerateExplorerWindows()
                .Select(window => window.Handle)
                .ToHashSet();
            LaunchExplorer(target.Path);
            var explorer = WaitForExplorerWindow(target.Path, existingExplorerHandles, TimeSpan.FromSeconds(15));
            Assert.NotNull(explorer);
            using var explorerCleanup = new OwnedExplorerWindow(explorer.Handle, target.Path);
            Assert.True(TryActivateWindow(explorer.Handle));

            var firefoxPath = FindFirefox();
            Assert.True(File.Exists(firefoxPath), $"Firefox executable was not found: {firefoxPath}");

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Process? appProcess = null;
            E2ePipeClient? control = null;
            FirefoxSession? firefox = null;
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

                // Keep the test-owned Explorer in front long enough for the production
                // observation scheduler to remember it before Firefox exists.
                Assert.True(TryActivateWindow(explorer.Handle));
                Thread.Sleep(TimeSpan.FromSeconds(2));

                firefox = FirefoxSession.Start(firefoxPath, repository);
                Assert.True(TryActivateWindow(firefox.MainWindow));
                // Expire the six-second automatic dialog-jump window while Firefox—not
                // Explorer—is foreground. The subsequent movement must be caused by Ctrl+G.
                Thread.Sleep(TimeSpan.FromSeconds(7));
                var dialog = firefox.OpenFilePicker();
                var dialogClass = ReadWindowClass(dialog);
                var dialogProcessName = ReadProcessName(dialog);
                Assert.Equal("#32770", dialogClass);
                Assert.Equal("firefox", dialogProcessName, ignoreCase: true);
                _ = GetWindowThreadProcessId(dialog, out var dialogProcessId);
                Assert.NotEqual(0u, dialogProcessId);

                Assert.True(
                    WaitUntil(
                        () => IsModuleLoaded((int)dialogProcessId, hookDllPath),
                        TimeSpan.FromSeconds(15)),
                    "The package hook DLL was not loaded into the Firefox file-dialog process.");

                var fileNameBefore = ReadFileNameEdit(dialog);
                Assert.Equal(string.Empty, fileNameBefore);
                Assert.False(
                    DialogContainsExactName(dialog, selectedFileName),
                    "The picker already displayed the target marker before Ctrl+G, so this run cannot prove hotkey navigation.");
                Assert.False(
                    DialogBreadcrumbShowsFolder(dialog, target.Path),
                    "The picker reached the target before Ctrl+G; automatic navigation would make this a false green.");

                var quickSwitch = WaitForTopLevelWindow(appProcess.Id, "Quick Switch", TimeSpan.FromSeconds(12));
                Assert.NotEqual(IntPtr.Zero, quickSwitch);
                Assert.True(IsWindowVisible(quickSwitch));
                var precaptureProofReply = control.Exchange(nonce + " DialogPrecaptureProof " + dialogProcessId);
                var precaptureProofFields = precaptureProofReply.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(4, precaptureProofFields.Length);
                Assert.Equal("PrecaptureProof", precaptureProofFields[0]);
                Assert.Equal(dialogProcessId, uint.Parse(precaptureProofFields[1], System.Globalization.CultureInfo.InvariantCulture));
                var firefoxFileDialogUtility = bool.Parse(precaptureProofFields[2]);
                var preloadConfirmedBeforeDialog = bool.Parse(precaptureProofFields[3]);
                var queryBefore = ReadQuickSwitchQuery(quickSwitch);
                Assert.Equal(string.Empty, queryBefore);

                Assert.True(TryActivateWindow(dialog));
                SendCtrlG();
                Assert.True(
                    WaitUntil(
                        () => DialogBreadcrumbShowsFolder(dialog, target.Path) &&
                            DialogContainsExactName(dialog, selectedFileName),
                        TimeSpan.FromSeconds(12)),
                    $"The packaged app did not directly navigate Firefox to '{target.Path}' after real Ctrl+G input.");

                var fileNameAfter = ReadFileNameEdit(dialog);
                var queryAfter = ReadQuickSwitchQuery(quickSwitch);
                Assert.Equal(fileNameBefore, fileNameAfter);
                Assert.Equal(string.Empty, fileNameAfter);
                Assert.Equal(queryBefore, queryAfter);
                Assert.Equal(string.Empty, queryAfter);
                Assert.True(IsModuleLoaded((int)dialogProcessId, hookDllPath));

                Assert.True(GetWindowRect(dialog, out var dialogBounds));
                Assert.True(GetWindowRect(quickSwitch, out var quickSwitchBounds));
                Assert.True(IsWindowVisible(quickSwitch), "Quick Switch was not visible in the final composed evidence.");
                Assert.Equal(0, ReadCloakedState(quickSwitch));
                Assert.InRange(
                    Math.Min(
                        Math.Abs(quickSwitchBounds.Top - dialogBounds.Bottom),
                        Math.Abs(quickSwitchBounds.Bottom - dialogBounds.Top)),
                    0,
                    16);
                AssertQuickSwitchUnoccluded(dialogBounds, quickSwitchBounds, quickSwitch);
                var screenshotPath = CaptureComposedEvidence(
                    dialogBounds,
                    quickSwitchBounds,
                    "30-packaged-firefox-direct-hotkey");

                var hookLoaded = IsModuleLoaded((int)dialogProcessId, hookDllPath);
                var fallbackDetected = !hookLoaded ||
                    fileNameBefore.Length != 0 ||
                    !string.Equals(fileNameBefore, fileNameAfter, StringComparison.Ordinal) ||
                    queryBefore.Length != 0 ||
                    !string.Equals(queryBefore, queryAfter, StringComparison.Ordinal);
                Assert.False(
                    fallbackDetected,
                    "A path-input fallback was observable; this scenario only accepts native-hook direct navigation.");
                Assert.True(TryActivateWindow(dialog));
                SendVirtualKey(VkEscape);
                // Quick Switch can retain focus after the native jump. Close
                // the owned dialog explicitly so cleanup does not turn a
                // successful navigation assertion into a false failure.
                if (!WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(2)))
                {
                    PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
                }
                Assert.True(
                    WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(5)),
                    "The packaged Firefox Ctrl+O dialog did not close on Escape after jump verification.");

                WriteEvidenceMetadata(
                    screenshotPath,
                    packageExePath,
                    packageExeSha256,
                    hookDllPath,
                    hookDllSha256,
                    appProcess.Id,
                    firefoxPath,
                    firefox.MainWindow,
                    dialog,
                    dialogProcessId,
                    firefoxFileDialogUtility,
                    preloadConfirmedBeforeDialog,
                    dialogClass,
                    dialogProcessName,
                    explorer.Handle,
                    target.Path,
                    selectedFileName,
                    fileNameBefore,
                    fileNameAfter,
                    queryBefore,
                    queryAfter,
                    hookLoaded,
                    fallbackDetected,
                    dialogBounds,
                    quickSwitch,
                    quickSwitchBounds);

                Assert.Equal("Ok", control.Exchange(nonce + " Shutdown"));
                shutdownSucceeded = appProcess.WaitForExit(15_000);
                Assert.True(shutdownSucceeded, "The packaged app did not exit after authenticated Shutdown.");
                Assert.Equal(0, appProcess.ExitCode);
                var unloadStopwatch = Stopwatch.StartNew();
                var nativeModulesUnloaded =
                    WaitUntil(
                        () => AreModulesUnloaded((int)dialogProcessId, hookDllPath, hookRuntimePath),
                        TimeSpan.FromSeconds(10));
                Assert.True(
                    nativeModulesUnloaded,
                    "The published app exited but its native hook DLL/runtime remained loaded in Firefox.");
                AppendNativeUnloadEvidence(
                    Path.Combine(Path.GetDirectoryName(screenshotPath)!, "30-packaged-firefox-direct-hotkey.json"),
                    "firefox",
                    dialogProcessId,
                    hookDllPath,
                    hookRuntimePath,
                    unloadStopwatch.Elapsed);
            }
            finally
            {
                firefox?.Dispose();
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
            Assert.Equal(hookDllSha256, Sha256(hookDllPath));
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
            Name = "ListaryOpen packaged Firefox black-box E2E"
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

    private static ExplorerWindow? WaitForExplorerWindow(
        string folderPath,
        IReadOnlySet<IntPtr> existingHandles,
        TimeSpan timeout)
    {
        ExplorerWindow? match = null;
        return WaitUntil(
            () =>
            {
                match = EnumerateExplorerWindows().FirstOrDefault(window =>
                    !existingHandles.Contains(window.Handle) && PathsEqual(window.FolderPath, folderPath));
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
        exception is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException ||
        string.Equals(exception.GetType().FullName, "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException", StringComparison.Ordinal);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static string FindFirefox()
    {
        var configured = Environment.GetEnvironmentVariable("LISTARYOPEN_FIREFOX_PATH");
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "firefox", "current", "firefox.exe")
        };
        candidates.AddRange(Process.GetProcessesByName("firefox").Select(process =>
        {
            using (process)
            {
                try { return process.MainModule?.FileName; }
                catch { return null; }
            }
        }));
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? string.Empty;
    }

    private static IntPtr WaitForTopLevelWindow(int processId, string title, TimeSpan timeout)
    {
        var result = IntPtr.Zero;
        Assert.True(
            WaitUntil(() =>
            {
                result = FindWindow(window =>
                {
                    _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                    return ownerProcessId == unchecked((uint)processId) &&
                        string.Equals(ReadWindowText(window), title, StringComparison.Ordinal) &&
                        IsWindowVisible(window);
                });
                return result != IntPtr.Zero;
            }, timeout),
            $"A visible '{title}' window owned by PID {processId} did not appear.");
        return result;
    }

    private static string ReadFileNameEdit(IntPtr dialog)
    {
        var root = AutomationElement.FromHandle(dialog);
        Assert.NotNull(root);
        var edit = root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        Assert.NotNull(edit);
        Assert.True(edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern));
        return Assert.IsType<ValuePattern>(pattern).Current.Value;
    }

    private static string ReadQuickSwitchQuery(IntPtr quickSwitch)
    {
        var root = AutomationElement.FromHandle(quickSwitch);
        Assert.NotNull(root);
        var query = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Quick Switch folder search"));
        Assert.NotNull(query);
        Assert.True(query.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern));
        return Assert.IsType<ValuePattern>(pattern).Current.Value;
    }

    private static bool DialogContainsExactName(IntPtr dialog, string name)
    {
        try
        {
            var root = AutomationElement.FromHandle(dialog);
            return root?.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name)) is not null;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool DialogBreadcrumbShowsFolder(IntPtr dialog, string folderPath)
    {
        try
        {
            var expected = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
            var root = AutomationElement.FromHandle(dialog);
            if (root is null)
            {
                return false;
            }

            return root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element =>
                {
                    var type = element.Current.ControlType;
                    return (type == ControlType.Button || type == ControlType.Text || type == ControlType.ToolBar) &&
                        element.Current.Name.Contains(expected, StringComparison.OrdinalIgnoreCase);
                });
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool ClickDialogFile(IntPtr dialog, string fileName)
    {
        AutomationElement? file = null;
        if (!WaitUntil(() =>
            {
                try
                {
                    file = AutomationElement.FromHandle(dialog)?.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, fileName));
                    return file is not null && !file.Current.BoundingRectangle.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(6)))
        {
            return false;
        }

        var bounds = file!.Current.BoundingRectangle;
        Assert.True(SetCursorPos(
            checked((int)Math.Round(bounds.Left + bounds.Width / 2)),
            checked((int)Math.Round(bounds.Top + bounds.Height / 2))));
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    private static bool IsModuleLoaded(int processId, string expectedPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(module.FileName, expectedPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool AreModulesUnloaded(int processId, params string[] expectedPaths)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var loadedPaths = process.Modules.Cast<ProcessModule>()
                .Select(module => Path.GetFullPath(module.FileName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return expectedPaths.All(path => !loadedPaths.Contains(Path.GetFullPath(path)));
        }
        catch (ArgumentException)
        {
            // A terminated Firefox process cannot retain an injected module.
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Module enumeration failure is not proof of unload.
            return false;
        }
    }

    private static void SendCtrlG()
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkG, 0, 0, UIntPtr.Zero);
        keybd_event(VkG, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static void SendCtrlO()
        => DesktopInput.SendChord(VkControl, VkO);

    private static void SendVirtualKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static bool TryActivateWindow(IntPtr window)
    {
        _ = ShowWindow(window, SwRestore);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event(VkMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
            _ = SetForegroundWindow(window);
            if (WaitUntil(() => GetForegroundWindow() == window, TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
        }

        return false;
    }

    private static int ReadCloakedState(IntPtr window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)));
        return cloaked;
    }

    private static void AssertQuickSwitchUnoccluded(NativeRect dialog, NativeRect quickSwitch, IntPtr quickSwitchHandle)
    {
        var y = quickSwitch.Top >= dialog.Bottom - 16
            ? Math.Min(quickSwitch.Bottom - 12, dialog.Bottom + 20)
            : Math.Max(quickSwitch.Top + 12, dialog.Top - 20);
        Assert.InRange(y, quickSwitch.Top + 8, quickSwitch.Bottom - 9);
        foreach (var x in new[] { quickSwitch.Left + 20, (quickSwitch.Left + quickSwitch.Right) / 2, quickSwitch.Right - 20 })
        {
            Assert.Equal(quickSwitchHandle, GetAncestor(WindowFromPoint(new NativePoint(x, y)), 2));
        }
    }

    private static string CaptureComposedEvidence(NativeRect dialog, NativeRect quickSwitch, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        _ = DwmFlush();
        Thread.Sleep(150);

        var left = Math.Min(dialog.Left, quickSwitch.Left) - 12;
        var top = Math.Min(dialog.Top, quickSwitch.Top) - 12;
        var right = Math.Max(dialog.Right, quickSwitch.Right) + 12;
        var bottom = Math.Max(dialog.Bottom, quickSwitch.Bottom) + 12;
        var width = right - left;
        var height = bottom - top;
        var desktop = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(memory, 0, 0, width, height, desktop, left, top, SourceCopy | CaptureBlt));
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
            Assert.True(stream.Length > 10_000, $"The Firefox/Quick Switch screenshot is unexpectedly small: {path}");
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
        string packageExePath,
        string packageExeSha256,
        string hookDllPath,
        string hookDllSha256,
        int appProcessId,
        string firefoxPath,
        IntPtr firefoxWindow,
        IntPtr dialog,
        uint dialogProcessId,
        bool firefoxFileDialogUtility,
        bool preloadConfirmedBeforeDialog,
        string dialogClass,
        string dialogProcessName,
        IntPtr explorerWindow,
        string targetFolder,
        string selectedFileName,
        string fileNameBefore,
        string fileNameAfter,
        string queryBefore,
        string queryAfter,
        bool hookLoaded,
        bool fallbackDetected,
        NativeRect dialogBounds,
        IntPtr quickSwitch,
        NativeRect quickSwitchBounds)
    {
        var metadataPath = Path.Combine(Path.GetDirectoryName(screenshotPath)!, "30-packaged-firefox-direct-hotkey.json");
        var metadata = new
        {
            schemaVersion = 1,
            scenario = "published-elevated-app-real-firefox-native-hook-ctrl-g",
            passed = true,
            package = new
            {
                executable = packageExePath,
                executableSha256 = packageExeSha256,
                processId = appProcessId,
                nativeHookDll = hookDllPath,
                nativeHookDllSha256 = hookDllSha256
            },
            realWindows = new
            {
                explorerHwnd = explorerWindow.ToInt64(),
                firefoxHwnd = firefoxWindow.ToInt64(),
                firefoxPath,
                firefoxVersion = FileVersionInfo.GetVersionInfo(firefoxPath).FileVersion,
                dialogHwnd = dialog.ToInt64(),
                dialogProcessId,
                dialogClass,
                dialogProcessName,
                dialogBounds = EvidenceBounds(dialogBounds),
                quickSwitchHwnd = quickSwitch.ToInt64(),
                quickSwitchBounds = EvidenceBounds(quickSwitchBounds)
            },
            trigger = new
            {
                input = "Ctrl+G",
                e2eControlCommands = new[] { "Ready", "AllowInjectedInput", "DialogPrecaptureProof", "Shutdown" },
                testConstructedQuickSwitch = false,
                testConstructedDialogBridge = false,
                testInvokedBridge = false
            },
            directNavigationOracle = new
            {
                targetFolder,
                marker = selectedFileName,
                breadcrumbReachedTarget = true,
                markerVisibleAfterHotkey = true,
                nativeHookDllLoadedInDialogProcess = hookLoaded,
                firefoxFileDialogUtility,
                preloadConfirmedBeforeDialog,
                fileNameEditAutomationId = "1148",
                fileNameBefore,
                fileNameAfter,
                quickSwitchQueryBefore = queryBefore,
                quickSwitchQueryAfter = queryAfter,
                fileNameInputUnchangedAndEmpty = fileNameBefore.Length == 0 && fileNameBefore == fileNameAfter,
                quickSwitchQueryUnchangedAndEmpty = queryBefore.Length == 0 && queryBefore == queryAfter,
                fallback = fallbackDetected
            },
            selectionOracle = new
            {
                fileOpened = false,
                dialogClosedWithoutOpening = true
            },
            visualOracle = new
            {
                quickSwitchVisible = true,
                quickSwitchUncloaked = true,
                quickSwitchUnoccluded = true,
                screenshot = Path.GetFileName(screenshotPath),
                screenshotSha256 = Sha256(screenshotPath)
            }
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 1_000);
    }

    private static object EvidenceBounds(NativeRect bounds) => new
    {
        left = bounds.Left,
        top = bounds.Top,
        right = bounds.Right,
        bottom = bounds.Bottom,
        width = bounds.Right - bounds.Left,
        height = bounds.Bottom - bounds.Top
    };

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

    private static IntPtr FindWindow(Func<IntPtr, bool> predicate)
    {
        var result = IntPtr.Zero;
        _ = EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && predicate(window))
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
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

    private static string ReadProcessName(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
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

    private sealed record ExplorerWindow(IntPtr Handle, string FolderPath);

    private sealed class OwnedExplorerWindow : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly string _folder;

        public OwnedExplorerWindow(IntPtr handle, string folder)
        {
            _handle = handle;
            _folder = folder;
        }

        public void Dispose()
        {
            if (!IsWindow(_handle) ||
                !EnumerateExplorerWindows().Any(window => window.Handle == _handle && PathsEqual(window.FolderPath, _folder)))
            {
                return;
            }
            _ = PostMessage(_handle, WmClose, IntPtr.Zero, IntPtr.Zero);
            _ = WaitUntil(() => !IsWindow(_handle), TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FirefoxSession : IDisposable
    {
        private readonly HashSet<int> _existingProcessIds;
        private readonly string _profile;
        private readonly IntPtr _mainWindow;
        private readonly int _launchedProcessId;
        private readonly int _mainWindowProcessId;

        private FirefoxSession(
            HashSet<int> existingProcessIds,
            string profile,
            IntPtr mainWindow,
            int launchedProcessId,
            int mainWindowProcessId)
        {
            _existingProcessIds = existingProcessIds;
            _profile = profile;
            _mainWindow = mainWindow;
            _launchedProcessId = launchedProcessId;
            _mainWindowProcessId = mainWindowProcessId;
        }

        public IntPtr MainWindow => _mainWindow;
        public string MainWindowTitle => ReadWindowText(_mainWindow);

        public static FirefoxSession Start(string firefoxPath, string repository)
        {
            var existing = Process.GetProcessesByName("firefox")
                .Select(process => { using (process) { return process.Id; } })
                .ToHashSet();
            var profile = Path.Combine(Path.GetTempPath(), "listary-packaged-firefox-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            File.WriteAllText(
                Path.Combine(profile, "user.js"),
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");\n" +
                "user_pref(\"accessibility.force_disabled\", -1);\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n");
            var fixture = new Uri(Path.Combine(repository, "tests", "fixtures", "firefox-file-dialog.html")).AbsoluteUri;
            var start = new ProcessStartInfo
            {
                FileName = firefoxPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repository
            };
            foreach (var argument in new[] { "-no-remote", "-profile", profile, "-new-window", fixture })
            {
                start.ArgumentList.Add(argument);
            }
            using var launched = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated Firefox.");
            var launchedProcessId = launched.Id;
            var main = IntPtr.Zero;
            Assert.True(
                WaitUntil(() =>
                {
                    main = FindWindow(window =>
                        string.Equals(ReadWindowClass(window), "MozillaWindowClass", StringComparison.Ordinal) &&
                        ReadWindowText(window).Contains("ListaryOpen Firefox file dialog fixture", StringComparison.Ordinal) &&
                        IsOwnedFirefoxWindow(window, existing));
                    return main != IntPtr.Zero;
                }, TimeSpan.FromSeconds(20)),
                "The isolated Firefox fixture window did not appear.");
            _ = GetWindowThreadProcessId(main, out var mainWindowProcessId);
            Assert.NotEqual(0u, mainWindowProcessId);
            return new FirefoxSession(
                existing,
                profile,
                main,
                launchedProcessId,
                checked((int)mainWindowProcessId));
        }

        public IntPtr OpenFilePicker()
        {
            var dialog = IntPtr.Zero;
            Assert.True(TryActivateWindow(_mainWindow));
            SendCtrlO();
            Assert.True(
                WaitUntil(() =>
                {
                    dialog = FindWindow(window =>
                        string.Equals(ReadWindowClass(window), "#32770", StringComparison.Ordinal) &&
                        string.Equals(ReadProcessName(window), "firefox", StringComparison.OrdinalIgnoreCase) &&
                        IsOwnedFirefoxWindow(window, _existingProcessIds));
                    return dialog != IntPtr.Zero;
                }, TimeSpan.FromSeconds(15)),
                "Firefox Ctrl+O did not create a test-owned native file dialog.");
            Assert.NotEqual(IntPtr.Zero, dialog);
            Assert.True(TryActivateWindow(dialog));
            return dialog;
        }

        public string? ReadPageTitle()
        {
            try
            {
                var root = AutomationElement.FromHandle(_mainWindow);
                var selectedTab = root?.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(element =>
                        element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern) &&
                        ((SelectionItemPattern)pattern).Current.IsSelected);
                if (!string.IsNullOrWhiteSpace(selectedTab?.Current.Name))
                {
                    return selectedTab.Current.Name;
                }

                var title = MainWindowTitle;
                var separator = title.IndexOf(" — Mozilla Firefox", StringComparison.Ordinal);
                return separator >= 0 ? title[..separator] : title;
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var processId in new[] { _mainWindowProcessId, _launchedProcessId }.Distinct())
            {
                if (_existingProcessIds.Contains(processId))
                {
                    continue;
                }
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(3_000);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            for (var attempt = 0; attempt < 10 && Directory.Exists(_profile); attempt++)
            {
                try { Directory.Delete(_profile, recursive: true); }
                catch { Thread.Sleep(100); }
            }
        }

        private static bool IsOwnedFirefoxWindow(IntPtr window, IReadOnlySet<int> existing)
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            return processId != 0 && !existing.Contains((int)processId) &&
                string.Equals(ReadProcessName(window), "firefox", StringComparison.OrdinalIgnoreCase);
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
                        $"The packaged app exited with code {appProcess.ExitCode} before Ready.",
                        lastError);
                }
                DismissOwnedHotkeyStartupMessage(appProcess.Id);
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
            throw new TimeoutException("The packaged app did not create its nonce-scoped E2E endpoint.", lastError);
        }

        private static void DismissOwnedHotkeyStartupMessage(int appProcessId)
        {
            var message = FindWindow(window =>
            {
                _ = GetWindowThreadProcessId(window, out var processId);
                return processId == appProcessId &&
                    string.Equals(ReadWindowClass(window), "#32770", StringComparison.Ordinal) &&
                    string.Equals(ReadWindowText(window), "ListaryOpen Hotkeys", StringComparison.Ordinal);
            });
            if (message != IntPtr.Zero)
            {
                _ = PostMessage(message, WmClose, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public string Exchange(string request)
        {
            _writer.WriteLine(request);
            return _reader.ReadLine() ?? throw new IOException("The app closed the E2E pipe without a response.");
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
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
            var data = Path.GetFullPath(Path.Combine(normalized, "data"));
            Assert.Equal(normalized, Path.GetDirectoryName(data), ignoreCase: true);
            Assert.False(Directory.Exists(data), $"The package data directory must initially be absent: {data}");
            return new PackageDataCleanup(normalized, data);
        }

        public void Dispose()
        {
            if (!Directory.Exists(_dataDirectory))
            {
                return;
            }
            Assert.Equal(_packageDirectory, Path.GetDirectoryName(_dataDirectory), ignoreCase: true);
            Assert.False((File.GetAttributes(_dataDirectory) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(_dataDirectory, recursive: true);
            Assert.False(Directory.Exists(_dataDirectory));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TemporaryDirectory Create(string prefix) =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
}
