using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ListaryOpen.TestHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        try
        {
            if (options.Mode is "open-file" or "open-folder"
                && !string.IsNullOrWhiteSpace(options.ReadyEventName))
            {
                WaitForNativeHookPreload(options);
            }
            return options.Mode switch
            {
                "open-file" => ShowOpenFileDialog(options),
                "open-folder" => ShowOpenFolderDialog(options),
                "input-window" => ShowInputWindow(options),
                "custom-browser" => ShowCustomBrowser(options),
                _ => throw new ArgumentException($"Unsupported test-host mode '{options.Mode}'.")
            };
        }
        catch (Exception exception)
        {
            WriteState(options.StatePath, new HostState("Failed", options.Mode, null, null, exception.ToString()));
            return 1;
        }
    }

    private static void WaitForNativeHookPreload(HostOptions options)
    {
        using var readyEvent = EventWaitHandle.OpenExisting(options.ReadyEventName!);
        var window = new Window
        {
            Title = "ListaryOpen Native Hook Preload",
            Width = 360,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        timer.Tick += (_, _) =>
        {
            if (readyEvent.WaitOne(0))
            {
                timer.Stop();
                window.Close();
            }
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            WriteState(
                options.StatePath,
                new HostState("HookPreloadReady", options.Mode, null, null, null, hwnd.ToInt64()));
            timer.Start();
        };
        _ = window.ShowDialog();
    }

    private static int ShowOpenFileDialog(HostOptions options)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ListaryOpen Integration Open File",
            InitialDirectory = options.InitialDirectory,
            CheckFileExists = false,
            CheckPathExists = true,
            Multiselect = false
        };
        WriteState(options.StatePath, new HostState("DialogOpening", options.Mode, null, null, null));
        var accepted = dialog.ShowDialog() == true;
        WriteState(
            options.StatePath,
            new HostState("DialogClosed", options.Mode, accepted, accepted ? dialog.FileName : null, null));
        return 0;
    }

    private static int ShowOpenFolderDialog(HostOptions options)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ListaryOpen Integration Choose Folder",
            InitialDirectory = options.InitialDirectory,
            Multiselect = false
        };
        WriteState(options.StatePath, new HostState("DialogOpening", options.Mode, null, null, null));
        var accepted = dialog.ShowDialog() == true;
        WriteState(
            options.StatePath,
            new HostState("DialogClosed", options.Mode, accepted, accepted ? dialog.FolderName : null, null));
        return 0;
    }

    private static int ShowInputWindow(HostOptions options)
    {
        return NativeInputHost.Run(
            options.TopLevelClass,
            options.FocusedClass,
            options.FocusedControlId,
            options.Title,
            (windowHandle, focusedHandle) => WriteState(
                options.StatePath,
                new HostState(
                    "WindowReady",
                    options.Mode,
                    null,
                    null,
                    null,
                    windowHandle.ToInt64(),
                    focusedHandle.ToInt64())));
    }

    private static int ShowCustomBrowser(HostOptions options) =>
        CustomDialogPluginHost.Run(
            options.InitialDirectory,
            (windowHandle, folderPath, navigated) => WriteState(
                options.StatePath,
                new HostState(
                    navigated ? "CustomBrowserNavigated" : "CustomBrowserReady",
                    options.Mode,
                    null,
                    folderPath,
                    null,
                    windowHandle.ToInt64())));


    private static void WriteState(string path, HostState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record HostState(
        string Stage,
        string Mode,
        bool? Accepted,
        string? SelectedPath,
        string? Error,
        long? WindowHandle = null,
        long? FocusedHandle = null);

    private sealed record HostOptions(
        string Mode,
        string InitialDirectory,
        string StatePath,
        string TopLevelClass,
        string FocusedClass,
        int FocusedControlId,
        string Title,
        string? ReadyEventName)
    {
        public static HostOptions Parse(IReadOnlyList<string> args)
        {
            string? Value(string name)
            {
                for (var index = 0; index + 1 < args.Count; index++)
                {
                    if (string.Equals(args[index], name, StringComparison.Ordinal))
                    {
                        return args[index + 1];
                    }
                }

                return null;
            }

            var mode = Value("--mode") ?? "open-file";
            var initialDirectory = Path.GetFullPath(Value("--initial-directory") ?? Environment.CurrentDirectory);
            var statePath = Path.GetFullPath(
                Value("--state") ?? Path.Combine(Path.GetTempPath(), $"listary-open-test-host-{Environment.ProcessId}.json"));
            var topLevelClass = Value("--top-level-class") ?? "ListaryOpenIntegrationWindow";
            var focusedClass = Value("--focused-class") ?? "DirectUIHWND";
            var focusedControlId = int.TryParse(Value("--focused-control-id"), out var parsedControlId)
                ? parsedControlId
                : 0;
            var title = Value("--title") ?? (mode == "custom-browser"
                ? CustomDialogPluginHost.WindowTitle
                : "ListaryOpen Input Integration Host");
            var readyEventName = Value("--ready-event");
            return new HostOptions(
                mode,
                initialDirectory,
                statePath,
                topLevelClass,
                focusedClass,
                focusedControlId,
                title,
                readyEventName);
        }
    }
}
