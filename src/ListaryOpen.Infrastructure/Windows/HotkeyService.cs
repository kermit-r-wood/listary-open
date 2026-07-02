namespace ListaryOpen.Infrastructure.Windows;

public sealed class HotkeyService : IDisposable
{
    public event EventHandler<string>? HotkeyPressed;

    public void RegisterDefaults()
    {
    }

    public void RaiseForTests(string name)
    {
        HotkeyPressed?.Invoke(this, name);
    }

    public void Dispose()
    {
    }
}
