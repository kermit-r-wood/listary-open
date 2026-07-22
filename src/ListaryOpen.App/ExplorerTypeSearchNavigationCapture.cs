using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App;

/// <summary>
/// Publishes the Explorer overlay session as one immutable reference so the
/// low-level hook never observes a window handle without its active state.
/// </summary>
internal sealed class ExplorerTypeSearchNavigationCapture
{
    private Session? _activeSession;

    public Session Activate(IntPtr explorerWindow)
    {
        if (explorerWindow == IntPtr.Zero)
        {
            throw new ArgumentException("An active Explorer window is required.", nameof(explorerWindow));
        }

        var session = new Session(explorerWindow);
        Interlocked.Exchange(ref _activeSession, session);
        return session;
    }

    public void Deactivate()
    {
        Interlocked.Exchange(ref _activeSession, null);
    }

    public Session? TryCapture(GlobalNavigationInputEventArgs input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var session = Volatile.Read(ref _activeSession);
        if (session is null ||
            input.ForegroundWindow != session.ExplorerWindow ||
            GlobalTextInputService.IsTextEntryControlClass(input.FocusedControlClass) ||
            !ReferenceEquals(Volatile.Read(ref _activeSession), session))
        {
            return null;
        }

        input.Handled = true;
        return session;
    }

    public Session? TryCapture(GlobalEditCommandInputEventArgs input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var session = TryCapture(input.ForegroundWindow, input.FocusedControlClass);
        if (session is not null)
        {
            input.Handled = true;
        }

        return session;
    }

    public Session? TryCapture(GlobalResultShortcutInputEventArgs input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var session = TryCapture(input.ForegroundWindow, input.FocusedControlClass);
        if (session is not null)
        {
            input.Handled = true;
        }

        return session;
    }

    private Session? TryCapture(IntPtr foregroundWindow, string focusedControlClass)
    {
        var session = Volatile.Read(ref _activeSession);
        return session is null ||
            foregroundWindow != session.ExplorerWindow ||
            GlobalTextInputService.IsTextEntryControlClass(focusedControlClass) ||
            !ReferenceEquals(Volatile.Read(ref _activeSession), session)
                ? null
                : session;
    }

    public bool IsCurrent(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ReferenceEquals(Volatile.Read(ref _activeSession), session);
    }

    internal sealed class Session
    {
        public Session(IntPtr explorerWindow)
        {
            ExplorerWindow = explorerWindow;
        }

        public IntPtr ExplorerWindow { get; }
    }
}
