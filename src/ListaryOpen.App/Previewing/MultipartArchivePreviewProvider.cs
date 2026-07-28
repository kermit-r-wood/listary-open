using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ListaryOpen.App.Previewing;

internal sealed class MultipartArchivePreviewProvider : IFilePreviewProvider
{
    private const int MaximumParts = 512;
    private static readonly Regex NumberedSuffix = new(
        @"(?ix)^(?<base>.+\.(?:7z|zip))\.(?<part>\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex RarPart = new(
        @"(?ix)^(?<base>.+)\.part(?<part>\d+)\.rar$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex LegacyPart = new(
        @"(?ix)^(?<base>.+)\.(?<kind>[rz])(?<part>\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.IsMultipartArchive(context.Name);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        var descriptor = Describe(context.Name);
        if (descriptor is null)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(context.FullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var parts = new List<(string Name, long Size)>();
        foreach (var path in Directory.EnumerateFiles(directory).Take(MaximumParts + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var candidate = Describe(name);
            if (name.Equals(descriptor.FirstVolume, StringComparison.OrdinalIgnoreCase) ||
                candidate is not null &&
                candidate.Series.Equals(descriptor.Series, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    parts.Add((name, new FileInfo(path).Length));
                }
                catch (IOException)
                {
                    parts.Add((name, 0));
                }
            }
        }

        parts.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        long total = 0;
        foreach (var part in parts.Take(MaximumParts))
        {
            total = part.Size > 0 && total > long.MaxValue - part.Size ? long.MaxValue : total + Math.Max(0, part.Size);
        }
        var builder = new StringBuilder("Multipart archive").AppendLine().AppendLine()
            .Append("Series: ").AppendLine(descriptor.Series)
            .Append("Current volume: ").AppendLine(context.Name)
            .Append("Volume number: ").AppendLine(descriptor.Part)
            .Append("Volumes found: ").Append(parts.Count > MaximumParts ? $"{MaximumParts}+" : parts.Count).AppendLine()
            .Append("Combined size shown: ").AppendLine(FilePreviewPane.FormatSize(total))
            .Append("Expected first volume: ").AppendLine(descriptor.FirstVolume)
            .AppendLine()
            .AppendLine("Volumes are not extracted or combined during preview.");
        return PreviewContent.ForText("Multipart archive metadata", builder.ToString());
    }

    private static Descriptor? Describe(string name)
    {
        var match = NumberedSuffix.Match(name);
        if (match.Success)
        {
            return new Descriptor(
                match.Groups["base"].Value,
                match.Groups["part"].Value,
                match.Groups["base"].Value + ".001");
        }
        match = RarPart.Match(name);
        if (match.Success)
        {
            var width = match.Groups["part"].Value.Length;
            return new Descriptor(
                match.Groups["base"].Value,
                match.Groups["part"].Value,
                $"{match.Groups["base"].Value}.part{1.ToString($"D{width}")}.rar");
        }
        match = LegacyPart.Match(name);
        if (match.Success)
        {
            var kind = match.Groups["kind"].Value;
            return new Descriptor(
                match.Groups["base"].Value + "." + kind,
                match.Groups["part"].Value,
                kind.Equals("z", StringComparison.OrdinalIgnoreCase)
                    ? match.Groups["base"].Value + ".zip"
                    : match.Groups["base"].Value + ".rar");
        }
        return null;
    }

    private sealed record Descriptor(string Series, string Part, string FirstVolume);
}
