using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopIntegrationCollection
{
    public const string Name = "Desktop integration";
}

[Collection(DesktopIntegrationCollection.Name)]
public sealed class NativeDialogHookIntegrationTests
{
    private const byte VkEscape = 0x1B;
    private const byte VkControl = 0x11;
    private const byte VkA = 0x41;
    private const byte VkBack = 0x08;
    private const byte VkReturn = 0x0D;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint InputKeyboard = 1;

    [Theory]
    [InlineData("x64", "open-file", 1148)]
    [InlineData("x64", "open-folder", 1152)]
    [InlineData("x86", "open-file", 1148)]
    [InlineData("x86", "open-folder", 1152)]
    [Trait("Category", "DesktopIntegration")]
    public async Task NativeHookCapturesJumpsRestoresInputFocusAndEscapeClosesDialog(
        string architecture,
        string mode,
        int expectedFocusControlId)
    {
        using var initialDirectory = TemporaryDirectory.Create("listary-open-initial");
        using var targetDirectory = TemporaryDirectory.Create("listary-open-target");
        var visualMarker = mode == "open-file"
            ? Path.Combine(targetDirectory.Path, "listary-native-direct-jump-marker.txt")
            : Path.Combine(targetDirectory.Path, "listary-native-direct-jump-marker");
        if (mode == "open-file")
        {
            File.WriteAllText(visualMarker, "Direct native COM jump evidence");
        }
        else
        {
            Directory.CreateDirectory(visualMarker);
        }
        using var session = await NativeDialogSession.StartAsync(architecture, mode, initialDirectory.Path);

        var active = await session.WaitForActiveDialogAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HookJumpStatus.Success, active.Status);
        var dialog = Assert.IsType<HookDialogContext>(active.Dialog);
        Assert.Equal(session.TestHost.Id, (int)dialog.ProcessId);
        Assert.Equal("#32770", dialog.ClassName);

        var jump = await session.Client.JumpDialogToFolderAsync(
            dialog.DialogId,
            targetDirectory.Path,
            CancellationToken.None);

        Assert.True(
            jump.Status == HookJumpStatus.Success,
            $"Native jump failed with {jump.Status}: {jump.Message}");
        Assert.True(
            await WaitUntilAsync(
                () => GetFocusedControlId(dialog.WindowHandle) == expectedFocusControlId,
                TimeSpan.FromSeconds(3)),
            $"Expected focus control {expectedFocusControlId}, actual {GetFocusedControlId(dialog.WindowHandle)}.");
        Assert.True(
            await WaitUntilAsync(
                () => DialogVisuallyContains(dialog.WindowHandle, Path.GetFileName(visualMarker)),
                TimeSpan.FromSeconds(5)),
            $"The jumped native dialog did not visibly contain '{Path.GetFileName(visualMarker)}'.");
        CaptureWindowEvidence(dialog.WindowHandle, $"20-native-{architecture}-{mode}-jump-focus");

        Assert.True(TryActivateWindow(dialog.WindowHandle));
        Assert.True(
            await WaitUntilAsync(
                () => GetForegroundWindow() == dialog.WindowHandle &&
                    GetFocusedControlId(dialog.WindowHandle) == expectedFocusControlId,
                TimeSpan.FromSeconds(3)),
            "The native dialog did not become the focused foreground target before Escape.");

        var closed = false;
        for (var attempt = 0; attempt < 3 && !closed; attempt++)
        {
            Assert.True(TryActivateWindow(dialog.WindowHandle));
            Assert.True(
                await WaitUntilAsync(
                    () => GetForegroundWindow() == dialog.WindowHandle,
                    TimeSpan.FromSeconds(2)),
                $"The native dialog was not foreground before Escape attempt {attempt + 1}.");
            keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
            keybd_event(VkEscape, 0, KeyEventKeyUp, UIntPtr.Zero);
            closed = session.TestHost.WaitForExit(2_000);
        }

        Assert.True(
            closed,
            "Escape did not close the native dialog after the hook restored its input focus.");
        Assert.Equal(0, session.TestHost.ExitCode);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    [Trait("Category", "DesktopIntegration")]
    public async Task NativeFileDialogAcceptsTypingControlAAndBackspaceAfterFolderJump(string architecture)
    {
        using var initialDirectory = TemporaryDirectory.Create("listary-open-input-initial");
        using var targetDirectory = TemporaryDirectory.Create("listary-open-input-target");
        const string selectionName = "verified-focus-input.txt";
        var expectedPath = Path.Combine(targetDirectory.Path, selectionName);
        File.WriteAllText(expectedPath, "ListaryOpen desktop integration test");
        using var session = await NativeDialogSession.StartAsync(architecture, "open-file", initialDirectory.Path);

        var active = await session.WaitForActiveDialogAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HookJumpStatus.Success, active.Status);
        var dialog = Assert.IsType<HookDialogContext>(active.Dialog);
        var jump = await session.Client.JumpDialogToFolderAsync(
            dialog.DialogId,
            targetDirectory.Path,
            CancellationToken.None);
        Assert.True(
            jump.Status == HookJumpStatus.Success,
            $"Native jump failed with {jump.Status}: {jump.Message}");
        Assert.True(
            await WaitUntilAsync(
                () => GetFocusedControlId(dialog.WindowHandle) == 1148,
                TimeSpan.FromSeconds(3)),
            $"Expected file-name focus after jump, actual {GetFocusedControlId(dialog.WindowHandle)}.");
        Assert.True(
            await WaitUntilAsync(
                () => DialogVisuallyContains(dialog.WindowHandle, selectionName),
                TimeSpan.FromSeconds(5)),
            $"The jumped native file dialog did not visibly contain '{selectionName}'.");
        CaptureWindowEvidence(dialog.WindowHandle, $"21-native-{architecture}-open-file-input-focus");

        Assert.True(TryActivateWindow(dialog.WindowHandle));
        Assert.True(
            await WaitUntilAsync(
                () => GetForegroundWindow() == dialog.WindowHandle &&
                    GetFocusedControlId(dialog.WindowHandle) == 1148,
                TimeSpan.FromSeconds(3)),
            "The native file dialog did not become the focused foreground input target.");

        var accepted = false;
        for (var attempt = 0; attempt < 3 && !accepted; attempt++)
        {
            Assert.True(TryActivateWindow(dialog.WindowHandle));
            Assert.True(
                await WaitUntilAsync(
                    () => GetForegroundWindow() == dialog.WindowHandle &&
                        GetFocusedControlId(dialog.WindowHandle) == 1148,
                    TimeSpan.FromSeconds(2)),
                $"The native file dialog lost input focus before attempt {attempt + 1}.");
            SendVirtualKeyChord(VkControl, VkA);
            SendUnicodeText(selectionName + "x");
            SendVirtualKey(VkBack);
            SendVirtualKey(VkReturn);
            accepted = session.TestHost.WaitForExit(2_000);
        }

        Assert.True(
            accepted,
            "Typing, Ctrl+A, Backspace, and Enter did not accept the selected file after the folder jump.");
        Assert.Equal(0, session.TestHost.ExitCode);
        var state = session.ReadState();
        Assert.Equal("DialogClosed", state.Stage);
        Assert.True(state.Accepted);
        Assert.True(
            PathsEqual(expectedPath, state.SelectedPath),
            $"Expected selected path '{expectedPath}', actual '{state.SelectedPath}'.");
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    [Trait("Category", "DesktopIntegration")]
    public async Task NativeFolderDialogAcceptsTheActuallyJumpedFolder(string architecture)
    {
        using var initialDirectory = TemporaryDirectory.Create("listary-open-folder-initial");
        using var targetDirectory = TemporaryDirectory.Create("listary-open-folder-target");
        using var session = await NativeDialogSession.StartAsync(architecture, "open-folder", initialDirectory.Path);

        var active = await session.WaitForActiveDialogAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HookJumpStatus.Success, active.Status);
        var dialog = Assert.IsType<HookDialogContext>(active.Dialog);
        var jump = await session.Client.JumpDialogToFolderAsync(
            dialog.DialogId,
            targetDirectory.Path,
            CancellationToken.None);
        Assert.True(
            jump.Status == HookJumpStatus.Success,
            $"Native folder jump failed with {jump.Status}: {jump.Message}");

        Assert.True(TryActivateWindow(dialog.WindowHandle));
        Assert.True(
            await WaitUntilAsync(
                () => GetForegroundWindow() == dialog.WindowHandle &&
                    GetFocusedControlId(dialog.WindowHandle) == 1152,
                TimeSpan.FromSeconds(3)),
            "The jumped folder dialog did not restore its expected input focus.");
        SendVirtualKey(VkReturn);

        Assert.True(
            session.TestHost.WaitForExit(3_000),
            "The folder dialog did not accept the jumped target folder.");
        Assert.Equal(0, session.TestHost.ExitCode);
        var state = session.ReadState();
        Assert.Equal("DialogClosed", state.Stage);
        Assert.True(state.Accepted);
        Assert.True(
            PathsEqual(targetDirectory.Path, state.SelectedPath),
            $"Expected selected folder '{targetDirectory.Path}', actual '{state.SelectedPath}'.");
    }

    private static void SendVirtualKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static void SendVirtualKeyChord(byte modifier, byte virtualKey)
    {
        keybd_event(modifier, 0, 0, UIntPtr.Zero);
        SendVirtualKey(virtualKey);
        keybd_event(modifier, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = text
            .SelectMany(character => new[]
            {
                CreateUnicodeInput(character, keyUp: false),
                CreateUnicodeInput(character, keyUp: true)
            })
            .ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        Assert.True(
            sent == inputs.Length,
            $"SendInput sent {sent} of {inputs.Length} keyboard events. Win32 error: {Marshal.GetLastWin32Error()}.");
    }

    private static NativeInput CreateUnicodeInput(char character, bool keyUp) =>
        new()
        {
            Type = InputKeyboard,
            Union = new NativeInputUnion
            {
                Keyboard = new NativeKeyboardInput
                {
                    ScanCode = character,
                    Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
                }
            }
        };

    private static void CaptureWindowEvidence(IntPtr window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Assert.True(GetWindowRect(window, out var bounds));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, 300, 4_000);
        Assert.InRange(height, 200, 2_500);
        Directory.CreateDirectory(directory);

        Assert.True(TryActivateWindow(window));
        Assert.True(
            WaitUntilAsync(() => GetForegroundWindow() == window, TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult(),
            "The native dialog must be foreground before capturing visual evidence.");

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
            Thread.Sleep(150);
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
            Assert.True(stream.Length > 1_024, $"Native dialog screenshot was unexpectedly small: {path}");
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static bool DialogVisuallyContains(IntPtr window, string expectedName)
    {
        try
        {
            return AutomationElement.FromHandle(window)?
                .FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
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

    private static int GetFocusedControlId(IntPtr dialogWindow)
    {
        var threadId = GetWindowThreadProcessId(dialogWindow, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return threadId != 0 && GetGUIThreadInfo(threadId, ref info) && info.FocusWindow != IntPtr.Zero
            ? GetDlgCtrlID(info.FocusWindow)
            : 0;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return predicate();
    }

    private static bool TryActivateWindow(IntPtr window) => DesktopWindowActivator.TryActivate(window);

    private sealed class NativeDialogSession : IDisposable
    {
        private readonly Process _hookHost;
        private readonly string _statePath;

        private NativeDialogSession(Process testHost, Process hookHost, HookIpcClient client, string statePath)
        {
            TestHost = testHost;
            _hookHost = hookHost;
            Client = client;
            _statePath = statePath;
        }

        public Process TestHost { get; }

        public HookIpcClient Client { get; }

        public async Task<HookActiveDialogResult> WaitForActiveDialogAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            HookActiveDialogResult result;
            do
            {
                result = await Client.GetActiveDialogResultAsync(CancellationToken.None);
                if (result.Status == HookJumpStatus.Success)
                {
                    return result;
                }

                await Task.Delay(50);
            }
            while (DateTime.UtcNow < deadline);

            return result;
        }

        public HostState ReadState()
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<HostState>(json)
                ?? throw new InvalidDataException($"Test host state was empty: {_statePath}");
        }

        public static async Task<NativeDialogSession> StartAsync(
            string architecture,
            string mode,
            string initialDirectory)
        {
            Assert.True(
                architecture is "x64" or "x86",
                $"Unsupported native hook architecture '{architecture}'.");
            var repository = FindRepositoryRoot();
            var packageDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_NATIVE_PACKAGE_DIR");
            packageDirectory = string.IsNullOrWhiteSpace(packageDirectory)
                ? Path.Combine(repository, "artifacts", "ListaryOpen")
                : Path.GetFullPath(packageDirectory);
            var hookHostPath = Path.Combine(packageDirectory, "hooks", architecture, "ListaryOpen.HookHost.exe");
            var hookDllPath = Path.Combine(packageDirectory, "hooks", architecture, "ListaryOpen.Hook.dll");
            Assert.True(File.Exists(hookHostPath), $"Native hook host was not found: {hookHostPath}");
            Assert.True(File.Exists(hookDllPath), $"Native hook DLL was not found: {hookDllPath}");

            var testHostPath = FindTestHost(repository, architecture);
            var statePath = Path.Combine(Path.GetTempPath(), $"listary-open-test-host-{Guid.NewGuid():N}.json");
            var readyEventName = $"Local\\ListaryOpen-HookPreload-{Environment.ProcessId}-{Guid.NewGuid():N}";
            using var readyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, readyEventName);
            var testHostStart = new ProcessStartInfo
            {
                FileName = testHostPath,
                UseShellExecute = false,
                WorkingDirectory = repository
            };
            testHostStart.ArgumentList.Add("--mode");
            testHostStart.ArgumentList.Add(mode);
            testHostStart.ArgumentList.Add("--initial-directory");
            testHostStart.ArgumentList.Add(initialDirectory);
            testHostStart.ArgumentList.Add("--state");
            testHostStart.ArgumentList.Add(statePath);
            testHostStart.ArgumentList.Add("--ready-event");
            testHostStart.ArgumentList.Add(readyEventName);
            var testHost = Process.Start(testHostStart) ?? throw new InvalidOperationException("Could not start dialog test host.");

            Process? hookHost = null;
            HookIpcClient? client = null;
            try
            {
                var preloadWindow = await WaitForPreloadWindowAsync(statePath, TimeSpan.FromSeconds(10));
                Assert.NotEqual(IntPtr.Zero, preloadWindow);
                Assert.True(TryActivateWindow(preloadWindow));

                var pipeName = $"listary-open-integration-{architecture}-{Environment.ProcessId}-{Guid.NewGuid():N}";
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var hookStart = new ProcessStartInfo
                {
                    FileName = hookHostPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(hookHostPath)!
                };
                hookStart.ArgumentList.Add("--pipe");
                hookStart.ArgumentList.Add(pipeName);
                hookStart.ArgumentList.Add("--dll");
                hookStart.ArgumentList.Add(hookDllPath);
                hookStart.ArgumentList.Add("--parent-pid");
                hookStart.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                hookStart.ArgumentList.Add("--secret");
                hookStart.ArgumentList.Add(secret);
                hookStart.ArgumentList.Add("--preload-pid");
                hookStart.ArgumentList.Add(testHost.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                hookHost = Process.Start(hookStart) ?? throw new InvalidOperationException("Could not start native hook host.");
                client = new HookIpcClient(
                    pipeName,
                    TimeSpan.FromSeconds(3),
                    Environment.ProcessId,
                    secret);

                Assert.True(
                    await WaitUntilAsync(
                        () => client.ProbeHealthAsync(CancellationToken.None).GetAwaiter().GetResult().Status == HookJumpStatus.Success,
                        TimeSpan.FromSeconds(20)),
                    $"Native hook host did not become healthy. HostExited={hookHost.HasExited}; " +
                    $"ExitCode={(hookHost.HasExited ? hookHost.ExitCode : -1)}; Pipe={pipeName}; Dll={hookDllPath}");
                Assert.True(
                    await WaitUntilAsync(
                        () => IsModuleLoaded(testHost.Id, hookDllPath),
                        TimeSpan.FromSeconds(10)),
                    "Native hook DLL was not loaded before the test host created IFileDialog.");
                Assert.True(readyEvent.Set());
                var dialogWindow = await WaitForDialogWindowAsync(testHost, TimeSpan.FromSeconds(10));
                var stateAfterOpen = File.Exists(statePath) ? File.ReadAllText(statePath) : "<missing>";
                Assert.True(
                    dialogWindow != IntPtr.Zero,
                    $"Dialog did not open after preloading the hook. HostExited={testHost.HasExited}; " +
                    $"ExitCode={(testHost.HasExited ? testHost.ExitCode : -1)}; State={stateAfterOpen}");
                Assert.True(TryActivateWindow(dialogWindow));
                return new NativeDialogSession(testHost, hookHost, client, statePath);
            }
            catch
            {
                client?.Dispose();
                Terminate(hookHost);
                Terminate(testHost);
                TryDelete(statePath);
                throw;
            }
        }

        private static async Task<IntPtr> WaitForPreloadWindowAsync(string statePath, TimeSpan timeout)
        {
            var result = IntPtr.Zero;
            await WaitUntilAsync(
                () =>
                {
                    try
                    {
                        var state = JsonSerializer.Deserialize<HostState>(File.ReadAllText(statePath));
                        if (state?.Stage == "HookPreloadReady" && state.WindowHandle is > 0)
                        {
                            result = new IntPtr(state.WindowHandle.Value);
                            return true;
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                    return false;
                },
                timeout);
            return result;
        }

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

        public void Dispose()
        {
            Client.Dispose();
            Terminate(_hookHost);
            Terminate(TestHost);
            TryDelete(_statePath);
        }

        private static async Task<IntPtr> WaitForDialogWindowAsync(Process process, TimeSpan timeout)
        {
            var result = IntPtr.Zero;
            await WaitUntilAsync(
                () =>
                {
                    result = FindVisibleDialogWindow(process.Id);
                    return result != IntPtr.Zero;
                },
                timeout);
            return result;
        }

        private static string FindTestHost(string repository, string architecture)
        {
            var configured = Environment.GetEnvironmentVariable(
                architecture == "x86"
                    ? "LISTARYOPEN_TEST_HOST_X86_PATH"
                    : "LISTARYOPEN_TEST_HOST_X64_PATH");
            configured = string.IsNullOrWhiteSpace(configured)
                ? Environment.GetEnvironmentVariable("LISTARYOPEN_TEST_HOST_PATH")
                : configured;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            var publishedPath = Path.Combine(
                repository,
                "tests",
                "ListaryOpen.TestHost",
                "bin",
                "Release",
                "net8.0-windows",
                $"win-{architecture}",
                "publish",
                "ListaryOpen.TestHost.exe");
            if (File.Exists(publishedPath))
            {
                return publishedPath;
            }

            Assert.True(
                architecture != "x86",
                $"The mandatory x86 dialog test host was not found: {publishedPath}. " +
                "Publish ListaryOpen.TestHost for win-x86 or set LISTARYOPEN_TEST_HOST_X86_PATH; " +
                "falling back to the default x64 host would make native-hook evidence invalid.");

            var path = Path.Combine(
                repository,
                "tests",
                "ListaryOpen.TestHost",
                "bin",
                "Release",
                "net8.0-windows",
                "ListaryOpen.TestHost.exe");
            Assert.True(File.Exists(path), $"Dialog test host was not found: {path}");
            return path;
        }
    }

    private static IntPtr FindVisibleDialogWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows(
            (window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != (uint)processId || !IsWindowVisible(window))
                {
                    return true;
                }

                var className = new StringBuilder(64);
                _ = GetClassName(window, className, className.Capacity);
                if (string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
                {
                    found = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }


    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the ListaryOpen repository root.");
    }

    private static void Terminate(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record HostState(
        string Stage,
        string Mode,
        bool? Accepted,
        string? SelectedPath,
        string? Error,
        long? WindowHandle = null);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public NativeRect CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicObject);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
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

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo information);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr window);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
}
