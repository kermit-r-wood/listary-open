using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads bounded, format-specific structure from binary files that otherwise
/// would only receive a signature/size preview. No bytecode is loaded, disks
/// are never mounted, and dump or module payloads are never symbolized.
/// </summary>
internal sealed class BinaryStructurePreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 256L * 1024 * 1024 * 1024;
    private const int MaximumProbeBytes = 8 * 1024 * 1024;
    private const int MaximumDirectoryBytes = 512 * 1024;
    private const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        IsSupportedExtension(context.Extension) &&
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Opaque);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < 4 || context.SizeBytes > MaximumInputBytes)
        {
            return null;
        }

        using var stream = new FileStream(
            context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        cancellationToken.ThrowIfCancellationRequested();
        return context.Extension.ToLowerInvariant() switch
        {
            ".class" => ReadClass(context, stream, cancellationToken),
            ".wasm" => ReadWasm(context, stream, cancellationToken),
            ".dmp" or ".mdmp" => ReadMiniDump(context, stream, cancellationToken),
            ".vdi" => ReadVdi(context, stream, cancellationToken),
            ".vmdk" => ReadVmdk(context, stream, cancellationToken),
            ".qcow" or ".qcow2" => ReadQcow(context, stream, cancellationToken),
            ".dmg" => ReadDmg(context, stream, cancellationToken),
            _ => null
        };
    }

    private static PreviewContent? ReadClass(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = ReadRange(stream, 0, (int)Math.Min(stream.Length, MaximumProbeBytes));
        if (bytes is null || bytes.Length < 10 || !bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }))
        {
            return null;
        }

        try
        {
            var reader = new ClassReader(bytes, cancellationToken);
            var parsed = reader.Read();
            var builder = new StringBuilder("Java class structure")
                .AppendLine().AppendLine()
                .Append("Class version: ").Append(parsed.Major).Append('.').AppendLine(parsed.Minor.ToString(CultureInfo.InvariantCulture))
                .Append("This class: ").AppendLine(parsed.ThisClass ?? "(unknown)")
                .Append("Super class: ").AppendLine(parsed.SuperClass ?? "(none)")
                .Append("Constant-pool entries: ").AppendLine(parsed.ConstantPoolEntries.ToString(CultureInfo.InvariantCulture))
                .Append("Interfaces: ").AppendLine(parsed.Interfaces.ToString(CultureInfo.InvariantCulture))
                .Append("Fields: ").AppendLine(parsed.Fields.ToString(CultureInfo.InvariantCulture))
                .Append("Methods: ").AppendLine(parsed.Methods.ToString(CultureInfo.InvariantCulture));
            if (parsed.Strings.Count > 0)
            {
                builder.AppendLine().AppendLine("Readable constants:");
                foreach (var value in parsed.Strings)
                {
                    builder.Append("- ").AppendLine(value);
                }
            }
            AppendSafety(builder, "Class bytecode was not loaded, linked, executed, or decompiled.",
                context.SizeBytes > bytes.Length);
            return PreviewContent.ForText("Java class structure", Bound(builder.ToString()));
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static PreviewContent? ReadWasm(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = ReadRange(stream, 0, (int)Math.Min(stream.Length, MaximumProbeBytes));
        if (bytes is null || bytes.Length < 8 || !bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0x61, 0x73, 0x6D }))
        {
            return null;
        }

        try
        {
            var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            var offset = 8;
            var sections = 0;
            var names = new List<string>();
            var sizes = new List<string>();
            while (offset < bytes.Length && sections < 1024)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = bytes[offset++];
                var length = ReadLeb128(bytes, ref offset);
                if (length > (ulong)(bytes.Length - offset) || length > MaximumProbeBytes)
                {
                    throw new InvalidDataException("Wasm section exceeds preview bounds.");
                }
                var section = bytes.AsSpan(offset, checked((int)length));
                if (id == 0 && section.Length > 0)
                {
                    var nameOffset = 0;
                    var nameLength = ReadLeb128(section, ref nameOffset);
                    if (nameLength <= (ulong)(section.Length - nameOffset) && nameLength <= 256)
                    {
                        var name = Encoding.UTF8.GetString(section.Slice(nameOffset, checked((int)nameLength)))
                            .ReplaceLineEndings(" ").Trim();
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                    }
                }
                sizes.Add($"section {id}: {length:N0} bytes");
                offset = checked(offset + (int)length);
                sections++;
            }

            var builder = new StringBuilder("WebAssembly module structure")
                .AppendLine().AppendLine()
                .Append("Binary version: ").AppendLine(version.ToString(CultureInfo.InvariantCulture))
                .Append("Sections scanned: ").AppendLine(sections.ToString(CultureInfo.InvariantCulture));
            if (names.Count > 0)
            {
                builder.Append("Custom section names: ").AppendLine(string.Join(", ", names.Take(32)));
            }
            foreach (var size in sizes.Take(128)) builder.Append("- ").AppendLine(size);
            AppendSafety(builder, "WebAssembly code was not instantiated, executed, or disassembled.",
                context.SizeBytes > bytes.Length || offset < bytes.Length);
            return PreviewContent.ForText("WebAssembly module structure", Bound(builder.ToString()));
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static PreviewContent? ReadMiniDump(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = ReadRange(stream, 0, 32);
        if (header is null || !header.AsSpan(0, 4).SequenceEqual("MDMP"u8))
        {
            return null;
        }

        var streamCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var directoryRva = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (streamCount > 4096 || directoryRva > stream.Length ||
            (ulong)streamCount * 12 > (ulong)stream.Length - directoryRva ||
            (ulong)streamCount * 12 > MaximumDirectoryBytes)
        {
            return PreviewContent.ForText(
                "Windows minidump structure",
                $"Windows minidump{Environment.NewLine}{Environment.NewLine}Declared streams: {streamCount:N0}{Environment.NewLine}The stream directory exceeded the bounded preview range.");
        }

        var directory = ReadRange(stream, directoryRva, checked((int)streamCount * 12));
        if (directory is null) return null;
        var valid = 0;
        var modules = 0u;
        var threads = 0u;
        var hasException = false;
        var hasSystemInfo = false;
        ushort processorArchitecture = 0;
        for (var index = 0; index < streamCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = directory.AsSpan(checked(index * 12));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var rva = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            if (rva <= stream.Length && size <= (ulong)stream.Length - rva)
            {
                valid++;
            }
            switch (type)
            {
                case 3: // ThreadListStream
                    threads = TryReadCount(stream, rva, size);
                    break;
                case 4: // ModuleListStream
                    modules = TryReadCount(stream, rva, size);
                    break;
                case 6: // ExceptionStream
                    hasException = true;
                    break;
                case 7: // SystemInfoStream
                    hasSystemInfo = true;
                    var system = ReadRange(stream, rva, (int)Math.Min(size, 2));
                    if (system is { Length: 2 }) processorArchitecture = BinaryPrimitives.ReadUInt16LittleEndian(system);
                    break;
            }
        }

        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)));
        var builder = new StringBuilder("Windows minidump structure")
            .AppendLine().AppendLine()
            .Append("Format version: ").AppendLine(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)).ToString(CultureInfo.InvariantCulture))
            .Append("Streams declared: ").AppendLine(streamCount.ToString(CultureInfo.InvariantCulture))
            .Append("Streams in bounds: ").Append(valid.ToString(CultureInfo.InvariantCulture)).Append('/').AppendLine(streamCount.ToString(CultureInfo.InvariantCulture))
            .Append("Modules: ").AppendLine(modules.ToString(CultureInfo.InvariantCulture))
            .Append("Threads: ").AppendLine(threads.ToString(CultureInfo.InvariantCulture))
            .Append("Exception stream: ").AppendLine(hasException ? "present" : "not present")
            .Append("System information: ").AppendLine(hasSystemInfo ? $"present (architecture {processorArchitecture})" : "not present")
            .Append("Dump timestamp: ").AppendLine(timestamp.ToString("u", CultureInfo.InvariantCulture));
        AppendSafety(builder, "Dump memory, module paths, symbols, and exception payloads were not opened or resolved.",
            context.SizeBytes > stream.Length);
        return PreviewContent.ForText("Windows minidump structure", Bound(builder.ToString()));
    }

    private static PreviewContent? ReadVdi(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = ReadRange(stream, 0, 0x1A0);
        if (bytes is null || bytes.Length < 0x1A0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x40)) != 0xBEDA107F)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x44));
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x48));
        var imageType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x4C));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x50));
        var description = Encoding.Unicode.GetString(bytes, 0x54, 256).TrimEnd('\0', ' ');
        var diskSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x170));
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x178));
        var allocatedBlocks = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x184));
        var builder = new StringBuilder("VirtualBox VDI structure")
            .AppendLine().AppendLine()
            .Append("Format version: ").Append(version >> 16).Append('.').AppendLine((version & 0xFFFF).ToString(CultureInfo.InvariantCulture))
            .Append("Header size: ").AppendLine(headerSize.ToString(CultureInfo.InvariantCulture))
            .Append("Image type: ").AppendLine(imageType switch { 1 => "Dynamic", 2 => "Fixed", _ => $"Unknown ({imageType})" })
            .Append("Flags: ").AppendLine($"0x{flags:X8}")
            .Append("Virtual disk size: ").AppendLine(FilePreviewPane.FormatSize(diskSize > long.MaxValue ? long.MaxValue : (long)diskSize))
            .Append("Block size: ").AppendLine(FilePreviewPane.FormatSize(blockSize))
            .Append("Allocated blocks: ").AppendLine(allocatedBlocks.ToString("N0", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(description)) builder.Append("Description: ").AppendLine(Bound(description));
        AppendSafety(builder, "The VDI image was not mounted, attached, or read for filesystem content.", context.SizeBytes > bytes.Length);
        return PreviewContent.ForText("VirtualBox VDI structure", Bound(builder.ToString()));
    }

    private static PreviewContent? ReadVmdk(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = ReadRange(stream, 0, (int)Math.Min(stream.Length, 64 * 1024));
        if (bytes is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new StringBuilder("VMware VMDK structure").AppendLine().AppendLine();
        if (bytes.Length >= 40 && bytes.AsSpan(0, 4).SequenceEqual("KDMV"u8))
        {
            var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            var capacity = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12));
            var grain = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20));
            builder.Append("Sparse version: ").AppendLine(version.ToString(CultureInfo.InvariantCulture))
                .Append("Virtual sectors: ").AppendLine(capacity.ToString("N0", CultureInfo.InvariantCulture))
                .Append("Virtual size: ").AppendLine(FilePreviewPane.FormatSize(capacity > long.MaxValue / 512 ? long.MaxValue : (long)capacity * 512))
                .Append("Grain sectors: ").AppendLine(grain.ToString("N0", CultureInfo.InvariantCulture));
            var descriptorOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24));
            var descriptorSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32));
            if (descriptorOffset <= (ulong)stream.Length && descriptorSize <= 64 * 1024 && descriptorSize <= (ulong)stream.Length - descriptorOffset)
            {
                var descriptor = ReadRange(stream, checked((long)descriptorOffset), checked((int)descriptorSize));
                AppendDescriptorLines(builder, descriptor);
            }
        }
        else
        {
            var text = Encoding.ASCII.GetString(bytes);
            if (!text.Contains("Disk DescriptorFile", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("createType=", StringComparison.OrdinalIgnoreCase)) return null;
            builder.AppendLine("Descriptor text:");
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(24))
            {
                var safe = line.Trim();
                if (safe.Length > 512) safe = safe[..512] + "…";
                builder.Append("- ").AppendLine(safe);
            }
        }
        AppendSafety(builder, "The VMDK extent and descriptor were read as metadata; no disk was mounted or opened.", context.SizeBytes > bytes.Length);
        return PreviewContent.ForText("VMware VMDK structure", Bound(builder.ToString()));
    }

    private static PreviewContent? ReadQcow(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = ReadRange(stream, 0, 72);
        if (bytes is null || bytes.Length < 32 || !bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x51, 0x46, 0x49, 0xFB })) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
        var backingOffset = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8));
        var backingSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16));
        var clusterBits = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        var virtualSize = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(24));
        var builder = new StringBuilder("QEMU QCOW structure")
            .AppendLine().AppendLine()
            .Append("Format version: ").AppendLine(version.ToString(CultureInfo.InvariantCulture))
            .Append("Cluster size: ").AppendLine(clusterBits is < 9 or > 21 ? "invalid" : FilePreviewPane.FormatSize(1L << checked((int)clusterBits)))
            .Append("Virtual disk size: ").AppendLine(FilePreviewPane.FormatSize(virtualSize > long.MaxValue ? long.MaxValue : (long)virtualSize))
            .Append("Backing file descriptor: ").AppendLine(backingSize == 0 ? "none" : $"offset {backingOffset}, {backingSize:N0} bytes");
        AppendSafety(builder, "The QCOW image and backing file were not mounted, opened, or resolved.", context.SizeBytes > bytes.Length);
        return PreviewContent.ForText("QEMU QCOW structure", Bound(builder.ToString()));
    }

    private static PreviewContent? ReadDmg(
        PreviewContext context,
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length < 512) return null;
        var footer = ReadRange(stream, stream.Length - 512, 512);
        if (footer is null || !footer.AsSpan(0, 4).SequenceEqual("koly"u8)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var version = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(4));
        var headerSize = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(8));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(12));
        var dataOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(24));
        var dataLength = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(32));
        var segmentCount = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
        var builder = new StringBuilder("Apple UDIF disk image structure")
            .AppendLine().AppendLine()
            .Append("UDIF version: ").AppendLine(version.ToString(CultureInfo.InvariantCulture))
            .Append("Header size: ").AppendLine(headerSize.ToString(CultureInfo.InvariantCulture))
            .Append("Flags: ").AppendLine($"0x{flags:X8}")
            .Append("Data fork: ").Append(dataOffset.ToString(CultureInfo.InvariantCulture)).Append(" + ")
            .AppendLine(FilePreviewPane.FormatSize(dataLength > long.MaxValue ? long.MaxValue : (long)dataLength))
            .Append("Segments: ").AppendLine(segmentCount.ToString(CultureInfo.InvariantCulture))
            .Append("Stored file size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes));
        AppendSafety(builder, "The disk image was not mounted, decompressed, or opened for filesystem content.", false);
        return PreviewContent.ForText("Apple UDIF disk image structure", Bound(builder.ToString()));
    }

    private static void AppendDescriptorLines(StringBuilder builder, byte[]? descriptor)
    {
        if (descriptor is null || descriptor.Length == 0) return;
        builder.AppendLine().AppendLine("Descriptor text:");
        var text = Encoding.ASCII.GetString(descriptor);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Where(line => line.Contains('=') || line.StartsWith("#", StringComparison.Ordinal))
                     .Take(24))
        {
            var safe = line.Trim();
            if (safe.Length > 512) safe = safe[..512] + "…";
            builder.Append("- ").AppendLine(safe);
        }
    }

    private static uint TryReadCount(Stream stream, uint rva, uint size)
    {
        if (size < 4 || rva > stream.Length || size > (ulong)stream.Length - rva) return 0;
        var bytes = ReadRange(stream, rva, 4);
        return bytes is null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static byte[]? ReadRange(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length || (ulong)count > (ulong)stream.Length - (ulong)offset)
        {
            return null;
        }
        var bytes = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ulong ReadLeb128(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Length) throw new EndOfStreamException();
            var current = bytes[offset++];
            if (shift == 63 && (current & 0xFE) != 0) throw new InvalidDataException("LEB128 overflow.");
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Overlong LEB128 value.");
    }

    private static void AppendSafety(StringBuilder builder, string message, bool truncated)
    {
        if (truncated) builder.AppendLine("… structure scan was bounded to the safe prefix.");
        builder.AppendLine().AppendLine(message);
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private static bool IsSupportedExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".class" or ".wasm" or ".dmp" or ".mdmp" or ".vdi" or ".vmdk" or
        ".qcow" or ".qcow2" or ".dmg" => true,
        _ => false
    };

    private sealed record ClassSummary(
        ushort Minor,
        ushort Major,
        string? ThisClass,
        string? SuperClass,
        int ConstantPoolEntries,
        int Interfaces,
        int Fields,
        int Methods,
        IReadOnlyList<string> Strings);

    private sealed class ClassReader
    {
        private readonly byte[] _bytes;
        private readonly CancellationToken _cancellationToken;
        private int _offset;
        private CpEntry[] _pool = [];

        internal ClassReader(byte[] bytes, CancellationToken cancellationToken)
        {
            _bytes = bytes;
            _cancellationToken = cancellationToken;
        }

        internal ClassSummary Read()
        {
            if (U4() != 0xCAFEBABE) throw new InvalidDataException("Invalid class signature.");
            var minor = U2();
            var major = U2();
            var count = U2();
            if (count is 0 or > 65535) throw new InvalidDataException("Invalid constant-pool count.");
            _pool = new CpEntry[count];
            for (var index = 1; index < count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var tag = U1();
                switch (tag)
                {
                    case 1:
                        var length = U2();
                        if (length > _bytes.Length - _offset) throw new EndOfStreamException();
                        var text = Encoding.UTF8.GetString(_bytes, _offset, length)
                            .Replace('\0', ' ').ReplaceLineEndings(" ").Trim();
                        _offset += length;
                        _pool[index] = new CpEntry(tag, 0, 0, text);
                        break;
                    case 3 or 4 or 9 or 10 or 11 or 12 or 17 or 18:
                        _pool[index] = new CpEntry(tag, U2(), U2());
                        break;
                    case 5 or 6:
                        _pool[index] = new CpEntry(tag, checked((int)U4()), checked((int)U4()));
                        index++;
                        break;
                    case 7 or 8 or 16 or 19 or 20:
                        _pool[index] = new CpEntry(tag, U2(), 0);
                        break;
                    case 15:
                        _pool[index] = new CpEntry(tag, U1(), U2());
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported class constant tag {tag}.");
                }
            }

            _ = U2(); // access flags
            var thisClass = ResolveClass(U2());
            var superClass = ResolveClass(U2());
            var interfaces = U2();
            Skip(checked(interfaces * 2));
            var fields = U2();
            SkipMembers(fields);
            var methods = U2();
            SkipMembers(methods);
            var attributes = U2();
            SkipAttributes(attributes);
            var strings = _pool.Where(entry => entry.Tag == 1 && !string.IsNullOrWhiteSpace(entry.Text))
                .Select(entry => entry.Text!)
                .Where(value => value.Length is <= 256 and > 1 && value.Any(char.IsLetterOrDigit))
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
            return new ClassSummary(minor, major, thisClass, superClass, count - 1,
                interfaces, fields, methods, strings);
        }

        private void SkipMembers(int count)
        {
            if (count > 8192) throw new InvalidDataException("Class member count exceeds bounds.");
            for (var index = 0; index < count; index++)
            {
                Skip(6);
                var attributes = U2();
                SkipAttributes(attributes);
            }
        }

        private void SkipAttributes(int count)
        {
            if (count > 8192) throw new InvalidDataException("Class attribute count exceeds bounds.");
            for (var index = 0; index < count; index++)
            {
                Skip(2);
                var length = U4();
                if (length > (uint)(_bytes.Length - _offset)) throw new EndOfStreamException();
                _offset += checked((int)length);
            }
        }

        private string? ResolveClass(ushort index)
        {
            if (index == 0 || index >= _pool.Length || _pool[index].Tag != 7) return null;
            var nameIndex = _pool[index].A;
            return nameIndex < _pool.Length ? _pool[nameIndex].Text?.Replace('/', '.') : null;
        }

        private byte U1()
        {
            if (_offset >= _bytes.Length) throw new EndOfStreamException();
            return _bytes[_offset++];
        }

        private ushort U2()
        {
            if (_offset > _bytes.Length - 2) throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(_offset));
            _offset += 2;
            return value;
        }

        private uint U4()
        {
            if (_offset > _bytes.Length - 4) throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(_offset));
            _offset += 4;
            return value;
        }

        private void Skip(int count)
        {
            if (count < 0 || count > _bytes.Length - _offset) throw new EndOfStreamException();
            _offset += count;
        }

        private readonly record struct CpEntry(byte Tag, int A, int B, string? Text = null);
    }
}
