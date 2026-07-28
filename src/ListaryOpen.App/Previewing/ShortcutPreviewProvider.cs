using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal sealed class ShortcutPreviewProvider : IFilePreviewProvider
{
    private const int MaximumShortcutBytes = 1024 * 1024;
    private const int MaximumUrlBytes = 64 * 1024;
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Shortcut);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        context.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? ReadUrlAsync(context.FullPath, cancellationToken)
            : ReadShellLinkAsync(context.FullPath, cancellationToken);

    private static async Task<PreviewContent?> ReadUrlAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path, useAsync: true);
        var buffer = new byte[Math.Min(stream.Length, MaximumUrlBytes)];
        var count = await stream.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        var text = PreviewTextReader.Decode(buffer.AsSpan(0, count));
        if (text is null)
        {
            return null;
        }

        var inSection = false;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(512))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = rawLine[..Math.Min(rawLine.Length, 8192)].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = inSection ? line.IndexOf('=') : -1;
            if (equals <= 0 || values.Count >= 64)
            {
                continue;
            }

            var key = line[..equals].Trim();
            if (key is "URL" or "IconFile" or "WorkingDirectory" or "HotKey")
            {
                values.TryAdd(key, Sanitize(line[(equals + 1)..]));
            }
        }

        if (!values.TryGetValue("URL", out var url))
        {
            return null;
        }

        var builder = new StringBuilder()
            .AppendLine("Internet shortcut")
            .AppendLine()
            .Append("Target: ").AppendLine(RedactUserInfo(url));
        Append(builder, "Icon", values.GetValueOrDefault("IconFile"));
        Append(builder, "Working directory", values.GetValueOrDefault("WorkingDirectory"));
        Append(builder, "Hot key", values.GetValueOrDefault("HotKey"));
        builder.AppendLine().AppendLine("The target is displayed as text and is never opened.");
        return PreviewContent.ForText("Internet shortcut", builder.ToString());
    }

    private static async Task<PreviewContent?> ReadShellLinkAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path, useAsync: true);
        if (stream.Length is < 76 or > MaximumShortcutBytes)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return ParseShellLink(bytes);
    }

    private static PreviewContent? ParseShellLink(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x4C ||
            new Guid(span.Slice(4, 16)) != ShellLinkClassId)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        var offset = 76;
        if ((flags & 0x1) != 0)
        {
            if (!TryReadUInt16(span, offset, out var idListSize) ||
                !TryAdvance(ref offset, 2 + idListSize, span.Length))
            {
                return null;
            }
        }

        string? linkInfoTarget = null;
        if ((flags & 0x2) != 0)
        {
            if (!TryReadUInt32(span, offset, out var linkInfoSize) ||
                linkInfoSize < 0x1C || linkInfoSize > span.Length - offset)
            {
                return null;
            }

            var linkInfo = span.Slice(offset, checked((int)linkInfoSize));
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.Slice(4, 4));
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.Slice(16, 4));
            var suffixOffset = BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.Slice(24, 4));
            var localUnicodeOffset = headerSize >= 0x24
                ? BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.Slice(28, 4))
                : 0;
            var suffixUnicodeOffset = headerSize >= 0x24
                ? BinaryPrimitives.ReadUInt32LittleEndian(linkInfo.Slice(32, 4))
                : 0;
            var local = ReadLinkInfoString(linkInfo, localUnicodeOffset, localOffset);
            var suffix = ReadLinkInfoString(linkInfo, suffixUnicodeOffset, suffixOffset);
            linkInfoTarget = CombineDisplayPath(local, suffix);
            offset += checked((int)linkInfoSize);
        }

        var unicode = (flags & 0x80) != 0;
        string? ReadStringData(uint flag)
        {
            if ((flags & flag) == 0)
            {
                return null;
            }

            if (!TryReadUInt16(bytes, offset, out var characterCount) || characterCount > 4096)
            {
                offset = bytes.Length;
                return null;
            }

            offset += 2;
            var byteCount = checked(characterCount * (unicode ? 2 : 1));
            if (!TryAdvance(ref offset, byteCount, bytes.Length))
            {
                return null;
            }

            var start = offset - byteCount;
            return Sanitize((unicode ? Encoding.Unicode : Encoding.Default)
                .GetString(bytes, start, byteCount));
        }

        var description = ReadStringData(0x4);
        var relativePath = ReadStringData(0x8);
        var workingDirectory = ReadStringData(0x10);
        var arguments = ReadStringData(0x20);
        var iconLocation = ReadStringData(0x40);
        var builder = new StringBuilder()
            .AppendLine("Windows shortcut")
            .AppendLine()
            .Append("Target: ").AppendLine(linkInfoTarget ?? relativePath ?? "—");
        Append(builder, "Description", description);
        Append(builder, "Working directory", workingDirectory);
        Append(builder, "Arguments", arguments);
        Append(builder, "Icon", iconLocation);
        builder.Append("Declared target size: ")
            .AppendLine(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(56, 4)).ToString())
            .AppendLine()
            .AppendLine("The shortcut target is never resolved or opened.");
        return PreviewContent.ForText("Windows shortcut", builder.ToString());
    }

    private static string? ReadLinkInfoString(
        ReadOnlySpan<byte> linkInfo,
        uint unicodeOffset,
        uint ansiOffset)
    {
        var unicode = unicodeOffset > 0;
        var rawOffset = unicode ? unicodeOffset : ansiOffset;
        if (rawOffset == 0 || rawOffset >= linkInfo.Length)
        {
            return null;
        }

        var offset = checked((int)rawOffset);
        var maximum = Math.Min(linkInfo.Length - offset, 8192);
        var length = 0;
        if (unicode)
        {
            while (length + 1 < maximum &&
                (linkInfo[offset + length] != 0 || linkInfo[offset + length + 1] != 0))
            {
                length += 2;
            }
        }
        else
        {
            while (length < maximum && linkInfo[offset + length] != 0)
            {
                length++;
            }
        }

        return Sanitize((unicode ? Encoding.Unicode : Encoding.Default)
            .GetString(linkInfo.Slice(offset, length)));
    }

    private static FileStream OpenRead(string path, bool useAsync) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, useAsync);

    private static bool TryReadUInt16(ReadOnlySpan<byte> bytes, int offset, out ushort value)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> bytes, int offset, out uint value)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        return true;
    }

    private static bool TryAdvance(ref int offset, int amount, int length)
    {
        if (amount < 0 || offset > length - amount)
        {
            return false;
        }

        offset += amount;
        return true;
    }

    private static string? CombineDisplayPath(string? root, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return suffix;
        }

        return string.IsNullOrWhiteSpace(suffix) || root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? root
            : root.TrimEnd('\\') + "\\" + suffix.TrimStart('\\');
    }

    private static string RedactUserInfo(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return value.Replace(uri.UserInfo + "@", "***@", StringComparison.Ordinal);
        }

        return value;
    }

    private static string Sanitize(string value) =>
        new(value.Where(character => character is '\t' or >= ' ').Take(8192).ToArray());

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(label).Append(": ").AppendLine(value);
        }
    }
}
