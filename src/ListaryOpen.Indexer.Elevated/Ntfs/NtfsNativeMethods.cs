using System.Runtime.InteropServices;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal static class NtfsNativeMethods
{
    internal const uint FsctlEnumUsnData = 0x000900b3;
    internal const uint FsctlReadUsnJournal = 0x000900bb;
    internal const uint FsctlQueryUsnJournal = 0x000900f4;
    internal const uint FsctlGetNtfsVolumeData = 0x00090064;
    internal const uint FsctlGetNtfsFileRecord = 0x00090068;

    internal const uint GenericRead = 0x80000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagNoBuffering = 0x20000000;
    internal const int ErrorHandleEof = 38;
    internal const uint FileBegin = 0;

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

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlQueryUsnJournal(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out UsnJournalDataV0 lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControlGetNtfsVolumeData(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out NtfsVolumeDataBuffer lpOutBuffer,
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
    internal static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref ReadUsnJournalDataV0 lpInBuffer,
        int nInBufferSize,
        [Out] byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref NtfsFileRecordInputBuffer lpInBuffer,
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(
        IntPtr hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

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
    internal struct ReadUsnJournalDataV0
    {
        public long StartUsn;

        public uint ReasonMask;

        public uint ReturnOnlyOnClose;

        public ulong Timeout;

        public ulong BytesToWaitFor;

        public ulong UsnJournalId;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct NtfsVolumeDataBuffer
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NtfsFileRecordInputBuffer
    {
        public long FileReferenceNumber;
    }
}
