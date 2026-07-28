using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal sealed class VirtualDiskPreviewProvider : IFilePreviewProvider
{
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.VirtualDisk);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        cancellationToken.ThrowIfCancellationRequested();
        return context.Extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase)
            ? ReadVhd(context, stream)
            : ReadVhdx(context, stream);
    }

    private static PreviewContent? ReadVhd(PreviewContext context, Stream stream)
    {
        if (stream.Length < 512)
        {
            return null;
        }
        var trailing = ReadVhdFooter(stream, stream.Length - 512);
        var leading = stream.Length >= 1024 ? ReadVhdFooter(stream, 0) : default;
        var parsed = trailing.Valid ? trailing : leading.Valid ? leading : default;
        if (!parsed.Valid)
        {
            return trailing.Recognized || leading.Recognized
                ? PreviewContent.ForText(
                    "VHD metadata",
                    $"Virtual Hard Disk{Environment.NewLine}{Environment.NewLine}" +
                    $"Stored file size: {FilePreviewPane.FormatSize(context.SizeBytes)}{Environment.NewLine}" +
                    "No VHD footer passed checksum and structural validation.")
                : null;
        }
        var created = DateTimeOffset.UnixEpoch.AddYears(30).AddSeconds(parsed.Timestamp);
        var builder = new StringBuilder("Virtual Hard Disk")
            .AppendLine().AppendLine()
            .Append("Disk type: ").AppendLine(VhdTypeName(parsed.DiskType))
            .Append("Current virtual size: ").AppendLine(FilePreviewPane.FormatSize((long)parsed.CurrentSize))
            .Append("Original virtual size: ").AppendLine(FilePreviewPane.FormatSize((long)parsed.OriginalSize))
            .Append("Format version: ").Append(parsed.FormatVersion >> 16).Append('.')
                .AppendLine((parsed.FormatVersion & 0xFFFF).ToString())
            .Append("Created: ").AppendLine(created.ToString("u"))
            .Append("Creator: ").Append(parsed.Creator)
                .Append(' ').Append(parsed.CreatorVersion >> 16).Append('.')
                .AppendLine((parsed.CreatorVersion & 0xFFFF).ToString())
            .Append("Identifier: ").AppendLine(parsed.Identifier.ToString())
            .AppendLine("Footer checksum: valid")
            .Append("Footer copy: ").AppendLine(trailing.Valid ? "trailing" : "leading redundant copy")
            .Append("Stored file size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes))
            .AppendLine()
            .AppendLine("The virtual disk was read as metadata only and was not mounted or attached.");
        return PreviewContent.ForText("VHD metadata", builder.ToString());
    }

    private static PreviewContent? ReadVhdx(PreviewContext context, Stream stream)
    {
        const int headerLength = 4096;
        if (stream.Length < 128 * 1024L + headerLength)
        {
            return null;
        }
        Span<byte> identifier = stackalloc byte[8];
        stream.ReadExactly(identifier);
        if (!identifier.SequenceEqual("vhdxfile"u8))
        {
            return null;
        }
        var first = ReadVhdxHeader(stream, 64 * 1024, stream.Length);
        var second = ReadVhdxHeader(stream, 128 * 1024, stream.Length);
        var selected = new[] { first, second }
            .Where(header => header.Valid)
            .OrderByDescending(header => header.Sequence)
            .FirstOrDefault();
        if (!selected.Valid)
        {
            return PreviewContent.ForText(
                "VHDX metadata",
                $"VHDX virtual disk{Environment.NewLine}{Environment.NewLine}" +
                $"Stored file size: {FilePreviewPane.FormatSize(context.SizeBytes)}{Environment.NewLine}" +
                "Neither redundant VHDX header passed its CRC32C validation.");
        }
        var builder = new StringBuilder("Virtual Hard Disk v2")
            .AppendLine().AppendLine()
            .Append("Active header sequence: ").AppendLine(selected.Sequence.ToString())
            .Append("Format version: ").AppendLine(selected.Version.ToString())
            .Append("Log version: ").AppendLine(selected.LogVersion.ToString())
            .Append("File write identifier: ").AppendLine(selected.FileWriteGuid.ToString())
            .Append("Data write identifier: ").AppendLine(selected.DataWriteGuid.ToString())
            .Append("Log identifier: ").AppendLine(selected.LogGuid.ToString())
            .Append("Log length: ").AppendLine(FilePreviewPane.FormatSize(selected.LogLength))
            .Append("Stored file size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes))
            .AppendLine()
            .AppendLine("The virtual disk was read as metadata only and was not mounted or attached.");
        return PreviewContent.ForText("VHDX metadata", builder.ToString());
    }

    private static VhdFooter ReadVhdFooter(Stream stream, long offset)
    {
        var footer = new byte[512];
        stream.Position = offset;
        stream.ReadExactly(footer);
        if (!footer.AsSpan(0, 8).SequenceEqual("conectix"u8))
        {
            return default;
        }
        var storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(64));
        footer.AsSpan(64, 4).Clear();
        uint sum = 0;
        foreach (var value in footer)
        {
            sum += value;
        }
        var formatVersion = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(12));
        var originalSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(40));
        var currentSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48));
        var diskType = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
        var structurallyValid =
            BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(8)) == 2 &&
            formatVersion == 0x00010000 &&
            diskType is >= 2 and <= 4 &&
            originalSize is > 0 and <= long.MaxValue &&
            currentSize is > 0 and <= long.MaxValue &&
            originalSize % 512 == 0 &&
            currentSize % 512 == 0 &&
            footer.AsSpan(85, 427).IndexOfAnyExcept((byte)0) < 0;
        return new VhdFooter(
            true,
            storedChecksum == ~sum && structurallyValid,
            formatVersion,
            BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(24)),
            SafeFourCc(footer.AsSpan(28, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(32)),
            originalSize,
            currentSize,
            diskType,
            new Guid(footer.AsSpan(68, 16), bigEndian: true));
    }

    private static VhdxHeader ReadVhdxHeader(Stream stream, long offset, long fileLength)
    {
        var bytes = new byte[4096];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        if (!bytes.AsSpan(0, 4).SequenceEqual("head"u8))
        {
            return default;
        }
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        bytes.AsSpan(4, 4).Clear();
        var logVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(64));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(66));
        var logLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(68));
        var logOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(72));
        var valid = Crc32C(bytes) == storedCrc &&
            version == 1 &&
            logVersion == 0 &&
            logLength >= 1024 * 1024 &&
            logLength % (1024 * 1024) == 0 &&
            logOffset >= 1024 * 1024 &&
            logOffset % (1024 * 1024) == 0 &&
            logOffset <= (ulong)fileLength &&
            logLength <= (ulong)fileLength - logOffset &&
            bytes.AsSpan(80).IndexOfAnyExcept((byte)0) < 0;
        return new VhdxHeader(
            valid,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)),
            new Guid(bytes.AsSpan(16, 16)),
            new Guid(bytes.AsSpan(32, 16)),
            new Guid(bytes.AsSpan(48, 16)),
            logVersion,
            version,
            logLength);
    }

    internal static uint Crc32C(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0);
            }
        }
        return ~crc;
    }

    private static string VhdTypeName(uint value) => value switch
    {
        2 => "Fixed",
        3 => "Dynamic",
        4 => "Differencing",
        _ => $"Unknown ({value})"
    };

    private static string SafeFourCc(ReadOnlySpan<byte> bytes)
    {
        var value = Encoding.ASCII.GetString(bytes);
        return value.All(character => character is >= ' ' and <= '~') ? value : "(unknown)";
    }

    private readonly record struct VhdFooter(
        bool Recognized,
        bool Valid,
        uint FormatVersion,
        uint Timestamp,
        string Creator,
        uint CreatorVersion,
        ulong OriginalSize,
        ulong CurrentSize,
        uint DiskType,
        Guid Identifier);

    private readonly record struct VhdxHeader(
        bool Valid,
        ulong Sequence,
        Guid FileWriteGuid,
        Guid DataWriteGuid,
        Guid LogGuid,
        ushort LogVersion,
        ushort Version,
        uint LogLength);
}
