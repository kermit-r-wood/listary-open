using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads packet-capture global headers and bounded record metadata. Packet
/// bytes are skipped by declared length and are never decoded or displayed.
/// </summary>
internal sealed class PacketCapturePreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumScanBytes = 16 * 1024 * 1024;
    private const int MaximumRecords = 2000;
    private const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        (context.Extension.Equals(".pcap", StringComparison.OrdinalIgnoreCase) ||
         context.Extension.Equals(".pcapng", StringComparison.OrdinalIgnoreCase)) &&
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
        var count = checked((int)Math.Min(stream.Length, MaximumScanBytes));
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.AsSpan().StartsWith(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }))
        {
            return ReadPcapNg(context, bytes, cancellationToken);
        }
        var endian = GetPcapEndian(bytes);
        if (endian is not { } resolvedEndian) return null;
        return ReadPcap(context, bytes, resolvedEndian, cancellationToken);
    }

    private static PreviewContent? ReadPcap(
        PreviewContext context,
        byte[] bytes,
        PcapEndian endian,
        CancellationToken cancellationToken)
    {
        if (bytes.Length < 24) return null;
        var major = ReadU16(bytes, 4, endian);
        var minor = ReadU16(bytes, 6, endian);
        var snapLength = ReadU32(bytes, 16, endian);
        var linkType = ReadU32(bytes, 20, endian);
        var position = 24;
        var records = 0;
        ulong captured = 0, original = 0;
        while (position + 16 <= bytes.Length && records < MaximumRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var included = ReadU32(bytes, position + 8, endian);
            var total = ReadU32(bytes, position + 12, endian);
            if ((ulong)included > (ulong)(bytes.Length - position - 16) || included > 64 * 1024 * 1024) break;
            captured += included;
            original += total;
            records++;
            position = checked(position + 16 + (int)included);
        }
        var builder = new StringBuilder("PCAP packet capture structure")
            .AppendLine().Append("PCAP version: ").Append(major).Append('.').AppendLine(minor.ToString())
            .Append("Link type: ").AppendLine(linkType.ToString())
            .Append("Snap length: ").AppendLine(snapLength.ToString())
            .Append("Records scanned: ").AppendLine(records.ToString())
            .Append("Captured bytes declared: ").AppendLine(captured.ToString("N0"))
            .Append("Original bytes declared: ").AppendLine(original.ToString("N0"));
        if (position < bytes.Length || records >= MaximumRecords) builder.AppendLine("… capture scan bounded or truncated");
        builder.AppendLine().AppendLine("Packet payloads were not decoded or displayed.");
        return PreviewContent.ForText("PCAP packet capture structure", Bound(builder.ToString()));
    }

    private static PreviewContent? ReadPcapNg(
        PreviewContext context,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (bytes.Length < 12) return null;
        var endian = GetPcapNgEndian(bytes);
        if (endian is not { } resolvedEndian) return null;
        var position = 0;
        var blocks = 0;
        var packets = 0;
        ulong captured = 0, original = 0;
        while (position + 12 <= bytes.Length && blocks < MaximumRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = ReadU32(bytes, position, resolvedEndian);
            var length = ReadU32(bytes, position + 4, resolvedEndian);
            if (length < 12 || length > bytes.Length - position || (length & 3) != 0) break;
            if (type == 0x00000006 && length >= 32)
            {
                captured += ReadU32(bytes, position + 20, resolvedEndian);
                original += ReadU32(bytes, position + 24, resolvedEndian);
                packets++;
            }
            blocks++;
            position = checked(position + (int)length);
        }
        var builder = new StringBuilder("PCAPNG packet capture structure")
            .AppendLine().Append("Blocks scanned: ").AppendLine(blocks.ToString())
            .Append("Enhanced packets: ").AppendLine(packets.ToString())
            .Append("Captured bytes declared: ").AppendLine(captured.ToString("N0"))
            .Append("Original bytes declared: ").AppendLine(original.ToString("N0"));
        if (position < bytes.Length || blocks >= MaximumRecords) builder.AppendLine("… capture scan bounded or truncated");
        builder.AppendLine().AppendLine("Packet payloads were not decoded or displayed.");
        return PreviewContent.ForText("PCAPNG packet capture structure", Bound(builder.ToString()));
    }

    private static PcapEndian? GetPcapEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return null;
        return bytes[..4] switch
        {
            [0xD4, 0xC3, 0xB2, 0xA1] or [0x4D, 0x3C, 0xB2, 0xA1] => PcapEndian.Little,
            [0xA1, 0xB2, 0xC3, 0xD4] or [0xA1, 0xB2, 0x3C, 0x4D] => PcapEndian.Big,
            _ => null
        };
    }

    private static PcapEndian? GetPcapNgEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A })) return null;
        return bytes[8..12] switch
        {
            [0x4D, 0x3C, 0x2B, 0x1A] => PcapEndian.Little,
            [0x1A, 0x2B, 0x3C, 0x4D] => PcapEndian.Big,
            _ => null
        };
    }

    private static ushort ReadU16(byte[] bytes, int offset, PcapEndian endian) =>
        endian == PcapEndian.Little
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));

    private static uint ReadU32(byte[] bytes, int offset, PcapEndian endian) =>
        endian == PcapEndian.Little
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));

    private static string Bound(string value) => value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private enum PcapEndian { Little, Big }
}
