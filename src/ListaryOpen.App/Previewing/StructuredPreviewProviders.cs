using System.Buffers.Binary;
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
using K4os.Compression.LZ4.Streams;
using SharpCompress.Archives;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Lzw;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Readers;

namespace ListaryOpen.App.Previewing;

internal sealed class ArchivePreviewProvider : IFilePreviewProvider
{
    private const int MaximumEntries = 200;
    private const int MaximumOutputCharacters = 120_000;
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Archive);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent Load(PreviewContext context, CancellationToken cancellationToken)
    {
        var text = context.Extension.ToLowerInvariant() switch
        {
            ".3mf" or ".aab" or ".apk" or ".appx" or ".appxbundle" or ".cbz" or ".ear" or
                ".ipa" or ".jar" or ".key" or ".kmz" or ".msix" or ".msixbundle" or ".numbers" or
                ".nupkg" or ".olm" or ".pages" or ".sketch" or ".snupkg" or ".vsix" or ".war" or ".xpi" or ".zip" =>
                ReadZip(context.FullPath, cancellationToken),
            ".maff" => ReadMaff(context.FullPath, cancellationToken),
            ".cbt" or ".ova" or ".tar" => ReadTar(context.FullPath, compressed: false, cancellationToken),
            ".tgz" => ReadTar(context.FullPath, compressed: true, cancellationToken),
            ".gz" when context.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) =>
                ReadTar(context.FullPath, compressed: true, cancellationToken),
            ".gz" => ReadGzip(context.FullPath, cancellationToken),
            ".7z" or ".ace" or ".arc" or ".arj" or ".cb7" or ".cbr" or ".rar" or ".zipx" =>
                ReadSharpArchive(context.FullPath, context.Extension, cancellationToken),
            ".cab" or ".msu" => CabinetPreviewReader.Read(context.FullPath, cancellationToken),
            ".iso" => IsoPreviewReader.Read(context.FullPath, cancellationToken),
            ".taz" or ".tb2" or ".tbr" or ".tbz" or ".tbz2" or ".tlz" or ".tlz4" or ".tz" or ".tz2" or
                ".txz" or ".tzst" or ".tzstd" =>
                ReadCompressedTar(context.FullPath, context.Extension, cancellationToken),
            ".br" or ".bz2" or ".lzip" or ".lz" or ".lz4" or ".xz" or ".z" or ".zst" or ".zstd"
                when IsCompressedTar(context.Name) =>
                ReadCompressedTar(context.FullPath, context.Extension, cancellationToken),
            ".br" or ".bz2" or ".lzip" or ".lz" or ".lz4" or ".xz" or ".z" or ".zst" or ".zstd" =>
                ReadCompressedStream(context.FullPath, context.Extension, cancellationToken),
            _ => ReadOpaqueArchive(context, cancellationToken)
        };
        return PreviewContent.ForText("Archive summary", text);
    }

    private static string ReadZip(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        ZipPreviewBudget.ValidatePackage(stream);
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
                .AppendLine(TruncateName(entry.FullName));
            if (builder.Length >= MaximumOutputCharacters)
            {
                builder.Length = MaximumOutputCharacters;
                builder.AppendLine().AppendLine("… output truncated");
                break;
            }
        }

        if (archive.Entries.Count > MaximumEntries)
        {
            builder.Append("… ").Append(archive.Entries.Count - MaximumEntries).AppendLine(" more entries");
        }

        return builder.ToString();
    }

    private static string ReadMaff(string path, CancellationToken cancellationToken)
    {
        const int maximumPages = 16;
        const int maximumPageBytes = 256 * 1024;
        const int maximumAggregateBytes = 1024 * 1024;
        using var stream = OpenRead(path);
        ZipPreviewBudget.ValidatePackage(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var pages = archive.Entries
            .Where(entry =>
                entry.FullName.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith("/index.htm", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Equals("index.htm", StringComparison.OrdinalIgnoreCase))
            .Take(maximumPages)
            .ToArray();
        if (pages.Length == 0)
        {
            return "MAFF web archive" + Environment.NewLine + Environment.NewLine +
                "No bounded main-page entry was found.";
        }

        var builder = new StringBuilder("MAFF web archive")
            .AppendLine().Append("Pages shown: ").AppendLine(pages.Length.ToString()).AppendLine();
        var aggregate = 0;
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Math.Min(maximumPageBytes, maximumAggregateBytes - aggregate);
            if (remaining <= 0)
            {
                break;
            }
            using var input = page.Open();
            var bytes = new byte[remaining + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(bytes, count, bytes.Length - count);
                if (read == 0)
                {
                    break;
                }
                count += read;
            }
            aggregate += Math.Min(count, remaining);
            var source = PreviewTextReader.Decode(bytes.AsSpan(0, Math.Min(count, remaining)));
            builder.Append("— ").AppendLine(TruncateName(page.FullName)).AppendLine();
            if (source is not null)
            {
                builder.AppendLine(TextPreviewProvider.ConvertHtmlToText(source));
            }
            if (count > remaining)
            {
                builder.AppendLine("… page truncated");
            }
            builder.AppendLine();
            if (builder.Length >= MaximumOutputCharacters)
            {
                builder.Length = MaximumOutputCharacters;
                builder.AppendLine().AppendLine("… output truncated");
                break;
            }
        }
        return builder.ToString();
    }

    private static string ReadTar(string path, bool compressed, CancellationToken cancellationToken)
    {
        using var file = OpenRead(path);
        using var gzip = compressed ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false) : null;
        using var bounded = new CountingReadStream(
            gzip is null ? file : gzip,
            64L * 1024 * 1024,
            "Compressed TAR exceeds the 64 MiB decompressed scan budget.");
        using var reader = new TarReader(bounded, leaveOpen: false);
        var builder = new StringBuilder().AppendLine(compressed ? "GZip TAR archive" : "TAR archive").AppendLine();
        var count = 0;
        var truncated = false;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            builder.Append(FormatSize(entry.Length).PadLeft(11))
                .Append("  ")
                .AppendLine(TruncateName(entry.Name));
            if (count >= MaximumEntries || builder.Length >= MaximumOutputCharacters)
            {
                truncated = true;
                break;
            }
        }

        builder.Insert(builder.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length,
            truncated ? $"Entries shown: {count}+{Environment.NewLine}" : $"Entries: {count}{Environment.NewLine}");
        if (truncated)
        {
            builder.AppendLine("… archive listing truncated");
        }

        return builder.ToString();
    }

    private static string ReadGzip(string path, CancellationToken cancellationToken)
    {
        using var file = OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        const int maximumSampleBytes = 64 * 1024;
        var buffer = new byte[maximumSampleBytes + 1];
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

        var sample = PreviewTextReader.Decode(buffer.AsSpan(0, Math.Min(count, maximumSampleBytes)));
        return sample is null
            ? $"GZip compressed stream{Environment.NewLine}{Environment.NewLine}Decompressed content is binary."
            : $"GZip compressed text{Environment.NewLine}{Environment.NewLine}{sample}" +
                (count > maximumSampleBytes ? Environment.NewLine + "…" : string.Empty);
    }

    private static string ReadSharpArchive(
        string path,
        string extension,
        CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        using var archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = false });
        var builder = new StringBuilder()
            .Append(extension.TrimStart('.').ToUpperInvariant())
            .AppendLine(" archive")
            .AppendLine();
        var count = 0;
        var totalSize = 0L;
        var encrypted = false;
        var truncated = false;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            encrypted |= entry.IsEncrypted;
            totalSize = SaturatingAdd(totalSize, entry.Size);
            builder.Append(FormatSize(entry.Size).PadLeft(11))
                .Append("  ")
                .AppendLine(TruncateName(entry.Key ?? "(unnamed)"));
            if (count >= MaximumEntries || builder.Length >= MaximumOutputCharacters)
            {
                truncated = true;
                break;
            }
        }

        builder.Insert(
            builder.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length,
            $"Entries shown: {count}{(truncated ? "+" : string.Empty)}{Environment.NewLine}" +
            $"Total declared size shown: {FormatSize(totalSize)}{Environment.NewLine}" +
            $"Encrypted entries shown: {(encrypted ? "yes" : "no")}{Environment.NewLine}");
        if (truncated)
        {
            builder.AppendLine("… archive listing truncated");
        }

        return builder.ToString();
    }

    private static string ReadCompressedStream(
        string path,
        string extension,
        CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var entryStream = OpenCompressedStream(stream, extension);
        const int maximumSampleBytes = 64 * 1024;
        var buffer = new byte[maximumSampleBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = entryStream.Read(buffer, count, buffer.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        var sampleLength = Math.Min(count, maximumSampleBytes);
        var text = PreviewTextReader.Decode(buffer.AsSpan(0, sampleLength));
        var builder = new StringBuilder()
            .Append(extension.TrimStart('.').ToUpperInvariant())
            .AppendLine(" compressed stream")
            .Append("Source: ").AppendLine(Path.GetFileName(path))
            .AppendLine();
        if (text is null)
        {
            builder.AppendLine("The decompressed sample is binary.");
        }
        else
        {
            builder.AppendLine("Decompressed text sample:")
                .Append(text);
            if (count > maximumSampleBytes)
            {
                builder.AppendLine().Append('…');
            }
        }

        return builder.ToString();
    }

    private static string ReadCompressedTar(
        string path,
        string extension,
        CancellationToken cancellationToken)
    {
        using var file = OpenRead(path);
        using var decompressor = OpenCompressedStream(file, NormalizeCompressionExtension(extension));
        using var bounded = new CountingReadStream(
            decompressor,
            64L * 1024 * 1024,
            "Compressed TAR exceeds the 64 MiB decompressed scan budget.");
        using var reader = new TarReader(bounded, leaveOpen: false);
        var builder = new StringBuilder()
            .Append(extension.TrimStart('.').ToUpperInvariant())
            .AppendLine(" TAR archive")
            .AppendLine();
        var count = 0;
        var truncated = false;
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            builder.Append(FormatSize(entry.Length).PadLeft(11))
                .Append("  ")
                .AppendLine(TruncateName(entry.Name));
            if (count >= MaximumEntries || builder.Length >= MaximumOutputCharacters)
            {
                truncated = true;
                break;
            }
        }

        builder.Insert(
            builder.ToString().IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length,
            $"Entries shown: {count}{(truncated ? "+" : string.Empty)}{Environment.NewLine}" +
            $"Decompressed scan budget: 64 MiB{Environment.NewLine}");
        if (truncated)
        {
            builder.AppendLine("… archive listing truncated");
        }
        return builder.ToString();
    }

    private static Stream OpenCompressedStream(Stream stream, string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".br" => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: false),
            ".bz2" => BZip2Stream.Create(
                stream, SharpCompress.Compressors.CompressionMode.Decompress,
                decompressConcatenated: false, leaveOpen: false, tolerateTruncatedStream: false),
            ".xz" => new XZStream(stream),
            ".zst" or ".zstd" => new DecompressionStream(stream, 64 * 1024, true, false),
            ".lz" or ".lzip" => LZipStream.Create(
                stream, SharpCompress.Compressors.CompressionMode.Decompress, leaveOpen: false),
            ".lz4" => LZ4Stream.Decode(
                new CountingReadStream(
                    stream,
                    16L * 1024 * 1024,
                    "LZ4 frame exceeds the 16 MiB compressed-input budget."),
                leaveOpen: false),
            ".z" => new LzwStream(stream),
            _ => throw new NotSupportedException($"Unsupported compressed stream: {extension}")
        };

    private static bool IsCompressedTar(string name) =>
        name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.br", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.zstd", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.lzip", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.lz4", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.lz", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.z", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCompressionExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".tbr" => ".br",
            ".taz" or ".tz" => ".z",
            ".tb2" or ".tbz" or ".tbz2" or ".tz2" => ".bz2",
            ".tlz" => ".lz",
            ".tlz4" => ".lz4",
            ".txz" => ".xz",
            ".tzst" => ".zst",
            ".tzstd" => ".zstd",
            _ => extension
        };

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

    private static string TruncateName(string name) =>
        name.Length <= 512 ? name : name[..509] + "…";

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + Math.Max(0, right);

    private sealed class CountingReadStream(
        Stream inner,
        long maximumBytes,
        string budgetError) : Stream
    {
        private long _bytesRead;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, Limit(count)));

        public override int Read(Span<byte> buffer) =>
            Count(inner.Read(buffer[..Limit(buffer.Length)]));

        public override int ReadByte()
        {
            EnsureBudget(1);
            var value = inner.ReadByte();
            if (value >= 0)
            {
                _bytesRead++;
            }
            return value;
        }

        private int Limit(int requested)
        {
            if (requested == 0)
            {
                return 0;
            }
            if (_bytesRead == maximumBytes)
            {
                if (inner.ReadByte() < 0)
                {
                    return 0;
                }
                throw new InvalidDataException(budgetError);
            }
            EnsureBudget(1);
            return checked((int)Math.Min(requested, maximumBytes - _bytesRead));
        }

        private int Count(int count)
        {
            _bytesRead += count;
            return count;
        }

        private void EnsureBudget(int requested)
        {
            if (_bytesRead + requested > maximumBytes)
            {
                throw new InvalidDataException(budgetError);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class DocumentPreviewProvider : IFilePreviewProvider
{
    private const int MaximumOutputCharacters = 120_000;
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Document);

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

        if (extension is ".xls" or ".xlt")
        {
            return LegacySpreadsheetPreviewReader.Read(context, cancellationToken);
        }

        if (IsPackagedDocument(extension))
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
        ZipPreviewBudget.ValidatePackage(stream);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        IEnumerable<ZipArchiveEntry> entries = extension switch
        {
            ".docm" or ".docx" or ".dotm" or ".dotx" => archive.Entries.Where(entry =>
                entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)),
            ".xlsm" or ".xlsx" or ".xltm" or ".xltx" => archive.Entries.Where(entry =>
                entry.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)),
            ".potm" or ".potx" or ".ppsm" or ".ppsx" or ".pptm" or ".pptx" => archive.Entries.Where(entry =>
                entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)),
            ".odt" or ".ods" or ".odp" or ".odg" => archive.Entries.Where(entry =>
                entry.FullName.Equals("content.xml", StringComparison.OrdinalIgnoreCase)),
            ".vsdx" => archive.Entries.Where(entry =>
                entry.FullName.StartsWith("visio/pages/page", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)),
            ".epub" => archive.Entries.Where(entry =>
                entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)),
            ".xps" or ".oxps" => archive.Entries.Where(entry =>
                entry.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase)),
            _ => []
        };

        var builder = new StringBuilder();
        long totalEntryBytes = 0;
        foreach (var entry in entries.Take(32).OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > 8 * 1024 * 1024)
            {
                continue;
            }
            totalEntryBytes = checked(totalEntryBytes + entry.Length);
            if (totalEntryBytes > 16 * 1024 * 1024)
            {
                builder.AppendLine("… package extraction stopped at the 16 MiB decompressed budget");
                break;
            }

            if (extension is ".potm" or ".potx" or ".ppsm" or ".ppsx" or ".pptm" or ".pptx" or
                ".xlsm" or ".xlsx" or ".xltm" or ".xltx" or ".epub")
            {
                builder.AppendLine($"— {Path.GetFileNameWithoutExtension(entry.Name)} —");
            }

            ExtractXmlText(entry, builder, cancellationToken, extractGlyphText: extension is ".xps" or ".oxps");
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
        CancellationToken cancellationToken,
        bool extractGlyphText = false)
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
            if (extractGlyphText && reader.NodeType == XmlNodeType.Element &&
                reader.LocalName.Equals("Glyphs", StringComparison.OrdinalIgnoreCase))
            {
                var value = reader.GetAttribute("UnicodeString")?.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    AppendBounded(builder, value);
                    builder.AppendLine();
                }
            }
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                var value = reader.Value.Trim();
                if (value.Length > 0)
                {
                    AppendBounded(builder, value);
                    if (builder.Length < MaximumOutputCharacters)
                    {
                        builder.Append(' ');
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName is "p" or "tr" or "row" or "si")
            {
                builder.AppendLine();
            }
        }
    }

    private static bool IsPackagedDocument(string extension) =>
        extension is ".docm" or ".docx" or ".dotm" or ".dotx" or
            ".xlsm" or ".xlsx" or ".xltm" or ".xltx" or
            ".potm" or ".potx" or ".ppsm" or ".ppsx" or ".pptm" or ".pptx" or
            ".odt" or ".ods" or ".odp" or ".odg" or ".epub" or ".vsdx" or ".xps" or ".oxps";

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

    private static void AppendBounded(StringBuilder builder, string value)
    {
        var remaining = MaximumOutputCharacters - builder.Length;
        if (remaining > 0)
        {
            builder.Append(value, 0, Math.Min(value.Length, remaining));
        }
    }
}

internal static class ZipPreviewBudget
{
    private const int MaximumEntries = 4096;
    private const int MaximumCentralDirectoryBytes = 4 * 1024 * 1024;
    private const int MaximumEndSearchBytes = ushort.MaxValue + 22;

    internal static void ValidatePackage(FileStream stream)
    {
        if (!stream.CanSeek || stream.Length < 22)
        {
            throw new InvalidDataException("The ZIP package is truncated.");
        }

        var searchLength = checked((int)Math.Min(stream.Length, MaximumEndSearchBytes));
        var tail = new byte[searchLength];
        stream.Position = stream.Length - searchLength;
        stream.ReadExactly(tail);
        var eocd = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054B50)
            {
                eocd = index;
                break;
            }
        }
        if (eocd < 0)
        {
            throw new InvalidDataException("The ZIP package has no end-of-central-directory record.");
        }

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
        if (entryCount == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            throw new InvalidDataException("ZIP64 packages require the Windows preview handler.");
        }
        if (entryCount > MaximumEntries || directorySize > MaximumCentralDirectoryBytes ||
            (long)directoryOffset + directorySize > stream.Length)
        {
            throw new InvalidDataException("The ZIP package exceeds preview directory budgets.");
        }
        stream.Position = 0;
    }
}

internal sealed class PdfPreviewProvider : IFilePreviewProvider
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Pdf);

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
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Media);

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
        else if (context.Extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".oga", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".opus", StringComparison.OrdinalIgnoreCase))
        {
            AppendOggMetadata(builder, stream, buffer.AsSpan(0, count), cancellationToken);
        }
        else if (context.Extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase))
        {
            AppendAiffMetadata(builder, buffer.AsSpan(0, count));
        }
        else if (context.Extension.Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".midi", StringComparison.OrdinalIgnoreCase))
        {
            AppendMidiMetadata(builder, buffer.AsSpan(0, count));
        }
        else if (context.Extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".adts", StringComparison.OrdinalIgnoreCase))
        {
            AppendAacMetadata(builder, buffer.AsSpan(0, count));
        }
        else if (context.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".3gp", StringComparison.OrdinalIgnoreCase))
        {
            AppendIsoBmffMetadata(builder, stream, cancellationToken);
        }
        else if (context.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                 context.Extension.Equals(".mka", StringComparison.OrdinalIgnoreCase))
        {
            EbmlMediaPreviewReader.AppendMetadata(builder, stream, cancellationToken);
        }

        builder.AppendLine().AppendLine("Playback uses installed Windows media codecs.");
        return PreviewContent.ForMedia("Built-in media player", context.FullPath, builder.ToString());
    }

    private static void AppendOggMetadata(
        StringBuilder builder,
        Stream stream,
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        var packets = ReadOggPackets(bytes, cancellationToken);
        if (packets.Count == 0)
        {
            return;
        }
        uint sampleRate = 0;
        ushort preSkip = 0;
        if (packets[0].AsSpan().StartsWith("OpusHead"u8) && packets[0].Length >= 19)
        {
            var header = packets[0].AsSpan();
            var channels = header[9];
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            sampleRate = 48_000;
            builder.Append("Codec: Opus").AppendLine()
                .Append("Audio: ").Append(channels).Append(" channel(s), 48000 Hz").AppendLine()
                .Append("Pre-skip: ").Append(preSkip).AppendLine(" samples");
            if (packets.Count > 1 && packets[1].AsSpan().StartsWith("OpusTags"u8))
            {
                AppendOggComments(builder, packets[1].AsSpan(8));
            }
        }
        else if (packets[0].Length >= 30 &&
                 packets[0][0] == 1 &&
                 packets[0].AsSpan(1).StartsWith("vorbis"u8))
        {
            var header = packets[0].AsSpan();
            var channels = header[11];
            sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            builder.AppendLine("Codec: Vorbis")
                .Append("Audio: ").Append(channels).Append(" channel(s), ")
                .Append(sampleRate).AppendLine(" Hz");
            if (packets.Count > 1 && packets[1].Length >= 7 &&
                packets[1][0] == 3 && packets[1].AsSpan(1).StartsWith("vorbis"u8))
            {
                AppendOggComments(builder, packets[1].AsSpan(7));
            }
        }
        if (sampleRate == 0)
        {
            return;
        }
        var tailLength = checked((int)Math.Min(stream.Length, 256 * 1024));
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadLastOggGranule(tail, out var granule) && granule >= preSkip)
        {
            var seconds = (double)(granule - preSkip) / sampleRate;
            if (double.IsFinite(seconds) && seconds >= 0)
            {
                builder.Append("Duration: ").AppendLine(
                    TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff"));
            }
        }
    }

    private static List<byte[]> ReadOggPackets(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        const int maximumPages = 64;
        const int maximumPackets = 4;
        const int maximumPacketBytes = 64 * 1024;
        var packets = new List<byte[]>(maximumPackets);
        using var packet = new MemoryStream();
        var offset = 0;
        var pages = 0;
        while (offset + 27 <= bytes.Length && pages++ < maximumPages && packets.Count < maximumPackets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bytes[offset..].StartsWith("OggS"u8) || bytes[offset + 4] != 0)
            {
                break;
            }
            var segments = bytes[offset + 26];
            var tableOffset = offset + 27;
            var dataOffset = tableOffset + segments;
            if (dataOffset > bytes.Length)
            {
                break;
            }
            var pageDataLength = 0;
            for (var index = 0; index < segments; index++)
            {
                pageDataLength += bytes[tableOffset + index];
            }
            if (dataOffset + pageDataLength > bytes.Length)
            {
                break;
            }
            var cursor = dataOffset;
            for (var index = 0; index < segments && packets.Count < maximumPackets; index++)
            {
                var length = bytes[tableOffset + index];
                if (packet.Length + length > maximumPacketBytes)
                {
                    return packets;
                }
                packet.Write(bytes.Slice(cursor, length));
                cursor += length;
                if (length < 255)
                {
                    packets.Add(packet.ToArray());
                    packet.SetLength(0);
                }
            }
            offset = dataOffset + pageDataLength;
        }
        return packets;
    }

    private static void AppendOggComments(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        const int maximumComments = 128;
        const int maximumCommentBytes = 64 * 1024;
        if (bytes.Length < 8)
        {
            return;
        }
        var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (vendorLength > bytes.Length - 8)
        {
            return;
        }
        var offset = checked(4 + (int)vendorLength);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        offset += 4;
        var consumed = 0;
        for (var index = 0u; index < Math.Min(count, (uint)maximumComments); index++)
        {
            if (offset + 4 > bytes.Length)
            {
                break;
            }
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            offset += 4;
            if (length > 4096 || length > bytes.Length - offset ||
                length > maximumCommentBytes ||
                consumed > maximumCommentBytes - (int)length)
            {
                break;
            }
            var comment = Encoding.UTF8.GetString(bytes.Slice(offset, checked((int)length)));
            offset += checked((int)length);
            consumed += checked((int)length);
            var equals = comment.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var name = comment[..equals];
            if (name.Equals("TITLE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ARTIST", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ALBUM", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(char.ToUpperInvariant(name[0]))
                    .Append(name[1..].ToLowerInvariant()).Append(": ")
                    .AppendLine(comment[(equals + 1)..]);
            }
        }
    }

    private static bool TryReadLastOggGranule(ReadOnlySpan<byte> bytes, out ulong granule)
    {
        granule = 0;
        for (var offset = bytes.Length - 27; offset >= 0; offset--)
        {
            if (bytes[offset..].StartsWith("OggS"u8) && bytes[offset + 4] == 0)
            {
                var segments = bytes[offset + 26];
                if (offset + 27 + segments > bytes.Length)
                {
                    continue;
                }
                var dataLength = 0;
                for (var index = 0; index < segments; index++)
                {
                    dataLength += bytes[offset + 27 + index];
                }
                if (offset + 27 + segments + dataLength > bytes.Length)
                {
                    continue;
                }
                granule = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 6)..]);
                return granule != ulong.MaxValue;
            }
        }
        return false;
    }

    private static void AppendAiffMetadata(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("FORM"u8) ||
            (!bytes[8..12].SequenceEqual("AIFF"u8) && !bytes[8..12].SequenceEqual("AIFC"u8)))
        {
            return;
        }
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]);
            var dataOffset = offset + 8;
            if (bytes[offset..].StartsWith("COMM"u8) && size >= 18 && dataOffset + 18 <= bytes.Length)
            {
                var channels = BinaryPrimitives.ReadUInt16BigEndian(bytes[dataOffset..]);
                var frames = BinaryPrimitives.ReadUInt32BigEndian(bytes[(dataOffset + 2)..]);
                var bits = BinaryPrimitives.ReadUInt16BigEndian(bytes[(dataOffset + 6)..]);
                var sampleRate = DecodeExtended80(bytes.Slice(dataOffset + 8, 10));
                builder.Append("Audio: ").Append(channels).Append(" channel(s), ")
                    .Append(sampleRate.ToString("0.##")).Append(" Hz, ").Append(bits).AppendLine(" bit");
                if (sampleRate > 0 && frames > 0)
                {
                    builder.Append("Duration: ").AppendLine(
                        TimeSpan.FromSeconds(frames / sampleRate).ToString(@"hh\:mm\:ss\.fff"));
                }
                if (bytes[8..12].SequenceEqual("AIFC"u8) && size >= 22 && dataOffset + 22 <= bytes.Length)
                {
                    builder.Append("Compression: ")
                        .AppendLine(ReadFourCc(bytes.Slice(dataOffset + 18, 4)));
                }
                return;
            }
            var next = (long)dataOffset + size + (size & 1);
            if (next > bytes.Length || next > int.MaxValue)
            {
                break;
            }
            offset = (int)next;
        }
    }

    private static double DecodeExtended80(ReadOnlySpan<byte> bytes)
    {
        var exponent = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        var negative = (exponent & 0x8000) != 0;
        exponent &= 0x7FFF;
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0 || exponent == 0x7FFF || mantissa == 0)
        {
            return 0;
        }
        var value = Math.ScaleB((double)mantissa, exponent - 16383 - 63);
        return negative ? -value : value;
    }

    private static void AppendMidiMetadata(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 || !bytes[..4].SequenceEqual("MThd"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) < 6)
        {
            return;
        }
        var format = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]);
        var tracks = BinaryPrimitives.ReadUInt16BigEndian(bytes[10..]);
        var division = BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]);
        builder.Append("MIDI format: ").AppendLine(format.ToString())
            .Append("Tracks: ").AppendLine(tracks.ToString());
        if ((division & 0x8000) == 0)
        {
            builder.Append("Timing: ").Append(division).AppendLine(" ticks per quarter note");
        }
        else
        {
            var framesPerSecond = -(sbyte)(division >> 8);
            var ticksPerFrame = division & 0xFF;
            builder.Append("Timing: ").Append(framesPerSecond).Append(" frames/s, ")
                .Append(ticksPerFrame).AppendLine(" ticks/frame");
        }
    }

    private static void AppendAacMetadata(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<int> sampleRates =
            [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        if (bytes.Length < 7 || bytes[0] != 0xFF || (bytes[1] & 0xF6) != 0xF0)
        {
            return;
        }
        var profile = ((bytes[2] >> 6) & 0x03) + 1;
        var frequencyIndex = (bytes[2] >> 2) & 0x0F;
        if (frequencyIndex >= sampleRates.Length)
        {
            return;
        }
        var channels = ((bytes[2] & 0x01) << 2) | (bytes[3] >> 6);
        var frameLength = ((bytes[3] & 0x03) << 11) | (bytes[4] << 3) | (bytes[5] >> 5);
        if (frameLength < 7)
        {
            return;
        }
        builder.Append("Codec: AAC profile ").AppendLine(profile.ToString())
            .Append("Audio: ").Append(channels).Append(" channel(s), ")
            .Append(sampleRates[frequencyIndex]).AppendLine(" Hz")
            .Append("First ADTS frame: ").Append(frameLength).AppendLine(" bytes");
    }

    private static void AppendIsoBmffMetadata(
        StringBuilder builder,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var budget = new IsoBmffBudget(cancellationToken);
        var topLevel = ReadIsoAtoms(stream, 0, stream.Length, budget);
        var fileType = topLevel.FirstOrDefault(atom => atom.Type == "ftyp");
        if (fileType.Size > 0)
        {
            var data = budget.Read(stream, fileType.DataOffset,
                checked((int)Math.Min(fileType.DataLength, 128)));
            if (data.Length >= 8)
            {
                builder.Append("Major brand: ").AppendLine(ReadFourCc(data))
                    .Append("Brand version: ")
                    .AppendLine(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)).ToString());
                var compatible = new List<string>();
                for (var offset = 8; offset + 4 <= data.Length && compatible.Count < 16; offset += 4)
                {
                    compatible.Add(ReadFourCc(data.AsSpan(offset, 4)));
                }
                if (compatible.Count > 0)
                {
                    builder.Append("Compatible brands: ")
                        .AppendLine(string.Join(", ", compatible.Distinct(StringComparer.Ordinal)));
                }
            }
        }

        var movie = topLevel.FirstOrDefault(atom => atom.Type == "moov");
        if (movie.Size == 0)
        {
            return;
        }
        var movieAtoms = ReadIsoAtoms(stream, movie.DataOffset, movie.EndOffset, budget);
        var movieHeader = movieAtoms.FirstOrDefault(atom => atom.Type == "mvhd");
        if (movieHeader.Size > 0)
        {
            var header = budget.Read(stream, movieHeader.DataOffset,
                checked((int)Math.Min(movieHeader.DataLength, 40)));
            if (TryReadIsoDuration(header, out var duration))
            {
                builder.Append("Duration: ").AppendLine(
                    TimeSpan.FromSeconds(duration).ToString(@"hh\:mm\:ss\.fff"));
            }
        }

        var trackIndex = 0;
        foreach (var track in movieAtoms.Where(atom => atom.Type == "trak").Take(32))
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            var metadata = ReadIsoTrack(stream, track, budget);
            if (metadata is null)
            {
                continue;
            }
            builder.Append("Track ").Append(++trackIndex).Append(": ")
                .Append(metadata.Handler switch
                {
                    "vide" => "video",
                    "soun" => "audio",
                    "text" or "sbtl" or "subt" => "subtitle",
                    _ => metadata.Handler
                });
            if (!string.IsNullOrWhiteSpace(metadata.Codec))
            {
                builder.Append(", codec ").Append(metadata.Codec);
            }
            if (metadata.Width > 0 && metadata.Height > 0)
            {
                builder.Append(", ").Append(metadata.Width).Append('×').Append(metadata.Height);
            }
            if (metadata.Channels > 0)
            {
                builder.Append(", ").Append(metadata.Channels).Append(" channel(s)");
            }
            if (metadata.SampleRate > 0)
            {
                builder.Append(", ").Append(metadata.SampleRate).Append(" Hz");
            }
            builder.AppendLine();
        }
    }

    private static IsoTrackMetadata? ReadIsoTrack(Stream stream, IsoAtom track, IsoBmffBudget budget)
    {
        var trackAtoms = ReadIsoAtoms(stream, track.DataOffset, track.EndOffset, budget);
        var metadata = new IsoTrackMetadata();
        var trackHeader = trackAtoms.FirstOrDefault(atom => atom.Type == "tkhd");
        if (trackHeader.DataLength >= 8)
        {
            var dimensions = budget.Read(stream, trackHeader.EndOffset - 8, 8);
            metadata.Width = checked((int)(BinaryPrimitives.ReadUInt32BigEndian(dimensions) >> 16));
            metadata.Height = checked((int)(BinaryPrimitives.ReadUInt32BigEndian(dimensions.AsSpan(4)) >> 16));
        }
        var media = trackAtoms.FirstOrDefault(atom => atom.Type == "mdia");
        if (media.Size == 0)
        {
            return null;
        }
        var mediaAtoms = ReadIsoAtoms(stream, media.DataOffset, media.EndOffset, budget);
        var handler = mediaAtoms.FirstOrDefault(atom => atom.Type == "hdlr");
        if (handler.DataLength >= 12)
        {
            var handlerBytes = budget.Read(stream, handler.DataOffset, 12);
            metadata.Handler = ReadFourCc(handlerBytes.AsSpan(8, 4));
        }
        var mediaInfo = mediaAtoms.FirstOrDefault(atom => atom.Type == "minf");
        if (mediaInfo.Size > 0)
        {
            var infoAtoms = ReadIsoAtoms(stream, mediaInfo.DataOffset, mediaInfo.EndOffset, budget);
            var sampleTable = infoAtoms.FirstOrDefault(atom => atom.Type == "stbl");
            if (sampleTable.Size > 0)
            {
                var tableAtoms = ReadIsoAtoms(stream, sampleTable.DataOffset, sampleTable.EndOffset, budget);
                var description = tableAtoms.FirstOrDefault(atom => atom.Type == "stsd");
                ReadIsoSampleDescription(stream, description, metadata, budget);
            }
        }
        return string.IsNullOrWhiteSpace(metadata.Handler) ? null : metadata;
    }

    private static void ReadIsoSampleDescription(
        Stream stream,
        IsoAtom description,
        IsoTrackMetadata metadata,
        IsoBmffBudget budget)
    {
        if (description.DataLength < 16)
        {
            return;
        }
        var prefix = budget.Read(stream, description.DataOffset,
            checked((int)Math.Min(description.DataLength, 64)));
        if (prefix.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4)) == 0)
        {
            return;
        }
        var entrySize = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(8));
        if (entrySize < 16 || entrySize > description.DataLength - 8)
        {
            return;
        }
        metadata.Codec = ReadFourCc(prefix.AsSpan(12, 4));
        if (metadata.Handler == "vide" && prefix.Length >= 44)
        {
            metadata.Width = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(40));
            metadata.Height = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(42));
        }
        else if (metadata.Handler == "soun" && prefix.Length >= 44)
        {
            metadata.Channels = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(32));
            metadata.SampleRate = checked((int)(BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(40)) >> 16));
        }
    }

    private static bool TryReadIsoDuration(ReadOnlySpan<byte> bytes, out double duration)
    {
        duration = 0;
        if (bytes.Length < 20)
        {
            return false;
        }
        uint timeScale;
        ulong units;
        if (bytes[0] == 1)
        {
            if (bytes.Length < 32)
            {
                return false;
            }
            timeScale = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            units = BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]);
        }
        else
        {
            timeScale = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
            units = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        }
        if (timeScale == 0 || units == ulong.MaxValue)
        {
            return false;
        }
        duration = (double)units / timeScale;
        return double.IsFinite(duration) && duration >= 0;
    }

    private static IReadOnlyList<IsoAtom> ReadIsoAtoms(
        Stream stream,
        long start,
        long end,
        IsoBmffBudget budget)
    {
        var atoms = new List<IsoAtom>();
        var offset = start;
        while (offset < end && atoms.Count < 4096)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (end - offset < 8)
            {
                break;
            }
            var header = budget.Read(stream, offset, checked((int)Math.Min(16, end - offset)));
            var shortSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = ReadFourCc(header.AsSpan(4, 4));
            var headerLength = 8;
            ulong size = shortSize;
            if (shortSize == 1)
            {
                if (header.Length < 16)
                {
                    break;
                }
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerLength = 16;
            }
            else if (shortSize == 0)
            {
                size = checked((ulong)(end - offset));
            }
            if (size < (ulong)headerLength || size > long.MaxValue)
            {
                break;
            }
            var atomEnd = offset + (long)size;
            if (atomEnd < offset || atomEnd > end)
            {
                break;
            }
            atoms.Add(new IsoAtom(type, offset, atomEnd, headerLength));
            budget.CountAtom();
            offset = atomEnd;
        }
        return atoms;
    }

    private static string ReadFourCc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return "????";
        }
        Span<char> value = stackalloc char[4];
        for (var index = 0; index < value.Length; index++)
        {
            value[index] = bytes[index] is >= 0x20 and <= 0x7E ? (char)bytes[index] : '?';
        }
        return new string(value);
    }

    private readonly record struct IsoAtom(string Type, long Offset, long EndOffset, int HeaderLength)
    {
        internal long Size => EndOffset - Offset;
        internal long DataOffset => Offset + HeaderLength;
        internal long DataLength => EndOffset - DataOffset;
    }

    private sealed class IsoTrackMetadata
    {
        internal string Handler { get; set; } = string.Empty;
        internal string? Codec { get; set; }
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal int Channels { get; set; }
        internal int SampleRate { get; set; }
    }

    private sealed class IsoBmffBudget(CancellationToken cancellationToken)
    {
        private const int MaximumBytesRead = 1024 * 1024;
        private const int MaximumAtoms = 4096;
        private int _bytesRead;
        private int _atoms;
        internal CancellationToken CancellationToken => cancellationToken;

        internal byte[] Read(Stream stream, long offset, int requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < 0 || requested < 0 || offset > stream.Length)
            {
                throw new InvalidDataException("Invalid ISO-BMFF range.");
            }
            var count = checked((int)Math.Min(requested, stream.Length - offset));
            if (_bytesRead > MaximumBytesRead - count)
            {
                throw new InvalidDataException("ISO-BMFF metadata exceeds the 1 MiB read budget.");
            }
            var bytes = new byte[count];
            stream.Position = offset;
            stream.ReadExactly(bytes);
            _bytesRead += count;
            return bytes;
        }

        internal void CountAtom()
        {
            if (++_atoms > MaximumAtoms)
            {
                throw new InvalidDataException("ISO-BMFF metadata exceeds the atom-count budget.");
            }
        }
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
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Executable);

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
