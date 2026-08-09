using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Windows;

/// <summary>
/// Restores keyboard focus to Explorer's folder view after in-place navigation.
/// Navigate2 often leaves focus in the address band or search box; those text
/// entry controls intentionally block type-to-search so subsequent typing never
/// reopens the overlay.
/// </summary>
public static class ExplorerFolderViewFocus
{
    public static bool TryFocusFolderView(IntPtr explorerWindow)
    {
        if (explorerWindow == IntPtr.Zero || !NativeMethods.IsWindow(explorerWindow))
        {
            return false;
        }

        var candidates = EnumerateDescendants(explorerWindow);
        var target = ChooseFocusTarget(candidates);
        if (target == IntPtr.Zero)
        {
            return false;
        }

        return TrySetForegroundAndFocus(explorerWindow, target);
    }

    /// <summary>
    /// Picks the best non-text folder-view HWND from a flattened descendant list.
    /// Prefer the content under SHELLDLL_DefView (list/details), then known item
    /// view classes. Never return address-band or search edit controls.
    /// </summary>
    internal static IntPtr ChooseFocusTarget(IReadOnlyList<WindowClassNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return IntPtr.Zero;
        }

        var classByHandle = new Dictionary<IntPtr, string>(nodes.Count);
        foreach (var node in nodes)
        {
            classByHandle[node.Handle] = node.ClassName;
        }

        IntPtr best = IntPtr.Zero;
        var bestScore = 0;
        foreach (var node in nodes)
        {
            if (GlobalTextInputService.IsTextEntryControlClass(node.ClassName))
            {
                continue;
            }

            classByHandle.TryGetValue(node.ParentHandle, out var parentClassName);
            var score = ScoreFolderViewCandidate(node.ClassName, parentClassName);
            if (score > bestScore)
            {
                bestScore = score;
                best = node.Handle;
            }
        }

        return bestScore > 0 ? best : IntPtr.Zero;
    }

    internal static int ScoreFolderViewCandidate(string className, string? parentClassName)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return 0;
        }

        var underDefView = string.Equals(parentClassName, "SHELLDLL_DefView", StringComparison.Ordinal);
        if (underDefView &&
            (string.Equals(className, "DirectUIHWND", StringComparison.Ordinal) ||
                string.Equals(className, "SysListView32", StringComparison.Ordinal)))
        {
            return 100;
        }

        if (string.Equals(className, "UIItemsView", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "SysListView32", StringComparison.Ordinal))
        {
            return 80;
        }

        if (string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal))
        {
            return 60;
        }

        if (underDefView)
        {
            return 40;
        }

        // Generic content host under the shell view stack — usable but weak.
        if (string.Equals(className, "DirectUIHWND", StringComparison.Ordinal) &&
            (string.Equals(parentClassName, "CtrlNotifySink", StringComparison.Ordinal) ||
                string.Equals(parentClassName, "DUIViewWndClassName", StringComparison.Ordinal)))
        {
            return 30;
        }

        return 0;
    }

    private static List<WindowClassNode> EnumerateDescendants(IntPtr root)
    {
        var results = new List<WindowClassNode>(64);
        var queue = new Queue<IntPtr>();
        queue.Enqueue(root);
        while (queue.Count > 0 && results.Count < 512)
        {
            var parent = queue.Dequeue();
            NativeMethods.EnumChildWindows(
                parent,
                (child, _) =>
                {
                    results.Add(new WindowClassNode(child, GetClassName(child), parent));
                    queue.Enqueue(child);
                    return true;
                },
                IntPtr.Zero);
        }

        return results;
    }

    private static bool TrySetForegroundAndFocus(IntPtr explorerWindow, IntPtr focusTarget)
    {
        try
        {
            var foregroundThreadId = NativeMethods.GetWindowThreadProcessId(
                NativeMethods.GetForegroundWindow(),
                out _);
            var explorerThreadId = NativeMethods.GetWindowThreadProcessId(explorerWindow, out _);
            var attached = false;
            if (foregroundThreadId != 0 &&
                explorerThreadId != 0 &&
                foregroundThreadId != explorerThreadId)
            {
                attached = NativeMethods.AttachThreadInput(foregroundThreadId, explorerThreadId, true);
            }

            try
            {
                _ = NativeMethods.SetForegroundWindow(explorerWindow);
                return NativeMethods.SetFocus(focusTarget) != IntPtr.Zero
                    || NativeMethods.GetForegroundWindow() == explorerWindow;
            }
            finally
            {
                if (attached)
                {
                    _ = NativeMethods.AttachThreadInput(foregroundThreadId, explorerThreadId, false);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            return false;
        }
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new char[64];
        var length = NativeMethods.GetClassName(window, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    internal readonly record struct WindowClassNode(IntPtr Handle, string ClassName, IntPtr ParentHandle);
}
