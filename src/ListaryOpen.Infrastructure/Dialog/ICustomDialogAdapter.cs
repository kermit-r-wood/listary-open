namespace ListaryOpen.Infrastructure.Dialog;

public interface ICustomDialogAdapter
{
    string Name { get; }

    bool CanHandleActiveWindow();

    Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken);
}

internal sealed record DialogWindowSnapshot(
    IntPtr WindowHandle,
    string ProcessName,
    string ClassName,
    string Title);
