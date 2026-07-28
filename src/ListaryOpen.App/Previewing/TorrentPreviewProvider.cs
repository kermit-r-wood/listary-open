using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Parses the bounded bencode metadata dictionary of a .torrent file. Tracker
/// requests are never made and piece hashes or file payloads are not opened.
/// </summary>
internal sealed class TorrentPreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const int MaximumScanBytes = 16 * 1024 * 1024;
    private const int MaximumStringBytes = 64 * 1024;
    private const int MaximumNodes = 10_000;
    private const int MaximumDepth = 8;
    private const int MaximumFiles = 500;
    private const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        context.Extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase) &&
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Opaque);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < 3 || context.SizeBytes > MaximumInputBytes)
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
        var parser = new BencodeParser(bytes);
        if (!parser.TryRead(out var root) || root.Kind != BencodeKind.Dictionary ||
            parser.Position < bytes.Length && !parser.OnlyZeroPadding())
        {
            return null;
        }

        var builder = new StringBuilder("BitTorrent metadata")
            .AppendLine().Append("Metadata bytes scanned: ").AppendLine(FilePreviewPane.FormatSize(count));
        var announce = root.Get("announce")?.StringValue;
        if (!string.IsNullOrWhiteSpace(announce))
        {
            builder.Append("Tracker: ").AppendLine(Truncate(announce));
        }
        var announceList = root.Get("announce-list");
        if (announceList?.List is { } tiers)
        {
            builder.Append("Tracker tiers: ").AppendLine(tiers.Count.ToString());
        }
        var info = root.Get("info");
        if (info?.Kind == BencodeKind.Dictionary)
        {
            AppendString(builder, "Name", info.Get("name"));
            AppendInteger(builder, "Piece length", info.Get("piece length"));
            AppendInteger(builder, "Length", info.Get("length"));
            var pieces = info.Get("pieces");
            if (pieces?.ByteLength is { } pieceBytes)
            {
                builder.Append("Piece hashes: ").AppendLine((pieceBytes / 20d).ToString("N0"));
            }
            if (info.Get("private")?.Integer is not null)
            {
                builder.Append("Private flag: ").AppendLine(info.Get("private")!.Integer == 0 ? "no" : "yes");
            }
            if (info.Get("files")?.List is { } files)
            {
                builder.Append("Files: ").AppendLine(files.Count.ToString());
                var shown = 0;
                foreach (var file in files)
                {
                    if (shown >= MaximumFiles) break;
                    if (file.Kind != BencodeKind.Dictionary) continue;
                    var path = FormatPath(file.Get("path"));
                    if (path is null) continue;
                    builder.Append("- ").Append(path);
                    if (file.Get("length")?.Integer is { } length)
                    {
                        builder.Append(" (").Append(FilePreviewPane.FormatSize(length)).Append(')');
                    }
                    builder.AppendLine();
                    shown++;
                }
                if (files.Count > MaximumFiles) builder.AppendLine("… additional files omitted");
            }
        }
        builder.AppendLine()
            .AppendLine("Tracker requests were not made; piece hashes and file payloads were not opened.");
        if (parser.Truncated) builder.AppendLine("… metadata was bounded or truncated");
        return PreviewContent.ForText("BitTorrent metadata", Bound(builder.ToString()));
    }

    private static void AppendString(StringBuilder builder, string label, BencodeValue? value)
    {
        if (!string.IsNullOrWhiteSpace(value?.StringValue)) builder.Append(label).Append(": ").AppendLine(Truncate(value.StringValue));
    }

    private static void AppendInteger(StringBuilder builder, string label, BencodeValue? value)
    {
        if (value?.Integer is { } number) builder.Append(label).Append(": ").AppendLine(number.ToString());
    }

    private static string? FormatPath(BencodeValue? value)
    {
        if (value?.List is not { } parts || parts.Count == 0) return null;
        var names = parts.Select(part => part.StringValue).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        return names.Length == 0 ? null : string.Join('/', names.Select(name => Truncate(name!)));
    }

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..509] + "…";
    private static string Bound(string value) => value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private enum BencodeKind { Integer, ByteString, List, Dictionary }

    private sealed class BencodeValue(BencodeKind kind)
    {
        internal BencodeKind Kind { get; } = kind;
        internal long? Integer { get; init; }
        internal byte[]? Bytes { get; init; }
        internal int? ByteLength { get; init; }
        internal List<BencodeValue>? List { get; init; }
        internal Dictionary<string, BencodeValue>? Dictionary { get; init; }
        internal string? StringValue => Bytes is null ? null : Encoding.UTF8.GetString(Bytes).Replace('\0', ' ').Trim();
        internal BencodeValue? Get(string key) => Dictionary?.TryGetValue(key, out var value) == true ? value : null;
    }

    private sealed class BencodeParser(byte[] bytes)
    {
        private readonly byte[] _bytes = bytes;
        private int _nodes;
        internal int Position { get; private set; }
        internal bool Truncated { get; private set; }
        internal bool OnlyZeroPadding()
        {
            for (var index = Position; index < _bytes.Length; index++)
            {
                if (_bytes[index] != 0) return false;
            }
            return true;
        }

        internal bool TryRead(out BencodeValue value) => TryReadValue(0, out value);

        private bool TryReadValue(int depth, out BencodeValue value)
        {
            value = null!;
            if (depth > MaximumDepth || Position >= _bytes.Length || ++_nodes > MaximumNodes) { Truncated = true; return false; }
            switch (_bytes[Position])
            {
                case (byte)'i': return TryReadInteger(out value);
                case (byte)'l': return TryReadList(depth, out value);
                case (byte)'d': return TryReadDictionary(depth, out value);
                default: return TryReadString(out value);
            }
        }

        private bool TryReadInteger(out BencodeValue value)
        {
            value = null!; Position++;
            var start = Position;
            while (Position < _bytes.Length && _bytes[Position] != (byte)'e') Position++;
            if (Position >= _bytes.Length || Position == start ||
                !long.TryParse(Encoding.ASCII.GetString(_bytes, start, Position - start), out var number)) { Truncated = true; return false; }
            Position++;
            value = new BencodeValue(BencodeKind.Integer) { Integer = number };
            return true;
        }

        private bool TryReadString(out BencodeValue value)
        {
            value = null!; var start = Position;
            while (Position < _bytes.Length && _bytes[Position] is >= (byte)'0' and <= (byte)'9') Position++;
            if (Position == start || Position >= _bytes.Length || _bytes[Position++] != (byte)':' ||
                !int.TryParse(Encoding.ASCII.GetString(_bytes, start, Position - start - 1), out var length) ||
                length < 0 || length > _bytes.Length - Position) { Truncated = true; return false; }
            var save = Position;
            Position += length;
            byte[]? bytes = null;
            if (length <= MaximumStringBytes) bytes = _bytes.AsSpan(save, length).ToArray();
            else Truncated = true;
            value = new BencodeValue(BencodeKind.ByteString) { Bytes = bytes, ByteLength = length };
            return true;
        }

        private bool TryReadList(int depth, out BencodeValue value)
        {
            value = null!; Position++; var list = new List<BencodeValue>();
            while (Position < _bytes.Length && _bytes[Position] != (byte)'e')
            {
                if (list.Count >= MaximumNodes || !TryReadValue(depth + 1, out var item)) return false;
                list.Add(item);
            }
            if (Position >= _bytes.Length) { Truncated = true; return false; }
            Position++; value = new BencodeValue(BencodeKind.List) { List = list }; return true;
        }

        private bool TryReadDictionary(int depth, out BencodeValue value)
        {
            value = null!; Position++; var dictionary = new Dictionary<string, BencodeValue>(StringComparer.Ordinal);
            while (Position < _bytes.Length && _bytes[Position] != (byte)'e')
            {
                if (!TryReadString(out var key) || key.StringValue is null || !TryReadValue(depth + 1, out var item)) return false;
                if (dictionary.Count < MaximumNodes) dictionary[key.StringValue] = item;
            }
            if (Position >= _bytes.Length) { Truncated = true; return false; }
            Position++; value = new BencodeValue(BencodeKind.Dictionary) { Dictionary = dictionary }; return true;
        }
    }
}
