using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XstReader;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads the safe, folder-level portion of a PST/OST file.  XstReader's message
/// enumeration performs signed/encrypted message processing, so this provider
/// deliberately never touches Messages, subjects, bodies, recipients, or
/// attachments.
/// </summary>
internal sealed class OutlookStorePreviewProvider : IFilePreviewProvider
{
    internal const long MaximumInputBytes = 64L * 1024 * 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.OutlookStore);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Load(context, cancellationToken), cancellationToken);
    }

    private static PreviewContent? Load(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SizeBytes <= 0 || context.SizeBytes > MaximumInputBytes)
        {
            return null;
        }

        var kind = ReadSignature(context.FullPath, context.Extension);
        if (kind is null)
        {
            return null;
        }

        try
        {
            using var file = new XstFile(context.FullPath);
            var root = file.RootFolder;
            var text = OutlookStoreFolderSummary.Build(
                new XstFolderNode(root),
                kind,
                context.SizeBytes,
                cancellationToken);
            return PreviewContent.ForText("Outlook store folder structure", text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not StackOverflowException &&
            exception is not AccessViolationException)
        {
            // A corrupt, locked, password-protected, or unsupported store is
            // intentionally allowed to fall through to the metadata provider.
            return null;
        }
    }

    private static string? ReadSignature(string path, string extension)
    {
        if (!extension.Equals(".pst", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Span<byte> signature = stackalloc byte[12];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < signature.Length)
        {
            return null;
        }
        stream.ReadExactly(signature);
        if (!signature[..4].SequenceEqual("!BDN"u8))
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(signature[10..]);
        var generation = version >= 23 ? "Unicode" : version > 0 ? "ANSI" : "unknown generation";
        return extension.Equals(".ost", StringComparison.OrdinalIgnoreCase)
            ? $"Outlook Offline Storage ({generation})"
            : $"Outlook Personal Folders ({generation})";
    }

    private sealed class XstFolderNode(XstFolder folder) : IOutlookStoreFolderNode
    {
        public string DisplayName => folder.DisplayName;

        public int ContentCount => folder.ContentCount;

        public int ContentUnreadCount => folder.ContentUnreadCount;

        public IEnumerable<IOutlookStoreFolderNode> Children =>
            folder.Folders.Select(static child => (IOutlookStoreFolderNode)new XstFolderNode(child));
    }
}

internal interface IOutlookStoreFolderNode
{
    string? DisplayName { get; }

    int ContentCount { get; }

    int ContentUnreadCount { get; }

    IEnumerable<IOutlookStoreFolderNode> Children { get; }
}

internal static class OutlookStoreFolderSummary
{
    internal const int MaximumFolders = 500;
    internal const int MaximumDepth = 32;
    internal const int MaximumNameCharacters = 512;
    internal const int MaximumOutputCharacters = 120_000;

    internal static string Build(
        IOutlookStoreFolderNode root,
        string storeKind,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Outlook store folder structure");
        builder.Append("Type: ").AppendLine(storeKind);
        builder.Append("Size: ").AppendLine(FilePreviewPane.FormatSize(sizeBytes));
        builder.AppendLine();
        builder.AppendLine("Folder listing (message counts are stored folder metadata):");

        var queue = new Queue<(IOutlookStoreFolderNode Node, int Depth)>();
        queue.Enqueue((root, 0));
        var foldersShown = 0;
        var folderLimitReached = false;
        var depthLimitReached = false;
        var childEnumerationFailed = false;
        var outputLimitReached = false;

        while (queue.Count > 0 && foldersShown < MaximumFolders && !outputLimitReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, depth) = queue.Dequeue();
            foldersShown++;

            var name = SanitizeName(node.DisplayName);
            var indent = new string(' ', Math.Min(depth, MaximumDepth) * 2);
            var folderLine = new StringBuilder(indent)
                .Append("- ").Append(name)
                .Append(" [messages: ").Append(Math.Max(0, node.ContentCount))
                .Append(", unread: ").Append(Math.Max(0, node.ContentUnreadCount))
                .Append("]")
                .ToString();
            if (!TryAppendListingLine(builder, folderLine))
            {
                outputLimitReached = true;
                break;
            }

            if (depth >= MaximumDepth)
            {
                depthLimitReached = true;
                continue;
            }

            IEnumerable<IOutlookStoreFolderNode> children;
            try
            {
                children = node.Children;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException &&
                                               exception is not StackOverflowException &&
                                               exception is not AccessViolationException)
            {
                childEnumerationFailed = true;
                continue;
            }

            try
            {
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child is null)
                    {
                        continue;
                    }
                    if (foldersShown + queue.Count >= MaximumFolders)
                    {
                        folderLimitReached = true;
                        break;
                    }
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException &&
                                               exception is not StackOverflowException &&
                                               exception is not AccessViolationException)
            {
                childEnumerationFailed = true;
            }
        }

        if (foldersShown >= MaximumFolders || folderLimitReached || outputLimitReached)
        {
            TryAppendListingLine(builder, "… folder listing truncated by the folder/output budget.");
        }
        if (depthLimitReached)
        {
            builder.AppendLine("… folder listing depth capped at 32 levels.");
        }
        if (childEnumerationFailed)
        {
            builder.AppendLine("… one or more subfolder lists could not be read.");
        }

        builder.AppendLine();
        builder.AppendLine("This preview reads only folder names and stored message/unread counts.");
        builder.AppendLine("Message subjects, bodies, recipients, and attachments were not read or saved.");
        builder.AppendLine("The file is never modified, mounted, or imported.");

        return builder.Length <= MaximumOutputCharacters
            ? builder.ToString()
            : builder.ToString(0, MaximumOutputCharacters - 1) + "…";
    }

    private static bool TryAppendListingLine(StringBuilder builder, string line)
    {
        const int reservedFooterCharacters = 768;
        var required = line.Length + Environment.NewLine.Length;
        return builder.Length + required + reservedFooterCharacters <= MaximumOutputCharacters &&
            builder.AppendLine(line) is not null;
    }

    private static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(unnamed folder)";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaximumNameCharacters));
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
            if (builder.Length >= MaximumNameCharacters)
            {
                break;
            }
        }

        return builder.Length == 0 ? "(unnamed folder)" : builder.ToString();
    }
}
