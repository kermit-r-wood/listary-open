using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ListaryOpen.Infrastructure.Windows;

public sealed record TaskManagerItem(string Id, string Name, string Details, string IconPath = "");

public interface ITaskManagerAutomationService : IDisposable
{
    Task<IReadOnlyList<TaskManagerItem>> GetItemsAsync(IntPtr taskManagerWindow, CancellationToken cancellationToken);

    void QueueSelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate = false);

    void CancelPending();
}

public sealed class TaskManagerAutomationService : ITaskManagerAutomationService
{
    // Keep selection synchronization immediate. Task Manager runtime IDs are volatile,
    // and the host row should track every keyboard or pointer selection change.
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.Zero;
    private static readonly TimeSpan SelectionRetryDelay = TimeSpan.FromMilliseconds(45);
    private const int SelectionAttemptCount = 3;
    private readonly ITaskManagerAutomationProvider _provider;
    private readonly TimeSpan _debounceDelay;
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(initialState: false);
    private readonly Thread _selectionThread;
    private SelectionRequest? _pending;
    private int _generation;
    private bool _started;
    private bool _disposed;

    public TaskManagerAutomationService()
        : this(new UiAutomationTaskManagerProvider(), DefaultDebounceDelay)
    {
    }

    internal TaskManagerAutomationService(ITaskManagerAutomationProvider provider, TimeSpan debounceDelay)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        }

        _debounceDelay = debounceDelay;
        _selectionThread = new Thread(ProcessSelections)
        {
            IsBackground = true,
            Name = "ListaryOpen Task Manager selection"
        };
        _selectionThread.SetApartmentState(ApartmentState.STA);
    }

    public Task<IReadOnlyList<TaskManagerItem>> GetItemsAsync(
        IntPtr taskManagerWindow,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (taskManagerWindow == IntPtr.Zero)
        {
            return Task.FromResult<IReadOnlyList<TaskManagerItem>>(Array.Empty<TaskManagerItem>());
        }

        return Task.Run(
            () => (IReadOnlyList<TaskManagerItem>)_provider.GetItems(taskManagerWindow),
            cancellationToken);
    }

    public static bool IsNativeTextInputFocused(IntPtr taskManagerWindow)
    {
        if (taskManagerWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return false;
            }

            // Only treat real text entry as native search. Matching names/automation ids
            // that merely contain "search" false-positives on Task Manager chrome and
            // prevents our overlay from opening when focus is on the process list.
            var controlType = focused.Current.ControlType;
            return controlType == ControlType.Edit || controlType == ControlType.Document;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    public void QueueSelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        if (taskManagerWindow == IntPtr.Zero)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = new SelectionRequest(taskManagerWindow, item, activate, _generation);
            if (!_started)
            {
                _started = true;
                _selectionThread.Start();
            }
        }

        _signal.Set();
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _generation++;
            _pending = null;
        }

        _signal.Set();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
        }

        if (!_started)
        {
            _signal.Dispose();
            return;
        }

        _signal.Set();
        if (Thread.CurrentThread != _selectionThread && _selectionThread.Join(TimeSpan.FromSeconds(2)))
        {
            _signal.Dispose();
        }
    }

    private void ProcessSelections()
    {
        while (true)
        {
            _signal.WaitOne();
            if (IsDisposed())
            {
                return;
            }

            while (_signal.WaitOne(_debounceDelay))
            {
                if (IsDisposed())
                {
                    return;
                }
            }

            SelectionRequest? request;
            lock (_gate)
            {
                request = _pending;
                _pending = null;
                if (request is null || request.Generation != _generation || _disposed)
                {
                    continue;
                }
            }

            try
            {
                for (var attempt = 0; attempt < SelectionAttemptCount; attempt++)
                {
                    if (_provider.TrySelectItem(request.TaskManagerWindow, request.Item, request.Activate))
                    {
                        break;
                    }

                    if (attempt + 1 >= SelectionAttemptCount || IsSelectionSuperseded(request))
                    {
                        break;
                    }

                    Thread.Sleep(SelectionRetryDelay);
                }
            }
            catch (Exception exception) when (IsExpectedAutomationException(exception))
            {
            }
            catch (Exception exception)
            {
                Trace.TraceError("Task Manager selection synchronization failed: {0}", exception);
            }
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private bool IsSelectionSuperseded(SelectionRequest request)
    {
        lock (_gate)
        {
            return _disposed || request.Generation != _generation || _pending is not null;
        }
    }

    private static bool IsExpectedAutomationException(Exception exception) => exception is
        ArgumentException or
        ElementNotAvailableException or
        InvalidOperationException or
        COMException or
        UnauthorizedAccessException;

    private sealed record SelectionRequest(
        IntPtr TaskManagerWindow,
        TaskManagerItem Item,
        bool Activate,
        int Generation);
}

internal interface ITaskManagerAutomationProvider
{
    IReadOnlyList<TaskManagerItem> GetItems(IntPtr taskManagerWindow);

    bool TrySelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate);
}

internal sealed class UiAutomationTaskManagerProvider : ITaskManagerAutomationProvider
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private static readonly TimeSpan ForegroundActivationDelay = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan SelectionConfirmationTimeout = TimeSpan.FromMilliseconds(250);
    // Task Manager has used DataItem, ListItem, TreeItem, and custom WinUI rows across
    // Windows releases. Query those row-shaped controls and project their child text;
    // newer builds often leave the row's own accessible Name empty.
    private static readonly Condition CandidateCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
        new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));
    private static readonly Condition TextCondition = new PropertyCondition(
        AutomationElement.ControlTypeProperty,
        ControlType.Text);

    public IReadOnlyList<TaskManagerItem> GetItems(IntPtr taskManagerWindow)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskManagerWindow);
            var processIconPaths = CreateProcessIconPathMap();
            var items = EnumerateCandidates(root)
                .Select(element => CreateItem(element, processIconPaths))
                .Where(item => item is not null)
                .Cast<TaskManagerItem>()
                .DistinctBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            return DeduplicateItems(items);
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return Array.Empty<TaskManagerItem>();
        }
    }

    public bool TrySelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskManagerWindow);
            var element = FindBestMatch(root, item);
            if (element is null)
            {
                return false;
            }

            if (activate)
            {
                _ = SetForegroundWindow(taskManagerWindow);
                Thread.Sleep(ForegroundActivationDelay);
            }

            if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollPatternObject) &&
                scrollPatternObject is ScrollItemPattern scrollPattern)
            {
                scrollPattern.ScrollIntoView();
            }

            var selected = TrySelectWithPattern(element);

            if (activate)
            {
                if (!selected)
                {
                    selected = TryClickElement(element);
                }

                if (!selected)
                {
                    try
                    {
                        element.SetFocus();
                    }
                    catch (Exception exception) when (IsExpectedAutomationException(exception))
                    {
                    }
                }
            }

            return selected;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateCandidates(AutomationElement root)
    {
        var elements = root.FindAll(TreeScope.Descendants, CandidateCondition);
        for (var index = 0; index < elements.Count; index++)
        {
            yield return elements[index];
        }
    }

    private static AutomationElement? FindBestMatch(AutomationElement root, TaskManagerItem item)
    {
        return EnumerateCandidates(root)
            .Select(element => new
            {
                Element = element,
                MatchScore = GetCandidateMatchScore(element, item),
                SelectionScore = GetSelectionScore(element)
            })
            .Where(candidate => candidate.MatchScore > 0)
            .OrderByDescending(candidate => candidate.MatchScore)
            .ThenByDescending(candidate => candidate.SelectionScore)
            .Select(candidate => candidate.Element)
            .FirstOrDefault();
    }

    private static int GetCandidateMatchScore(AutomationElement element, TaskManagerItem item)
    {
        try
        {
            return GetCandidateMatchScore(
                GetElementId(element),
                element.Current.Name,
                ReadDescendantText(element),
                item);
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return 0;
        }
    }

    internal static int GetCandidateMatchScore(
        string candidateId,
        string? candidateName,
        IEnumerable<string?> descendantText,
        TaskManagerItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(descendantText);
        ArgumentNullException.ThrowIfNull(item);
        if (string.Equals(candidateId, item.Id, StringComparison.Ordinal))
        {
            return 3;
        }

        var projected = CreateProjectedItem(candidateId, candidateName, descendantText);
        if (projected is null)
        {
            return 0;
        }

        if (string.Equals(projected.Name, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return string.Equals(
            GetProcessIdentity(projected.Name),
            GetProcessIdentity(item.Name),
            StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static int GetSelectionScore(AutomationElement element)
    {
        try
        {
            if (element.GetCurrentPropertyValue(
                    AutomationElement.IsSelectionItemPatternAvailableProperty) is true)
            {
                return 2;
            }

            return element.Current.IsKeyboardFocusable ? 1 : 0;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return 0;
        }
    }

    private static bool TrySelectWithPattern(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var patternObject) ||
                patternObject is not SelectionItemPattern pattern)
            {
                return false;
            }

            pattern.Select();
            var deadline = DateTime.UtcNow + SelectionConfirmationTimeout;
            do
            {
                if (pattern.Current.IsSelected)
                {
                    return true;
                }

                Thread.Sleep(15);
            }
            while (DateTime.UtcNow < deadline);

            return pattern.Current.IsSelected;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static bool TryClickElement(AutomationElement element)
    {
        try
        {
            if (!element.TryGetClickablePoint(out var point))
            {
                return false;
            }

            var restoreCursor = GetCursorPos(out var originalCursor);
            if (!SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)))
            {
                return false;
            }

            try
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                return true;
            }
            finally
            {
                if (restoreCursor)
                {
                    _ = SetCursorPos(originalCursor.X, originalCursor.Y);
                }
            }
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static TaskManagerItem? CreateItem(
        AutomationElement element,
        IReadOnlyDictionary<string, string> processIconPaths)
    {
        try
        {
            var controlType = element.Current.ControlType;
            var childText = ReadDescendantText(element);
            if (controlType == ControlType.Custom && childText.Count < 2)
            {
                return null;
            }

            var item = CreateProjectedItem(
                GetElementId(element),
                element.Current.Name,
                childText);
            return item is null
                ? null
                : item with { IconPath = ResolveIconPath(item.Name, childText, processIconPaths) };
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return null;
        }
    }

    internal static TaskManagerItem? CreateProjectedItem(
        string id,
        string? rowName,
        IEnumerable<string?> descendantText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(descendantText);

        var childValues = descendantText
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trimmedRowName = rowName?.Trim() ?? string.Empty;
        var name = !string.IsNullOrWhiteSpace(trimmedRowName)
            ? trimmedRowName
            : childValues.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var details = childValues
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new TaskManagerItem(id, name, string.Join(" · ", details));
    }

    internal static IReadOnlyList<TaskManagerItem> DeduplicateItems(IEnumerable<TaskManagerItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items
            .GroupBy(item => GetProcessIdentity(item.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.IconPath))
                .First())
            .ToArray();
    }

    internal static string ResolveIconPath(
        string processDisplayName,
        IEnumerable<string?> rowText,
        IReadOnlyDictionary<string, string> processIconPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDisplayName);
        ArgumentNullException.ThrowIfNull(rowText);
        ArgumentNullException.ThrowIfNull(processIconPaths);

        foreach (var value in new[] { processDisplayName }.Concat(rowText))
        {
            foreach (var candidate in GetProcessLookupCandidates(value))
            {
                if (processIconPaths.TryGetValue(candidate, out var path))
                {
                    return path;
                }
            }
        }

        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> CreateProcessIconPathMap()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    AddProcessIconPath(paths, process.ProcessName, path);
                    AddProcessIconPath(paths, Path.GetFileNameWithoutExtension(path), path);
                    var version = FileVersionInfo.GetVersionInfo(path);
                    AddProcessIconPath(paths, version.FileDescription, path);
                    AddProcessIconPath(paths, version.ProductName, path);
                }
                catch (Exception exception) when (exception is Win32Exception
                                                   or InvalidOperationException
                                                   or NotSupportedException
                                                   or UnauthorizedAccessException)
                {
                }
            }
        }

        return paths;
    }

    private static void AddProcessIconPath(
        IDictionary<string, string> paths,
        string? processName,
        string path)
    {
        foreach (var candidate in GetProcessLookupCandidates(processName))
        {
            paths.TryAdd(candidate, path);
        }
    }

    private static IEnumerable<string> GetProcessLookupCandidates(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var normalized = NormalizeProcessName(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        var colon = normalized.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < normalized.Length)
        {
            var suffix = NormalizeProcessName(normalized[(colon + 1)..]);
            if (!string.IsNullOrWhiteSpace(suffix) &&
                !string.Equals(suffix, normalized, StringComparison.OrdinalIgnoreCase))
            {
                yield return suffix;
            }
        }
    }

    private static string GetProcessIdentity(string processDisplayName)
    {
        var candidates = GetProcessLookupCandidates(processDisplayName).ToArray();
        return candidates.LastOrDefault() ?? processDisplayName.Trim();
    }

    private static string NormalizeProcessName(string value)
    {
        var normalized = value.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4].TrimEnd();
        }

        var countStart = normalized.LastIndexOf(" (", StringComparison.Ordinal);
        if (countStart >= 0 && normalized.EndsWith(')') &&
            int.TryParse(normalized.AsSpan(countStart + 2, normalized.Length - countStart - 3), out _))
        {
            normalized = normalized[..countStart].TrimEnd();
        }

        return normalized;
    }

    private static IReadOnlyList<string> ReadDescendantText(AutomationElement element)
    {
        var textElements = element.FindAll(TreeScope.Descendants, TextCondition);
        var values = new List<string>(textElements.Count);
        for (var index = 0; index < textElements.Count; index++)
        {
            try
            {
                var value = textElements[index].Current.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
            catch (Exception exception) when (IsExpectedAutomationException(exception))
            {
            }
        }

        return values;
    }

    private static string GetElementId(AutomationElement element)
    {
        var runtimeId = element.GetRuntimeId();
        return runtimeId is { Length: > 0 }
            ? string.Join(".", runtimeId)
            : $"{element.Current.AutomationId}|{element.Current.Name}";
    }

    private static bool IsExpectedAutomationException(Exception exception) => exception is
        ArgumentException or
        ElementNotAvailableException or
        InvalidOperationException or
        COMException or
        UnauthorizedAccessException;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
