using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads bounded event-record metadata from an EVTX file.  Event messages are
/// deliberately not formatted because that can load provider resources; no event
/// payload is executed, rendered, or exported.
/// </summary>
internal sealed class WindowsEventLogPreviewProvider : IFilePreviewProvider
{
    internal const long MaximumInputBytes = 8L * 1024 * 1024 * 1024;
    internal const int MaximumEvents = 200;
    internal const int MaximumOutputCharacters = 120_000;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.EventLog);

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
        if (!LooksLikeEventLog(context.FullPath))
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Windows Event Log metadata");
            builder.Append("Size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes));
            builder.AppendLine();
            builder.AppendLine("Events (newest first; payloads and message resources are not read):");

            var count = 0;
            var query = new EventLogQuery(context.FullPath, PathType.FilePath)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };
            using var reader = new EventLogReader(query);
            while (count < MaximumEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                var line = new StringBuilder("- ")
                    .Append(record.TimeCreated?.ToString("u") ?? "(time unavailable)")
                    .Append(" provider=").Append(Sanitize(record.ProviderName))
                    .Append(" id=").Append(record.Id)
                    .Append(" level=").Append(record.Level?.ToString() ?? "-")
                    .Append(" task=").Append(record.Task?.ToString() ?? "-")
                    .Append(" opcode=").Append(record.Opcode?.ToString() ?? "-")
                    .Append(" record=").Append(record.RecordId?.ToString() ?? "-")
                    .ToString();
                if (builder.Length + line.Length + Environment.NewLine.Length > MaximumOutputCharacters)
                {
                    break;
                }
                builder.AppendLine(line);
                count++;
            }

            if (count == 0)
            {
                builder.AppendLine("(no readable events)");
            }
            else if (count >= MaximumEvents)
            {
                builder.AppendLine("… event listing truncated after 200 records.");
            }

            builder.AppendLine();
            builder.AppendLine("Only bounded event metadata was read. Event payloads, rendered messages, and provider resources were not loaded.");
            builder.AppendLine("The log is never modified, replayed, or executed.");
            var text = builder.Length <= MaximumOutputCharacters
                ? builder.ToString()
                : builder.ToString(0, MaximumOutputCharacters - 1) + "…";
            return PreviewContent.ForText("Windows Event Log metadata", text);
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
            return null;
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(unknown)";
        }
        var builder = new StringBuilder(Math.Min(value.Length, 256));
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
            if (builder.Length >= 256)
            {
                break;
            }
        }
        return builder.Length == 0 ? "(unknown)" : builder.ToString();
    }

    private static bool LooksLikeEventLog(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < header.Length)
        {
            return false;
        }
        stream.ReadExactly(header);
        return header.SequenceEqual("ElfFile\0"u8);
    }
}
