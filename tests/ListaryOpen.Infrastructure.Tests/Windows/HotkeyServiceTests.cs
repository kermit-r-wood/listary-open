using ListaryOpen.Infrastructure.Windows;
using System.Windows.Interop;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void RaiseForTestsPublishesHotkeyName()
    {
        using var service = new HotkeyService();
        var receivedName = string.Empty;

        service.HotkeyPressed += (_, name) => receivedName = name;

        service.RaiseForTests("Search");

        Assert.Equal("Search", receivedName);
    }

    [Fact]
    public void RegisterDefaultsRegistersCtrlSpaceAndCtrlG()
    {
        var registrar = new RecordingHotkeyRegistrar();
        var messageSource = new RecordingHotkeyMessageSource();
        using var service = new HotkeyService(registrar, messageSource);

        var result = service.RegisterDefaults();

        Assert.Equal(
            new[]
            {
                new ObservedHotkey(1, HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, (uint)KeyCodes.Space),
                new ObservedHotkey(2, HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, (uint)KeyCodes.G)
            },
            registrar.RegisteredHotkeys);
        Assert.True(result.AllRegistered);
        Assert.True(result.AnyRegistered);
        Assert.Equal(
            new[]
            {
                new HotkeyRegistration("Ctrl+Space", true),
                new HotkeyRegistration("Ctrl+G", true)
            },
            result.Hotkeys);
        Assert.Equal(1, messageSource.AttachCount);
    }

    [Fact]
    public void RegisterDefaultsReportsPartialRegistrationFailure()
    {
        var registrar = new RecordingHotkeyRegistrar
        {
            FailedRegistrationIds = { 1 }
        };
        var messageSource = new RecordingHotkeyMessageSource();
        using var service = new HotkeyService(registrar, messageSource);

        var result = service.RegisterDefaults();

        Assert.False(result.AllRegistered);
        Assert.True(result.AnyRegistered);
        Assert.Equal(new[] { "Ctrl+Space" }, result.FailedHotkeys.Select(hotkey => hotkey.Name));
        Assert.Equal(new[] { "Ctrl+G" }, result.RegisteredHotkeys.Select(hotkey => hotkey.Name));
        Assert.Equal(1, messageSource.AttachCount);
    }

    [Fact]
    public void RegisterDefaultsReportsTotalRegistrationFailureWithoutMessageHook()
    {
        var registrar = new RecordingHotkeyRegistrar
        {
            FailedRegistrationIds = { 1, 2 }
        };
        var messageSource = new RecordingHotkeyMessageSource();
        using var service = new HotkeyService(registrar, messageSource);

        var result = service.RegisterDefaults();

        Assert.False(result.AllRegistered);
        Assert.False(result.AnyRegistered);
        Assert.Equal(new[] { "Ctrl+Space", "Ctrl+G" }, result.FailedHotkeys.Select(hotkey => hotkey.Name));
        Assert.Equal(0, messageSource.AttachCount);
    }

    [Fact]
    public void RegisteredHotkeyMessagePublishesSemanticHotkeyName()
    {
        var messageSource = new RecordingHotkeyMessageSource();
        using var service = new HotkeyService(new RecordingHotkeyRegistrar(), messageSource);
        var receivedNames = new List<string>();
        service.HotkeyPressed += (_, name) => receivedNames.Add(name);
        service.RegisterDefaults();

        messageSource.RaiseHotkey(1);
        messageSource.RaiseHotkey(2);

        Assert.Equal(new[] { "Ctrl+Space", "Ctrl+G" }, receivedNames);
    }

    [Fact]
    public void DisposeUnregistersRegisteredHotkeysAndDetachesMessageHookOnce()
    {
        var registrar = new RecordingHotkeyRegistrar
        {
            FailedRegistrationIds = { 2 }
        };
        var messageSource = new RecordingHotkeyMessageSource();
        var service = new HotkeyService(registrar, messageSource);

        var result = service.RegisterDefaults();
        var repeatedResult = service.RegisterDefaults();
        service.Dispose();
        service.Dispose();

        Assert.Same(result, repeatedResult);
        Assert.Equal(new[] { 1 }, registrar.UnregisteredIds);
        Assert.Equal(1, messageSource.AttachCount);
        Assert.Equal(1, messageSource.DetachCount);
    }

    private sealed class RecordingHotkeyRegistrar : IHotkeyRegistrar
    {
        public List<int> FailedRegistrationIds { get; } = new();

        public List<ObservedHotkey> RegisteredHotkeys { get; } = new();

        public List<int> UnregisteredIds { get; } = new();

        public bool Register(int id, HotkeyModifiers modifiers, uint virtualKey)
        {
            RegisteredHotkeys.Add(new ObservedHotkey(id, modifiers, virtualKey));
            return !FailedRegistrationIds.Contains(id);
        }

        public void Unregister(int id)
        {
            UnregisteredIds.Add(id);
        }
    }

    private sealed record ObservedHotkey(int Id, HotkeyModifiers Modifiers, uint VirtualKey);

    private sealed class RecordingHotkeyMessageSource : IHotkeyMessageSource
    {
        private ThreadMessageEventHandler? _handler;

        public int AttachCount { get; private set; }

        public int DetachCount { get; private set; }

        public void Attach(ThreadMessageEventHandler handler)
        {
            AttachCount++;
            _handler += handler;
        }

        public void Detach(ThreadMessageEventHandler handler)
        {
            DetachCount++;
            _handler -= handler;
        }

        public void RaiseHotkey(int id)
        {
            var message = new MSG
            {
                message = HotkeyService.HotkeyMessageId,
                wParam = id
            };
            var handled = false;

            _handler?.Invoke(ref message, ref handled);

            Assert.True(handled);
        }
    }
}
