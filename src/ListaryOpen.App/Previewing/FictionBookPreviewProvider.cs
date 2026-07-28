using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ListaryOpen.App.Previewing;

internal sealed class FictionBookPreviewProvider : IFilePreviewProvider
{
    private const int MaximumInputBytes = 8 * 1024 * 1024;
    private const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.FictionBook) ||
        PreviewFormatRegistry.IsFictionBookZip(context.Name);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        using var file = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (context.Extension.Equals(".fb2", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewContent.ForText("FictionBook text", ReadXml(file, cancellationToken));
        }

        ZipPreviewBudget.ValidatePackage(file);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase) &&
            candidate.Length <= MaximumInputBytes);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return PreviewContent.ForText("FictionBook text", ReadXml(stream, cancellationToken));
    }

    private static string ReadXml(Stream stream, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumInputBytes,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(stream, settings);
        var binaryDepth = -1;
        while (reader.Read() && builder.Length < MaximumOutputCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName.Equals("binary", StringComparison.OrdinalIgnoreCase))
                {
                    binaryDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                }
                else if (reader.LocalName is "p" or "v" or "subtitle" or "title")
                {
                    builder.AppendLine();
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName.Equals("binary", StringComparison.OrdinalIgnoreCase) &&
                reader.Depth == binaryDepth)
            {
                binaryDepth = -1;
            }
            else if (binaryDepth < 0 && reader.NodeType is (XmlNodeType.Text or XmlNodeType.CDATA))
            {
                var value = reader.Value.Trim();
                if (value.Length > 0)
                {
                    var remaining = MaximumOutputCharacters - builder.Length;
                    builder.Append(value, 0, Math.Min(value.Length, remaining));
                    if (builder.Length < MaximumOutputCharacters)
                    {
                        builder.Append(' ');
                    }
                }
            }
        }

        if (builder.Length >= MaximumOutputCharacters)
        {
            builder.Length = MaximumOutputCharacters;
            builder.AppendLine().Append('…');
        }
        return builder.ToString().Trim();
    }
}
