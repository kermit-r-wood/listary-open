using System.IO;
using System.IO.Compression;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Provides a bounded structural preview for Kindle Format 10 files. KFX-ZIP
/// central directories can be listed safely; raw Amazon Ion/DRM records are
/// only sampled for printable metadata and are never decrypted or interpreted.
/// </summary>
internal sealed class KfxPreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumScanBytes = 4 * 1024 * 1024;
    private const int MaximumEntries = 200;
    private const int MaximumStrings = 80;
    private const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        context.Extension.Equals(".kfx", StringComparison.OrdinalIgnoreCase) &&
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Ebook);

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
        Span<byte> signature = stackalloc byte[4];
        stream.ReadExactly(signature);
        stream.Position = 0;
        if (signature.SequenceEqual("PK\x03\x04"u8) || signature.SequenceEqual("PK\x05\x06"u8) ||
            signature.SequenceEqual("PK\x07\x08"u8))
        {
            return ReadKfxZip(stream, cancellationToken);
        }

        stream.Position = 0;
        var scanLength = checked((int)Math.Min(stream.Length, MaximumScanBytes));
        var bytes = new byte[scanLength];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var strings = ReadPrintableRuns(bytes, cancellationToken);
        var builder = new StringBuilder("KFX / Amazon Ion structure")
            .AppendLine()
            .Append("Input size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes))
            .Append("Signature: ").AppendLine(Convert.ToHexString(signature))
            .AppendLine()
            .AppendLine("Printable Ion metadata samples (records were not decoded):");
        if (strings.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var value in strings)
            {
                builder.Append("- ").AppendLine(value);
            }
        }
        builder.AppendLine()
            .AppendLine("Amazon Ion, DRM, images, fonts, and text records were not decrypted or interpreted.");
        return PreviewContent.ForText("KFX structure", Bound(builder.ToString()));
    }

    private static PreviewContent ReadKfxZip(FileStream stream, CancellationToken cancellationToken)
    {
        ZipPreviewBudget.ValidatePackage(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var builder = new StringBuilder("KFX-ZIP container")
            .AppendLine().Append("Entries: ").AppendLine(archive.Entries.Count.ToString())
            .AppendLine().AppendLine("Members (payloads not opened):");
        foreach (var entry in archive.Entries.Take(MaximumEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("- ").Append(entry.FullName)
                .Append(" (").Append(FilePreviewPane.FormatSize(entry.Length)).AppendLine(")");
            if (builder.Length >= MaximumOutputCharacters)
            {
                builder.AppendLine("… output truncated");
                break;
            }
        }
        if (archive.Entries.Count > MaximumEntries)
        {
            builder.Append("… ").Append(archive.Entries.Count - MaximumEntries).AppendLine(" more entries");
        }
        builder.AppendLine()
            .AppendLine("Ion, DRM, image, font, and text payloads were not decrypted or opened.");
        return PreviewContent.ForText("KFX-ZIP structure", Bound(builder.ToString()));
    }

    private static List<string> ReadPrintableRuns(byte[] bytes, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        for (var index = 0; index < bytes.Length; index++)
        {
            if ((index & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = bytes[index];
            if (value is >= 0x20 and <= 0x7E)
            {
                if (current.Length < 512) current.Append((char)value);
                continue;
            }
            AddRun(values, current);
            if (values.Count >= MaximumStrings) break;
        }
        AddRun(values, current);
        return values;
    }

    private static void AddRun(List<string> values, StringBuilder current)
    {
        var value = current.ToString().Trim();
        current.Clear();
        if (value.Length >= 4 && value.Any(char.IsLetter) && values.Count < MaximumStrings)
        {
            values.Add(value);
        }
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";
}
