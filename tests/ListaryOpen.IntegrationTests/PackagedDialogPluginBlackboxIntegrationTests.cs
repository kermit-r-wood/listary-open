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
using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class PackagedDialogPluginBlackboxIntegrationTests
{
    private const uint WmClose = 0x0010;
    private const byte VkControl = 0x11;
    private const byte VkG = 0x47;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [Fact]
    [Trait("Category", "ElevatedPackagedBlackboxE2E")]
    public async Task PublishedAppUsesCompiledPluginToDirectlyNavigateRealNonstandardWindow()
    {
        await RunInStaAsync(() =>
        {
            Assert.True(
                IsCurrentProcessElevated(),
                "ElevatedPackagedBlackboxE2E must run elevated; this mandatory plugin test fails instead of skipping.");
            var packageValue = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
            Assert.False(string.IsNullOrWhiteSpace(packageValue));
            var packageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageValue));
            var packageExe = Path.Combine(packageDirectory, "ListaryOpen.App.exe");
            Assert.True(File.Exists(packageExe));
            Assert.True(
                IsSingleInstanceMutexAvailable(),
                "ListaryOpen is already running. The test will not terminate a user-owned instance.");

            var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "plugin-fixture");
            var fixtureManifest = Path.Combine(fixtureRoot, DialogPluginLoader.ManifestFileName);
            var fixtureAssembly = Path.Combine(fixtureRoot, "ListaryOpen.DialogPlugin.TestFixture.dll");
            Assert.True(File.Exists(fixtureManifest));
            Assert.True(File.Exists(fixtureAssembly));
            using var installedPlugin = InstalledPlugin.InstallUnique(fixtureManifest, fixtureAssembly);
            using var packageData = PackageDataCleanup.RequireInitiallyAbsent(packageDirectory);
            using var target = TemporaryDirectory.Create("listary-packaged-plugin-target");
            using var initial = TemporaryDirectory.Create("listary-packaged-plugin-initial");
            using var explorer = OwnedExplorerWindow.Start(target.Path);

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Process? app = null;
            E2ePipeClient? control = null;
            OwnedTestHost? neutral = null;
            OwnedTestHost? customBrowser = null;
            var shutdownSucceeded = false;
            try
            {
                app = Process.Start(new ProcessStartInfo
                {
                    FileName = packageExe,
                    Arguments = "--listary-e2e-control=" + nonce,
                    WorkingDirectory = packageDirectory,
                    UseShellExecute = false
                });
                Assert.NotNull(app);
                control = E2ePipeClient.Connect("ListaryOpen.E2E." + nonce, app, TimeSpan.FromSeconds(45));
                Assert.Equal($"Ready {app.Id}", control.Exchange(nonce + " Ready"));
                Assert.Equal("Ok", control.Exchange(nonce + " AllowInjectedInput"));

                Assert.True(ActivateWindow(explorer.WindowHandle));
                Thread.Sleep(TimeSpan.FromSeconds(2));
                neutral = OwnedTestHost.StartInputWindow(FindTestHost());
                Assert.True(ActivateWindow(neutral.WindowHandle));
                Thread.Sleep(TimeSpan.FromSeconds(7));

                customBrowser = OwnedTestHost.StartCustomBrowser(FindTestHost(), initial.Path);
                Assert.True(ActivateWindow(customBrowser.WindowHandle));
                Assert.Equal("ListaryOpenPluginFixtureBrowser", GetWindowClass(customBrowser.WindowHandle));
                Assert.NotEqual("#32770", GetWindowClass(customBrowser.WindowHandle));
                var quickSwitch = WaitForTopLevelWindow(app.Id, "Quick Switch", TimeSpan.FromSeconds(10));
                Assert.NotEqual(IntPtr.Zero, quickSwitch);
                Assert.True(IsWindowVisible(quickSwitch));
                Assert.True(
                    PathsEqual(customBrowser.ReadState().SelectedPath, initial.Path),
                    "The custom browser moved before Ctrl+G, so automatic navigation would make this a false green.");

                SendCtrlG();
                Assert.True(
                    WaitUntil(
                        () =>
                        {
                            var state = customBrowser.ReadState();
                            return state.Stage == "CustomBrowserNavigated" && PathsEqual(state.SelectedPath, target.Path);
                        },
                        TimeSpan.FromSeconds(12)),
                    "The published app did not navigate the nonstandard browser after real Ctrl+G input.");
                var breadcrumb = WaitForVisibleText(customBrowser.WindowHandle, target.Path, TimeSpan.FromSeconds(6));
                Assert.NotNull(breadcrumb);
                Assert.True(IsModuleLoaded(app, installedPlugin.AssemblyPath));
                Assert.True(IsWindowVisible(quickSwitch));

                var screenshot = CaptureComposedEvidence(
                    customBrowser.WindowHandle,
                    quickSwitch,
                    "32-packaged-nonstandard-plugin-direct");
                WriteMetadata(
                    screenshot,
                    packageExe,
                    app.Id,
                    installedPlugin,
                    explorer.WindowHandle,
                    customBrowser,
                    quickSwitch,
                    initial.Path,
                    target.Path,
                    breadcrumb);

                Assert.Equal("Ok", control.Exchange(nonce + " Shutdown"));
                shutdownSucceeded = app.WaitForExit(15_000);
                Assert.True(shutdownSucceeded);
                Assert.Equal(0, app.ExitCode);
            }
            finally
            {
                if (app is { HasExited: false })
                {
                    if (!shutdownSucceeded && control is not null)
                    {
                        try
                        {
                            _ = control.Exchange(nonce + " Shutdown");
                            shutdownSucceeded = app.WaitForExit(10_000);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidOperationException)
                        {
                        }
                    }

                    if (!app.HasExited)
                    {
                        app.Kill(entireProcessTree: true);
                        _ = app.WaitForExit(10_000);
                    }
                }

                customBrowser?.Dispose();
                neutral?.Dispose();
                control?.Dispose();
                app?.Dispose();
            }
        });
    }

    private static Task RunInStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true, Name = "ListaryOpen packaged plugin black-box STA" };
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
        using var mutex = new Mutex(false, @"Local\ListaryOpen.SingleInstance");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { acquired = true; }
            return acquired;
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string FindTestHost()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var path = Path.Combine(
            FindRepositoryRoot(), "tests", "ListaryOpen.TestHost", "bin", configuration,
            "net8.0-windows", "ListaryOpen.TestHost.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Compiled TestHost is missing.", path);
    }

    private static bool ActivateWindow(IntPtr window)
    {
        _ = ShowWindow(window, 9);
        _ = SetForegroundWindow(window);
        Assert.True(GetWindowRect(window, out var bounds));
        Assert.True(SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2));
        var clicks = new[]
        {
            MouseInput(MouseEventLeftDown),
            MouseInput(MouseEventLeftUp)
        };
        Assert.Equal((uint)clicks.Length, SendInput((uint)clicks.Length, clicks, Marshal.SizeOf<NativeInput>()));
        return WaitUntil(() => GetForegroundWindow() == window, TimeSpan.FromSeconds(5));
    }

    private static NativeInput MouseInput(uint flags) => new()
    {
        Type = InputMouse,
        Union = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = flags } }
    };

    private static void SendCtrlG()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, false),
            KeyboardInput(VkG, false),
            KeyboardInput(VkG, true),
            KeyboardInput(VkControl, true)
        };
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()));
    }

    private static NativeInput KeyboardInput(byte key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = key,
                ScanCode = unchecked((ushort)MapVirtualKey(key, 0)),
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    private static bool IsModuleLoaded(Process process, string expectedPath)
    {
        process.Refresh();
        foreach (ProcessModule module in process.Modules)
        {
            if (string.Equals(
                    Path.GetFullPath(module.FileName),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AutomationElement? WaitForVisibleText(IntPtr window, string value, TimeSpan timeout)
    {
        AutomationElement? result = null;
        return WaitUntil(
            () =>
            {
                try
                {
                    result = AutomationElement.FromHandle(window)?.FindFirst(
                        TreeScope.Descendants,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                            new PropertyCondition(AutomationElement.NameProperty, value)));
                    return result is not null && !result.Current.IsOffscreen;
                }
                catch (ElementNotAvailableException) { return false; }
            },
            timeout) ? result : null;
    }

    private static IntPtr WaitForTopLevelWindow(int processId, string title, TimeSpan timeout)
    {
        var result = IntPtr.Zero;
        return WaitUntil(
            () =>
            {
                result = IntPtr.Zero;
                _ = EnumWindows((window, parameter) =>
                {
                    _ = GetWindowThreadProcessId(window, out var owner);
                    if (owner == (uint)processId && IsWindowVisible(window) && GetWindowText(window) == title)
                    {
                        result = window;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                return result != IntPtr.Zero;
            }, timeout) ? result : IntPtr.Zero;
    }

    private static string CaptureComposedEvidence(IntPtr firstWindow, IntPtr secondWindow, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Assert.True(GetWindowRect(firstWindow, out var first));
        Assert.True(GetWindowRect(secondWindow, out var second));
        var left = Math.Min(first.Left, second.Left);
        var top = Math.Min(first.Top, second.Top);
        var right = Math.Max(first.Right, second.Right);
        var bottom = Math.Max(first.Bottom, second.Bottom);
        var width = right - left;
        var height = bottom - top;
        var desktop = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        Assert.NotEqual(IntPtr.Zero, desktop);
        Assert.NotEqual(IntPtr.Zero, memory);
        Assert.NotEqual(IntPtr.Zero, bitmap);
        var previous = SelectObject(memory, bitmap);
        try
        {
            _ = DwmFlush();
            Thread.Sleep(180);
            Assert.True(BitBlt(memory, 0, 0, width, height, desktop, left, top, 0x00CC0020 | 0x40000000));
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            var path = Path.Combine(directory, name + ".png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            stream.Flush();
            Assert.True(stream.Length > 5_000);
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

    private static void WriteMetadata(
        string screenshot,
        string packageExe,
        int appProcessId,
        InstalledPlugin plugin,
        IntPtr explorer,
        OwnedTestHost browser,
        IntPtr quickSwitch,
        string initialFolder,
        string targetFolder,
        AutomationElement breadcrumb)
    {
        var metadata = new
        {
            schemaVersion = 1,
            passed = true,
            scenario = "published-app-real-ctrl-g-compiled-plugin-nonstandard-direct-navigation",
            package = new { executable = packageExe, executableSha256 = Sha256(packageExe), processId = appProcessId },
            plugin = new
            {
                id = "listaryopen.fixture.custom-browser",
                installedDirectory = plugin.DirectoryPath,
                assembly = plugin.AssemblyPath,
                assemblySha256 = Sha256(plugin.AssemblyPath),
                manifest = plugin.ManifestPath,
                manifestSha256 = Sha256(plugin.ManifestPath),
                loadedInPublishedApp = true
            },
            trigger = new { input = "Ctrl+G", e2eControlOnlyAllowedInjectedInput = true },
            sourceExplorer = new { handle = explorer.ToInt64(), folder = targetFolder },
            nonstandardTarget = new
            {
                handle = browser.WindowHandle.ToInt64(),
                processName = browser.Process.ProcessName,
                className = GetWindowClass(browser.WindowHandle),
                standardDialog = false,
                initialFolder,
                targetFolder,
                hostState = browser.ReadState()
            },
            transport = "WM_COPYDATA-json-v1-SetFolder",
            directNavigationOracle = new
            {
                breadcrumbName = breadcrumb.Current.Name,
                breadcrumbVisible = !breadcrumb.Current.IsOffscreen,
                pathInput = false,
                fallback = false
            },
            visualOracle = new
            {
                quickSwitchHandle = quickSwitch.ToInt64(),
                quickSwitchVisible = IsWindowVisible(quickSwitch),
                screenshot = Path.GetFileName(screenshot),
                screenshotSha256 = Sha256(screenshot)
            }
        };
        var path = Path.ChangeExtension(screenshot, ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(path).Length > 1_024);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static string GetWindowClass(IntPtr window)
    {
        var value = new char[256];
        var length = GetClassName(window, value, value.Length);
        return length > 0 ? new string(value, 0, length) : string.Empty;
    }

    private static string GetWindowText(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        var value = new StringBuilder(length + 1);
        _ = GetWindowText(window, value, value.Capacity);
        return value.ToString();
    }

    private sealed class InstalledPlugin : IDisposable
    {
        private InstalledPlugin(string directoryPath, string manifestPath, string assemblyPath)
        { DirectoryPath = directoryPath; ManifestPath = manifestPath; AssemblyPath = assemblyPath; }
        public string DirectoryPath { get; }
        public string ManifestPath { get; }
        public string AssemblyPath { get; }
        public static InstalledPlugin InstallUnique(string sourceManifest, string sourceAssembly)
        {
            var root = DialogPluginLoader.DefaultPluginRoot;
            Directory.CreateDirectory(root);
            var directory = Path.Combine(root, "e2e-fixture-" + Guid.NewGuid().ToString("N"));
            Assert.False(Directory.Exists(directory));
            Directory.CreateDirectory(directory);
            var manifest = Path.Combine(directory, DialogPluginLoader.ManifestFileName);
            var assembly = Path.Combine(directory, Path.GetFileName(sourceAssembly));
            File.Copy(sourceManifest, manifest);
            File.Copy(sourceAssembly, assembly);
            return new InstalledPlugin(directory, manifest, assembly);
        }
        public void Dispose()
        {
            if (!Directory.Exists(DirectoryPath)) return;
            var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DialogPluginLoader.DefaultPluginRoot));
            var actualParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(DirectoryPath)!));
            if (!string.Equals(expectedRoot, actualParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to delete a plugin outside the verified default plugin root.");
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class PackageDataCleanup : IDisposable
    {
        private readonly string _package;
        private readonly string _data;
        private PackageDataCleanup(string package, string data) { _package = package; _data = data; }
        public static PackageDataCleanup RequireInitiallyAbsent(string package)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package));
            var data = Path.GetFullPath(Path.Combine(normalized, "data"));
            Assert.Equal(normalized, Path.GetDirectoryName(data), ignoreCase: true);
            Assert.False(Directory.Exists(data));
            return new PackageDataCleanup(normalized, data);
        }
        public void Dispose()
        {
            if (!Directory.Exists(_data)) return;
            Assert.Equal(_package, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(_data)!)), ignoreCase: true);
            Directory.Delete(_data, recursive: true);
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
            _reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 256, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 256, true) { AutoFlush = true, NewLine = "\n" };
        }
        public static E2ePipeClient Connect(string name, Process app, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                app.Refresh();
                if (app.HasExited) throw new InvalidOperationException($"Published app exited early: {app.ExitCode}");
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
                try { pipe.Connect(750); return new E2ePipeClient(pipe); }
                catch (Exception exception) when (exception is TimeoutException or IOException) { pipe.Dispose(); }
            }
            throw new TimeoutException("Published app E2E pipe did not become ready.");
        }
        public string Exchange(string command) { _writer.WriteLine(command); return _reader.ReadLine() ?? throw new IOException(); }
        public void Dispose() { _writer.Dispose(); _reader.Dispose(); _pipe.Dispose(); }
    }

    private sealed class OwnedTestHost : IDisposable
    {
        private readonly string _statePath;
        private OwnedTestHost(Process process, string statePath, IntPtr window) { Process = process; _statePath = statePath; WindowHandle = window; }
        public Process Process { get; }
        public IntPtr WindowHandle { get; }
        public static OwnedTestHost StartInputWindow(string executable) => Start(executable, [
            "--mode", "input-window", "--top-level-class", "ListaryOpenPluginNeutralHost",
            "--focused-class", "Button", "--title", "ListaryOpen Plugin Neutral Host"], "WindowReady");
        public static OwnedTestHost StartCustomBrowser(string executable, string initial) => Start(executable, [
            "--mode", "custom-browser", "--initial-directory", initial], "CustomBrowserReady");
        private static OwnedTestHost Start(string executable, IReadOnlyList<string> arguments, string readyStage)
        {
            var state = Path.Combine(Path.GetTempPath(), "listary-plugin-blackbox-host-" + Guid.NewGuid().ToString("N") + ".json");
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--state"); start.ArgumentList.Add(state);
            var process = Process.Start(start); Assert.NotNull(process);
            HostState? value = null;
            Assert.True(WaitUntil(() => { value = TryReadState(state); return value?.Stage == readyStage && value.WindowHandle > 0; }, TimeSpan.FromSeconds(10)));
            return new OwnedTestHost(process, state, new IntPtr(value!.WindowHandle));
        }
        public HostState ReadState() => TryReadState(_statePath) ?? throw new InvalidDataException();
        private static HostState? TryReadState(string path)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<HostState>(File.ReadAllText(path)) : null; }
            catch (Exception exception) when (exception is IOException or JsonException) { return null; }
        }
        public void Dispose()
        {
            if (!Process.HasExited) { _ = PostMessage(WindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero); if (!Process.WaitForExit(5_000)) { Process.Kill(true); _ = Process.WaitForExit(5_000); } }
            Process.Dispose(); try { File.Delete(_statePath); } catch (IOException) { }
        }
    }

    private sealed record HostState(string Stage, string? SelectedPath, long WindowHandle);

    private sealed class OwnedExplorerWindow : IDisposable
    {
        private OwnedExplorerWindow(IntPtr windowHandle, string folder) { WindowHandle = windowHandle; Folder = folder; }
        public IntPtr WindowHandle { get; }
        public string Folder { get; }
        public static OwnedExplorerWindow Start(string folder)
        {
            var existing = EnumerateExplorerWindows().Select(item => item.Window).ToHashSet();
            using var process = Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/n,\"{folder}\"", UseShellExecute = true });
            ExplorerEntry? result = null;
            Assert.True(WaitUntil(() => { result = EnumerateExplorerWindows().FirstOrDefault(item => !existing.Contains(item.Window) && PathsEqual(item.Folder, folder)); return result is not null; }, TimeSpan.FromSeconds(15)));
            return new OwnedExplorerWindow(result!.Window, folder);
        }
        public void Dispose() { if (EnumerateExplorerWindows().Any(item => item.Window == WindowHandle && PathsEqual(item.Folder, Folder))) _ = PostMessage(WindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero); }
    }

    private static IReadOnlyList<ExplorerEntry> EnumerateExplorerWindows()
    {
        var result = new List<ExplorerEntry>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return result;
        object? shell = null; object? windows = null;
        try
        {
            shell = Activator.CreateInstance(type); windows = ((dynamic)shell!).Windows();
            foreach (var value in (System.Collections.IEnumerable)windows)
            {
                object? document = null; object? folder = null; object? self = null;
                try
                {
                    dynamic window = value; var handle = new IntPtr(Convert.ToInt64(window.HWND));
                    document = window.Document; folder = document is null ? null : ((dynamic)document).Folder; self = folder is null ? null : ((dynamic)folder).Self;
                    var path = self is null ? null : ((dynamic)self).Path as string;
                    if (handle != IntPtr.Zero && !string.IsNullOrWhiteSpace(path)) result.Add(new ExplorerEntry(handle, path));
                }
                catch { }
                finally { ReleaseCom(self); ReleaseCom(folder); ReleaseCom(document); ReleaseCom(value); }
            }
        }
        finally { ReleaseCom(windows); ReleaseCom(shell); }
        return result;
    }
    private static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value); }
    private sealed record ExplorerEntry(IntPtr Window, string Folder);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create(string prefix) { var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new TemporaryDirectory(path); }
        public void Dispose() => Directory.Delete(Path, true);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputUnion { [FieldOffset(0)] public NativeKeyboardInput Keyboard; [FieldOffset(0)] public NativeMouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouseInput { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, char[] value, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder value, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
