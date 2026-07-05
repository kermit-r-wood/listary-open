namespace ListaryOpen.Infrastructure.Windows;

public sealed record QuickSwitchFolderCandidate(
    string FolderPath,
    string SourceName,
    IntPtr WindowHandle,
    bool IsForeground);

public interface IQuickSwitchWindowProvider
{
    IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates();
}

public interface IRefreshableQuickSwitchWindowProvider : IQuickSwitchWindowProvider
{
    void Refresh();
}
