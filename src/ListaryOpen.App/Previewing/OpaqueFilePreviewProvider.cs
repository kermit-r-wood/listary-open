using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal sealed class OpaqueFilePreviewProvider : IFilePreviewProvider
{
    private const int SignatureBytes = 512;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Opaque);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var signature = new byte[Math.Min(SignatureBytes, checked((int)Math.Min(stream.Length, SignatureBytes)))];
        stream.ReadExactly(signature);
        var count = signature.Length;
        var footer = new byte[Math.Min(SignatureBytes, checked((int)Math.Min(stream.Length, SignatureBytes)))];
        if (stream.Length > 0)
        {
            stream.Position = Math.Max(0, stream.Length - footer.Length);
            stream.ReadExactly(footer);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var description = Describe(context.Extension, signature.AsSpan(0, count), footer);
        var builder = new StringBuilder(description).AppendLine().AppendLine()
            .Append("Size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes))
            .Append("Signature: ").AppendLine(count == 0
                ? "(empty)"
                : Convert.ToHexString(signature.AsSpan(0, Math.Min(count, 32))))
            .Append("Modified: ").AppendLine(context.LastWriteTime.ToString("u"))
            .AppendLine()
            .AppendLine("Content is not executed, mounted, imported, or decoded by the built-in preview.");
        if (PreviewFormatRegistry.ShouldPreferSystem(context.Extension))
        {
            builder.AppendLine("A matching Windows preview handler is used first when installed.");
        }
        return Task.FromResult<PreviewContent?>(
            PreviewContent.ForText("File metadata", builder.ToString()));
    }

    private static string Describe(
        string extension,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> footer)
    {
        if (signature.StartsWith("SQLite format 3\0"u8))
        {
            return "SQLite database";
        }
        if (signature.StartsWith("vhdxfile"u8))
        {
            return "VHDX virtual disk";
        }
        if (footer.StartsWith("conectix"u8))
        {
            return "VHD virtual disk";
        }
        if (footer.StartsWith("koly"u8))
        {
            return "Apple UDIF disk image";
        }
        if (signature.StartsWith("KDMV"u8))
        {
            return "VMware virtual disk";
        }
        if (signature.StartsWith(new byte[] { (byte)'Q', (byte)'F', (byte)'I', 0xFB }))
        {
            var version = signature.Length >= 8
                ? BinaryPrimitives.ReadUInt32BigEndian(signature[4..])
                : 0;
            return $"QCOW virtual disk (version {version})";
        }
        if (signature.Length >= 0x44 &&
            BinaryPrimitives.ReadUInt32LittleEndian(signature[0x40..]) == 0xBEDA107F)
        {
            return "VirtualBox VDI disk";
        }
        if (signature.StartsWith("MDMP"u8) || signature.StartsWith("PAGE"u8))
        {
            return "Windows crash dump";
        }
        if (signature.StartsWith(new byte[] { 0x0A, 0x0D, 0x0D, 0x0A }))
        {
            return "PCAP Next Generation capture";
        }
        if (signature.Length >= 4 &&
            (signature[..4].SequenceEqual(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }) ||
             signature[..4].SequenceEqual(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }) ||
             signature[..4].SequenceEqual(new byte[] { 0x4D, 0x3C, 0xB2, 0xA1 }) ||
             signature[..4].SequenceEqual(new byte[] { 0xA1, 0xB2, 0x3C, 0x4D })))
        {
            return "Packet capture";
        }
        if ((extension.Equals(".pst", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".ost", StringComparison.OrdinalIgnoreCase)) &&
            signature.StartsWith("!BDN"u8))
        {
            var version = signature.Length >= 12
                ? BinaryPrimitives.ReadUInt16LittleEndian(signature[10..])
                : (ushort)0;
            var generation = version >= 23 ? "Unicode" : version > 0 ? "ANSI" : "unknown generation";
            return extension.Equals(".ost", StringComparison.OrdinalIgnoreCase)
                ? $"Outlook Offline Storage ({generation})"
                : $"Outlook Personal Folders ({generation})";
        }
        if (signature.IndexOf("Standard Jet DB"u8) >= 0)
        {
            return "Microsoft Access Jet database";
        }
        if (signature.IndexOf("Standard ACE DB"u8) >= 0)
        {
            return "Microsoft Access ACE database";
        }
        if (extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase) &&
            !signature.IsEmpty && signature[0] == (byte)'d')
        {
            return "BitTorrent metadata";
        }
        if (signature.StartsWith(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }))
        {
            var major = signature.Length >= 8
                ? BinaryPrimitives.ReadUInt16BigEndian(signature[6..])
                : (ushort)0;
            return major == 0
                ? "Java class file"
                : $"Java class file (major version {major})";
        }
        if (signature.StartsWith(new byte[] { 0x00, 0x61, 0x73, 0x6D }))
        {
            var version = signature.Length >= 8
                ? BinaryPrimitives.ReadUInt32LittleEndian(signature[4..])
                : 0;
            return $"WebAssembly module (version {version})";
        }
        if (signature.StartsWith("PAR1"u8) && footer.EndsWith("PAR1"u8))
        {
            return "Apache Parquet data file";
        }
        if (signature.StartsWith(new byte[] { (byte)'O', (byte)'b', (byte)'j', 0x01 }))
        {
            return "Apache Avro object container";
        }
        if (signature.StartsWith("ARROW1"u8))
        {
            return "Apache Arrow / Feather data file";
        }
        if (signature.StartsWith("ORC"u8))
        {
            return "Apache ORC data file";
        }
        if (signature.Length >= 6 &&
            Encoding.ASCII.GetString(signature[..6]).StartsWith("AC10", StringComparison.Ordinal))
        {
            var version = Encoding.ASCII.GetString(signature[..6]);
            return $"AutoCAD drawing ({version})";
        }
        if (extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase))
        {
            return "VHD virtual disk";
        }
        return $"{extension.TrimStart('.').ToUpperInvariant()} file";
    }
}
