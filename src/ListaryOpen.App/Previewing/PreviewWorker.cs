using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ListaryOpen.App.Previewing;

internal sealed class IsolatedTextPreviewProvider : IFilePreviewProvider
{
    private const PreviewFallback IsolatedFallbacks =
        PreviewFallback.Document |
        PreviewFallback.Archive |
        PreviewFallback.Pdf |
        PreviewFallback.Database |
        PreviewFallback.Shortcut |
        PreviewFallback.Model |
        PreviewFallback.Email |
        PreviewFallback.Ebook |
        PreviewFallback.WebFont |
        PreviewFallback.FictionBook |
        PreviewFallback.MultipartArchive |
        PreviewFallback.Pim |
        PreviewFallback.Executable |
        PreviewFallback.Opaque |
        PreviewFallback.OutlookMessage |
        PreviewFallback.WebArchive |
        PreviewFallback.VirtualDisk |
        PreviewFallback.WindowsFilter |
        PreviewFallback.OutlookStore |
        PreviewFallback.EventLog |
        PreviewFallback.CompoundDocument |
        PreviewFallback.LhaArchive |
        PreviewFallback.DjvuDocument |
        PreviewFallback.PostScriptVector |
        PreviewFallback.ChmDocument;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.IsMultipartArchive(context.Name) ||
        PreviewFormatRegistry.IsFictionBookZip(context.Name) ||
        PreviewFormatRegistry.TryGet(context.Extension, out var format) &&
        (format.Fallbacks & IsolatedFallbacks) != 0;

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken)
    {
        var executable = PreviewWorkerClient.ResolveExecutablePath();
        return executable is null
            ? Task.FromResult<PreviewContent?>(null)
            : PreviewWorkerClient.LoadAsync(executable, context, cancellationToken);
    }
}

internal static class PreviewWorkerClient
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);

    internal static string? ResolveExecutablePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ListaryOpen.PreviewHost.exe");
        return File.Exists(path) ? path : null;
    }

    internal static async Task<PreviewContent?> LoadAsync(
        string executable,
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(context.FullPath);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(WorkerTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || stdout.Length > PreviewWorker.MaximumProtocolCharacters)
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<PreviewWorkerResult>(stdout);
            return result is null || string.IsNullOrEmpty(result.Source)
                ? null
                : PreviewContent.ForText(result.Source, result.Text ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

internal static class PreviewWorker
{
    internal const int MaximumProtocolCharacters = 256 * 1024;
    private static readonly IReadOnlyList<IFilePreviewProvider> Providers =
    [
        new KfxPreviewProvider(),
        new AccessDatabasePreviewProvider(),
        new TorrentPreviewProvider(),
        new PacketCapturePreviewProvider(),
        new BinaryStructurePreviewProvider(),
        new PostScriptPreviewProvider(),
        new ChmPreviewProvider(),
        new WindowsFilterPreviewProvider(),
        new CompoundDocumentPreviewProvider(),
        new DocumentPreviewProvider(),
        new FictionBookPreviewProvider(),
        new MultipartArchivePreviewProvider(),
        new WindowsImagePreviewProvider(),
        new ArchivePreviewProvider(),
        new PdfPreviewProvider(),
        new DatabasePreviewProvider(),
        new ShortcutPreviewProvider(),
        new ModelPreviewProvider(),
        new EmailPreviewProvider(),
        new OutlookMessagePreviewProvider(),
        new OutlookStorePreviewProvider(),
        new WindowsEventLogPreviewProvider(),
        new WebArchivePreviewProvider(),
        new EbookPreviewProvider(),
        new WebFontPreviewProvider(),
        new PimPreviewProvider(),
        new ExecutablePreviewProvider(),
        new ParquetPreviewProvider(),
        new VirtualDiskPreviewProvider(),
        new LhaArchivePreviewProvider(),
        new DjvuPreviewProvider(),
        new OpaqueFilePreviewProvider()
    ];

    internal static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            return 2;
        }
        try
        {
            var path = Path.GetFullPath(args[0]);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return 3;
            }
            var context = new PreviewContext(
                info.FullName,
                info.Name,
                info.Extension,
                info.Length,
                info.LastWriteTimeUtc);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var content = await LoadInProcessAsync(context, timeout.Token).ConfigureAwait(false);
            var result = content is null
                ? new PreviewWorkerResult(null, null)
                : new PreviewWorkerResult(content.Source, Bound(content.Text));
            var json = JsonSerializer.Serialize(result);
            await using var output = Console.OpenStandardOutput();
            await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: false);
            await writer.WriteAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 4;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or NotSupportedException or ArgumentException or FormatException or
            OverflowException or System.Xml.XmlException)
        {
            return 5;
        }
    }

    internal static async Task<PreviewContent?> LoadInProcessAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        foreach (var provider in Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.CanPreview(context))
            {
                continue;
            }
            try
            {
                var content = await provider.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                if (content is not null)
                {
                    return content.Kind == PreviewContentKind.Text ? content : null;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or NotSupportedException or ArgumentException or FormatException or
                OverflowException or System.Xml.XmlException or
                System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
            }
        }
        return null;
    }

    private static string? Bound(string? text) =>
        text is null || text.Length <= MaximumProtocolCharacters
            ? text
            : text[..MaximumProtocolCharacters] + Environment.NewLine + "…";
}

internal sealed record PreviewWorkerResult(string? Source, string? Text);
