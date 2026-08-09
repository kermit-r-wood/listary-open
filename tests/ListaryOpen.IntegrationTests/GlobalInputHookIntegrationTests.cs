using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class GlobalInputHookIntegrationTests
{
    private const byte VkControl = 0x11;
    private const byte VkA = 0x41;
    private const byte VkBack = 0x08;
    private const byte VkEscape = 0x1B;
    private const byte VkReturn = 0x0D;
    private const byte VkDown = 0x28;
    private const byte VkO = 0x4F;
    private const byte VkN = 0x4E;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;

    [Theory]
    [InlineData("CabinetWClass", "DirectUIHWND", 0, false, false, GlobalTextInputHost.Explorer)]
    [InlineData("TaskManagerWindow", "DirectUIHWND", 0, true, false, GlobalTextInputHost.TaskManager)]
    [InlineData("ListaryOpenDialogHost", "Edit", 1148, false, true, GlobalTextInputHost.Dialog)]
    [Trait("Category", "DesktopIntegration")]
    public async Task LowLevelHookRoutesTextFromRealForegroundAndFocusedHostControls(
        string topLevelClass,
        string focusedClass,
        int focusedControlId,
        bool taskManagerProcessName,
        bool attachAsDialog,
        GlobalTextInputHost expectedHost)
    {
        using var host = await InputWindowSession.StartAsync(
            topLevelClass,
            focusedClass,
            focusedControlId,
            taskManagerProcessName);
        using var hook = new GlobalInputHookHarness();
        if (attachAsDialog)
        {
            hook.Service.DialogInputWindow = host.WindowHandle;
        }

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        Assert.True(
            DesktopWindowActivator.TryActivate(host.WindowHandle, TimeSpan.FromSeconds(1))
            && GetForegroundWindow() == host.WindowHandle,
            "The unsupported host lost foreground before the real key input was sent.");
        SendVirtualKey(VkO);

        Assert.True(await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)), "The low-level hook did not route typed text.");
        Assert.True(hook.TextInputs.TryDequeue(out var input));
        Assert.Equal("o", input.Text, ignoreCase: true);
        Assert.Equal(expectedHost, input.Host);
        Assert.Equal(host.WindowHandle, input.ForegroundWindow);
        Assert.Equal(focusedClass, input.FocusedControlClass, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task ActiveTaskManagerOverlaySuppressesTextEvenWhenItsNativeSearchEditHasFocus()
    {
        using var host = await InputWindowSession.StartAsync(
            "TaskManagerWindow",
            "Edit",
            focusedControlId: 0,
            taskManagerProcessName: true);
        using var hook = new GlobalInputHookHarness();
        hook.Service.OverlayTextInputWindow = host.WindowHandle;
        hook.Service.CaptureOverlayInput = true;

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);

        Assert.True(
            await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)),
            "Text was not rerouted after the native Task Manager search edit regained focus.");
        Assert.True(hook.TextInputs.TryDequeue(out var input));
        Assert.Equal(GlobalTextInputHost.TaskManager, input.Host);
        Assert.Equal("o", input.Text, ignoreCase: true);
        await Task.Delay(100);
        Assert.Equal(
            string.Empty,
            ReadWindowText(host.FocusedHandle));
    }

    [Theory]
    [InlineData("CabinetWClass", false, GlobalTextInputHost.Explorer)]
    [InlineData("ListaryOpenDialogHost", true, GlobalTextInputHost.Dialog)]
    [Trait("Category", "DesktopIntegration")]
    public async Task ActiveExplorerAndDialogOverlaysSuppressTextWhenNativeEditsHaveFocus(
        string topLevelClass,
        bool attachAsDialog,
        GlobalTextInputHost expectedHost)
    {
        using var host = await InputWindowSession.StartAsync(
            topLevelClass,
            "Edit",
            focusedControlId: 41477,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();
        if (attachAsDialog)
        {
            hook.Service.DialogInputWindow = host.WindowHandle;
        }
        hook.Service.OverlayTextInputWindow = host.WindowHandle;
        hook.Service.CaptureOverlayInput = true;

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);

        Assert.True(
            await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)),
            "Text was not rerouted after a native host edit regained focus.");
        Assert.True(hook.TextInputs.TryDequeue(out var input));
        Assert.Equal(expectedHost, input.Host);
        Assert.Equal("o", input.Text, ignoreCase: true);
        await Task.Delay(100);
        Assert.Equal(string.Empty, ReadWindowText(host.FocusedHandle));
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task DialogInputCanRestartAfterACompletedSearchAndStillRoutesEditCommands()
    {
        using var host = await InputWindowSession.StartAsync(
            "ListaryOpenRepeatedDialogHost",
            "Edit",
            1148,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();
        hook.Service.DialogInputWindow = host.WindowHandle;

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);
        Assert.True(await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.TextInputs.TryDequeue(out var first));
        Assert.Equal("o", first.Text, ignoreCase: true);

        hook.Service.CaptureOverlayInput = true;
        SendVirtualKeyChord(VkControl, VkA);
        Assert.True(await hook.EditSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.EditCommands.TryDequeue(out var selectAll));
        Assert.Equal(GlobalEditCommand.SelectAll, selectAll.Command);

        hook.Service.CaptureOverlayInput = false;
        hook.Service.DialogInputWindow = host.WindowHandle;
        SendVirtualKey(VkN);
        Assert.True(await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)), "The second dialog search input was not routed.");
        Assert.True(hook.TextInputs.TryDequeue(out var second));
        Assert.Equal("n", second.Text, ignoreCase: true);
        Assert.Equal(GlobalTextInputHost.Dialog, second.Host);
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task OverlayCaptureRoutesBackspaceNavigationEnterAndEscapeWithoutClosingHost()
    {
        using var host = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "DirectUIHWND",
            0,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();
        hook.Service.CaptureOverlayInput = true;

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkBack);
        Assert.True(await hook.EditSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.EditCommands.TryDequeue(out var backspace));
        Assert.Equal(GlobalEditCommand.Backspace, backspace.Command);

        SendVirtualKey(VkDown);
        Assert.True(await hook.NavigationSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.NavigationInputs.TryDequeue(out var navigation));
        Assert.Equal(1, navigation.SelectionDelta);

        SendVirtualKey(VkReturn);
        Assert.True(await hook.ConfirmSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.ConfirmInputs.TryDequeue(out _));

        SendVirtualKey(VkEscape);
        Assert.True(await hook.EscapeSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.EscapeInputs.TryDequeue(out var escape));
        Assert.Equal(host.WindowHandle, escape.ForegroundWindow);
        Assert.False(host.Process.HasExited, "Captured Escape leaked through and closed the host window.");
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task FollowUpTextCaptureRoutesTypingButLeavesBackspaceForExplorer()
    {
        // Post-jump follow-up must reopen type-to-search on the next printable key
        // without stealing Explorer Backspace (parent-folder navigation).
        using var host = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "DirectUIHWND",
            0,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();
        hook.Service.OverlayTextInputWindow = host.WindowHandle;
        hook.Service.CaptureFollowUpTextInput = true;
        hook.Service.YieldHostSearchBoxToNativeInput = true;
        hook.Service.CaptureOverlayInput = false;

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        Assert.True(
            DesktopWindowActivator.TryActivate(host.WindowHandle, TimeSpan.FromSeconds(1))
            && GetForegroundWindow() == host.WindowHandle,
            "Explorer host lost foreground before follow-up input.");

        // Full overlay would raise EditCommand.Backspace; follow-up must not.
        SendVirtualKey(VkBack);
        Assert.False(
            await hook.EditSignal.WaitAsync(TimeSpan.FromMilliseconds(500)),
            "Follow-up mode must not treat Backspace as an overlay edit command.");
        Assert.Empty(hook.EditCommands);

        // Follow-up Backspace must always notify the app so it can navigate parent
        // when Shell focus is not on the items view. Soft-waiting here previously
        // masked a constructor bug that dropped the event entirely.
        Assert.True(
            await hook.FollowUpBackspaceSignal.WaitAsync(TimeSpan.FromSeconds(2)),
            "Follow-up mode must raise FollowUpHostBackspacePassthrough for Explorer Backspace.");
        Assert.True(hook.FollowUpBackspaces.TryDequeue(out var passthrough));
        Assert.Equal(host.WindowHandle, passthrough.ForegroundWindow);
        Assert.Equal(GlobalTextInputHost.Explorer, passthrough.Host);
        Assert.Equal(string.Empty, passthrough.Text);

        SendVirtualKey(VkO);
        Assert.True(
            await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)),
            "Follow-up mode did not route printable text after Backspace.");
        Assert.True(hook.TextInputs.TryDequeue(out var text));
        Assert.Equal("o", text.Text, ignoreCase: true);
        Assert.Equal(GlobalTextInputHost.Explorer, text.Host);
        Assert.Empty(hook.NavigationInputs);
        Assert.Empty(hook.ConfirmInputs);
        Assert.Empty(hook.EscapeInputs);
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task FollowUpTextCaptureYieldsNativeSearchBoxButCapturesAddressEdit()
    {
        using var searchBoxHost = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "SearchBoxControl",
            0,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();
        hook.Service.OverlayTextInputWindow = searchBoxHost.WindowHandle;
        hook.Service.CaptureFollowUpTextInput = true;
        hook.Service.YieldHostSearchBoxToNativeInput = true;
        hook.Service.CaptureOverlayInput = false;

        Assert.True(DesktopWindowActivator.TryActivate(searchBoxHost.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);
        Assert.False(
            await hook.TextSignal.WaitAsync(TimeSpan.FromMilliseconds(750)),
            "Follow-up capture must not steal Explorer native SearchBox keystrokes.");

        using var addressHost = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "Edit",
            0,
            taskManagerProcessName: false);
        hook.Service.OverlayTextInputWindow = addressHost.WindowHandle;
        Assert.True(DesktopWindowActivator.TryActivate(addressHost.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkN);
        Assert.True(
            await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)),
            "Follow-up capture should reclaim address-band Edit to reopen type-to-search.");
        Assert.True(hook.TextInputs.TryDequeue(out var text));
        Assert.Equal("n", text.Text, ignoreCase: true);
        Assert.Equal(GlobalTextInputHost.Explorer, text.Host);
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task FullOverlayCaptureStillInterceptsSearchBoxWhileSessionIsActive()
    {
        using var host = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "SearchBoxControl",
            0,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();
        hook.Service.OverlayTextInputWindow = host.WindowHandle;
        hook.Service.CaptureOverlayInput = true;
        hook.Service.YieldHostSearchBoxToNativeInput = false;

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);
        Assert.True(
            await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)),
            "Active Explorer type-to-search must keep keys out of the native SearchBox.");
        Assert.True(hook.TextInputs.TryDequeue(out var text));
        Assert.Equal("o", text.Text, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task ForegroundSwitchRoutesRepeatedInputToTheActiveExplorerWindow()
    {
        using var firstHost = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "DirectUIHWND",
            0,
            taskManagerProcessName: false);
        using var secondHost = await InputWindowSession.StartAsync(
            "CabinetWClass",
            "DirectUIHWND",
            0,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();

        Assert.True(DesktopWindowActivator.TryActivate(firstHost.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);
        Assert.True(await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.TextInputs.TryDequeue(out var firstInput));
        Assert.Equal(firstHost.WindowHandle, firstInput.ForegroundWindow);

        Assert.True(DesktopWindowActivator.TryActivate(secondHost.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkN);
        Assert.True(await hook.TextSignal.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(hook.TextInputs.TryDequeue(out var secondInput));
        Assert.Equal(secondHost.WindowHandle, secondInput.ForegroundWindow);
        Assert.Equal(GlobalTextInputHost.Explorer, secondInput.Host);
    }

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task UnsupportedForegroundWindowDoesNotStartGlobalTypeSearch()
    {
        using var host = await InputWindowSession.StartAsync(
            "ListaryOpenUnsupportedHost",
            "DirectUIHWND",
            0,
            taskManagerProcessName: false);
        using var hook = new GlobalInputHookHarness();

        Assert.True(DesktopWindowActivator.TryActivate(host.WindowHandle));
        await Task.Delay(100);
        SendVirtualKey(VkO);

        Assert.False(
            await hook.TextSignal.WaitAsync(TimeSpan.FromMilliseconds(750)),
            "Typing in an unsupported foreground window was incorrectly routed into ListaryOpen search.");
        Assert.Empty(hook.TextInputs);
        Assert.False(host.Process.HasExited);
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

    private static string ReadWindowText(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private sealed class GlobalInputHookHarness : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private uint _threadId;
        private int _disposed;
        private Exception? _startupFailure;
        private GlobalTextInputService? _service;

        public GlobalInputHookHarness()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ListaryOpen global input integration hook"
            };
            _thread.Start();
            var started = _ready.Wait(TimeSpan.FromSeconds(5));
            if (!started)
            {
                Dispose();
                throw new TimeoutException("Global input hook thread did not start.");
            }

            if (_startupFailure is not null)
            {
                Dispose();
                throw new InvalidOperationException("Global input hook could not start.", _startupFailure);
            }
        }

        public GlobalTextInputService Service =>
            _service ?? throw new InvalidOperationException("Global input hook is not ready.");

        public ConcurrentQueue<GlobalTextInputEventArgs> TextInputs { get; } = new();
        public ConcurrentQueue<GlobalEditCommandInputEventArgs> EditCommands { get; } = new();
        public ConcurrentQueue<GlobalNavigationInputEventArgs> NavigationInputs { get; } = new();
        public ConcurrentQueue<GlobalConfirmInputEventArgs> ConfirmInputs { get; } = new();
        public ConcurrentQueue<GlobalEscapeInputEventArgs> EscapeInputs { get; } = new();
        public ConcurrentQueue<GlobalTextInputEventArgs> FollowUpBackspaces { get; } = new();
        public SemaphoreSlim TextSignal { get; } = new(0);
        public SemaphoreSlim EditSignal { get; } = new(0);
        public SemaphoreSlim NavigationSignal { get; } = new(0);
        public SemaphoreSlim ConfirmSignal { get; } = new(0);
        public SemaphoreSlim EscapeSignal { get; } = new(0);
        public SemaphoreSlim FollowUpBackspaceSignal { get; } = new(0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var quitPosted = !_thread.IsAlive;
            var joined = !_thread.IsAlive;
            try
            {
                if (_thread.IsAlive && _threadId != 0)
                {
                    quitPosted = PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                }

                joined = _thread.Join(10_000);
                if (!joined)
                {
                    // Emergency unhook keeps a failed test from contaminating every
                    // following desktop test; the owning thread still gets another quit.
                    _service?.Dispose();
                    if (_threadId != 0)
                    {
                        quitPosted |= PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
                    }

                    joined = _thread.Join(2_000);
                }
            }
            finally
            {
                _ready.Dispose();
                TextSignal.Dispose();
                EditSignal.Dispose();
                NavigationSignal.Dispose();
                ConfirmSignal.Dispose();
                EscapeSignal.Dispose();
                FollowUpBackspaceSignal.Dispose();
            }

            Assert.True(quitPosted || joined, "The global input hook test could not stop its message-pump thread.");
            Assert.True(joined, "The global input hook test message-pump thread leaked into the next desktop test.");
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
                    TextSignal.Release();
                };
                _service.EditCommandPressed += (_, input) =>
                {
                    input.Handled = true;
                    EditCommands.Enqueue(input);
                    EditSignal.Release();
                };
                _service.NavigationPressed += (_, input) =>
                {
                    input.Handled = true;
                    NavigationInputs.Enqueue(input);
                    NavigationSignal.Release();
                };
                _service.ConfirmPressed += (_, input) =>
                {
                    input.Handled = true;
                    ConfirmInputs.Enqueue(input);
                    ConfirmSignal.Release();
                };
                _service.EscapePressed += (_, input) =>
                {
                    EscapeInputs.Enqueue(input);
                    EscapeSignal.Release();
                };
                _service.FollowUpHostBackspacePassthrough += (_, input) =>
                {
                    FollowUpBackspaces.Enqueue(input);
                    FollowUpBackspaceSignal.Release();
                };
                _service.Start();
            }
            catch (Exception exception)
            {
                _service?.Dispose();
                _startupFailure = exception;
                TrySignalReady();
                return;
            }

            TrySignalReady();
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
                _service.Dispose();
            }
        }

        private void TrySignalReady()
        {
            try
            {
                _ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class InputWindowSession : IDisposable
    {
        private readonly string _statePath;
        private readonly string? _renamedExecutable;

        private InputWindowSession(
            Process process,
            IntPtr windowHandle,
            IntPtr focusedHandle,
            string statePath,
            string? renamedExecutable)
        {
            Process = process;
            WindowHandle = windowHandle;
            FocusedHandle = focusedHandle;
            _statePath = statePath;
            _renamedExecutable = renamedExecutable;
        }

        public Process Process { get; }
        public IntPtr WindowHandle { get; }
        public IntPtr FocusedHandle { get; }

        public static async Task<InputWindowSession> StartAsync(
            string topLevelClass,
            string focusedClass,
            int focusedControlId,
            bool taskManagerProcessName)
        {
            var repository = FindRepositoryRoot();
            var hostPath = Path.Combine(
                repository,
                "tests",
                "ListaryOpen.TestHost",
                "bin",
                "Release",
                "net8.0-windows",
                "ListaryOpen.TestHost.exe");
            Assert.True(File.Exists(hostPath), $"Input test host was not found: {hostPath}");
            string? renamedExecutable = null;
            if (taskManagerProcessName)
            {
                renamedExecutable = Path.Combine(Path.GetDirectoryName(hostPath)!, "Taskmgr.exe");
                File.Copy(hostPath, renamedExecutable, overwrite: true);
                hostPath = renamedExecutable;
            }

            var statePath = Path.Combine(Path.GetTempPath(), $"listary-open-input-host-{Guid.NewGuid():N}.json");
            var start = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                WorkingDirectory = repository
            };
            AddArgument(start, "--mode", "input-window");
            AddArgument(start, "--top-level-class", topLevelClass);
            AddArgument(start, "--focused-class", focusedClass);
            AddArgument(start, "--focused-control-id", focusedControlId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddArgument(start, "--state", statePath);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the input test host.");
            try
            {
                var state = await WaitForStateAsync(statePath, process, TimeSpan.FromSeconds(10));
                Assert.Equal("WindowReady", state.Stage);
                Assert.NotNull(state.WindowHandle);
                Assert.NotNull(state.FocusedHandle);
                return new InputWindowSession(
                    process,
                    new IntPtr(state.WindowHandle.Value),
                    new IntPtr(state.FocusedHandle.Value),
                    statePath,
                    renamedExecutable);
            }
            catch
            {
                Terminate(process);
                TryDelete(statePath);
                TryDelete(renamedExecutable);
                throw;
            }
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                _ = PostMessage(WindowHandle, WmClose, UIntPtr.Zero, IntPtr.Zero);
                if (!Process.WaitForExit(3_000))
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(3_000);
                }
            }

            Process.Dispose();
            TryDelete(_statePath);
            TryDelete(_renamedExecutable);
        }

        private static void AddArgument(ProcessStartInfo start, string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }

        private static async Task<InputHostState> WaitForStateAsync(
            string statePath,
            Process process,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(statePath))
                    {
                        var state = TryReadState(statePath);
                        if (state?.Stage is "WindowReady" or "Failed")
                        {
                            if (state.Stage == "Failed")
                            {
                                throw new InvalidOperationException(state.Error);
                            }

                            return state;
                        }
                    }
                }
                catch (JsonException)
                {
                }
                catch (IOException)
                {
                }

                if (process.HasExited)
                {
                    var finalState = TryReadState(statePath);
                    throw new InvalidOperationException(
                        $"Input test host exited early with code {process.ExitCode}. " +
                        $"Stage: {finalState?.Stage ?? "missing"}. Error: {finalState?.Error ?? "none"}.");
                }

                await Task.Delay(50);
            }

            throw new TimeoutException("Input test host did not publish its window state.");
        }

        private static InputHostState? TryReadState(string statePath)
        {
            try
            {
                if (!File.Exists(statePath))
                {
                    return null;
                }

                using var stream = new FileStream(
                    statePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<InputHostState>(stream);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return null;
            }
        }
    }

    private sealed record InputHostState(
        string Stage,
        string Mode,
        bool? Accepted,
        string? SelectedPath,
        string? Error,
        long? WindowHandle,
        long? FocusedHandle);

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

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimum,
        uint maximum,
        uint removeMessage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
