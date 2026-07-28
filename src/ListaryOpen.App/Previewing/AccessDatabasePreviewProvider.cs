using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads only the public Jet/ACE header marker and bounded UTF-16 name-like
/// strings from Access databases. The database engine is never loaded and no
/// rows, memo/OLE values, VBA, macros, or attachments are queried.
/// </summary>
internal sealed class AccessDatabasePreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumScanBytes = 1024 * 1024;
    private const int MaximumNames = 80;
    private const int MaximumOutputCharacters = 120_000;
    private static readonly string[] Extensions = [".accdb", ".accde", ".mdb", ".mde"];

    public bool CanPreview(PreviewContext context) =>
        Extensions.Contains(context.Extension, StringComparer.OrdinalIgnoreCase) &&
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Database);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < 32 || context.SizeBytes > MaximumInputBytes)
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
        var marker = FindMarker(bytes, "Standard ACE DB"u8) ? "ACE" :
            FindMarker(bytes, "Standard Jet DB"u8) ? "Jet" : null;
        if (marker is null)
        {
            return null;
        }
        var names = ReadUtf16Runs(bytes, cancellationToken);
        var builder = new StringBuilder("Access database structure")
            .AppendLine()
            .Append("Engine marker: Standard ").Append(marker).AppendLine(" DB")
            .Append("File size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes))
            .Append("Approximate 4 KiB pages: ").AppendLine((context.SizeBytes / 4096d).ToString("N0"))
            .AppendLine()
            .AppendLine("Name-like UTF-16 header samples (not a table dump):");
        if (names.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var name in names) builder.Append("- ").AppendLine(name);
        }
        builder.AppendLine()
            .AppendLine("Rows, schema pages, memo/OLE values, VBA, macros, attachments, and linked data were not opened.");
        return PreviewContent.ForText("Access database structure", Bound(builder.ToString()));
    }

    private static bool FindMarker(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> marker)
    {
        for (var offset = 0; offset <= bytes.Length - marker.Length; offset++)
        {
            if (bytes.Slice(offset, marker.Length).SequenceEqual(marker)) return true;
        }
        return false;
    }

    private static List<string> ReadUtf16Runs(byte[] bytes, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        for (var offset = 0; offset + 8 <= bytes.Length && names.Count < MaximumNames; offset += 2)
        {
            if ((offset & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (bytes[offset + 1] != 0 || bytes[offset] < 0x20 || bytes[offset] > 0x7E) continue;
            var end = offset;
            while (end + 1 < bytes.Length && bytes[end + 1] == 0 && bytes[end] is >= 0x20 and <= 0x7E)
            {
                end += 2;
                if (end - offset >= 512) break;
            }
            if (end - offset < 8) continue;
            var value = Encoding.Unicode.GetString(bytes, offset, end - offset).Trim();
            if (value.Any(char.IsLetter) && !names.Contains(value, StringComparer.Ordinal)) names.Add(value);
            offset = end - 2;
        }
        return names;
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";
}
