using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ListaryOpen.App.Previewing;

internal sealed class ModelPreviewProvider : IFilePreviewProvider
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumLines = 100_000;
    private const int MaximumBinaryTriangles = 100_000;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Model);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            context.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16_384, useAsync: true);
        var buffer = new byte[Math.Min(stream.Length, MaximumBytes)];
        var count = await stream.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        var truncated = stream.Length > count;
        var data = buffer.AsMemory(0, count);
        return context.Extension.ToLowerInvariant() switch
        {
            ".stl" => PreviewContent.ForText("3D geometry summary",
                ReadStl(data.Span, stream.Length, truncated, cancellationToken)),
            ".obj" => PreviewContent.ForText("3D geometry summary",
                ReadObj(DecodeText(data.Span), truncated, cancellationToken)),
            ".ply" => PreviewContent.ForText("3D geometry summary",
                ReadPly(data.Span, truncated)),
            ".gltf" => PreviewContent.ForText("3D scene summary",
                ReadGltf(data.Span, truncated)),
            ".glb" => PreviewContent.ForText("3D scene summary",
                ReadGlb(data.Span, stream.Length)),
            ".dxf" => PreviewContent.ForText("CAD structure summary",
                ReadDxf(DecodeText(data.Span), truncated, cancellationToken)),
            _ => null
        };
    }

    private static string ReadStl(
        ReadOnlySpan<byte> bytes,
        long fileLength,
        bool truncated,
        CancellationToken cancellationToken)
    {
        if (bytes.Length >= 84)
        {
            var declaredTriangles = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(80, 4));
            var expectedLength = 84L + (50L * declaredTriangles);
            if (expectedLength <= fileLength && declaredTriangles > 0)
            {
                var bounds = new Bounds3();
                var available = Math.Min(
                    Math.Min((int)declaredTriangles, MaximumBinaryTriangles),
                    (bytes.Length - 84) / 50);
                for (var triangle = 0; triangle < available; triangle++)
                {
                    if ((triangle & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var offset = 84 + triangle * 50 + 12;
                    for (var vertex = 0; vertex < 3; vertex++)
                    {
                        bounds.Add(
                            ReadSingle(bytes, offset + vertex * 12),
                            ReadSingle(bytes, offset + vertex * 12 + 4),
                            ReadSingle(bytes, offset + vertex * 12 + 8));
                    }
                }

                return BuildGeometrySummary(
                    "Binary STL",
                    vertices: (long)declaredTriangles * 3,
                    faces: declaredTriangles,
                    bounds,
                    truncated || available < declaredTriangles);
            }
        }

        var text = DecodeText(bytes);
        var asciiBounds = new Bounds3();
        var vertices = 0L;
        var facets = 0L;
        var lines = 0;
        foreach (var line in EnumerateLines(text))
        {
            if (++lines > MaximumLines)
            {
                truncated = true;
                break;
            }

            if ((lines & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var value = line.Trim();
            if (value.StartsWith("facet ", StringComparison.OrdinalIgnoreCase))
            {
                facets++;
            }
            else if (value.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase) &&
                TryReadVector(value.AsSpan(7), out var x, out var y, out var z))
            {
                vertices++;
                asciiBounds.Add(x, y, z);
            }
        }

        return BuildGeometrySummary("ASCII STL", vertices, facets, asciiBounds, truncated);
    }

    private static string ReadObj(string text, bool truncated, CancellationToken cancellationToken)
    {
        var bounds = new Bounds3();
        long vertices = 0, textureCoordinates = 0, normals = 0, faces = 0, objects = 0, groups = 0;
        var externalMaterialLibraries = 0;
        var lines = 0;
        foreach (var sourceLine in EnumerateLines(text))
        {
            if (++lines > MaximumLines)
            {
                truncated = true;
                break;
            }

            if ((lines & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = sourceLine.TrimStart();
            if (line.StartsWith("v ", StringComparison.Ordinal) &&
                TryReadVector(line.AsSpan(2), out var x, out var y, out var z))
            {
                vertices++;
                bounds.Add(x, y, z);
            }
            else if (line.StartsWith("vt ", StringComparison.Ordinal)) textureCoordinates++;
            else if (line.StartsWith("vn ", StringComparison.Ordinal)) normals++;
            else if (line.StartsWith("f ", StringComparison.Ordinal)) faces++;
            else if (line.StartsWith("o ", StringComparison.Ordinal)) objects++;
            else if (line.StartsWith("g ", StringComparison.Ordinal)) groups++;
            else if (line.StartsWith("mtllib ", StringComparison.Ordinal)) externalMaterialLibraries++;
        }

        return new StringBuilder(BuildGeometrySummary("Wavefront OBJ", vertices, faces, bounds, truncated))
            .Append("Texture coordinates: ").AppendLine(textureCoordinates.ToString())
            .Append("Normals: ").AppendLine(normals.ToString())
            .Append("Objects: ").AppendLine(objects.ToString())
            .Append("Groups: ").AppendLine(groups.ToString())
            .Append("External material libraries ignored: ").AppendLine(externalMaterialLibraries.ToString())
            .ToString();
    }

    private static string ReadPly(ReadOnlySpan<byte> bytes, bool truncated)
    {
        var headerEnd = FindAscii(bytes, "end_header");
        if (headerEnd < 0 || headerEnd > 64 * 1024)
        {
            throw new InvalidDataException("The PLY header is missing or too large.");
        }

        var header = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, headerEnd + 10)]);
        var format = "unknown";
        long vertices = 0, faces = 0;
        var properties = 0;
        foreach (var line in EnumerateLines(header).Take(512))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0] == "format") format = parts[1];
            else if (parts.Length >= 3 && parts[0] == "element" &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                if (parts[1] == "vertex") vertices = count;
                else if (parts[1] == "face") faces = count;
            }
            else if (parts.Length >= 2 && parts[0] == "property") properties++;
        }

        return new StringBuilder()
            .AppendLine("Polygon File Format")
            .AppendLine()
            .Append("Encoding: ").AppendLine(format)
            .Append("Vertices: ").AppendLine(vertices.ToString())
            .Append("Faces: ").AppendLine(faces.ToString())
            .Append("Properties: ").AppendLine(properties.ToString())
            .Append("Preview truncated: ").AppendLine(truncated ? "yes" : "no")
            .AppendLine()
            .AppendLine("Header metadata only; geometry and external resources are not loaded.")
            .ToString();
    }

    private static string ReadGltf(ReadOnlySpan<byte> json, bool truncated)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        });
        return BuildGltfSummary(document.RootElement, "glTF JSON", truncated);
    }

    private static string ReadGlb(ReadOnlySpan<byte> bytes, long fileLength)
    {
        if (bytes.Length < 20 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67)
        {
            throw new InvalidDataException("The GLB header is invalid.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
        var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
        var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4));
        if (version != 2 || declaredLength > fileLength || chunkType != 0x4E4F534A ||
            chunkLength > bytes.Length - 20)
        {
            throw new InvalidDataException("The GLB JSON chunk is invalid.");
        }

        using var document = JsonDocument.Parse(bytes.Slice(20, checked((int)chunkLength)).ToArray(),
            new JsonDocumentOptions { MaxDepth = 64 });
        return BuildGltfSummary(document.RootElement, "Binary glTF 2.0", declaredLength > bytes.Length);
    }

    private static string BuildGltfSummary(JsonElement root, string label, bool truncated)
    {
        var version = root.TryGetProperty("asset", out var asset) &&
            asset.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString() ?? "—"
            : "—";
        var externalUris = 0;
        if (root.TryGetProperty("buffers", out var buffers) && buffers.ValueKind == JsonValueKind.Array)
        {
            foreach (var buffer in buffers.EnumerateArray())
            {
                if (buffer.TryGetProperty("uri", out var uri) &&
                    uri.ValueKind == JsonValueKind.String &&
                    !uri.GetString()!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    externalUris++;
                }
            }
        }

        return new StringBuilder()
            .AppendLine(label)
            .AppendLine()
            .Append("Asset version: ").AppendLine(version)
            .Append("Scenes: ").AppendLine(GetArrayLength(root, "scenes").ToString())
            .Append("Nodes: ").AppendLine(GetArrayLength(root, "nodes").ToString())
            .Append("Meshes: ").AppendLine(GetArrayLength(root, "meshes").ToString())
            .Append("Materials: ").AppendLine(GetArrayLength(root, "materials").ToString())
            .Append("Animations: ").AppendLine(GetArrayLength(root, "animations").ToString())
            .Append("External resources ignored: ").AppendLine(externalUris.ToString())
            .Append("Preview truncated: ").AppendLine(truncated ? "yes" : "no")
            .ToString();
    }

    private static string ReadDxf(string text, bool truncated, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = EnumerateLines(text).Take(MaximumLines * 2 + 2).ToArray();
        if (lines.Length >= MaximumLines * 2)
        {
            truncated = true;
        }

        for (var index = 0; index + 1 < lines.Length; index += 2)
        {
            if ((index & 2047) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!int.TryParse(lines[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                continue;
            }

            var value = lines[index + 1].Trim();
            if (code == 0 && value is "LINE" or "LWPOLYLINE" or "POLYLINE" or "CIRCLE" or
                "ARC" or "3DFACE" or "INSERT" or "TEXT" or "MTEXT")
            {
                counts[value] = counts.GetValueOrDefault(value) + 1;
            }
            else if (code == 8 && layers.Count < 256)
            {
                layers.Add(value[..Math.Min(value.Length, 256)]);
            }
        }

        var builder = new StringBuilder()
            .AppendLine("Drawing Exchange Format")
            .AppendLine()
            .Append("Layers shown: ").AppendLine(layers.Count.ToString());
        foreach (var (entity, count) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(entity).Append(": ").AppendLine(count.ToString());
        }

        builder.Append("Preview truncated: ").AppendLine(truncated ? "yes" : "no")
            .AppendLine()
            .AppendLine("External references and block contents are not expanded.");
        return builder.ToString();
    }

    private static string BuildGeometrySummary(
        string format,
        long vertices,
        long faces,
        Bounds3 bounds,
        bool truncated)
    {
        var builder = new StringBuilder()
            .AppendLine(format)
            .AppendLine()
            .Append("Vertices: ").AppendLine(vertices.ToString())
            .Append("Faces/triangles: ").AppendLine(faces.ToString());
        if (bounds.HasValue)
        {
            builder.Append("Bounds min: ").AppendLine(bounds.FormatMin())
                .Append("Bounds max: ").AppendLine(bounds.FormatMax())
                .Append("Dimensions: ").AppendLine(bounds.FormatDimensions());
        }

        return builder.Append("Preview truncated: ").AppendLine(truncated ? "yes" : "no")
            .AppendLine()
            .AppendLine("Geometry summary only; materials and external resources are not loaded.")
            .ToString();
    }

    private static int GetArrayLength(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string DecodeText(ReadOnlySpan<byte> bytes) =>
        PreviewTextReader.Decode(bytes) ??
        throw new InvalidDataException("The model text could not be decoded.");

    private static IEnumerable<string> EnumerateLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static bool TryReadVector(
        ReadOnlySpan<char> value,
        out double x,
        out double y,
        out double z)
    {
        x = y = z = 0;
        var parts = value.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z) &&
            double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z);
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));

    private static int FindAscii(ReadOnlySpan<byte> bytes, string value) =>
        bytes.IndexOf(Encoding.ASCII.GetBytes(value));

    private sealed class Bounds3
    {
        private double _minX = double.PositiveInfinity, _minY = double.PositiveInfinity, _minZ = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity, _maxY = double.NegativeInfinity, _maxZ = double.NegativeInfinity;

        internal bool HasValue => double.IsFinite(_minX);

        internal void Add(double x, double y, double z)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            {
                return;
            }

            _minX = Math.Min(_minX, x); _minY = Math.Min(_minY, y); _minZ = Math.Min(_minZ, z);
            _maxX = Math.Max(_maxX, x); _maxY = Math.Max(_maxY, y); _maxZ = Math.Max(_maxZ, z);
        }

        internal string FormatMin() => Format(_minX, _minY, _minZ);
        internal string FormatMax() => Format(_maxX, _maxY, _maxZ);
        internal string FormatDimensions() => Format(_maxX - _minX, _maxY - _minY, _maxZ - _minZ);

        private static string Format(double x, double y, double z) =>
            FormattableString.Invariant($"{x:G6} × {y:G6} × {z:G6}");
    }
}
