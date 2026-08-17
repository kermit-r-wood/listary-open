using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Hooks;
using ListaryOpen.Infrastructure.Windows;
using WpfApplication = ListaryOpen.App.App;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class FirefoxQuickSwitchIntegrationTests
{
    private const byte VkMenu = 0x12;
    private const byte VkControl = 0x11;
    private const byte VkO = 0x4F;
    private const byte VkTab = 0x09;
    private const byte VkReturn = 0x0D;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint CaptureBlt = 0x40000000;
    private const uint SourceCopy = 0x00CC0020;

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public void FirefoxCtrlOOpensNativeDialogWithoutHook()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var firefoxPath = FindFirefox();
                Assert.True(File.Exists(firefoxPath), $"Firefox executable was not found: {firefoxPath}");
                using var firefox = FirefoxSession.Start(firefoxPath, FindRepositoryRoot());
                var dialog = firefox.OpenFilePicker();
                Assert.NotEqual(IntPtr.Zero, dialog);
                Assert.Equal("#32770", GetClassName(dialog));
                Assert.Equal("firefox", GetProcessName(dialog), ignoreCase: true);
                FirefoxFixtureUi.CaptureWindowEvidence(dialog, "firefox-no-hook-native-dialog.png");
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Firefox Ctrl+O baseline timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public void FirefoxCtrlOFilePickerAttachesQuickSwitchAndJumpsWithoutOpeningFile()
    {
        Exception? failure = null;
        var completed = false;
        var stage = "not started";
        var thread = new Thread(() =>
        {
            WpfApplication? application = null;
            QuickSwitchBarWindow? quickSwitch = null;
            FirefoxSession? firefox = null;
            NativeFirefoxHookSession? nativeHook = null;
            try
            {
                stage = "locate Firefox";
                var firefoxPath = FindFirefox();
                Assert.True(File.Exists(firefoxPath), $"Firefox executable was not found: {firefoxPath}");

                stage = "launch local Firefox fixture";
                firefox = FirefoxSession.Start(firefoxPath, FindRepositoryRoot());
                stage = "preload native hook before Firefox file picker";
#pragma warning disable xUnit1031 // The dedicated STA thread waits for the external native hook process.
                nativeHook = NativeFirefoxHookSession.StartPreloadedAsync(firefox.MainWindow).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                stage = "open Firefox file picker";
                var dialog = firefox.OpenFilePicker();
                var dialogClassName = GetClassName(dialog);
                var dialogProcessName = GetProcessName(dialog);
                _ = GetWindowThreadProcessId(dialog, out var dialogProcessId);
                Assert.Equal("#32770", dialogClassName);
                Assert.Equal("firefox", dialogProcessName, ignoreCase: true);
                Assert.NotEqual(0u, dialogProcessId);

                stage = "capture Firefox through native hook";
#pragma warning disable xUnit1031 // The dedicated STA thread waits for the external native hook process.
                var capturedDialog = nativeHook.CaptureDialogAsync(dialog).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                Assert.Equal(dialog, capturedDialog.WindowHandle);
                Assert.Equal(dialogProcessId, capturedDialog.ProcessId);
                Assert.Equal("firefox", Path.GetFileNameWithoutExtension(capturedDialog.ProcessName), ignoreCase: true);
                Assert.True(
                    capturedDialog.FirefoxFileDialogUtility,
                    $"Firefox file-dialog PID {dialogProcessId} was not classified as the authorized sandboxingKind 4 Firefox utility process.");
                Assert.True(
                    capturedDialog.PreloadConfirmedBeforeDialog,
                    $"Firefox file-dialog PID {dialogProcessId} was not armed before the native picker was shown.");
                Assert.True(
                    nativeHook.IsHookDllLoaded((int)dialogProcessId),
                    "The native hook DLL was not loaded into Firefox's picker process.");

                stage = "initialize production Quick Switch";
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                application = new WpfApplication();
                application.InitializeComponent();
                ThemeManager.Apply(AppTheme.Light);
                LocalizationManager.Apply(AppLanguage.English);

                using var target = TemporaryDirectory.Create("listary-firefox-target");
                var selectedFileName = $"listary-firefox-e2e-{Guid.NewGuid():N}.txt";
                File.WriteAllText(Path.Combine(target.Path, selectedFileName), "Firefox Quick Switch integration");
                DialogJumpResult? activationResult = null;
                var viewModel = new SearchPanelViewModel(new EmptySearchIndex());
                quickSwitch = new QuickSwitchBarWindow(viewModel, SetForegroundWindow);
                CompleteWithDispatcher(quickSwitch.AttachAsync(
                        [new QuickSwitchFolderCandidate(target.Path, "Firefox E2E", dialog, true)],
                        async (folder, cancellationToken) =>
                        {
                            var hookResult = await nativeHook.Bridge.JumpDialogToFolderAsync(
                                capturedDialog,
                                folder,
                                cancellationToken);
                            activationResult = MapNativeHookResult(hookResult);
                            return activationResult;
                        },
                        dialog));
                Assert.NotEmpty(viewModel.Results);
                viewModel.SelectedResult = viewModel.Results[0];
                quickSwitch.UpdateLayout();
                PumpRender();

                stage = "measure composed Firefox and Quick Switch";
                var quickSwitchHandle = new WindowInteropHelper(quickSwitch).Handle;
                Assert.NotEqual(IntPtr.Zero, quickSwitchHandle);
                Assert.True(IsWindowVisible(quickSwitchHandle));
                Assert.True(GetWindowRect(dialog, out var dialogBounds));
                Assert.True(GetWindowRect(quickSwitchHandle, out var quickBounds));
                Assert.InRange(Math.Abs(quickBounds.Top - dialogBounds.Bottom), 0, 16);
                var fileNameBeforeJump = GetDialogFileNameValue(dialog);
                Assert.Equal(string.Empty, fileNameBeforeJump);
                stage = "activate expanded Quick Switch before native navigation";
                quickSwitch.Expand();
                quickSwitch.UpdateLayout();
                PumpRender();

                stage = "activate Quick Switch folder";
                CompleteWithDispatcher(quickSwitch.ActivateSelectedAsync());
                Assert.NotNull(activationResult);
                Assert.True(
                    activationResult!.Status == DialogJumpStatus.Success,
                    $"Expected a direct native Firefox jump, but got {activationResult.Status}: {activationResult.Message}. " +
                    $"DialogPid={dialogProcessId}; " +
                    $"observedFirefoxPids=[{string.Join(",", nativeHook.ObservedProcessIds.Order())}]; " +
                    $"preloadedFirefoxPids=[{string.Join(",", nativeHook.PreloadedProcessIds.Order())}]; " +
                    $"dialogProcessHasHookDll={nativeHook.IsHookDllLoaded((int)dialogProcessId)}.");
                var visuallyReachedTarget = WaitUntil(
                    () => DialogVisuallyShowsFolder(dialog, target.Path),
                    TimeSpan.FromSeconds(5));
                if (!visuallyReachedTarget)
                {
                    _ = CaptureComposedEvidence(
                        dialogBounds,
                        quickBounds,
                        "diagnostic-firefox-after-direct-set-folder");
                }
                Assert.True(visuallyReachedTarget, $"Firefox picker did not visibly reach '{target.Path}'.");
                var fileNameAfterJump = GetDialogFileNameValue(dialog);
                Assert.Equal(
                    fileNameBeforeJump,
                    fileNameAfterJump);
                Assert.Equal(string.Empty, viewModel.QueryText);

                stage = "capture Firefox after native navigation";
                PumpRender();
                Assert.True(IsWindowVisible(quickSwitchHandle), "Quick Switch became hidden after direct navigation.");
                Assert.Equal(0, GetDwmCloakedState(quickSwitchHandle));
                Assert.True(GetWindowRect(dialog, out dialogBounds));
                Assert.True(GetWindowRect(quickSwitchHandle, out quickBounds));
                AssertWithinVirtualDesktop(quickBounds);
                Assert.InRange(
                    Math.Min(
                        Math.Abs(quickBounds.Top - dialogBounds.Bottom),
                        Math.Abs(quickBounds.Bottom - dialogBounds.Top)),
                    0,
                    16);
                var screenshotPath = CaptureComposedEvidence(
                    dialogBounds,
                    quickBounds,
                    "26-firefox-quick-switch-composited");
                AssertQuickSwitchIsUnoccluded(dialogBounds, quickBounds, quickSwitchHandle);

                stage = "close jumped dialog without opening a file";
                Assert.True(TryActivateWindow(dialog));
                Assert.True(
                    CancelDialog(dialog),
                    "Firefox Ctrl+O dialog did not expose an invokable Cancel control.");
                Assert.True(
                    WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(5)),
                    "Firefox Ctrl+O dialog did not close after invoking Cancel.");

                stage = "write Firefox evidence metadata";
                WriteEvidenceMetadata(
                    screenshotPath,
                    firefoxPath,
                    dialog,
                    dialogProcessId,
                    capturedDialog,
                    dialogClassName,
                    dialogProcessName,
                    dialogBounds,
                    quickBounds,
                    target.Path,
                    selectedFileName,
                    fileNameBeforeJump,
                    fileNameAfterJump,
                    activationResult,
                    nativeHook);
            }
            catch (Exception exception)
            {
                failure = new InvalidOperationException($"Firefox Quick Switch integration failed at '{stage}'.", exception);
            }
            finally
            {
                quickSwitch?.Close();
                application?.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                application?.Shutdown();
                nativeHook?.Dispose();
                firefox?.Dispose();
                completed = true;
            }
        })
        {
            IsBackground = true,
            Name = "ListaryOpen Firefox Quick Switch STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"Firefox integration timed out at '{stage}'.");
        Assert.True(completed);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static DialogJumpResult MapNativeHookResult(HookJumpResult result)
    {
        var status = result.Status switch
        {
            HookJumpStatus.Success => DialogJumpStatus.Success,
            HookJumpStatus.AccessDenied => DialogJumpStatus.PermissionLimited,
            HookJumpStatus.TargetGone => DialogJumpStatus.TargetGone,
            HookJumpStatus.UnsupportedDialog => DialogJumpStatus.UnsupportedDialog,
            _ => DialogJumpStatus.Failed
        };
        return new DialogJumpResult(status, result.Message);
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
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? string.Empty;
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

    private static string CaptureComposedEvidence(NativeRect first, NativeRect second, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        _ = DwmFlush();
        Thread.Sleep(120);

        var left = Math.Min(first.Left, second.Left) - 12;
        var top = Math.Min(first.Top, second.Top) - 12;
        var right = Math.Max(first.Right, second.Right) + 12;
        var bottom = Math.Max(first.Bottom, second.Bottom) + 12;
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
            AssertQuickSwitchPixelsVisible(source, first, second, left, top);
            var path = Path.Combine(directory, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
            stream.Flush();
            Assert.True(stream.Length > 10_000, $"Composed Firefox screenshot was unexpectedly small: {path}");
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

    private static void AssertQuickSwitchPixelsVisible(
        BitmapSource screenshot,
        NativeRect dialogBounds,
        NativeRect quickBounds,
        int captureLeft,
        int captureTop)
    {
        var sampleTop = quickBounds.Top >= dialogBounds.Bottom - 16
            ? Math.Max(quickBounds.Top + 8, dialogBounds.Bottom + 1)
            : quickBounds.Top + 8;
        var sampleBottom = quickBounds.Top >= dialogBounds.Bottom - 16
            ? quickBounds.Bottom - 8
            : Math.Min(quickBounds.Bottom - 8, dialogBounds.Top - 1);
        var sampleLeft = quickBounds.Left + 8;
        var sampleRight = quickBounds.Right - 8;
        Assert.True(sampleRight > sampleLeft && sampleBottom > sampleTop);

        var converted = new FormatConvertedBitmap(screenshot, PixelFormats.Bgra32, null, 0);
        var width = sampleRight - sampleLeft;
        var height = sampleBottom - sampleTop;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(
            new Int32Rect(sampleLeft - captureLeft, sampleTop - captureTop, width, height),
            pixels,
            stride,
            0);
        var nonBlankPixels = 0;
        var colors = new HashSet<int>();
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            if (red < 245 || green < 245 || blue < 245)
            {
                nonBlankPixels++;
            }
            colors.Add((red << 16) | (green << 8) | blue);
        }

        Assert.True(nonBlankPixels > 100, "The composed screenshot does not visibly contain Quick Switch pixels.");
        Assert.True(colors.Count > 8, "The Quick Switch screenshot region is visually blank or occluded.");
    }

    private static void WriteEvidenceMetadata(
        string screenshotPath,
        string firefoxPath,
        IntPtr dialog,
        uint dialogProcessId,
        HookDialogContext capturedDialog,
        string dialogClassName,
        string dialogProcessName,
        NativeRect dialogBounds,
        NativeRect quickBounds,
        string targetFolder,
        string selectedFileName,
        string fileNameBeforeJump,
        string fileNameAfterJump,
        DialogJumpResult jump,
        NativeFirefoxHookSession nativeHook)
    {
        var path = Path.Combine(Path.GetDirectoryName(screenshotPath)!, "firefox-e2e.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 4,
                    passed = true,
                    interactionPath = "NativeHookHostDllDirectCom",
                    transport = "NativeHook",
                    directNavigation = "IFileDialog.SetFolder",
                    hookInstalledBeforeDialog = true,
                    hookLoadedBeforeShow = true,
                    firefoxFileDialogUtility = capturedDialog.FirefoxFileDialogUtility,
                    preloadConfirmedBeforeDialog = capturedDialog.PreloadConfirmedBeforeDialog,
                    fallbackUsed = false,
                    automationUsed = false,
                    hookHostPath = nativeHook.HostPath,
                    hookDllPath = nativeHook.DllPath,
                    hookDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativeHook.DllPath))),
                    firefoxPath,
                    firefoxVersion = FileVersionInfo.GetVersionInfo(firefoxPath).FileVersion,
                    dialog = new
                    {
                        hwnd = dialog.ToInt64(),
                        processId = dialogProcessId,
                        className = dialogClassName,
                        processName = dialogProcessName,
                        bounds = EvidenceBounds(dialogBounds)
                    },
                    quickSwitchBounds = EvidenceBounds(quickBounds),
                    quickSwitchVisibleAfterJump = true,
                    quickSwitchUncloakedAfterJump = true,
                    quickSwitchUnoccludedAfterJump = true,
                    targetFolder,
                    selectedFileName,
                    markerVisibleAfterJump = true,
                    fileOpened = false,
                    dialogClosedWithoutOpening = true,
                    fileNameBeforeJump,
                    fileNameAfterJump,
                    fileNameInputUnchanged = string.Equals(fileNameBeforeJump, fileNameAfterJump, StringComparison.Ordinal),
                    dialogTextInputObserved = !string.Equals(fileNameBeforeJump, fileNameAfterJump, StringComparison.Ordinal)
                        || !string.IsNullOrEmpty(fileNameAfterJump),
                    jumpStatus = jump.Status.ToString(),
                    screenshot = Path.GetFileName(screenshotPath),
                    screenshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshotPath)))
                },
                new JsonSerializerOptions { WriteIndented = true }));
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

    private static int GetDwmCloakedState(IntPtr window)
    {
        var cloaked = -1;
        Assert.Equal(0, DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)));
        return cloaked;
    }

    private static void AssertQuickSwitchIsUnoccluded(
        NativeRect dialogBounds,
        NativeRect quickBounds,
        IntPtr quickSwitchHandle)
    {
        var y = quickBounds.Top >= dialogBounds.Bottom - 16
            ? Math.Min(quickBounds.Bottom - 12, dialogBounds.Bottom + 20)
            : Math.Max(quickBounds.Top + 12, dialogBounds.Top - 20);
        Assert.InRange(y, quickBounds.Top + 8, quickBounds.Bottom - 9);
        var sampleXs = new[]
        {
            quickBounds.Left + 20,
            quickBounds.Left + ((quickBounds.Right - quickBounds.Left) / 2),
            quickBounds.Right - 20
        };
        foreach (var x in sampleXs)
        {
            var visibleWindow = GetAncestor(WindowFromPoint(new NativePoint(x, y)), 2);
            Assert.True(
                quickSwitchHandle == visibleWindow,
                $"Quick Switch was occluded at ({x},{y}). " +
                $"Expected=0x{quickSwitchHandle.ToInt64():X}; " +
                $"actual=0x{visibleWindow.ToInt64():X}; " +
                $"actualClass='{GetClassName(visibleWindow)}'; " +
                $"actualProcess='{GetProcessName(visibleWindow)}'; " +
                $"actualTitle='{GetWindowTitle(visibleWindow)}'; " +
                $"foreground=0x{GetForegroundWindow().ToInt64():X}.");
        }
    }

    private static void AssertWithinVirtualDesktop(NativeRect bounds)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var right = left + GetSystemMetrics(78);
        var bottom = top + GetSystemMetrics(79);
        Assert.InRange(bounds.Left, left, right - 1);
        Assert.InRange(bounds.Top, top, bottom - 1);
        Assert.InRange(bounds.Right, left + 1, right);
        Assert.InRange(bounds.Bottom, top + 1, bottom);
    }

    private static void PumpRender()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void CompleteWithDispatcher(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Thread.Sleep(100);
        }
        return predicate();
    }

    private static IntPtr FindWindow(Func<IntPtr, bool> predicate)
        => FindWindow(predicate, requireVisible: true);

    private static IntPtr FindWindow(Func<IntPtr, bool> predicate, bool requireVisible)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if ((!requireVisible || IsWindowVisible(window)) && predicate(window))
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var buffer = new StringBuilder(512);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetProcessName(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        try { using var process = Process.GetProcessById((int)processId); return process.ProcessName; }
        catch { return string.Empty; }
    }

    private static int GetProcessId(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return checked((int)processId);
    }

    private static int GetParentProcessId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (NtQueryInformationProcess(
                    process.Handle,
                    0,
                    out var information,
                    Marshal.SizeOf<ProcessBasicInformation>(),
                    out _) != 0)
            {
                return 0;
            }
            return checked((int)information.InheritedFromUniqueProcessId.ToInt64());
        }
        catch
        {
            return 0;
        }
    }

    private static bool DialogVisuallyShowsFolder(IntPtr dialog, string folderPath)
    {
        try
        {
            var expectedName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
            var root = AutomationElement.FromHandle(dialog);
            return root?.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element => element.Current.Name.Contains(
                    expectedName,
                    StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string GetDialogFileNameValue(IntPtr dialog)
    {
        var root = AutomationElement.FromHandle(dialog);
        Assert.NotNull(root);
        var fileNameEdit = root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        Assert.NotNull(fileNameEdit);
        Assert.True(fileNameEdit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern));
        return Assert.IsType<ValuePattern>(pattern).Current.Value;
    }

    private static bool ClickVisibleDialogFile(IntPtr dialog, string fileName)
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
            }, TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        var bounds = file!.Current.BoundingRectangle;
        if (!SetCursorPos(
                checked((int)Math.Round(bounds.Left + bounds.Width / 2)),
                checked((int)Math.Round(bounds.Top + bounds.Height / 2))))
        {
            return false;
        }
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    private static bool CancelDialog(IntPtr dialog)
    {
        try
        {
            var cancel = AutomationElement.FromHandle(dialog)?.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "2"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
            if (cancel is null ||
                !cancel.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                return false;
            }

            Assert.IsType<InvokePattern>(pattern).Invoke();
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return !IsWindow(dialog);
        }
    }

    private static void SendCtrlO()
        => DesktopInput.SendChord(VkControl, VkO);

    private static bool TryActivateWindow(IntPtr window)
    {
        _ = ShowWindow(window, 9);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event(VkMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
            _ = SetForegroundWindow(window);
            if (WaitUntil(() => GetForegroundWindow() == window, TimeSpan.FromMilliseconds(400)))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class FirefoxSession : IDisposable
    {
        private readonly int _rootProcessId;
        private readonly DateTime _rootCreationTimeUtc;
        private readonly string _profile;
        private readonly IntPtr _mainWindow;

        private FirefoxSession(int rootProcessId, DateTime rootCreationTimeUtc, string profile, IntPtr mainWindow)
        {
            _rootProcessId = rootProcessId;
            _rootCreationTimeUtc = rootCreationTimeUtc;
            _profile = profile;
            _mainWindow = mainWindow;
        }

        public string MainWindowTitle => GetWindowTitle(_mainWindow);
        public IntPtr MainWindow => _mainWindow;

        public static FirefoxSession Start(string firefoxPath, string repository)
        {
            var existing = Process.GetProcessesByName("firefox")
                .Select(process =>
                {
                    using (process)
                    {
                        try { return (process.Id, process.StartTime.ToUniversalTime().Ticks); }
                        catch { return (0, 0L); }
                    }
                })
                .Where(identity => identity.Item1 != 0)
                .ToHashSet();
            var profile = Path.Combine(Path.GetTempPath(), $"listary-open-firefox-e2e-{Guid.NewGuid():N}");
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
            using var started = Process.Start(start) ?? throw new InvalidOperationException("Could not start Firefox.");
            var main = IntPtr.Zero;
            Assert.True(
                WaitUntil(() =>
                {
                    main = FindWindow(window =>
                        string.Equals(GetClassName(window), "MozillaWindowClass", StringComparison.Ordinal) &&
                        GetWindowTitle(window).Contains("ListaryOpen Firefox file dialog fixture", StringComparison.Ordinal) &&
                        !existing.Contains(GetProcessIdentity(window)));
                    return main != IntPtr.Zero;
                }, TimeSpan.FromSeconds(15)),
                "Firefox fixture window did not appear.");
            var rootProcessId = GetProcessId(main);
            var rootCreationTimeUtc = new DateTime(GetProcessIdentity(main).Ticks, DateTimeKind.Utc);
            return new FirefoxSession(rootProcessId, rootCreationTimeUtc, profile, main);
        }

        private static (int ProcessId, long Ticks) GetProcessIdentity(IntPtr window)
        {
            var processId = GetProcessId(window);
            try
            {
                using var process = Process.GetProcessById(processId);
                return (processId, process.StartTime.ToUniversalTime().Ticks);
            }
            catch
            {
                return (0, 0L);
            }
        }

        public IntPtr OpenFilePicker()
        {
            var dialog = IntPtr.Zero;
            Assert.True(TryActivateWindow(_mainWindow));
            var foregroundBeforeCtrlO = GetForegroundWindow();
            Assert.Equal(_mainWindow, foregroundBeforeCtrlO);
            SendCtrlO();
            Assert.True(
                WaitUntil(() =>
                {
                    dialog = FindWindow(window =>
                        string.Equals(GetClassName(window), "#32770", StringComparison.Ordinal) &&
                        string.Equals(GetProcessName(window), "firefox", StringComparison.OrdinalIgnoreCase) &&
                        window == GetForegroundWindow() &&
                        window != foregroundBeforeCtrlO);
                    return dialog != IntPtr.Zero;
                }, TimeSpan.FromSeconds(15)),
                $"Firefox Ctrl+O did not create a test-owned native file dialog. {DescribeFirefoxWindows(_mainWindow)}");
            Assert.NotEqual(IntPtr.Zero, dialog);
            Assert.True(TryActivateWindow(dialog), "Firefox file dialog could not be activated after Ctrl+O.");
            return dialog;
        }

        public void Dispose()
        {
            try
            {
                using var process = Process.GetProcessById(_rootProcessId);
                if (process.StartTime.ToUniversalTime() == _rootCreationTimeUtc)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
            }
            catch { }
            for (var attempt = 0; attempt < 5 && Directory.Exists(_profile); attempt++)
            {
                try { Directory.Delete(_profile, recursive: true); }
                catch { Thread.Sleep(100); }
            }
        }

        private bool BelongsToSession(int processId)
        {
            var visited = new HashSet<int>();
            for (var current = processId; current > 0 && visited.Add(current); current = GetParentProcessId(current))
            {
                if (current != _rootProcessId)
                {
                    continue;
                }
                try
                {
                    using var root = Process.GetProcessById(current);
                    return root.StartTime.ToUniversalTime() == _rootCreationTimeUtc;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static string DescribeFirefoxWindows(IntPtr expectedMainWindow)
        {
            var foreground = GetForegroundWindow();
            var windows = new List<string>();
            _ = EnumWindows((window, _) =>
            {
                var className = GetClassName(window);
                var processName = GetProcessName(window);
                if (string.Equals(processName, "firefox", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(className, "#32770", StringComparison.Ordinal))
                {
                    windows.Add(
                        $"hwnd=0x{window.ToInt64():X},pid={GetProcessId(window)},class={className}," +
                        $"visible={IsWindowVisible(window)},title='{GetWindowTitle(window)}'");
                }
                return true;
            }, IntPtr.Zero);
            return $"expectedMain=0x{expectedMainWindow.ToInt64():X}; " +
                $"foreground=0x{foreground.ToInt64():X} class={GetClassName(foreground)} " +
                $"title='{GetWindowTitle(foreground)}'; windows=[{string.Join("; ", windows)}]";
        }

    }

    private sealed class EmptySearchIndex : ISearchIndex
    {
        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        public Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }

    private sealed class NativeFirefoxHookSession : IDisposable
    {
        private readonly Process _host;
        private readonly HookIpcClient _client;

        private NativeFirefoxHookSession(
            Process host,
            HookQuickSwitchBridge bridge,
            HookIpcClient client,
            string hostPath,
            string dllPath,
            IReadOnlySet<int> observedProcessIds,
            IReadOnlySet<int> preloadedProcessIds)
        {
            _host = host;
            _client = client;
            Bridge = bridge;
            HostPath = hostPath;
            DllPath = dllPath;
            ObservedProcessIds = observedProcessIds;
            PreloadedProcessIds = preloadedProcessIds;
        }

        public HookQuickSwitchBridge Bridge { get; }

        public string HostPath { get; }
        public string DllPath { get; }
        public IReadOnlySet<int> ObservedProcessIds { get; }
        public IReadOnlySet<int> PreloadedProcessIds { get; }

        public static async Task<NativeFirefoxHookSession> StartPreloadedAsync(IntPtr applicationWindow)
        {
            var repository = FindRepositoryRoot();
            var packageDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_NATIVE_PACKAGE_DIR");
            packageDirectory = string.IsNullOrWhiteSpace(packageDirectory)
                ? Path.Combine(repository, "artifacts", "ListaryOpen")
                : Path.GetFullPath(packageDirectory);
            var hostPath = Path.Combine(packageDirectory, "hooks", "x64", "ListaryOpen.HookHost.exe");
            var dllPath = Path.Combine(packageDirectory, "hooks", "x64", "ListaryOpen.Hook.dll");
            Assert.True(File.Exists(hostPath), $"Native x64 hook host was not found: {hostPath}");
            Assert.True(File.Exists(dllPath), $"Native x64 hook DLL was not found: {dllPath}");

            var pipeName = $"listary-open-firefox-native-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath)!
            };
            foreach (var argument in new[]
                     {
                         "--pipe", pipeName,
                         "--dll", dllPath,
                         "--preload-pid", GetProcessId(applicationWindow).ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--parent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--secret", secret
                     })
            {
                start.ArgumentList.Add(argument);
            }

            Assert.True(TryActivateWindow(applicationWindow));
            var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native Firefox hook host.");
            var hostOutput = host.StandardOutput.ReadToEndAsync();
            var hostErrors = host.StandardError.ReadToEndAsync();
            var client = new HookIpcClient(
                pipeName,
                TimeSpan.FromSeconds(2),
                Environment.ProcessId,
                secret);
            HookQuickSwitchBridge? bridge = null;
            try
            {
                Assert.True(
                    await WaitUntilAsync(
                        async () => (await client.ProbeHealthAsync(CancellationToken.None)).Status == HookJumpStatus.Success,
                        TimeSpan.FromSeconds(10)),
                    "Native Firefox hook host did not become healthy.");

                var applicationProcessId = GetProcessId(applicationWindow);
                Assert.True(
                    await WaitUntilAsync(
                        () => Task.FromResult(IsModuleLoaded(applicationProcessId, dllPath)),
                        TimeSpan.FromSeconds(10)),
                    "Native hook DLL was not loaded into Firefox before opening the file dialog.");
                var observedProcessIds = Process.GetProcessesByName("firefox")
                    .Select(process => { using (process) { return process.Id; } })
                    .ToHashSet();
                var preloadedProcessIds = observedProcessIds
                    .Where(processId => IsModuleLoaded(processId, dllPath))
                    .ToHashSet();
                Assert.Contains(applicationProcessId, preloadedProcessIds);

                var status = new HookQuickSwitchStatus(
                    true,
                    new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "Firefox native hook ready."),
                    new HookArchitectureStatus(HookArchitecture.X86, false, false, false, "Not required by Firefox test."));
                bridge = new HookQuickSwitchBridge(
                    status,
                    new Dictionary<HookArchitecture, IHookIpcClient>
                    {
                        [HookArchitecture.X64] = client
                    });
                return new NativeFirefoxHookSession(
                    host,
                    bridge,
                    client,
                    hostPath,
                    dllPath,
                    observedProcessIds,
                    preloadedProcessIds);
            }
            catch (Exception error)
            {
                bridge?.Dispose();
                if (bridge is null)
                {
                    client.Dispose();
                }
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); }
                catch { }
                try { await host.WaitForExitAsync(); }
                catch { }
                var output = await hostOutput;
                var errors = await hostErrors;
                host.Dispose();
                throw new InvalidOperationException(
                    $"Native Firefox hook host diagnostics: stdout='{BoundHostDiagnostics(output)}', " +
                    $"stderr='{BoundHostDiagnostics(errors)}'.",
                    error);
            }
        }

        private static string BoundHostDiagnostics(string value)
        {
            const int maxCharacters = 8_192;
            var trimmed = value.Trim();
            return trimmed.Length <= maxCharacters
                ? trimmed
                : $"[truncated {trimmed.Length - maxCharacters} chars]...{trimmed[^maxCharacters..]}";
        }

        public async Task<HookDialogContext> CaptureDialogAsync(IntPtr expectedDialog)
        {
            HookDialogContext? captured = null;
            var lastHookReply = "No hook response was received.";
            var hookReplies = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(
                await WaitUntilAsync(
                    async () =>
                    {
                        _ = TryActivateWindow(expectedDialog);
                        var result = await _client.GetActiveDialogResultAsync(CancellationToken.None);
                        captured = result.Dialog;
                        lastHookReply = $"{result.Status}: {result.Message}";
                        hookReplies.Add(lastHookReply);
                        return captured?.WindowHandle == expectedDialog;
                    },
                    TimeSpan.FromSeconds(10)),
                $"Native hook host did not capture the Firefox file dialog. " +
                $"DialogPid={GetProcessId(expectedDialog)}; " +
                $"observedFirefoxPids=[{string.Join(",", ObservedProcessIds.Order())}]; " +
                $"preloadedFirefoxPids=[{string.Join(",", PreloadedProcessIds.Order())}]; " +
                $"dialogProcessHasHookDll={IsHookDllLoaded(GetProcessId(expectedDialog))}; " +
                $"dialogVisible={IsWindowVisible(expectedDialog)}; " +
                $"expectedDialog=0x{expectedDialog.ToInt64():X}; " +
                $"foreground=0x{GetForegroundWindow().ToInt64():X}; " +
                $"hookReplies=[{string.Join(" | ", hookReplies)}]; lastHookReply={lastHookReply}.");
            return Assert.IsType<HookDialogContext>(captured);
        }

        public bool IsHookDllLoaded(int processId) => IsModuleLoaded(processId, DllPath);

        private static bool IsModuleLoaded(int processId, string expectedPath)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.Modules.Cast<ProcessModule>().Any(module =>
                    string.Equals(module.FileName, expectedPath, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> WaitUntilAsync(
            Func<Task<bool>> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await predicate())
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return await predicate();
        }

        public void Dispose()
        {
            Bridge.Dispose();
            try
            {
                if (!_host.HasExited)
                {
                    _host.Kill(entireProcessTree: true);
                    _host.WaitForExit(2_000);
                }
            }
            catch { }
            _host.Dispose();
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
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
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
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(
        IntPtr process,
        int informationClass,
        out ProcessBasicInformation information,
        int informationLength,
        out int returnLength);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

}
