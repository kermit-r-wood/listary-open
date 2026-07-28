using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal sealed class ParquetPreviewProvider : IFilePreviewProvider
{
    private const int MaximumFooterBytes = 8 * 1024 * 1024;
    private const int MaximumOutputCharacters = 120 * 1024;

    public bool CanPreview(PreviewContext context) =>
        context.Extension.Equals(".parquet", StringComparison.OrdinalIgnoreCase) &&
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Opaque);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 12)
        {
            return null;
        }
        Span<byte> head = stackalloc byte[4];
        Span<byte> tail = stackalloc byte[8];
        stream.ReadExactly(head);
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        if (!head.SequenceEqual("PAR1"u8) || !tail[4..].SequenceEqual("PAR1"u8))
        {
            return null;
        }
        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(tail);
        if (footerLength == 0 || footerLength > MaximumFooterBytes ||
            footerLength > stream.Length - 12)
        {
            throw new InvalidDataException("Parquet footer exceeds preview bounds.");
        }
        var footer = new byte[checked((int)footerLength)];
        stream.Position = stream.Length - tail.Length - footer.Length;
        stream.ReadExactly(footer);
        var metadata = CompactReader.Parse(footer, cancellationToken);

        var builder = new StringBuilder("Apache Parquet")
            .AppendLine().AppendLine()
            .Append("Format version: ").AppendLine(metadata.Version?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
            .Append("Rows: ").AppendLine(metadata.Rows?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown")
            .Append("Row groups: ").AppendLine(metadata.RowGroups?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown");
        if (!string.IsNullOrWhiteSpace(metadata.CreatedBy))
        {
            builder.Append("Created by: ").AppendLine(Bound(metadata.CreatedBy));
        }
        if (metadata.Schema.Count > 0)
        {
            builder.AppendLine().AppendLine("Schema:");
            var remainingChildren = new Stack<int>();
            foreach (var node in metadata.Schema)
            {
                while (remainingChildren.Count > 0 && remainingChildren.Peek() == 0)
                {
                    remainingChildren.Pop();
                }
                if (remainingChildren.Count > 0)
                {
                    var remaining = remainingChildren.Pop();
                    remainingChildren.Push(remaining - 1);
                }
                builder.Append(' ', Math.Min(remainingChildren.Count, 16) * 2)
                    .Append("• ").Append(Bound(node.Name ?? "(unnamed)"));
                var details = new List<string>();
                if (node.Type is { } type)
                {
                    details.Add(TypeName(type));
                }
                if (node.Repetition is { } repetition)
                {
                    details.Add(RepetitionName(repetition));
                }
                if (!string.IsNullOrWhiteSpace(node.LogicalType))
                {
                    details.Add(node.LogicalType);
                }
                if (details.Count > 0)
                {
                    builder.Append(" — ").Append(string.Join(", ", details));
                }
                builder.AppendLine();
                if (node.Children is > 0)
                {
                    remainingChildren.Push(node.Children.Value);
                }
                if (builder.Length >= MaximumOutputCharacters)
                {
                    builder.Length = MaximumOutputCharacters;
                    builder.AppendLine().AppendLine("… schema truncated");
                    break;
                }
            }
        }
        const string safetyLine =
            "Only the bounded metadata footer was read; column pages and row values were not opened or decompressed.";
        if (builder.Length > MaximumOutputCharacters - safetyLine.Length - 4)
        {
            builder.Length = MaximumOutputCharacters - safetyLine.Length - 4;
            builder.AppendLine("…");
        }
        builder.AppendLine().AppendLine(safetyLine);
        return PreviewContent.ForText("Parquet schema", builder.ToString());
    }

    private static string Bound(string value)
    {
        var normalized = value.Replace('\0', ' ').ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 4096 ? normalized : normalized[..4096] + "…";
    }

    private static string TypeName(int value) => value switch
    {
        0 => "BOOLEAN", 1 => "INT32", 2 => "INT64", 3 => "INT96",
        4 => "FLOAT", 5 => "DOUBLE", 6 => "BYTE_ARRAY", 7 => "FIXED_LEN_BYTE_ARRAY",
        _ => $"physical type {value}"
    };

    private static string RepetitionName(int value) => value switch
    {
        0 => "required", 1 => "optional", 2 => "repeated", _ => $"repetition {value}"
    };

    private sealed record Metadata(
        int? Version, long? Rows, int? RowGroups, string? CreatedBy,
        IReadOnlyList<SchemaNode> Schema);

    private sealed record SchemaNode(
        string? Name, int? Type, int? Repetition, int? Children, string? LogicalType);

    private sealed class CompactReader
    {
        private const int MaximumOperations = 2_000_000;
        private const int MaximumDepth = 32;
        private const int MaximumItems = 4096;
        private const int MaximumSchemaNodes = 2048;
        private const int MaximumStringBytes = 64 * 1024;
        private const int MaximumAggregateStringBytes = 1024 * 1024;
        private readonly byte[] _bytes;
        private readonly CancellationToken _cancellationToken;
        private int _offset;
        private int _operations;
        private int _stringBytes;
        private readonly int _operationLimit;

        private CompactReader(byte[] bytes, CancellationToken cancellationToken)
        {
            _bytes = bytes;
            _cancellationToken = cancellationToken;
            _operationLimit = Math.Min(MaximumOperations, Math.Max(65_536, bytes.Length * 4));
        }

        internal static Metadata Parse(byte[] bytes, CancellationToken cancellationToken) =>
            new CompactReader(bytes, cancellationToken).ReadFileMetadata();

        private Metadata ReadFileMetadata()
        {
            int? version = null;
            long? rows = null;
            int? rowGroups = null;
            string? createdBy = null;
            var schema = new List<SchemaNode>();
            short lastField = 0;
            while (true)
            {
                var field = ReadField(ref lastField);
                if (field.Type == CType.Stop)
                {
                    break;
                }
                switch (field.Id)
                {
                    case 1 when field.Type == CType.I32:
                        version = checked((int)ReadSigned());
                        break;
                    case 2 when field.Type == CType.List:
                        ReadSchema(schema);
                        break;
                    case 3 when field.Type == CType.I64:
                        rows = ReadSigned();
                        break;
                    case 4 when field.Type == CType.List:
                        rowGroups = ReadAndSkipCollection();
                        break;
                    case 6 when field.Type == CType.Binary:
                        createdBy = ReadString();
                        break;
                    default:
                        Skip(field.Type, 1);
                        break;
                }
            }
            if (_offset != _bytes.Length)
            {
                throw new InvalidDataException("Unexpected data follows the Parquet footer struct.");
            }
            if (version is < 0 || rows is < 0 || schema.Count == 0)
            {
                throw new InvalidDataException("Parquet metadata has invalid semantic values.");
            }
            return new Metadata(version, rows, rowGroups, createdBy, schema);
        }

        private void ReadSchema(List<SchemaNode> schema)
        {
            var (type, count) = ReadCollectionHeader();
            if (type != CType.Struct || count > MaximumSchemaNodes)
            {
                throw new InvalidDataException("Parquet schema exceeds preview bounds.");
            }
            for (var index = 0; index < count; index++)
            {
                schema.Add(ReadSchemaElement());
            }
        }

        private SchemaNode ReadSchemaElement()
        {
            int? type = null;
            int? repetition = null;
            int? children = null;
            int? converted = null;
            string? name = null;
            string? logical = null;
            short lastField = 0;
            while (true)
            {
                var field = ReadField(ref lastField);
                if (field.Type == CType.Stop)
                {
                    break;
                }
                switch (field.Id)
                {
                    case 1 when field.Type == CType.I32:
                        type = checked((int)ReadSigned());
                        break;
                    case 3 when field.Type == CType.I32:
                        repetition = checked((int)ReadSigned());
                        break;
                    case 4 when field.Type == CType.Binary:
                        name = ReadString();
                        break;
                    case 5 when field.Type == CType.I32:
                        children = checked((int)ReadSigned());
                        if (children is < 0 or > MaximumSchemaNodes)
                        {
                            throw new InvalidDataException("Invalid Parquet schema child count.");
                        }
                        break;
                    case 6 when field.Type == CType.I32:
                        converted = checked((int)ReadSigned());
                        break;
                    case 10 when field.Type == CType.Struct:
                        logical = ReadLogicalType();
                        break;
                    default:
                        Skip(field.Type, 2);
                        break;
                }
            }
            logical ??= converted is { } value ? ConvertedTypeName(value) : null;
            return new SchemaNode(name, type, repetition, children, logical);
        }

        private string? ReadLogicalType()
        {
            string? result = null;
            short lastField = 0;
            while (true)
            {
                var field = ReadField(ref lastField);
                if (field.Type == CType.Stop)
                {
                    return result;
                }
                result ??= field.Id switch
                {
                    1 => "STRING", 2 => "MAP", 3 => "LIST", 4 => "ENUM", 5 => "DECIMAL",
                    6 => "DATE", 7 => "TIME", 8 => "TIMESTAMP", 10 => "INTEGER",
                    11 => "UNKNOWN", 12 => "JSON", 13 => "BSON", 14 => "UUID", 15 => "FLOAT16",
                    _ => null
                };
                Skip(field.Type, 3);
            }
        }

        private int ReadAndSkipCollection()
        {
            var (type, count) = ReadCollectionHeader();
            if (type != CType.Struct)
            {
                throw new InvalidDataException("Parquet row groups must be Thrift structs.");
            }
            for (var index = 0; index < count; index++)
            {
                Skip(type, 2, collectionElement: true);
            }
            return count;
        }

        private (CType Type, int Count) ReadCollectionHeader()
        {
            var header = ReadByte();
            var count = header >> 4;
            if (count == 15)
            {
                var longCount = ReadUnsigned();
                if (longCount > MaximumItems)
                {
                    throw new InvalidDataException("Parquet collection exceeds preview bounds.");
                }
                count = checked((int)longCount);
            }
            return ((CType)(header & 0x0F), count);
        }

        private Field ReadField(ref short lastField)
        {
            Tick();
            var header = ReadByteRaw();
            var type = (CType)(header & 0x0F);
            if (type == CType.Stop)
            {
                return new Field(0, type);
            }
            var delta = header >> 4;
            short id;
            if (delta == 0)
            {
                var value = ReadSigned();
                if (value is < short.MinValue or > short.MaxValue)
                {
                    throw new InvalidDataException("Invalid Thrift field identifier.");
                }
                id = (short)value;
            }
            else
            {
                id = checked((short)(lastField + delta));
            }
            lastField = id;
            return new Field(id, type);
        }

        private void Skip(CType type, int depth, bool collectionElement = false)
        {
            Tick();
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException("Parquet metadata nesting exceeds preview bounds.");
            }
            switch (type)
            {
                case CType.Stop:
                case CType.BooleanTrue:
                case CType.BooleanFalse:
                    if (collectionElement)
                    {
                        var boolean = ReadByte();
                        if (boolean != (byte)CType.BooleanTrue &&
                            boolean != (byte)CType.BooleanFalse)
                        {
                            throw new InvalidDataException("Invalid compact boolean collection value.");
                        }
                    }
                    return;
                case CType.Byte:
                    ReadByte();
                    return;
                case CType.I16:
                case CType.I32:
                case CType.I64:
                    ReadUnsigned();
                    return;
                case CType.Double:
                    ReadBytes(8);
                    return;
                case CType.Binary:
                    SkipBinary();
                    return;
                case CType.List:
                case CType.Set:
                    var (itemType, count) = ReadCollectionHeader();
                    for (var index = 0; index < count; index++)
                    {
                        Skip(itemType, depth + 1, collectionElement: true);
                    }
                    return;
                case CType.Map:
                    var mapCount = ReadUnsigned();
                    if (mapCount > MaximumItems)
                    {
                        throw new InvalidDataException("Parquet map exceeds preview bounds.");
                    }
                    if (mapCount == 0)
                    {
                        return;
                    }
                    var types = ReadByte();
                    for (var index = 0u; index < mapCount; index++)
                    {
                        Skip((CType)(types >> 4), depth + 1, collectionElement: true);
                        Skip((CType)(types & 0x0F), depth + 1, collectionElement: true);
                    }
                    return;
                case CType.Struct:
                    short lastField = 0;
                    while (true)
                    {
                        var field = ReadField(ref lastField);
                        if (field.Type == CType.Stop)
                        {
                            return;
                        }
                        Skip(field.Type, depth + 1);
                    }
                default:
                    throw new InvalidDataException($"Unsupported Thrift compact type {(int)type}.");
            }
        }

        private long ReadSigned()
        {
            var value = ReadUnsigned();
            return unchecked((long)(value >> 1) ^ -((long)value & 1));
        }

        private ulong ReadUnsigned()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var current = ReadByte();
                if (shift == 63 && (current & 0xFE) != 0)
                {
                    throw new InvalidDataException("Thrift varint overflow.");
                }
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException("Overlong Thrift varint.");
        }

        private string ReadString() => Encoding.UTF8.GetString(ReadBinary());

        private ReadOnlySpan<byte> ReadBinary()
        {
            var length = ReadUnsigned();
            if (length > MaximumStringBytes ||
                length > (ulong)(MaximumAggregateStringBytes - _stringBytes))
            {
                throw new InvalidDataException("Parquet strings exceed preview bounds.");
            }
            _stringBytes += checked((int)length);
            return ReadBytes(checked((int)length));
        }

        private void SkipBinary()
        {
            var length = ReadUnsigned();
            if (length > int.MaxValue)
            {
                throw new InvalidDataException("Parquet binary field exceeds footer bounds.");
            }
            ReadBytes(checked((int)length));
        }

        private byte ReadByte()
        {
            Tick();
            return ReadByteRaw();
        }

        private byte ReadByteRaw()
        {
            if (_offset >= _bytes.Length)
            {
                throw new EndOfStreamException("Truncated Parquet metadata.");
            }
            return _bytes[_offset++];
        }

        private ReadOnlySpan<byte> ReadBytes(int count)
        {
            Tick();
            if (count < 0 || _offset > _bytes.Length - count)
            {
                throw new EndOfStreamException("Truncated Parquet metadata.");
            }
            var result = _bytes.AsSpan(_offset, count);
            _offset += count;
            return result;
        }

        private void Tick()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (++_operations > _operationLimit)
            {
                throw new InvalidDataException("Parquet metadata exceeds the operation budget.");
            }
        }

        private static string? ConvertedTypeName(int value) => value switch
        {
            0 => "UTF8", 1 => "MAP", 2 => "MAP_KEY_VALUE", 3 => "LIST", 4 => "ENUM",
            5 => "DECIMAL", 6 => "DATE", 7 => "TIME_MILLIS", 8 => "TIME_MICROS",
            9 => "TIMESTAMP_MILLIS", 10 => "TIMESTAMP_MICROS", 11 => "UINT_8",
            12 => "UINT_16", 13 => "UINT_32", 14 => "UINT_64", 15 => "INT_8",
            16 => "INT_16", 17 => "INT_32", 18 => "INT_64", 19 => "JSON",
            20 => "BSON", 21 => "INTERVAL", _ => null
        };

        private readonly record struct Field(short Id, CType Type);

        private enum CType : byte
        {
            Stop, BooleanTrue, BooleanFalse, Byte, I16, I32, I64,
            Double, Binary, List, Set, Map, Struct
        }
    }
}
