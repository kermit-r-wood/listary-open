using System.Diagnostics;
using System.Windows.Interop;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class HotkeyService : IDisposable
{
    internal const int HotkeyMessageId = 0x0312;

    private readonly IHotkeyMessageSource _messageSource;
    private readonly IHotkeyRegistrar _registrar;
    private readonly Dictionary<int, string> _registeredHotkeys = new();
    private bool _disposed;
    private bool _messageHookAttached;
    private bool _registrationAttempted;
    private HotkeyRegistrationResult? _registrationResult;
    private string? _configuredSearchHotkey;
    private string? _configuredDialogHotkey;

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
        => Register("Ctrl+Space", "Ctrl+G");

    public HotkeyRegistrationResult Register(string searchHotkey, string dialogHotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registrationAttempted &&
            string.Equals(_configuredSearchHotkey, searchHotkey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_configuredDialogHotkey, dialogHotkey, StringComparison.OrdinalIgnoreCase))
        {
            return _registrationResult ?? HotkeyRegistrationResult.Empty;
        }

        var searchValid = HotkeyGesture.TryParse(searchHotkey, out var searchGesture);
        var dialogValid = HotkeyGesture.TryParse(dialogHotkey, out var dialogGesture);
        if (!searchValid || !dialogValid)
        {
            return new HotkeyRegistrationResult(
            [
                new HotkeyRegistration(searchHotkey, searchValid),
                new HotkeyRegistration(dialogHotkey, dialogValid)
            ]);
        }

        UnregisterCurrent();
        _registrationAttempted = true;
        _configuredSearchHotkey = searchHotkey;
        _configuredDialogHotkey = dialogHotkey;

        var hotkeys = new[]
        {
            new HotkeyDefinition(1, "Search", searchHotkey, searchGesture!.Modifiers, searchGesture.VirtualKey),
            new HotkeyDefinition(2, "Dialog", dialogHotkey, dialogGesture!.Modifiers, dialogGesture.VirtualKey)
        };
        var registrations = new List<HotkeyRegistration>(hotkeys.Length);
        foreach (var hotkey in hotkeys)
        {
            var registered = _registrar.Register(hotkey.Id, hotkey.Modifiers, hotkey.VirtualKey);
            registrations.Add(new HotkeyRegistration(hotkey.DisplayName, registered));

            if (!registered)
            {
                Trace.TraceWarning("Failed to register global hotkey '{0}'. Another application may already own it.", hotkey.DisplayName);
                continue;
            }

            _registeredHotkeys.Add(hotkey.Id, hotkey.SemanticName);
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

        UnregisterCurrent();
        _disposed = true;
    }

    private void UnregisterCurrent()
    {
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

    private sealed record HotkeyDefinition(
        int Id,
        string SemanticName,
        string DisplayName,
        HotkeyModifiers Modifiers,
        uint VirtualKey);
}

internal sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.NoRepeat;
        uint virtualKey = 0;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= HotkeyModifiers.Control; continue;
                case "ALT": modifiers |= HotkeyModifiers.Alt; continue;
                case "SHIFT": modifiers |= HotkeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS": modifiers |= HotkeyModifiers.Windows; continue;
                case "SPACE": virtualKey = 0x20; continue;
            }

            if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
            {
                virtualKey = part[0];
                continue;
            }

            if (part.Length is 2 or 3 && part[0] == 'F' &&
                int.TryParse(part[1..], out var functionKey) && functionKey is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + functionKey - 1);
                continue;
            }

            return false;
        }

        var hasModifier = (modifiers & ~HotkeyModifiers.NoRepeat) != 0;
        if (!hasModifier || virtualKey == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }
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
