using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.IntegrationTests;

[Collection(DesktopIntegrationCollection.Name)]
public sealed class HotkeyServiceIntegrationTests
{
    private const byte VkControl = 0x11;
    private const byte VkShift = 0x10;
    private const byte VkAlt = 0x12;
    private const byte VkF23 = 0x86;
    private const byte VkF24 = 0x87;
    private const uint KeyEventKeyUp = 0x0002;

    [Fact]
    [Trait("Category", "DesktopIntegration")]
    public async Task RealWin32GlobalHotkeyRegistrationPublishesConfiguredGestures()
    {
        var ready = new TaskCompletionSource<(Dispatcher Dispatcher, HotkeyRegistrationResult Registration, IntPtr WindowHandle)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var searchPressed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogPressed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunHotkeyMessagePump(ready, dispatcherReady, searchPressed, dialogPressed))
        {
            IsBackground = true,
            Name = "ListaryOpen real global hotkey integration test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Dispatcher? dispatcher = null;
        try
        {
            var started = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher = started.Dispatcher;
            Assert.True(
                started.Registration.AllRegistered,
                "The real Win32 hotkeys could not be registered for the desktop integration test.");
            Assert.True(
                DesktopWindowActivator.TryActivate(started.WindowHandle),
                "The hotkey test-owned foreground window could not replace the system service foreground window.");

            ReleaseModifierKeys();
            Assert.Equal("Search", await SendHotkeyUntilPublishedAsync(
                searchPressed.Task,
                VkControl,
                VkShift,
                VkF23,
                TimeSpan.FromSeconds(5)));

            Assert.Equal("Dialog", await SendHotkeyUntilPublishedAsync(
                dialogPressed.Task,
                VkAlt,
                VkShift,
                VkF24,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            ReleaseModifierKeys();
            if (dispatcher is null)
            {
                try
                {
                    dispatcher = await dispatcherReady.Task.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception)
                {
                }
            }

            RequestDispatcherShutdown(dispatcher);
            var joined = thread.Join(5_000);
            if (!joined)
            {
                RequestDispatcherShutdown(dispatcher);
                joined = thread.Join(2_000);
            }

            Assert.True(joined, "The global hotkey message-pump thread did not stop and may retain Win32 hotkeys.");
        }
    }

    private static void RunHotkeyMessagePump(
        TaskCompletionSource<(Dispatcher Dispatcher, HotkeyRegistrationResult Registration, IntPtr WindowHandle)> ready,
        TaskCompletionSource<Dispatcher> dispatcherReady,
        TaskCompletionSource<string> searchPressed,
        TaskCompletionSource<string> dialogPressed)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcherReady.TrySetResult(dispatcher);
            using var foregroundWindow = new HwndSource(new HwndSourceParameters("ListaryOpen Hotkey Integration Target")
            {
                Width = 480,
                Height = 140,
                PositionX = 80,
                PositionY = 80,
                WindowStyle = unchecked((int)0x10CF0000)
            });
            using var service = new HotkeyService();
            service.HotkeyPressed += (_, name) =>
            {
                if (string.Equals(name, "Search", StringComparison.Ordinal))
                {
                    searchPressed.TrySetResult(name);
                }
                else if (string.Equals(name, "Dialog", StringComparison.Ordinal))
                {
                    dialogPressed.TrySetResult(name);
                }
            };
            var registration = service.Register("Ctrl+Shift+F23", "Alt+Shift+F24");
            ready.TrySetResult((dispatcher, registration, foregroundWindow.Handle));
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            dispatcherReady.TrySetException(exception);
            ready.TrySetException(exception);
            searchPressed.TrySetException(exception);
            dialogPressed.TrySetException(exception);
        }
    }

    private static void RequestDispatcherShutdown(Dispatcher? dispatcher)
    {
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void SendHotkey(byte firstModifier, byte secondModifier, byte virtualKey)
    {
        keybd_event(firstModifier, 0, 0, UIntPtr.Zero);
        keybd_event(secondModifier, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(secondModifier, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(firstModifier, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static async Task<string> SendHotkeyUntilPublishedAsync(
        Task<string> published,
        byte firstModifier,
        byte secondModifier,
        byte virtualKey,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            SendHotkey(firstModifier, secondModifier, virtualKey);
            var completed = await Task.WhenAny(published, Task.Delay(250));
            if (completed == published)
            {
                return await published;
            }
        }

        throw new TimeoutException("The registered Win32 hotkey did not publish before the deadline.");
    }

    private static void ReleaseModifierKeys()
    {
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkShift, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkAlt, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
