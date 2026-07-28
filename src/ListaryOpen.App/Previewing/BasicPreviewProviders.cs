using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace ListaryOpen.App.Previewing;

internal sealed class RasterImagePreviewProvider : IFilePreviewProvider
{
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Raster);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        var bitmap = await Task.Run(
            () => LoadBitmap(context.FullPath),
            cancellationToken).ConfigureAwait(false);
        return PreviewContent.ForImage("Built-in image", bitmap);
    }

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1200;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class TextPreviewProvider : IFilePreviewProvider
{
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.CanAttemptText(context.Extension);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        var text = await PreviewTextReader.ReadAsync(context.FullPath, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        var formatted = context.Extension.ToLowerInvariant() switch
        {
            ".htm" or ".html" or ".shtm" or ".shtml" or ".xht" or ".xhtml" => ConvertHtmlToText(text),
            ".md" or ".markdown" => ConvertMarkdownToText(text),
            _ => text
        };
        return PreviewContent.ForText("Built-in text", formatted);
    }

    internal static string ConvertHtmlToText(string html)
    {
        var value = Regex.Replace(
            html,
            @"<(script|style|noscript)\b[^>]*>.*?</\1>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(
            value,
            @"</?(address|article|aside|blockquote|br|div|footer|h[1-6]|header|hr|li|main|p|pre|section|table|tr)\b[^>]*>",
            Environment.NewLine,
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(
            value,
            @"<[^>]+>",
            string.Empty,
            RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(200));
        return NormalizeBlankLines(WebUtility.HtmlDecode(value));
    }

    private static string ConvertMarkdownToText(string markdown)
    {
        var value = Regex.Replace(
            markdown,
            @"```[^\r\n]*\r?\n|```|~~~[^\r\n]*\r?\n|~~~",
            string.Empty,
            RegexOptions.None,
            TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(
            value,
            @"!\[(?<alt>[^]]*)\]\([^)]*\)",
            match => match.Groups["alt"].Value,
            RegexOptions.None,
            TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(
            value,
            @"\[(?<text>[^]]+)\]\([^)]*\)",
            match => match.Groups["text"].Value,
            RegexOptions.None,
            TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(value, @"(?m)^\s{0,3}#{1,6}\s+", string.Empty,
            RegexOptions.None, TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(value, @"(?m)^\s*>\s?", string.Empty,
            RegexOptions.None, TimeSpan.FromMilliseconds(200));
        value = Regex.Replace(value, @"(?<!\w)(\*\*|__|\*|_|~~|`)(?!\s)|(?<!\s)(\*\*|__|\*|_|~~|`)(?!\w)",
            string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return ConvertHtmlToText(value);
    }

    private static string NormalizeBlankLines(string value) =>
        Regex.Replace(
            value.Replace("\r\n", "\n", StringComparison.Ordinal),
            @"[ \t]*\n(?:[ \t]*\n){2,}",
            Environment.NewLine + Environment.NewLine,
            RegexOptions.None,
            TimeSpan.FromMilliseconds(200)).Trim();
}

internal static class PreviewTextReader
{
    internal const int MaximumBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static PreviewTextReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16_384,
            useAsync: true);
        var buffer = new byte[Math.Min(MaximumBytes, checked((int)Math.Min(stream.Length, MaximumBytes)))];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        var text = Decode(buffer.AsSpan(0, count));
        if (text is null)
        {
            return null;
        }

        return stream.Length <= count ? text : text + Environment.NewLine + "…";
    }

    internal static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (LooksLikeUtf16(bytes, littleEndian: true))
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (LooksLikeUtf16(bytes, littleEndian: false))
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        if (IsProbablyBinary(bytes))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(
                936,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback).GetString(bytes);
        }
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        var pairs = Math.Min(bytes.Length / 2, 128);
        var zeroes = 0;
        for (var index = 0; index < pairs; index++)
        {
            var expectedZeroIndex = (index * 2) + (littleEndian ? 1 : 0);
            if (bytes[expectedZeroIndex] == 0)
            {
                zeroes++;
            }
        }

        return zeroes >= pairs * 3 / 4;
    }

    private static bool IsProbablyBinary(ReadOnlySpan<byte> bytes)
    {
        var controls = 0;
        foreach (var value in bytes[..Math.Min(bytes.Length, 4096)])
        {
            if (value == 0)
            {
                return true;
            }

            if (value < 0x20 && value is not (0x09 or 0x0A or 0x0C or 0x0D))
            {
                controls++;
            }
        }

        return controls > Math.Min(bytes.Length, 4096) / 20;
    }
}

internal sealed class SvgPreviewProvider : IFilePreviewProvider
{
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Svg);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        var source = context.Extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase)
            ? await ReadCompressedSvgAsync(context.FullPath, cancellationToken).ConfigureAwait(false)
            : await PreviewTextReader.ReadAsync(context.FullPath, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        using var stringReader = new StringReader(source);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = PreviewTextReader.MaximumBytes
        });
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var elementCount = document.Descendants().Count();
        var unsafeReferenceCount = root.DescendantsAndSelf()
            .Attributes()
            .Count(attribute =>
                attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase) &&
                !attribute.Value.StartsWith('#') &&
                !attribute.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
        var summary = new StringBuilder()
            .AppendLine("Scalable Vector Graphics")
            .AppendLine()
            .Append("Dimensions: ")
            .Append(root.Attribute("width")?.Value ?? "auto")
            .Append(" × ")
            .AppendLine(root.Attribute("height")?.Value ?? "auto")
            .Append("View box: ")
            .AppendLine(root.Attribute("viewBox")?.Value ?? "—")
            .Append("Elements: ")
            .AppendLine(elementCount.ToString())
            .Append("External references blocked: ")
            .AppendLine(unsafeReferenceCount.ToString())
            .AppendLine()
            .AppendLine("Source preview (scripts and external resources are never executed):")
            .Append(source)
            .ToString();
        return PreviewContent.ForText("Safe SVG", summary);
    }

    private static async Task<string?> ReadCompressedSvgAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16_384, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var bytes = new byte[PreviewTextReader.MaximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await gzip.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            count += read;
        }
        var length = Math.Min(count, PreviewTextReader.MaximumBytes);
        var source = PreviewTextReader.Decode(bytes.AsSpan(0, length));
        return source is null || count <= PreviewTextReader.MaximumBytes
            ? source
            : source + Environment.NewLine + "…";
    }
}

internal sealed class FontPreviewProvider : IFilePreviewProvider
{
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Font);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var glyphTypeface = new GlyphTypeface(new Uri(context.FullPath, UriKind.Absolute));
        var familyName = glyphTypeface.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(context.Name);
        var directoryUri = new Uri(Path.GetDirectoryName(context.FullPath) + Path.DirectorySeparatorChar, UriKind.Absolute);
        var fontFamily = new FontFamily(directoryUri, $"./#{familyName}");
        return Task.FromResult<PreviewContent?>(
            PreviewContent.ForFont("Built-in font", fontFamily, familyName));
    }
}
