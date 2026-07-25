using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ListaryOpen.App.Previewing;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.App;

public partial class FilePreviewPane : UserControl
{
    /// <summary>Hard ceiling for a single preview load (built-in providers + shell path).</summary>
    internal static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(5);

    private readonly PreviewCoordinator _previewCoordinator = new();
    private CancellationTokenSource? _previewCancellation;
    private long _previewRequestId;

    public FilePreviewPane()
    {
        InitializeComponent();
        Unloaded += (_, _) => Clear();
    }

    internal async Task ShowPreviewAsync(FileRecord? record)
    {
        var requestId = Interlocked.Increment(ref _previewRequestId);
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var userCancellation = _previewCancellation.Token;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(userCancellation);
        timeoutCts.CancelAfter(PreviewTimeout);
        var cancellationToken = timeoutCts.Token;
        ShellHost.ClearPreview();
        ResetContent();

        if (record is null)
        {
            PreviewName.Text = string.Empty;
            PreviewPath.Text = string.Empty;
            PreviewDetails.Text = string.Empty;
            PreviewSource.Text = string.Empty;
            EmptyMessage.Text = LocalizationManager.Translate("Select a result to preview");
            FileIcon.SetPath(PreviewIcon, null);
            return;
        }

        PreviewName.Text = record.Name;
        PreviewPath.Text = record.FullPath;
        PreviewDetails.Text = CreateDetails(record);
        PreviewSource.Text = string.Empty;
        FileIcon.SetIsDirectory(PreviewIcon, record.IsDirectory);
        FileIcon.SetPath(PreviewIcon, record.FullPath);
        EmptyMessage.Text = LocalizationManager.Translate(record.IsDirectory ? "Folder" : "Preview is not available");
        if (record.IsDirectory || !File.Exists(record.FullPath))
        {
            return;
        }

        var context = new PreviewContext(
            record.FullPath,
            record.Name,
            Path.GetExtension(record.Name),
            record.SizeBytes,
            record.LastWriteTime);
        try
        {
            EmptyMessage.Text = LocalizationManager.Translate("Loading preview…");
            await PreviewCoordinator.WaitForSelectionAsync(cancellationToken);
            ThrowIfStale(requestId, cancellationToken);

            // Prefer built-in providers first (cancellable/timeout-friendly), then shell handlers.
            var content = await _previewCoordinator.LoadAsync(context, cancellationToken);
            ThrowIfStale(requestId, cancellationToken);
            if (content is not null)
            {
                ApplyContent(content, requestId);
                return;
            }

            if (PreviewCoordinator.ShouldPreferSystemPreview(context)
                && await TrySystemPreviewWithTimeoutAsync(record.FullPath, requestId, cancellationToken))
            {
                return;
            }

            if (requestId == Volatile.Read(ref _previewRequestId))
            {
                EmptyMessage.Text = LocalizationManager.Translate("Preview is not available");
            }
        }
        catch (OperationCanceledException) when (userCancellation.IsCancellationRequested)
        {
            // User switched selection or closed pane — ignore.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requestId == Volatile.Read(ref _previewRequestId))
            {
                ShellHost.ClearPreview();
                EmptyMessage.Text = LocalizationManager.Translate("Preview timed out");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or InvalidDataException or FormatException or
            System.Runtime.InteropServices.COMException)
        {
            if (requestId == Volatile.Read(ref _previewRequestId))
            {
                EmptyMessage.Text = LocalizationManager.Translate("Could not load preview");
            }
        }
    }

    private async Task<bool> TrySystemPreviewWithTimeoutAsync(
        string fullPath,
        long requestId,
        CancellationToken cancellationToken)
    {
        // Shell DoPreview is synchronous COM; race against the shared deadline token so a
        // hung handler does not keep the pane in "Loading…" forever after CancelAfter fires.
        var previewTask = Dispatcher.InvokeAsync(
            () => ShellHost.TryPreview(fullPath),
            System.Windows.Threading.DispatcherPriority.Background).Task;

        try
        {
            var success = await previewTask.WaitAsync(cancellationToken).ConfigureAwait(true);
            ThrowIfStale(requestId, cancellationToken);
            if (!success)
            {
                return false;
            }

            if (requestId != Volatile.Read(ref _previewRequestId))
            {
                ShellHost.ClearPreview();
                return false;
            }

            PreviewSource.Text = LocalizationManager.Translate("Preview source: Windows system");
            ShowOnly(ShellHost);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ShellHost.ClearPreview();
            throw;
        }
    }

    internal void Clear()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        Interlocked.Increment(ref _previewRequestId);
        ShellHost.ClearPreview();
        ResetContent();
    }

    private void ResetContent()
    {
        PreviewImage.Source = null;
        PreviewText.Text = string.Empty;
        PreviewText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        FontPreviewHeading.Text = string.Empty;
        FontPreviewSample.FontFamily = new FontFamily("Segoe UI");
        ImagePreviewSurface.Visibility = Visibility.Collapsed;
        FontPreviewSurface.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        ShellHost.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
    }

    private void ShowOnly(UIElement element)
    {
        ImagePreviewSurface.Visibility = ReferenceEquals(element, ImagePreviewSurface) ? Visibility.Visible : Visibility.Collapsed;
        FontPreviewSurface.Visibility = ReferenceEquals(element, FontPreviewSurface) ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Visibility = ReferenceEquals(element, PreviewText) ? Visibility.Visible : Visibility.Collapsed;
        ShellHost.Visibility = ReferenceEquals(element, ShellHost) ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Collapsed;
    }

    private void ApplyContent(PreviewContent content, long requestId)
    {
        if (requestId != Volatile.Read(ref _previewRequestId))
        {
            return;
        }

        PreviewSource.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Translate("Preview source: {0}"),
            LocalizationManager.Translate(content.Source));
        switch (content.Kind)
        {
            case PreviewContentKind.Image when content.Image is not null:
                PreviewImage.Source = content.Image;
                ShowOnly(ImagePreviewSurface);
                break;
            case PreviewContentKind.Font when content.FontFamily is not null:
                FontPreviewHeading.Text = content.Heading ?? string.Empty;
                FontPreviewSample.FontFamily = content.FontFamily;
                ShowOnly(FontPreviewSurface);
                break;
            default:
                PreviewText.Text = content.Text ?? string.Empty;
                ShowOnly(PreviewText);
                break;
        }
    }

    private void ThrowIfStale(long requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestId != Volatile.Read(ref _previewRequestId))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static string CreateDetails(FileRecord record)
    {
        var size = record.IsDirectory ? "—" : FormatSize(record.SizeBytes);
        DateTimeOffset created;
        try
        {
            created = record.IsDirectory
                ? Directory.GetCreationTime(record.FullPath)
                : File.GetCreationTime(record.FullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            created = DateTimeOffset.MinValue;
        }

        return string.Join(
            Environment.NewLine,
            size,
            created == DateTimeOffset.MinValue ? "—" : created.ToString("g"),
            record.LastWriteTime.ToLocalTime().ToString("g"));
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }
}
