using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal static class CabinetPreviewReader
{
    private const uint CabinetInfoNotification = 0x10;
    private const uint FileInCabinetNotification = 0x11;
    private const uint NeedNewCabinetNotification = 0x12;
    private const uint FileOperationAbort = 0;
    private const uint FileOperationSkip = 2;

    internal static string Read(string path, CancellationToken cancellationToken)
    {
        var state = new CabinetState(cancellationToken);
        var handle = GCHandle.Alloc(state);
        try
        {
            CabinetCallback callback = OnCabinetNotification;
            var succeeded = SetupIterateCabinet(path, 0, callback, GCHandle.ToIntPtr(handle));
            GC.KeepAlive(callback);
            cancellationToken.ThrowIfCancellationRequested();
            if (!succeeded && !state.Multipart)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidDataException(
                    error == 0 ? "The CAB file is invalid." : new Win32Exception(error).Message);
            }

            return state.BuildSummary();
        }
        finally
        {
            handle.Free();
        }
    }

    private static uint OnCabinetNotification(
        IntPtr context,
        uint notification,
        UIntPtr parameter1,
        UIntPtr parameter2)
    {
        var state = (CabinetState?)GCHandle.FromIntPtr(context).Target;
        if (state is null)
        {
            return FileOperationAbort;
        }

        if (state.CancellationToken.IsCancellationRequested)
        {
            state.Cancelled = true;
            return FileOperationAbort;
        }

        if (notification == NeedNewCabinetNotification)
        {
            state.Multipart = true;
            return FileOperationAbort;
        }

        if (notification != FileInCabinetNotification)
        {
            return notification == CabinetInfoNotification ? 0 : FileOperationSkip;
        }

        var info = Marshal.PtrToStructure<FileInCabinetInfo>((IntPtr)parameter1);
        state.Add(info.NameInCabinet, info.FileSize);
        return FileOperationSkip;
    }

    private sealed class CabinetState(CancellationToken cancellationToken)
    {
        private const int MaximumEntries = 200;
        private const int MaximumOutputCharacters = 120_000;
        private readonly StringBuilder _entries = new();

        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal bool Cancelled { get; set; }
        internal bool Multipart { get; set; }
        internal int Count { get; private set; }
        internal bool Truncated { get; private set; }

        internal void Add(IntPtr namePointer, uint size)
        {
            if (Count >= MaximumEntries || _entries.Length >= MaximumOutputCharacters)
            {
                Truncated = true;
                return;
            }

            var name = Marshal.PtrToStringUni(namePointer) ?? "(unnamed)";
            if (name.Length > 512)
            {
                name = name[..509] + "…";
            }

            Count++;
            _entries.Append(FilePreviewPane.FormatSize(size).PadLeft(11))
                .Append("  ")
                .AppendLine(name);
        }

        internal string BuildSummary()
        {
            if (Cancelled)
            {
                CancellationToken.ThrowIfCancellationRequested();
            }

            var builder = new StringBuilder()
                .AppendLine("CAB archive")
                .Append("Entries shown: ").Append(Count).AppendLine(Truncated ? "+" : string.Empty)
                .Append("Multipart cabinet: ").AppendLine(Multipart ? "unsupported" : "no")
                .AppendLine()
                .Append(_entries);
            if (Truncated)
            {
                builder.AppendLine("… archive listing truncated");
            }

            return builder.ToString();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInCabinetInfo
    {
        internal IntPtr NameInCabinet;
        internal uint FileSize;
        internal uint Win32Error;
        internal ushort DosDate;
        internal ushort DosTime;
        internal ushort DosAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string FullTargetName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CabinetCallback(
        IntPtr context,
        uint notification,
        UIntPtr parameter1,
        UIntPtr parameter2);

    [DllImport("setupapi.dll", EntryPoint = "SetupIterateCabinetW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupIterateCabinet(
        string cabinetFile,
        uint reserved,
        CabinetCallback callback,
        IntPtr context);
}

internal static class IsoPreviewReader
{
    private const int SectorSize = 2048;
    private const int MaximumDescriptors = 256;
    private const int MaximumEntries = 200;
    private const int MaximumDepth = 16;
    private const int MaximumDirectories = 128;
    private const long MaximumScannedBytes = 8 * 1024 * 1024;

    internal static string Read(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var descriptor = FindVolumeDescriptor(stream, cancellationToken, out var joliet);
        var root = ParseRecord(descriptor.AsSpan(156), joliet, stream.Length);
        if (root is null || !root.Value.IsDirectory)
        {
            throw new InvalidDataException("The ISO root directory record is invalid.");
        }

        var builder = new StringBuilder()
            .AppendLine(joliet ? "ISO 9660 / Joliet image" : "ISO 9660 image")
            .AppendLine();
        var queue = new Queue<(IsoRecord Record, string Prefix, int Depth)>();
        queue.Enqueue((root.Value, string.Empty, 0));
        var visited = new HashSet<uint>();
        var entryCount = 0;
        var scannedBytes = 0L;
        var truncated = false;
        while (queue.Count > 0 && entryCount < MaximumEntries &&
            visited.Count < MaximumDirectories && scannedBytes < MaximumScannedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, prefix, depth) = queue.Dequeue();
            if (!visited.Add(directory.Extent) || depth > MaximumDepth)
            {
                continue;
            }

            var directoryBytes = ReadExtent(stream, directory, ref scannedBytes);
            var offset = 0;
            while (offset < directoryBytes.Length && entryCount < MaximumEntries)
            {
                var recordLength = directoryBytes[offset];
                if (recordLength == 0)
                {
                    offset = ((offset / SectorSize) + 1) * SectorSize;
                    continue;
                }

                if (offset + recordLength > directoryBytes.Length)
                {
                    truncated = true;
                    break;
                }

                var record = ParseRecord(directoryBytes.AsSpan(offset, recordLength), joliet, stream.Length);
                offset += recordLength;
                if (record is null || record.Value.Name is "\0" or "\u0001")
                {
                    continue;
                }

                entryCount++;
                var displayName = record.Value.Name.Length <= 512
                    ? record.Value.Name
                    : record.Value.Name[..509] + "…";
                var fullName = prefix + displayName;
                builder.Append(record.Value.IsDirectory ? "      <DIR>" : FilePreviewPane.FormatSize(record.Value.Length).PadLeft(11))
                    .Append("  ")
                    .AppendLine(fullName);
                if (record.Value.IsDirectory && depth < MaximumDepth)
                {
                    queue.Enqueue((record.Value, fullName + "/", depth + 1));
                }
            }
        }

        truncated |= queue.Count > 0 || entryCount >= MaximumEntries ||
            visited.Count >= MaximumDirectories || scannedBytes >= MaximumScannedBytes;
        builder.Insert(
            builder.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length,
            $"Entries shown: {entryCount}{(truncated ? "+" : string.Empty)}{Environment.NewLine}");
        if (truncated)
        {
            builder.AppendLine("… image listing truncated");
        }

        return builder.ToString();
    }

    private static byte[] FindVolumeDescriptor(
        FileStream stream,
        CancellationToken cancellationToken,
        out bool joliet)
    {
        byte[]? primary = null;
        byte[]? supplementary = null;
        var buffer = new byte[SectorSize];
        for (var index = 0; index < MaximumDescriptors; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = checked((long)(16 + index) * SectorSize);
            if (offset + SectorSize > stream.Length)
            {
                break;
            }

            stream.Position = offset;
            stream.ReadExactly(buffer);
            if (!buffer.AsSpan(1, 5).SequenceEqual("CD001"u8) || buffer[6] != 1)
            {
                continue;
            }

            if (buffer[0] == 1)
            {
                primary = buffer.ToArray();
            }
            else if (buffer[0] == 2 &&
                buffer[88] == (byte)'%' && buffer[89] == (byte)'/' &&
                buffer[90] is 0x40 or 0x43 or 0x45)
            {
                supplementary = buffer.ToArray();
            }
            else if (buffer[0] == 255)
            {
                break;
            }
        }

        joliet = supplementary is not null;
        return supplementary ?? primary ??
            throw new InvalidDataException("No ISO 9660 volume descriptor was found.");
    }

    private static byte[] ReadExtent(FileStream stream, IsoRecord record, ref long scannedBytes)
    {
        var offset = checked((long)record.Extent * SectorSize);
        var length = checked((int)Math.Min(record.Length, MaximumScannedBytes - scannedBytes));
        if (length < 0 || offset < 0 || offset + length > stream.Length)
        {
            throw new InvalidDataException("An ISO directory extent is outside the image.");
        }

        var bytes = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        scannedBytes += length;
        return bytes;
    }

    private static IsoRecord? ParseRecord(ReadOnlySpan<byte> bytes, bool joliet, long fileLength)
    {
        if (bytes.Length < 34 || bytes[0] < 34 || bytes[0] > bytes.Length)
        {
            return null;
        }

        var extent = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(2, 4));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(10, 4));
        var nameLength = bytes[32];
        if (33 + nameLength > bytes[0])
        {
            return null;
        }

        var offset = checked((long)extent * SectorSize);
        if (offset < 0 || offset + length > fileLength)
        {
            throw new InvalidDataException("An ISO record points outside the image.");
        }

        var nameBytes = bytes.Slice(33, nameLength);
        string name;
        if (nameLength == 1 && nameBytes[0] <= 1)
        {
            name = ((char)nameBytes[0]).ToString();
        }
        else
        {
            name = joliet
                ? Encoding.BigEndianUnicode.GetString(nameBytes)
                : Encoding.ASCII.GetString(nameBytes);
            var version = name.LastIndexOf(';');
            if (version > 0)
            {
                name = name[..version];
            }
        }

        return new IsoRecord(extent, length, (bytes[25] & 0x02) != 0, name.TrimEnd('.'));
    }

    private readonly record struct IsoRecord(uint Extent, uint Length, bool IsDirectory, string Name);
}
