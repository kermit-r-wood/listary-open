using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class DialogPluginIntegrationTests
{
    private const uint WmClose = 0x0010;

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task CompiledPluginDirectlyNavigatesRealNonstandardHostThroughStructuredMessage()
    {
        using var initial = TemporaryDirectory.Create("listary-plugin-initial");
        using var target = TemporaryDirectory.Create("listary-plugin-target");
        using var host = CustomBrowserHost.Start(initial.Path, FindTestHost());
        Assert.NotEqual("#32770", GetWindowClass(host.WindowHandle));
        Assert.Equal("ListaryOpenPluginFixtureBrowser", GetWindowClass(host.WindowHandle));
        Assert.True(IsWindowVisible(host.WindowHandle));
        Assert.True(
            DesktopWindowActivator.TryActivate(host.WindowHandle),
            "The nonstandard fixture browser did not become foreground.");

        var pluginRoot = Path.Combine(AppContext.BaseDirectory, "plugin-fixture");
        var manifestPath = Path.Combine(pluginRoot, DialogPluginLoader.ManifestFileName);
        var pluginAssemblyPath = Path.Combine(pluginRoot, "ListaryOpen.DialogPlugin.TestFixture.dll");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(pluginAssemblyPath));
        var load = DialogPluginLoader.LoadFromRoot(pluginRoot);
        Assert.Empty(load.Diagnostics);
        var plugin = Assert.Single(load.Plugins);
        Assert.Equal("listaryopen.fixture.custom-browser", plugin.Id);
        var bridge = new DialogBridge(null, load.Plugins);

        var captured = Assert.IsType<DialogPluginTarget>(
            await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));
        Assert.Equal(host.WindowHandle, captured.Window.WindowHandle);
        Assert.Equal((uint)host.Process.Id, captured.Window.ProcessId);
        Assert.Equal("ListaryOpenPluginFixtureBrowser", captured.Window.ClassName);
        Assert.Equal(initial.Path, host.ReadState().SelectedPath, ignoreCase: true);

        var result = await bridge.JumpToFolderAsync(captured, target.Path, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Success, result.Status);
        Assert.Contains("structured direct navigation", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            WaitUntil(
                () =>
                {
                    var state = host.ReadState();
                    return string.Equals(state.Stage, "CustomBrowserNavigated", StringComparison.Ordinal) &&
                        PathsEqual(state.SelectedPath, target.Path);
                },
                TimeSpan.FromSeconds(5)),
            "The custom browser did not confirm the plugin's structured SetFolder command.");
        var breadcrumb = WaitForVisibleText(host.WindowHandle, target.Path, TimeSpan.FromSeconds(5));
        Assert.NotNull(breadcrumb);
        Assert.False(breadcrumb.Current.BoundingRectangle.IsEmpty);

        var screenshotPath = CaptureEvidence(host.WindowHandle, "31-nonstandard-dialog-plugin-direct");
        WriteEvidenceMetadata(
            screenshotPath,
            host,
            captured,
            target.Path,
            breadcrumb,
            manifestPath,
            pluginAssemblyPath);
    }

    private static string FindTestHost()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "ListaryOpen.TestHost",
            "bin",
            configuration,
            "net8.0-windows",
            "ListaryOpen.TestHost.exe");
        return File.Exists(path) ? path : throw new FileNotFoundException("Compiled TestHost is missing.", path);
    }

    private static AutomationElement? WaitForVisibleText(IntPtr window, string text, TimeSpan timeout)
    {
        AutomationElement? result = null;
        return WaitUntil(
            () =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(window);
                    result = root?.FindFirst(
                        TreeScope.Descendants,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                            new PropertyCondition(AutomationElement.NameProperty, text)));
                    return result is not null && !result.Current.IsOffscreen;
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

    private static string CaptureEvidence(IntPtr window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "acceptance-results", "visual-evidence")
            : Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Assert.True(GetWindowRect(window, out var bounds));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        Assert.InRange(width, 500, 2_000);
        Assert.InRange(height, 150, 1_200);
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
            Thread.Sleep(150);
            Assert.True(
                PrintWindow(window, memory, 2) ||
                BitBlt(memory, 0, 0, width, height, desktop, bounds.Left, bounds.Top, 0x00CC0020));
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
            Assert.True(stream.Length > 2_048, $"Plugin evidence screenshot was unexpectedly small: {path}");
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
        CustomBrowserHost host,
        DialogPluginTarget captured,
        string targetFolder,
        AutomationElement breadcrumb,
        string manifestPath,
        string pluginAssemblyPath)
    {
        Assert.True(GetWindowRect(host.WindowHandle, out var bounds));
        var metadata = new
        {
            schemaVersion = 1,
            passed = true,
            scenario = "compiled-dialog-plugin-real-nonstandard-host-structured-direct-navigation",
            pluginId = captured.PluginId,
            loader = "DialogPluginLoader",
            manifest = new { path = manifestPath, sha256 = Sha256(manifestPath) },
            assembly = new { path = pluginAssemblyPath, sha256 = Sha256(pluginAssemblyPath) },
            host = new
            {
                processId = host.Process.Id,
                processName = host.Process.ProcessName,
                windowHandle = host.WindowHandle.ToInt64(),
                className = GetWindowClass(host.WindowHandle),
                standardDialog = false,
                bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }
            },
            transport = "WM_COPYDATA-json-v1-SetFolder",
            pathInputSimulation = false,
            fallbackUsed = false,
            targetFolder,
            hostState = host.ReadState(),
            visualOracle = new
            {
                breadcrumbName = breadcrumb.Current.Name,
                breadcrumbVisible = !breadcrumb.Current.IsOffscreen
            },
            screenshot = Path.GetFileName(screenshotPath),
            screenshotSha256 = Sha256(screenshotPath)
        };
        var metadataPath = Path.ChangeExtension(screenshotPath, ".json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(new FileInfo(metadataPath).Length > 768);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(75);
        }

        return condition();
    }

    private static bool PathsEqual(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string GetWindowClass(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private sealed class CustomBrowserHost : IDisposable
    {
        private readonly string _statePath;

        private CustomBrowserHost(Process process, string statePath, IntPtr windowHandle)
        {
            Process = process;
            _statePath = statePath;
            WindowHandle = windowHandle;
        }

        public Process Process { get; }
        public IntPtr WindowHandle { get; }

        public static CustomBrowserHost Start(string initialDirectory, string executable)
        {
            var statePath = Path.Combine(Path.GetTempPath(), "listary-plugin-host-" + Guid.NewGuid().ToString("N") + ".json");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--mode");
            start.ArgumentList.Add("custom-browser");
            start.ArgumentList.Add("--initial-directory");
            start.ArgumentList.Add(initialDirectory);
            start.ArgumentList.Add("--state");
            start.ArgumentList.Add(statePath);
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the custom-browser TestHost.");
            HostState? state = null;
            try
            {
                Assert.True(
                    WaitUntil(
                        () =>
                        {
                            state = TryReadState(statePath);
                            return state is { Stage: "CustomBrowserReady", WindowHandle: > 0 };
                        },
                        TimeSpan.FromSeconds(10)),
                    "The nonstandard custom-browser TestHost did not become ready.");
                return new CustomBrowserHost(process, statePath, new IntPtr(state!.WindowHandle));
            }
            catch
            {
                try
                {
                    StopProcess(process, state?.WindowHandle ?? 0);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                   or System.ComponentModel.Win32Exception
                                                   or NotSupportedException)
                {
                }
                finally
                {
                    TryDeleteState(statePath);
                }

                throw;
            }
        }

        public HostState ReadState() => TryReadState(_statePath)
            ?? throw new InvalidDataException("Custom-browser state is unavailable.");

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                _ = PostMessage(WindowHandle, WmClose, IntPtr.Zero, IntPtr.Zero);
                if (!Process.WaitForExit(5_000))
                {
                    Process.Kill(entireProcessTree: true);
                    _ = Process.WaitForExit(5_000);
                }
            }

            Process.Dispose();
            TryDeleteState(_statePath);
        }

        private static void StopProcess(Process process, long windowHandle)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (windowHandle != 0)
                    {
                        _ = PostMessage(new IntPtr(windowHandle), WmClose, IntPtr.Zero, IntPtr.Zero);
                    }

                    if (!process.WaitForExit(2_000))
                    {
                        process.Kill(entireProcessTree: true);
                        _ = process.WaitForExit(5_000);
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void TryDeleteState(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static HostState? TryReadState(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<HostState>(File.ReadAllText(path))
                    : null;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return null;
            }
        }
    }

    private sealed record HostState(string Stage, string? SelectedPath, long WindowHandle);

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int xDestination, int yDestination, int width, int height, IntPtr source, int xSource, int ySource, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
