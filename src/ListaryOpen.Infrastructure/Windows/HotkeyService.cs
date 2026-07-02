using System.Diagnostics;
using System.Windows.Interop;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class HotkeyService : IDisposable
{
    internal const int HotkeyMessageId = 0x0312;

    private static readonly HotkeyDefinition[] DefaultHotkeys =
    {
        new(1, "Ctrl+Space", HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, KeyCodes.Space),
        new(2, "Ctrl+G", HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, KeyCodes.G)
    };

    private readonly IHotkeyMessageSource _messageSource;
    private readonly IHotkeyRegistrar _registrar;
    private readonly Dictionary<int, string> _registeredHotkeys = new();
    private bool _disposed;
    private bool _messageHookAttached;
    private bool _registrationAttempted;
    private HotkeyRegistrationResult? _registrationResult;

    public HotkeyService()
        : this(new Win32HotkeyRegistrar(), new ComponentDispatcherHotkeyMessageSource())
    {
    }

    internal HotkeyService(IHotkeyRegistrar registrar, IHotkeyMessageSource messageSource)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _messageSource = messageSource ?? throw new ArgumentNullException(nameof(messageSource));
    }

    public event EventHandler<string>? HotkeyPressed;

    public HotkeyRegistrationResult RegisterDefaults()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registrationAttempted)
        {
            return _registrationResult ?? HotkeyRegistrationResult.Empty;
        }

        _registrationAttempted = true;

        var registrations = new List<HotkeyRegistration>(DefaultHotkeys.Length);
        foreach (var hotkey in DefaultHotkeys)
        {
            var registered = _registrar.Register(hotkey.Id, hotkey.Modifiers, hotkey.VirtualKey);
            registrations.Add(new HotkeyRegistration(hotkey.Name, registered));

            if (!registered)
            {
                Trace.TraceWarning("Failed to register global hotkey '{0}'. Another application may already own it.", hotkey.Name);
                continue;
            }

            _registeredHotkeys.Add(hotkey.Id, hotkey.Name);
        }

        if (_registeredHotkeys.Count > 0)
        {
            _messageSource.Attach(OnThreadPreprocessMessage);
            _messageHookAttached = true;
        }

        _registrationResult = new HotkeyRegistrationResult(registrations);
        return _registrationResult;
    }

    public void RaiseForTests(string name)
    {
        HotkeyPressed?.Invoke(this, name);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_messageHookAttached)
        {
            _messageSource.Detach(OnThreadPreprocessMessage);
            _messageHookAttached = false;
        }

        foreach (var id in _registeredHotkeys.Keys)
        {
            _registrar.Unregister(id);
        }

        _registeredHotkeys.Clear();
        _disposed = true;
    }

    private void OnThreadPreprocessMessage(ref MSG message, ref bool handled)
    {
        if (message.message != HotkeyMessageId)
        {
            return;
        }

        var id = message.wParam.ToInt32();
        if (!_registeredHotkeys.TryGetValue(id, out var name))
        {
            return;
        }

        handled = true;
        HotkeyPressed?.Invoke(this, name);
    }

    private sealed record HotkeyDefinition(int Id, string Name, HotkeyModifiers Modifiers, uint VirtualKey);
}

public sealed record HotkeyRegistration(string Name, bool IsRegistered);

public sealed class HotkeyRegistrationResult
{
    public static HotkeyRegistrationResult Empty { get; } = new(Array.Empty<HotkeyRegistration>());

    public HotkeyRegistrationResult(IEnumerable<HotkeyRegistration> hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);

        Hotkeys = hotkeys.ToArray();
        RegisteredHotkeys = Hotkeys.Where(hotkey => hotkey.IsRegistered).ToArray();
        FailedHotkeys = Hotkeys.Where(hotkey => !hotkey.IsRegistered).ToArray();
    }

    public IReadOnlyList<HotkeyRegistration> Hotkeys { get; }

    public IReadOnlyList<HotkeyRegistration> RegisteredHotkeys { get; }

    public IReadOnlyList<HotkeyRegistration> FailedHotkeys { get; }

    public bool AnyRegistered => RegisteredHotkeys.Count > 0;

    public bool AllRegistered => Hotkeys.Count > 0 && FailedHotkeys.Count == 0;
}

[Flags]
internal enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

internal static class KeyCodes
{
    public const uint G = 0x47;
    public const uint Space = 0x20;
}

internal interface IHotkeyRegistrar
{
    bool Register(int id, HotkeyModifiers modifiers, uint virtualKey);

    void Unregister(int id);
}

internal interface IHotkeyMessageSource
{
    void Attach(ThreadMessageEventHandler handler);

    void Detach(ThreadMessageEventHandler handler);
}

internal sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    public bool Register(int id, HotkeyModifiers modifiers, uint virtualKey)
    {
        return NativeMethods.RegisterHotKey(IntPtr.Zero, id, (uint)modifiers, virtualKey);
    }

    public void Unregister(int id)
    {
        if (!NativeMethods.UnregisterHotKey(IntPtr.Zero, id))
        {
            Trace.TraceWarning("Failed to unregister global hotkey id {0}.", id);
        }
    }
}

internal sealed class ComponentDispatcherHotkeyMessageSource : IHotkeyMessageSource
{
    public void Attach(ThreadMessageEventHandler handler)
    {
        ComponentDispatcher.ThreadPreprocessMessage += handler;
    }

    public void Detach(ThreadMessageEventHandler handler)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= handler;
    }
}
