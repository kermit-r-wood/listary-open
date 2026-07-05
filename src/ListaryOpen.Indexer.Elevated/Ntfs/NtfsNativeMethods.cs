using System.Runtime.InteropServices;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal static class NtfsNativeMethods
{
    internal const uint FsctlEnumUsnData = 0x000900b3;
    internal const uint FsctlQueryUsnJournal = 0x000900f4;

    internal const uint GenericRead = 0x80000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const int ErrorHandleEof = 38;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out UsnJournalDataV0 lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref MftEnumDataV0 lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        IntPtr hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;

        public long LowUsn;

        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsnJournalDataV0
    {
        public ulong UsnJournalId;

        public long FirstUsn;

        public long NextUsn;

        public long LowestValidUsn;

        public long MaxUsn;

        public ulong MaximumSize;

        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;

        public FileTime CreationTime;

        public FileTime LastAccessTime;

        public FileTime LastWriteTime;

        public uint VolumeSerialNumber;

        public uint FileSizeHigh;

        public uint FileSizeLow;

        public uint NumberOfLinks;

        public uint FileIndexHigh;

        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint LowDateTime;

        public uint HighDateTime;
    }
}
