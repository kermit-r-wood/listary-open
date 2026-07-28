using System.IO;
using System.Text;
using OpenMcdf;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Provides a bounded structural preview for legacy compound documents whose
/// format is not safely decoded by the built-in document reader. It walks only
/// the CFB directory; stream payloads are never opened, copied, or decompressed.
/// </summary>
internal sealed class CompoundDocumentPreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 1000;
    private const int MaximumDepth = 16;
    private const int MaximumOutputCharacters = 120_000;
    private static ReadOnlySpan<byte> CompoundFileSignature =>
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.CompoundDocument);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < CompoundFileSignature.Length || context.SizeBytes > MaximumInputBytes)
        {
            return null;
        }

        using var stream = new FileStream(
            context.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(CompoundFileSignature))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var root = RootStorage.OpenRead(context.FullPath, StorageModeFlags.None);
        var builder = new StringBuilder()
            .AppendLine(GetDescription(context.Extension))
            .AppendLine()
            .AppendLine("Compound-file directory (stream payloads were not opened):");
        var state = new WalkState(builder, cancellationToken);
        Walk(root, string.Empty, 0, state);
        if (state.EntryCount == 0)
        {
            builder.AppendLine("(empty directory)");
        }
        if (state.Truncated)
        {
            builder.AppendLine("… directory listing truncated at the preview limit");
        }

        builder.AppendLine()
            .Append("Entries shown: ").Append(state.EntryCount.ToString("N0"));
        return PreviewContent.ForText("Compound document structure", Bound(builder.ToString()));
    }

    private static void Walk(Storage storage, string parentPath, int depth, WalkState state)
    {
        if (state.Truncated || depth > MaximumDepth)
        {
            state.Truncated = true;
            return;
        }

        foreach (var entry in storage.EnumerateEntries())
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (state.EntryCount >= MaximumEntries || state.Builder.Length >= MaximumOutputCharacters)
            {
                state.Truncated = true;
                return;
            }

            state.EntryCount++;
            var path = string.IsNullOrEmpty(parentPath)
                ? entry.Name
                : parentPath + "/" + entry.Name;
            var kind = entry.Type == EntryType.Storage ? "storage" : "stream";
            state.Builder.Append("- [").Append(kind).Append("] ")
                .Append(Truncate(path, 512));
            if (entry.Type == EntryType.Stream)
            {
                state.Builder.Append(" (").Append(entry.Length.ToString("N0")).Append(" bytes)");
            }
            state.Builder.AppendLine();

            if (entry.Type == EntryType.Storage)
            {
                var child = storage.OpenStorage(entry.Name);
                Walk(child, path, depth + 1, state);
            }
        }
    }

    private static string GetDescription(string extension) => extension.ToLowerInvariant() switch
    {
        ".one" => "OneNote compound document",
        ".pub" => "Publisher compound document",
        ".wps" => "Microsoft Works compound document",
        ".et" => "Kingsoft Spreadsheets compound document",
        ".dps" => "Kingsoft Presentation compound document",
        _ => "Compound document"
    };

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private sealed class WalkState(StringBuilder builder, CancellationToken cancellationToken)
    {
        internal StringBuilder Builder { get; } = builder;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal int EntryCount { get; set; }
        internal bool Truncated { get; set; }
    }
}
