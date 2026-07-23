using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ListaryOpen.App.Previewing;

internal sealed class ArchivePreviewProvider : IFilePreviewProvider
{
    private const int MaximumEntries = 200;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".bz2", ".gz", ".rar", ".tar", ".tgz", ".xz", ".zip"
    };

    public bool CanPreview(PreviewContext context) => Extensions.Contains(context.Extension);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent Load(PreviewContext context, CancellationToken cancellationToken)
    {
        var text = context.Extension.ToLowerInvariant() switch
        {
            ".zip" => ReadZip(context.FullPath, cancellationToken),
            ".tar" => ReadTar(context.FullPath, compressed: false, cancellationToken),
            ".tgz" => ReadTar(context.FullPath, compressed: true, cancellationToken),
            ".gz" when context.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) =>
                ReadTar(context.FullPath, compressed: true, cancellationToken),
            ".gz" => ReadGzip(context.FullPath, cancellationToken),
            _ => ReadOpaqueArchive(context, cancellationToken)
        };
        return PreviewContent.ForText("Archive summary", text);
    }

    private static string ReadZip(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var builder = new StringBuilder()
            .AppendLine("ZIP archive")
            .Append("Entries: ").AppendLine(archive.Entries.Count.ToString())
            .AppendLine();
        foreach (var entry in archive.Entries.Take(MaximumEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(FormatSize(entry.Length).PadLeft(11))
                .Append("  ")
                .AppendLine(entry.FullName);
        }

        if (archive.Entries.Count > MaximumEntries)
        {
            builder.Append("… ").Append(archive.Entries.Count - MaximumEntries).AppendLine(" more entries");
        }

        return builder.ToString();
    }

    private static string ReadTar(string path, bool compressed, CancellationToken cancellationToken)
    {
        using var file = OpenRead(path);
        using var gzip = compressed ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false) : null;
        using var reader = new TarReader(gzip is null ? file : gzip, leaveOpen: false);
        var builder = new StringBuilder().AppendLine(compressed ? "GZip TAR archive" : "TAR archive").AppendLine();
        var count = 0;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count <= MaximumEntries)
            {
                builder.Append(FormatSize(entry.Length).PadLeft(11))
                    .Append("  ")
                    .AppendLine(entry.Name);
            }
        }

        builder.Insert(builder.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length,
            $"Entries: {count}{Environment.NewLine}");
        if (count > MaximumEntries)
        {
            builder.Append("… ").Append(count - MaximumEntries).AppendLine(" more entries");
        }

        return builder.ToString();
    }

    private static string ReadGzip(string path, CancellationToken cancellationToken)
    {
        using var file = OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var buffer = new byte[64 * 1024];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = gzip.Read(buffer, count, buffer.Length - count);
            if (read == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            count += read;
        }

        var sample = PreviewTextReader.Decode(buffer.AsSpan(0, count));
        return sample is null
            ? $"GZip compressed stream{Environment.NewLine}{Environment.NewLine}Decompressed content is binary."
            : $"GZip compressed text{Environment.NewLine}{Environment.NewLine}{sample}" +
                (count == buffer.Length ? Environment.NewLine + "…" : string.Empty);
    }

    private static string ReadOpaqueArchive(PreviewContext context, CancellationToken cancellationToken)
    {
        using var stream = OpenRead(context.FullPath);
        Span<byte> signature = stackalloc byte[16];
        var count = stream.Read(signature);
        cancellationToken.ThrowIfCancellationRequested();
        return string.Join(
            Environment.NewLine,
            $"{context.Extension.TrimStart('.').ToUpperInvariant()} archive",
            string.Empty,
            $"Compressed size: {FormatSize(context.SizeBytes)}",
            $"Signature: {Convert.ToHexString(signature[..count])}",
            string.Empty,
            "A Windows preview handler can provide a full directory listing when installed.");
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string FormatSize(long bytes) => FilePreviewPane.FormatSize(bytes);
}

internal sealed class DocumentPreviewProvider : IFilePreviewProvider
{
    private const int MaximumOutputCharacters = 120_000;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".epub", ".msg", ".odp", ".ods", ".odt", ".ppt", ".pptx",
        ".rtf", ".xls", ".xlsx"
    };

    public bool CanPreview(PreviewContext context) => Extensions.Contains(context.Extension);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent Load(PreviewContext context, CancellationToken cancellationToken)
    {
        var extension = context.Extension.ToLowerInvariant();
        if (extension == ".rtf")
        {
            return PreviewContent.ForText("RTF text", ReadRtf(context.FullPath));
        }

        if (extension is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" or ".epub")
        {
            return PreviewContent.ForText(
                extension == ".epub" ? "EPUB text" : "Document text",
                ReadPackage(context.FullPath, extension, cancellationToken));
        }

        var version = FileVersionInfo.GetVersionInfo(context.FullPath);
        var summary = new StringBuilder()
            .AppendLine($"{extension.TrimStart('.').ToUpperInvariant()} document")
            .AppendLine()
            .Append("Size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes));
        if (!string.IsNullOrWhiteSpace(version.FileDescription))
        {
            summary.Append("Description: ").AppendLine(version.FileDescription);
        }

        summary.AppendLine().AppendLine("Install a matching Windows preview handler for formatted content.");
        return PreviewContent.ForText("Document summary", summary.ToString());
    }

    private static string ReadPackage(string path, string extension, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        IEnumerable<ZipArchiveEntry> entries = extension switch
        {
            ".docx" => archive.Entries.Where(entry => entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)),
            ".xlsx" => archive.Entries.Where(entry =>
                entry.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)),
            ".pptx" => archive.Entries.Where(entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)),
            ".odt" or ".ods" or ".odp" => archive.Entries.Where(entry =>
                entry.FullName.Equals("content.xml", StringComparison.OrdinalIgnoreCase)),
            ".epub" => archive.Entries.Where(entry =>
                entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)),
            _ => []
        };

        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Take(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > 8 * 1024 * 1024)
            {
                continue;
            }

            if (extension is ".pptx" or ".xlsx" or ".epub")
            {
                builder.AppendLine($"— {Path.GetFileNameWithoutExtension(entry.Name)} —");
            }

            ExtractXmlText(entry, builder, cancellationToken);
            builder.AppendLine();
            if (builder.Length >= MaximumOutputCharacters)
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            return "The document package contains no readable text preview.";
        }

        if (builder.Length > MaximumOutputCharacters)
        {
            builder.Length = MaximumOutputCharacters;
            builder.AppendLine().Append('…');
        }

        return NormalizeWhitespace(builder.ToString());
    }

    private static void ExtractXmlText(
        ZipArchiveEntry entry,
        StringBuilder builder,
        CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024
        });
        while (reader.Read() && builder.Length < MaximumOutputCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                var value = reader.Value.Trim();
                if (value.Length > 0)
                {
                    builder.Append(value).Append(' ');
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName is "p" or "tr" or "row" or "si")
            {
                builder.AppendLine();
            }
        }
    }

    private static string ReadRtf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(stream.Length, PreviewTextReader.MaximumBytes)];
        var count = stream.Read(buffer);
        var source = Encoding.Latin1.GetString(buffer, 0, count);
        var plain = Regex.Replace(source, @"\\'[0-9a-fA-F]{2}", match =>
            ((char)Convert.ToByte(match.Value.Substring(2), 16)).ToString(), RegexOptions.None, TimeSpan.FromMilliseconds(200));
        plain = Regex.Replace(plain, @"\\(par|line)\b", Environment.NewLine, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        plain = Regex.Replace(plain, @"\\[a-zA-Z]+-?\d* ?", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        plain = plain.Replace("\\{", "{", StringComparison.Ordinal)
            .Replace("\\}", "}", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        return plain.Trim('{', '}', ' ', '\r', '\n');
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpaces = false;
        var pendingNewLines = 0;
        var previousWasCarriageReturn = false;

        foreach (var character in value)
        {
            if (character is ' ' or '\t')
            {
                pendingSpaces = true;
                previousWasCarriageReturn = false;
                continue;
            }

            if (character == '\r')
            {
                pendingNewLines = Math.Min(2, pendingNewLines + 1);
                pendingSpaces = false;
                previousWasCarriageReturn = true;
                continue;
            }

            if (character == '\n')
            {
                if (!previousWasCarriageReturn)
                {
                    pendingNewLines = Math.Min(2, pendingNewLines + 1);
                }

                pendingSpaces = false;
                previousWasCarriageReturn = false;
                continue;
            }

            previousWasCarriageReturn = false;

            if (builder.Length > 0)
            {
                if (pendingNewLines > 0)
                {
                    for (var index = 0; index < pendingNewLines; index++)
                    {
                        builder.Append(Environment.NewLine);
                    }
                }
                else if (pendingSpaces)
                {
                    builder.Append(' ');
                }
            }

            pendingSpaces = false;
            pendingNewLines = 0;
            builder.Append(character);
        }

        return builder.ToString();
    }
}

internal sealed class PdfPreviewProvider : IFilePreviewProvider
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        context.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16_384, useAsync: true);
        var buffer = new byte[Math.Min(stream.Length, MaximumBytes)];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        var source = Encoding.Latin1.GetString(buffer, 0, count);
        if (!source.StartsWith("%PDF-", StringComparison.Ordinal))
        {
            return null;
        }

        var versionEnd = source.IndexOfAny(['\r', '\n']);
        var version = versionEnd > 0 ? source[1..versionEnd] : "PDF";
        var pages = Regex.Matches(source, @"/Type\s*/Page(?!s)\b", RegexOptions.None, TimeSpan.FromMilliseconds(200)).Count;
        var title = ReadInfoValue(source, "Title");
        var author = ReadInfoValue(source, "Author");
        var builder = new StringBuilder()
            .AppendLine("Portable Document Format")
            .AppendLine()
            .Append("Version: ").AppendLine(version)
            .Append("Detected pages: ").AppendLine(pages == 0 ? "unknown" : pages.ToString())
            .Append("Size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes));
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("Title: ").AppendLine(title);
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            builder.Append("Author: ").AppendLine(author);
        }

        builder.AppendLine().AppendLine("Windows native preview was unavailable; showing safe metadata instead.");
        return PreviewContent.ForText("PDF metadata", builder.ToString());
    }

    private static string? ReadInfoValue(string source, string name)
    {
        var match = Regex.Match(source, $@"/{name}\s*\((?<value>(?:\\.|[^)])*)\)",
            RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return match.Success
            ? match.Groups["value"].Value.Replace("\\(", "(", StringComparison.Ordinal)
                .Replace("\\)", ")", StringComparison.Ordinal)
            : null;
    }
}

internal sealed class MediaPreviewProvider : IFilePreviewProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".aac", ".avi", ".flac", ".m4a", ".m4v", ".mkv", ".mov", ".mp3",
        ".mp4", ".mpeg", ".mpg", ".ogg", ".wav", ".webm", ".wmv"
    };

    public bool CanPreview(PreviewContext context) => Extensions.Contains(context.Extension);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent Load(PreviewContext context, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(stream.Length, 256 * 1024)];
        var count = stream.Read(buffer);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new StringBuilder()
            .AppendLine($"{context.Extension.TrimStart('.').ToUpperInvariant()} media")
            .AppendLine()
            .Append("Size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes));
        if (context.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            AppendWaveMetadata(builder, buffer.AsSpan(0, count));
        }
        else if (context.Extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            AppendFlacMetadata(builder, buffer.AsSpan(0, count));
        }
        else if (context.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            AppendId3Metadata(builder, buffer.AsSpan(0, count));
        }

        builder.AppendLine().AppendLine("Playback is disabled in preview. Windows supplies thumbnails and formatted previews when available.");
        return PreviewContent.ForText("Media metadata", builder.ToString());
    }

    private static void AppendWaveMetadata(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8))
        {
            return;
        }

        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        uint byteRate = 0;
        uint dataSize = 0;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkSize = BitConverter.ToUInt32(bytes.Slice(offset + 4, 4));
            var dataOffset = offset + 8;
            if (bytes.Slice(offset, 4).SequenceEqual("fmt "u8) && chunkSize >= 16 && dataOffset + 16 <= bytes.Length)
            {
                channels = BitConverter.ToUInt16(bytes.Slice(dataOffset + 2, 2));
                sampleRate = BitConverter.ToUInt32(bytes.Slice(dataOffset + 4, 4));
                byteRate = BitConverter.ToUInt32(bytes.Slice(dataOffset + 8, 4));
                bitsPerSample = BitConverter.ToUInt16(bytes.Slice(dataOffset + 14, 2));
            }
            else if (bytes.Slice(offset, 4).SequenceEqual("data"u8))
            {
                dataSize = chunkSize;
            }

            var next = (long)dataOffset + chunkSize + (chunkSize & 1);
            if (next > bytes.Length || next > int.MaxValue)
            {
                break;
            }

            offset = (int)next;
        }

        if (sampleRate > 0)
        {
            builder.Append("Audio: ").Append(channels).Append(" channel(s), ")
                .Append(sampleRate).Append(" Hz, ").Append(bitsPerSample).AppendLine(" bit");
        }

        if (byteRate > 0 && dataSize > 0)
        {
            builder.Append("Duration: ").AppendLine(TimeSpan.FromSeconds((double)dataSize / byteRate).ToString(@"hh\:mm\:ss"));
        }
    }

    private static void AppendFlacMetadata(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 42 || !bytes[..4].SequenceEqual("fLaC"u8))
        {
            return;
        }

        ulong packed = 0;
        for (var index = 18; index < 26; index++)
        {
            packed = (packed << 8) | bytes[index];
        }

        var sampleRate = (uint)(packed >> 44);
        var channels = (int)((packed >> 41) & 0x7) + 1;
        var bits = (int)((packed >> 36) & 0x1F) + 1;
        var totalSamples = packed & 0xFFFFFFFFF;
        builder.Append("Audio: ").Append(channels).Append(" channel(s), ")
            .Append(sampleRate).Append(" Hz, ").Append(bits).AppendLine(" bit");
        if (sampleRate > 0 && totalSamples > 0)
        {
            builder.Append("Duration: ").AppendLine(TimeSpan.FromSeconds((double)totalSamples / sampleRate).ToString(@"hh\:mm\:ss"));
        }
    }

    private static void AppendId3Metadata(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10 || !bytes[..3].SequenceEqual("ID3"u8))
        {
            return;
        }

        var version = bytes[3];
        var tagSize = SyncSafe(bytes.Slice(6, 4));
        var offset = 10;
        var end = Math.Min(bytes.Length, 10 + tagSize);
        while (offset + 10 <= end)
        {
            var frameId = Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            if (frameId.Any(character => !char.IsLetterOrDigit(character)))
            {
                break;
            }

            var frameSize = version >= 4
                ? SyncSafe(bytes.Slice(offset + 4, 4))
                : (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4));
            if (frameSize <= 1 || offset + 10 + frameSize > end)
            {
                break;
            }

            var label = frameId switch
            {
                "TIT2" => "Title",
                "TPE1" => "Artist",
                "TALB" => "Album",
                _ => null
            };
            if (label is not null)
            {
                var value = DecodeId3Text(bytes.Slice(offset + 10, frameSize));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    builder.Append(label).Append(": ").AppendLine(value.TrimEnd('\0'));
                }
            }

            offset += 10 + frameSize;
        }
    }

    private static int SyncSafe(ReadOnlySpan<byte> bytes) =>
        ((bytes[0] & 0x7F) << 21) | ((bytes[1] & 0x7F) << 14) | ((bytes[2] & 0x7F) << 7) | (bytes[3] & 0x7F);

    private static string DecodeId3Text(ReadOnlySpan<byte> bytes) => bytes[0] switch
    {
        1 => Encoding.Unicode.GetString(bytes[1..]),
        2 => Encoding.BigEndianUnicode.GetString(bytes[1..]),
        3 => Encoding.UTF8.GetString(bytes[1..]),
        _ => Encoding.Latin1.GetString(bytes[1..])
    };
}

internal sealed class ExecutablePreviewProvider : IFilePreviewProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".msi", ".sys"
    };

    public bool CanPreview(PreviewContext context) => Extensions.Contains(context.Extension);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent Load(PreviewContext context, CancellationToken cancellationToken)
    {
        var info = FileVersionInfo.GetVersionInfo(context.FullPath);
        var builder = new StringBuilder()
            .AppendLine($"{context.Extension.TrimStart('.').ToUpperInvariant()} binary")
            .AppendLine()
            .Append("Description: ").AppendLine(info.FileDescription ?? "—")
            .Append("Product: ").AppendLine(info.ProductName ?? "—")
            .Append("Company: ").AppendLine(info.CompanyName ?? "—")
            .Append("File version: ").AppendLine(info.FileVersion ?? "—")
            .Append("Product version: ").AppendLine(info.ProductVersion ?? "—");
        if (!context.Extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (reader.PEHeaders.PEHeader is { } header)
            {
                builder.Append("Architecture: ").AppendLine(reader.PEHeaders.CoffHeader.Machine.ToString())
                    .Append("Format: ").AppendLine(header.Magic.ToString())
                    .Append("Managed metadata: ").AppendLine(reader.HasMetadata ? "yes" : "no");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(context.FullPath));
#pragma warning restore SYSLIB0057
            builder.Append("Signed by: ").AppendLine(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or
            ArgumentException or IOException)
        {
            builder.AppendLine("Signed by: unsigned or unverifiable");
        }

        builder.AppendLine().AppendLine("The file is inspected as data and is never executed.");
        return PreviewContent.ForText("Binary metadata", builder.ToString());
    }
}
