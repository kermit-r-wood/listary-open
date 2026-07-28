using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Claunia.PropertyList;

namespace ListaryOpen.App.Previewing;

internal sealed class WebArchivePreviewProvider : IFilePreviewProvider
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumMainResourceBytes = 1024 * 1024;
    private const int MaximumOutputCharacters = 120 * 1024;
    private const ulong MaximumBinaryObjects = 10_000;
    private const int MaximumBinaryDepth = 32;
    private const int MaximumBinaryReferenceVisits = 50_000;
    private const int MaximumMetadataCharacters = 4096;

    static WebArchivePreviewProvider() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.WebArchive);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        if (context.SizeBytes <= 0 || context.SizeBytes > MaximumFileBytes)
        {
            return PreviewContent.ForText(
                "WebArchive metadata",
                $"Safari WebArchive{Environment.NewLine}{Environment.NewLine}" +
                $"Size: {FilePreviewPane.FormatSize(context.SizeBytes)}{Environment.NewLine}" +
                "Main-resource extraction is limited to 4.0 MB files.");
        }
        using var stream = new FileStream(
            context.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
        {
            return PreviewContent.ForText(
                "WebArchive metadata",
                $"Safari WebArchive{Environment.NewLine}{Environment.NewLine}" +
                $"Size: {FilePreviewPane.FormatSize(stream.Length)}{Environment.NewLine}" +
                "Main-resource extraction is limited to 4.0 MB files.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = IsBinaryPlist(bytes)
            ? ParseBinary(bytes)
            : ParseXml(bytes);
        if (parsed is null)
        {
            return null;
        }
        var (url, mime, encodingName, resourceBytes) = parsed.Value;
        var builder = new StringBuilder("Safari WebArchive")
            .AppendLine().AppendLine();
        Append(builder, "URL", url);
        Append(builder, "MIME type", mime);
        Append(builder, "Text encoding", encodingName);

        if (resourceBytes is { Length: <= MaximumMainResourceBytes } &&
            IsTextMimeType(mime))
        {
            var source = Decode(resourceBytes, encodingName);
            if (!string.IsNullOrWhiteSpace(source))
            {
                var visible = mime?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                    ? TextPreviewProvider.ConvertHtmlToText(source)
                    : source;
                builder.AppendLine().AppendLine(Bound(visible));
            }
        }
        else
        {
            builder.AppendLine()
                .AppendLine("The main resource is non-textual, missing, or exceeds the 1.0 MB preview budget.");
        }
        builder.AppendLine()
            .AppendLine("Subresources, scripts, images, links, and external URLs were not loaded.");
        if (builder.Length > MaximumOutputCharacters)
        {
            builder.Length = MaximumOutputCharacters;
        }
        return PreviewContent.ForText("Safe WebArchive", builder.ToString());
    }

    private static ParsedWebArchive? ParseBinary(byte[] bytes)
    {
        ValidateBinaryPlist(bytes);
        if (PropertyListParser.Parse(bytes) is not NSDictionary root ||
            root.ObjectForKey("WebMainResource") is not NSDictionary main)
        {
            return null;
        }
        return new ParsedWebArchive(
            ReadString(main, "WebResourceURL"),
            ReadString(main, "WebResourceMIMEType"),
            ReadString(main, "WebResourceTextEncodingName"),
            main.ObjectForKey("WebResourceData") is NSData data ? data.Bytes : null);
    }

    private static ParsedWebArchive? ParseXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        var rootDictionaryDepth = -1;
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element &&
                reader.LocalName == "dict" &&
                rootDictionaryDepth < 0)
            {
                rootDictionaryDepth = reader.Depth;
                reader.Read();
                continue;
            }
            if (rootDictionaryDepth >= 0 &&
                reader.NodeType == XmlNodeType.Element &&
                reader.LocalName == "key" &&
                reader.Depth == rootDictionaryDepth + 1)
            {
                var key = reader.ReadElementContentAsString();
                reader.MoveToContent();
                if (key == "WebMainResource" &&
                    reader.NodeType == XmlNodeType.Element &&
                    reader.LocalName == "dict")
                {
                    using var subtree = reader.ReadSubtree();
                    var dictionary = XElement.Load(subtree, LoadOptions.None);
                    return ParseMainResourceXml(dictionary);
                }
                if (reader.NodeType == XmlNodeType.Element)
                {
                    reader.Skip();
                }
                continue;
            }
            reader.Read();
        }
        return null;
    }

    private static ParsedWebArchive ParseMainResourceXml(XElement dictionary)
    {
        string? url = null;
        string? mime = null;
        string? encoding = null;
        byte[]? data = null;
        var elements = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < elements.Length; index++)
        {
            if (elements[index].Name.LocalName != "key")
            {
                continue;
            }
            var key = elements[index].Value;
            var value = elements[++index];
            if (value.Name.LocalName == "string")
            {
                switch (key)
                {
                    case "WebResourceURL":
                        url = value.Value;
                        break;
                    case "WebResourceMIMEType":
                        mime = value.Value;
                        break;
                    case "WebResourceTextEncodingName":
                        encoding = value.Value;
                        break;
                }
            }
            else if (key == "WebResourceData" && value.Name.LocalName == "data")
            {
                var encoded = value.Value;
                if (encoded.Length <= ((MaximumMainResourceBytes + 2) / 3 * 4) + 4096)
                {
                    try
                    {
                        data = Convert.FromBase64String(encoded);
                    }
                    catch (FormatException)
                    {
                        data = null;
                    }
                }
            }
        }
        return new ParsedWebArchive(url, mime, encoding, data);
    }

    private static bool IsBinaryPlist(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("bplist00"u8);

    private static void ValidateBinaryPlist(byte[] bytes)
    {
        if (bytes.Length < 40 || !IsBinaryPlist(bytes))
        {
            throw new InvalidDataException("Invalid binary plist header or trailer.");
        }
        var trailer = bytes.AsSpan(bytes.Length - 32);
        var offsetSize = trailer[6];
        var referenceSize = trailer[7];
        var objectCount = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..16]);
        var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..24]);
        var offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..32]);
        if (offsetSize is < 1 or > 8 || referenceSize is < 1 or > 8 ||
            objectCount is 0 or > MaximumBinaryObjects || topObject >= objectCount ||
            offsetTableOffset > (ulong)bytes.Length ||
            objectCount > ((ulong)bytes.Length - offsetTableOffset) / offsetSize)
        {
            throw new InvalidDataException("Binary plist exceeds its structural budget.");
        }
        var offsets = new ulong[checked((int)objectCount)];
        for (var index = 0; index < offsets.Length; index++)
        {
            offsets[index] = ReadUnsigned(
                bytes.AsSpan(checked((int)(offsetTableOffset + (ulong)index * offsetSize)), offsetSize));
            if (offsets[index] < 8 || offsets[index] >= offsetTableOffset)
            {
                throw new InvalidDataException("Binary plist contains an invalid object offset.");
            }
        }
        var visits = 0;
        var active = new HashSet<ulong>();
        ValidateObject(topObject, 0);
        return;

        void ValidateObject(ulong objectIndex, int depth)
        {
            if (++visits > MaximumBinaryReferenceVisits || depth > MaximumBinaryDepth)
            {
                throw new InvalidDataException("Binary plist object graph exceeds its preview budget.");
            }
            if (!active.Add(objectIndex))
            {
                throw new InvalidDataException("Binary plist contains a reference cycle.");
            }
            try
            {
                var offset = offsets[checked((int)objectIndex)];
                var marker = bytes[checked((int)offset)];
                var kind = marker >> 4;
                var cursor = offset + 1;
                var count = ReadObjectCount(marker, ref cursor);
                ulong referenceCount = kind switch
                {
                    0xA or 0xC => count,
                    0xD => checked(count * 2),
                    _ => 0
                };
                if (referenceCount == 0)
                {
                    ValidateScalar(kind, count, cursor);
                    return;
                }
                if (referenceCount > MaximumBinaryReferenceVisits ||
                    cursor > offsetTableOffset ||
                    referenceCount > (offsetTableOffset - cursor) / referenceSize)
                {
                    throw new InvalidDataException("Binary plist reference table is invalid.");
                }
                for (ulong index = 0; index < referenceCount; index++)
                {
                    var child = ReadUnsigned(bytes.AsSpan(
                        checked((int)(cursor + index * referenceSize)), referenceSize));
                    if (child >= objectCount)
                    {
                        throw new InvalidDataException("Binary plist reference is out of range.");
                    }
                    ValidateObject(child, depth + 1);
                }
            }
            finally
            {
                active.Remove(objectIndex);
            }
        }

        ulong ReadObjectCount(byte marker, ref ulong cursor)
        {
            var compact = marker & 0x0F;
            if (compact != 0x0F)
            {
                return (ulong)compact;
            }
            if (cursor >= offsetTableOffset)
            {
                throw new InvalidDataException("Binary plist length object is missing.");
            }
            var lengthMarker = bytes[checked((int)cursor++)];
            if ((lengthMarker >> 4) != 0x1 || (lengthMarker & 0x0F) > 3)
            {
                throw new InvalidDataException("Binary plist length object is invalid.");
            }
            var lengthBytes = 1 << (lengthMarker & 0x0F);
            if (cursor > offsetTableOffset || (ulong)lengthBytes > offsetTableOffset - cursor)
            {
                throw new InvalidDataException("Binary plist length is truncated.");
            }
            var result = ReadUnsigned(bytes.AsSpan(checked((int)cursor), lengthBytes));
            cursor += (ulong)lengthBytes;
            return result;
        }

        void ValidateScalar(int kind, ulong count, ulong cursor)
        {
            ulong byteCount = kind switch
            {
                0x0 => 0,
                0x1 or 0x2 => checked(1UL << checked((int)count)),
                0x3 => 8,
                0x4 or 0x5 => count,
                0x6 => checked(count * 2),
                0x8 => checked(count + 1),
                _ => throw new InvalidDataException("Binary plist contains an unsupported object type.")
            };
            if (byteCount > (ulong)MaximumFileBytes ||
                cursor > offsetTableOffset || byteCount > offsetTableOffset - cursor)
            {
                throw new InvalidDataException("Binary plist scalar is truncated or oversized.");
            }
        }
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (var item in bytes)
        {
            value = (value << 8) | item;
        }
        return value;
    }

    private static string? ReadString(NSDictionary dictionary, string key) =>
        dictionary.ObjectForKey(key) is NSString value ? value.Content : null;

    private static bool IsTextMimeType(string? mime)
    {
        var mediaType = mime?.Split(';', 2)[0].Trim();
        return mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
            mediaType?.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) == true ||
            mediaType?.Equals("application/xml", StringComparison.OrdinalIgnoreCase) == true ||
            mediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? Decode(byte[] bytes, string? encodingName)
    {
        if (!string.IsNullOrWhiteSpace(encodingName))
        {
            try
            {
                var encoding = Encoding.GetEncoding(
                    encodingName,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
                return encoding.GetString(bytes);
            }
            catch (ArgumentException)
            {
            }
        }
        return PreviewTextReader.Decode(bytes);
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters - 4096
            ? value
            : value[..(MaximumOutputCharacters - 4096)] + Environment.NewLine + "… main resource truncated";

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            value = SanitizeMetadata(value);
            builder.Append(label).Append(": ")
                .AppendLine(value);
        }
    }

    private static string SanitizeMetadata(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        var builder = new StringBuilder(Math.Min(normalized.Length, MaximumMetadataCharacters));
        foreach (var character in normalized)
        {
            if (builder.Length >= MaximumMetadataCharacters)
            {
                break;
            }
            if ((!char.IsControl(character) || character == ' ') &&
                character is not '\u202A' and not '\u202B' and not '\u202C' and
                    not '\u202D' and not '\u202E' and not '\u2066' and
                    not '\u2067' and not '\u2068' and not '\u2069')
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private readonly record struct ParsedWebArchive(
        string? Url,
        string? Mime,
        string? Encoding,
        byte[]? Data);
}
