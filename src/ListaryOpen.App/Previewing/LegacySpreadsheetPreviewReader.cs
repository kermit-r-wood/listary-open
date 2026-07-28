using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace ListaryOpen.App.Previewing;

internal static class LegacySpreadsheetPreviewReader
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;
    private const int MaximumSheets = 32;
    private const int MaximumRowsPerSheet = 200;
    private const int MaximumColumnsPerRow = 64;
    private const int MaximumNonEmptyCells = 4096;
    private const int MaximumCellCharacters = 4096;
    private const int MaximumOutputCharacters = 120_000;
    private const int ReservedFooterCharacters = 512;

    static LegacySpreadsheetPreviewReader() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static PreviewContent Read(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            context.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
        {
            return MetadataOnly(
                context,
                $"Cell extraction is limited to {FilePreviewPane.FormatSize(MaximumFileBytes)} files.");
        }

        try
        {
            if (!HasLegacyExcelSignature(stream))
            {
                return MetadataOnly(
                    context,
                    "The file does not contain a supported BIFF2–BIFF8 workbook signature.");
            }
            using var bounded = new CapturedLengthStream(stream, stream.Length);
            using var reader = ExcelReaderFactory.CreateBinaryReader(
                bounded,
                new ExcelReaderConfiguration
                {
                    LeaveOpen = false,
                    FallbackEncoding = Encoding.GetEncoding(1252)
                });
            var builder = new StringBuilder()
                .AppendLine("Legacy Excel workbook")
                .AppendLine()
                .AppendLine("Cached cell values only; formulas and macros are not executed, and links or embedded objects are not extracted.")
                .AppendLine();
            var sheetCount = 0;
            var nonEmptyCells = 0;
            var truncated = false;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++sheetCount > MaximumSheets)
                {
                    truncated = true;
                    break;
                }
                builder.Append("— Sheet: ")
                    .AppendLine(Sanitize(reader.Name, 512))
                    .AppendLine();
                var rowCount = 0;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++rowCount > MaximumRowsPerSheet ||
                        nonEmptyCells >= MaximumNonEmptyCells ||
                        builder.Length >= MaximumOutputCharacters - ReservedFooterCharacters)
                    {
                        truncated = true;
                        break;
                    }
                    var values = new string[Math.Min(reader.FieldCount, MaximumColumnsPerRow)];
                    var lastNonEmpty = -1;
                    for (var column = 0; column < values.Length; column++)
                    {
                        var value = FormatValue(reader.GetValue(column));
                        values[column] = value;
                        if (value.Length == 0)
                        {
                            continue;
                        }
                        lastNonEmpty = column;
                        nonEmptyCells++;
                        if (nonEmptyCells >= MaximumNonEmptyCells)
                        {
                            truncated = true;
                            break;
                        }
                    }
                    if (reader.FieldCount > MaximumColumnsPerRow)
                    {
                        truncated = true;
                    }
                    if (lastNonEmpty >= 0)
                    {
                        for (var column = 0; column <= lastNonEmpty; column++)
                        {
                            if (column > 0)
                            {
                                AppendBounded(builder, "\t");
                            }
                            AppendBounded(builder, values[column]);
                        }
                        builder.AppendLine();
                    }
                    if (truncated)
                    {
                        break;
                    }
                }
                builder.AppendLine();
                if (truncated)
                {
                    break;
                }
            }
            while (reader.NextResult());

            if (truncated)
            {
                builder.AppendLine("… workbook preview stopped at the sheet, row, column, cell, or output budget.");
            }
            builder.AppendLine()
                .AppendLine("Preview budgets: 32 sheets, 200 rows per sheet, 64 columns per row, 4,096 non-empty cells, and 120,000 output characters.");
            if (builder.Length > MaximumOutputCharacters)
            {
                builder.Length = MaximumOutputCharacters;
            }
            return PreviewContent.ForText("Legacy Excel cells", builder.ToString());
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or NotSupportedException or
                ArgumentException or FormatException or OverflowException or
                ExcelDataReader.Exceptions.ExcelReaderException)
        {
            return MetadataOnly(
                context,
                "The workbook is encrypted, damaged, or uses an unsupported legacy Excel variant.");
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.ToString("u", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("u", CultureInfo.InvariantCulture),
        byte[] => "(binary value omitted)",
        IFormattable formattable => Sanitize(
            formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            MaximumCellCharacters),
        _ => Sanitize(value.ToString() ?? string.Empty, MaximumCellCharacters)
    };

    private static string Sanitize(string value, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters + 1));
        var wasSpace = false;
        foreach (var character in value)
        {
            if (builder.Length >= maximumCharacters)
            {
                builder.Append('…');
                break;
            }
            var current = character is '\0' or '\r' or '\n' ? ' ' : character;
            if (char.IsControl(current))
            {
                continue;
            }
            if (current == ' ')
            {
                if (wasSpace || builder.Length == 0)
                {
                    continue;
                }
                wasSpace = true;
            }
            else
            {
                wasSpace = false;
            }
            builder.Append(current);
        }
        return builder.ToString().TrimEnd();
    }

    private static void AppendBounded(StringBuilder builder, string value)
    {
        var remaining = MaximumOutputCharacters - builder.Length;
        if (remaining > 0)
        {
            builder.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        }
    }

    private static PreviewContent MetadataOnly(PreviewContext context, string reason) =>
        PreviewContent.ForText(
            "Legacy Excel metadata",
            $"Legacy Excel workbook{Environment.NewLine}{Environment.NewLine}" +
            $"Size: {FilePreviewPane.FormatSize(context.SizeBytes)}{Environment.NewLine}" +
            $"{reason}{Environment.NewLine}" +
            "No formulas, macros, links, embedded objects, or external workbooks were evaluated or opened.");

    private static bool HasLegacyExcelSignature(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        var read = stream.ReadAtLeast(header, 4, throwOnEndOfStream: false);
        stream.Position = 0;
        if (read >= 8 && header.SequenceEqual(
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            return true;
        }
        if (read < 4)
        {
            return false;
        }
        var record = header[0] | header[1] << 8;
        var length = header[2] | header[3] << 8;
        return (record is 0x0009 or 0x0209 or 0x0409 or 0x0809) &&
            length is >= 4 and <= 64;
    }

    private sealed class CapturedLengthStream(Stream inner, long capturedLength) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => capturedLength;
        public override long Position
        {
            get => inner.Position;
            set
            {
                if (value < 0 || value > capturedLength)
                {
                    throw new IOException("Legacy workbook seek exceeds the captured file length.");
                }
                inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Limit(count));

        public override int Read(Span<byte> buffer) =>
            inner.Read(buffer[..Limit(buffer.Length)]);

        public override int ReadByte() =>
            Position >= capturedLength ? -1 : inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(capturedLength + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return target;
        }

        private int Limit(int requested) =>
            checked((int)Math.Min(requested, Math.Max(0, capturedLength - Position)));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
