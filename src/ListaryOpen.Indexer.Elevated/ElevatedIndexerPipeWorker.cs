using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ListaryOpen.Indexer.Elevated;

/// <summary>
/// Elevated-side worker: connects to a pipe created by the medium-integrity app
/// and executes helper commands without additional UAC prompts.
/// </summary>
internal static class ElevatedIndexerPipeWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return 2;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WorkerRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<WorkerRequest>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    await WriteResponseAsync(writer, exitCode: 2, error: "Invalid worker request JSON.", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Cmd))
                {
                    await WriteResponseAsync(writer, exitCode: 2, error: "Worker request is missing cmd.", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(request.Cmd, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(writer, exitCode: 0, error: null, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var exitCode = await ExecuteAsync(request).ConfigureAwait(false);
                await WriteResponseAsync(writer, exitCode, error: null, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static Task<int> ExecuteAsync(WorkerRequest request)
    {
        var cmd = request.Cmd ?? string.Empty;
        return cmd.ToLowerInvariant() switch
        {
            "scan-to-file" => ElevatedIndexerCommands.ScanToFileAsync(
                request.Root ?? string.Empty,
                request.Output ?? string.Empty,
                request.Error ?? string.Empty),
            "journal-state-to-file" => ElevatedIndexerCommands.JournalStateToFileAsync(
                request.Root ?? string.Empty,
                request.Output ?? string.Empty,
                request.Error ?? string.Empty),
            "read-journal-to-file" => ElevatedIndexerCommands.ReadJournalToFileAsync(
                request.Root ?? string.Empty,
                request.ExpectedUsnJournalId ?? string.Empty,
                request.StartUsn ?? string.Empty,
                request.EndUsn ?? string.Empty,
                request.Output ?? string.Empty,
                request.Error ?? string.Empty),
            _ => Task.FromResult(2)
        };
    }

    private static async Task WriteResponseAsync(
        StreamWriter writer,
        int exitCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new WorkerResponse(exitCode, error),
            JsonOptions);
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private sealed class WorkerRequest
    {
        [JsonPropertyName("cmd")]
        public string? Cmd { get; init; }

        [JsonPropertyName("root")]
        public string? Root { get; init; }

        [JsonPropertyName("output")]
        public string? Output { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("expectedUsnJournalId")]
        public string? ExpectedUsnJournalId { get; init; }

        [JsonPropertyName("startUsn")]
        public string? StartUsn { get; init; }

        [JsonPropertyName("endUsn")]
        public string? EndUsn { get; init; }
    }

    private sealed record WorkerResponse(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("error")] string? Error);
}
