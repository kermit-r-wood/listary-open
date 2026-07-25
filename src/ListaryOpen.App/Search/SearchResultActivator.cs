using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ListaryOpen.App.Search;

internal interface ISearchResultActivationService
{
    Task OpenAsync(string path);

    Task RevealAsync(string path, bool isDirectory);

    void CopyPath(string path);

    /// <summary>
    /// Moves a file or directory to the Recycle Bin (FO_DELETE + FOF_ALLOWUNDO).
    /// </summary>
    Task DeleteAsync(string path, bool isDirectory, bool allowUndo = true);
}

internal sealed class SearchResultActivator : ISearchResultActivationService
{
    private const int FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofSilent = 0x0004;
    private const ushort FofWantNukeWarning = 0x4000;

    public Task OpenAsync(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    public Task RevealAsync(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return OpenAsync(path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\""
        });

        return Task.CompletedTask;
    }

    public void CopyPath(string path)
    {
        Clipboard.SetText(path);
    }

    public Task DeleteAsync(string path, bool isDirectory, bool allowUndo = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Double-null-terminated path list required by SHFileOperation.
        var pathList = path + "\0\0";
        var flags = (ushort)(FofNoConfirmation | FofSilent | FofWantNukeWarning);
        if (allowUndo)
        {
            flags |= FofAllowUndo;
        }

        var operation = new ShFileOpStruct
        {
            Hwnd = IntPtr.Zero,
            Func = FoDelete,
            From = pathList,
            To = null,
            Flags = flags,
            NameMappings = IntPtr.Zero,
            ProgressTitle = null
        };

        var result = SHFileOperationW(ref operation);
        if (result != 0 || operation.FAnyOperationsAborted)
        {
            throw new IOException(
                $"Could not delete '{path}' (shell result {result}, aborted={operation.FAnyOperationsAborted}).");
        }

        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public int Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool FAnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
