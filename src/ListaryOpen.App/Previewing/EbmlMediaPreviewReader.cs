using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal static class EbmlMediaPreviewReader
{
    private const long MaximumScannedBytes = 8L * 1024 * 1024;
    private const int MaximumElements = 4096;
    private const int MaximumTracks = 128;
    private const int MaximumDepth = 16;
    private const int MaximumStringBytes = 4096;

    internal static void AppendMetadata(
        StringBuilder output,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var reader = new Reader(stream, cancellationToken);
        if (!reader.TryReadElement(0, stream.Length, out var ebml) ||
            ebml.Id != 0x1A45DFA3 || ebml.UnknownSize)
        {
            return;
        }

        var docType = ReadStringChild(reader, ebml, 0x4282);
        if (!string.IsNullOrWhiteSpace(docType))
        {
            output.Append("Container: EBML / ").AppendLine(docType);
        }

        var cursor = ebml.End;
        while (cursor < stream.Length && reader.Elements < MaximumElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadElement(cursor, stream.Length, out var element))
            {
                return;
            }
            if (element.Id == 0x18538067)
            {
                ReadSegment(output, reader, element);
                return;
            }
            cursor = element.End;
        }
    }

    private static void ReadSegment(StringBuilder output, Reader reader, Element segment)
    {
        var end = segment.UnknownSize ? reader.Stream.Length : segment.End;
        var cursor = segment.DataOffset;
        double? durationUnits = null;
        ulong timecodeScale = 1_000_000;
        var tracks = new List<Track>();
        while (cursor < end && reader.Elements < MaximumElements)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadElement(cursor, end, out var child))
            {
                break;
            }
            if (child.Id == 0x1549A966 && !child.UnknownSize)
            {
                ReadInfo(output, reader, child, ref timecodeScale, ref durationUnits);
            }
            else if (child.Id == 0x1654AE6B && !child.UnknownSize)
            {
                ReadTracks(reader, child, tracks);
            }
            else if (child.Id == 0x1F43B675)
            {
                if (child.UnknownSize)
                {
                    break;
                }
            }
            cursor = child.End;
        }

        if (durationUnits is > 0 &&
            double.IsFinite(durationUnits.Value))
        {
            var seconds = durationUnits.Value * timecodeScale / 1_000_000_000d;
            if (double.IsFinite(seconds) && seconds >= 0 &&
                seconds <= TimeSpan.MaxValue.TotalSeconds)
            {
                output.Append("Duration: ").AppendLine(
                    FormatDuration(TimeSpan.FromSeconds(seconds)));
            }
        }

        if (tracks.Count > 0)
        {
            output.Append("Tracks: ").AppendLine(tracks.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var track in tracks)
            {
                output.Append("• ").Append(TrackTypeName(track.Type));
                if (!string.IsNullOrWhiteSpace(track.Codec))
                {
                    output.Append(" — ").Append(track.Codec);
                }
                if (!string.IsNullOrWhiteSpace(track.Name))
                {
                    output.Append(" — ").Append(track.Name);
                }
                if (!string.IsNullOrWhiteSpace(track.Language))
                {
                    output.Append(" [").Append(track.Language).Append(']');
                }
                if (track.Width > 0 && track.Height > 0)
                {
                    output.Append(" — ").Append(track.Width).Append('×').Append(track.Height);
                }
                if (track.SampleRate > 0)
                {
                    output.Append(" — ").Append(track.SampleRate.Value.ToString("0.##", CultureInfo.InvariantCulture))
                        .Append(" Hz");
                }
                if (track.Channels > 0)
                {
                    output.Append(", ").Append(track.Channels).Append(" channel(s)");
                }
                if (track.BitDepth > 0)
                {
                    output.Append(", ").Append(track.BitDepth).Append("-bit");
                }
                output.AppendLine();
            }
        }
    }

    private static void ReadInfo(
        StringBuilder output,
        Reader reader,
        Element info,
        ref ulong timecodeScale,
        ref double? duration)
    {
        foreach (var child in reader.Children(info, 2))
        {
            switch (child.Id)
            {
                case 0x2AD7B1:
                    var scale = reader.ReadUnsigned(child);
                    if (scale > 0)
                    {
                        timecodeScale = scale.Value;
                    }
                    break;
                case 0x4489:
                    duration = reader.ReadFloat(child);
                    break;
                case 0x7BA9:
                    AppendField(output, "Title", reader.ReadString(child));
                    break;
                case 0x4D80:
                    AppendField(output, "Muxing application", reader.ReadString(child));
                    break;
                case 0x5741:
                    AppendField(output, "Writing application", reader.ReadString(child));
                    break;
            }
        }
    }

    private static void ReadTracks(Reader reader, Element tracksElement, List<Track> tracks)
    {
        foreach (var entry in reader.Children(tracksElement, 2))
        {
            if (entry.Id != 0xAE || entry.UnknownSize || tracks.Count >= MaximumTracks)
            {
                continue;
            }
            var track = new Track();
            foreach (var child in reader.Children(entry, 3))
            {
                switch (child.Id)
                {
                    case 0x83:
                        track.Type = reader.ReadUnsigned(child);
                        break;
                    case 0x86:
                        track.Codec = reader.ReadString(child);
                        break;
                    case 0x536E:
                        track.Name = reader.ReadString(child);
                        break;
                    case 0x22B59C:
                        track.Language = reader.ReadString(child);
                        break;
                    case 0xE0:
                        ReadVideo(reader, child, track);
                        break;
                    case 0xE1:
                        ReadAudio(reader, child, track);
                        break;
                }
            }
            tracks.Add(track);
        }
    }

    private static void ReadVideo(Reader reader, Element video, Track track)
    {
        foreach (var child in reader.Children(video, 4))
        {
            if (child.Id == 0xB0)
            {
                track.Width = reader.ReadUnsigned(child);
            }
            else if (child.Id == 0xBA)
            {
                track.Height = reader.ReadUnsigned(child);
            }
        }
    }

    private static void ReadAudio(Reader reader, Element audio, Track track)
    {
        foreach (var child in reader.Children(audio, 4))
        {
            if (child.Id == 0xB5)
            {
                track.SampleRate = reader.ReadFloat(child);
            }
            else if (child.Id == 0x9F)
            {
                track.Channels = reader.ReadUnsigned(child);
            }
            else if (child.Id == 0x6264)
            {
                track.BitDepth = reader.ReadUnsigned(child);
            }
        }
    }

    private static string? ReadStringChild(Reader reader, Element parent, ulong id)
    {
        foreach (var child in reader.Children(parent, 1))
        {
            if (child.Id == id)
            {
                return reader.ReadString(child);
            }
        }
        return null;
    }

    private static void AppendField(StringBuilder output, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            output.Append(label).Append(": ").AppendLine(value);
        }
    }

    private static string TrackTypeName(ulong? type) => type switch
    {
        1 => "Video",
        2 => "Audio",
        3 => "Complex",
        0x10 => "Logo",
        0x11 => "Subtitle",
        0x12 => "Buttons",
        0x20 => "Control",
        _ => "Track"
    };

    private static string FormatDuration(TimeSpan value) =>
        $"{(long)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}.{value.Milliseconds:D3}";

    private sealed class Track
    {
        internal ulong? Type { get; set; }
        internal string? Codec { get; set; }
        internal string? Name { get; set; }
        internal string? Language { get; set; }
        internal ulong? Width { get; set; }
        internal ulong? Height { get; set; }
        internal double? SampleRate { get; set; }
        internal ulong? Channels { get; set; }
        internal ulong? BitDepth { get; set; }
    }

    private readonly record struct Element(
        ulong Id,
        long DataOffset,
        long End,
        long Size,
        bool UnknownSize);

    private sealed class Reader(Stream stream, CancellationToken cancellationToken)
    {
        internal Stream Stream { get; } = stream;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal int Elements { get; private set; }
        private long ScannedBytes { get; set; }
        private int StringBytes { get; set; }

        internal IEnumerable<Element> Children(Element parent, int depth)
        {
            if (depth > MaximumDepth || parent.UnknownSize)
            {
                yield break;
            }
            var cursor = parent.DataOffset;
            while (cursor < parent.End && Elements < MaximumElements)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (!TryReadElement(cursor, parent.End, out var child))
                {
                    yield break;
                }
                yield return child;
                cursor = child.End;
            }
        }

        internal bool TryReadElement(long offset, long parentEnd, out Element element)
        {
            element = default;
            if (offset < 0 || offset >= parentEnd || parentEnd > Stream.Length ||
                Elements >= MaximumElements)
            {
                return false;
            }
            Stream.Position = offset;
            if (!TryReadVInt(keepMarker: true, out var id, out _) ||
                !TryReadVInt(keepMarker: false, out var size, out var unknown))
            {
                return false;
            }
            var dataOffset = Stream.Position;
            if (unknown)
            {
                element = new Element(id, dataOffset, parentEnd, parentEnd - dataOffset, true);
                Elements++;
                return true;
            }
            if (size > long.MaxValue || dataOffset > parentEnd - (long)size)
            {
                return false;
            }
            element = new Element(id, dataOffset, dataOffset + (long)size, (long)size, false);
            Elements++;
            return true;
        }

        internal ulong? ReadUnsigned(Element element)
        {
            if (element.UnknownSize || element.Size is < 1 or > 8)
            {
                return null;
            }
            Span<byte> bytes = stackalloc byte[8];
            bytes.Clear();
            ReadExactly(element.DataOffset, bytes[(8 - (int)element.Size)..]);
            return BinaryPrimitives.ReadUInt64BigEndian(bytes);
        }

        internal double? ReadFloat(Element element)
        {
            if (element.UnknownSize || element.Size is not (4 or 8))
            {
                return null;
            }
            Span<byte> bytes = stackalloc byte[8];
            ReadExactly(element.DataOffset, bytes[..(int)element.Size]);
            return element.Size == 4
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes));
        }

        internal string? ReadString(Element element)
        {
            if (element.UnknownSize || element.Size < 0 || element.Size > MaximumStringBytes)
            {
                return null;
            }
            var bytes = new byte[(int)element.Size];
            ReadExactly(element.DataOffset, bytes);
            if (StringBytes > 64 * 1024 - bytes.Length)
            {
                throw new InvalidDataException("EBML strings exceed the preview budget.");
            }
            StringBytes += bytes.Length;
            var value = new UTF8Encoding(false, true).GetString(bytes)
                .ReplaceLineEndings(" ");
            var sanitized = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                var category = char.GetUnicodeCategory(character);
                if (!char.IsControl(character) &&
                    category != UnicodeCategory.Format)
                {
                    sanitized.Append(character);
                }
                else
                {
                    sanitized.Append(' ');
                }
            }
            return sanitized.ToString().Trim();
        }

        private void ReadExactly(long offset, Span<byte> bytes)
        {
            if (ScannedBytes > MaximumScannedBytes - bytes.Length)
            {
                throw new InvalidDataException("EBML metadata exceeds the preview scan budget.");
            }
            Stream.Position = offset;
            Stream.ReadExactly(bytes);
            ScannedBytes += bytes.Length;
            CancellationToken.ThrowIfCancellationRequested();
        }

        private bool TryReadVInt(bool keepMarker, out ulong value, out bool unknown)
        {
            value = 0;
            unknown = false;
            var first = ReadBudgetedByte();
            if (first <= 0)
            {
                return false;
            }
            ScannedBytes++;
            var mask = 0x80;
            var length = 1;
            while ((first & mask) == 0 && length < 8)
            {
                mask >>= 1;
                length++;
            }
            if ((first & mask) == 0 || keepMarker && length > 4)
            {
                return false;
            }
            value = keepMarker ? (uint)first : (uint)(first & (mask - 1));
            var allOnes = !keepMarker && (first & (mask - 1)) == mask - 1;
            for (var index = 1; index < length; index++)
            {
                var next = ReadBudgetedByte();
                if (next < 0)
                {
                    return false;
                }
                value = (value << 8) | (uint)next;
                allOnes &= next == 0xFF;
            }
            unknown = allOnes;
            return true;
        }

        private int ReadBudgetedByte()
        {
            if (ScannedBytes >= MaximumScannedBytes)
            {
                throw new InvalidDataException("EBML metadata exceeds the preview scan budget.");
            }
            var value = Stream.ReadByte();
            if (value >= 0)
            {
                ScannedBytes++;
            }
            return value;
        }
    }
}
