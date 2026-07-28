using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ListaryOpen.App.Previewing;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.App;

public partial class FilePreviewPane : UserControl
{
    /// <summary>Hard ceiling for a single preview load (built-in providers + shell path).</summary>
    internal static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(5);

    private readonly PreviewCoordinator _previewCoordinator = new();
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _previewCancellation;
    private long _previewRequestId;
    private long _mediaRequestId;
    private TimeSpan _mediaDuration;
    private string? _mediaFallbackText;
    private bool _mediaPlaying;

    public FilePreviewPane()
    {
        InitializeComponent();
        _mediaTimer.Tick += MediaTimer_Tick;
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

            // Rich Windows handlers preserve document layout, PDF pages and media controls.
            // The built-in providers remain a deterministic, safe fallback when no matching
            // handler is installed or a handler declines the file.
            if (PreviewCoordinator.ShouldPreferSystemPreview(context)
                && await TrySystemPreviewWithTimeoutAsync(record.FullPath, requestId, cancellationToken))
            {
                return;
            }

            var content = await _previewCoordinator.LoadAsync(context, cancellationToken);
            ThrowIfStale(requestId, cancellationToken);
            if (content is not null)
            {
                ApplyContent(content, requestId);
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
        ResetMedia();
        PreviewImage.Source = null;
        PreviewText.Text = string.Empty;
        PreviewText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        FontPreviewHeading.Text = string.Empty;
        FontPreviewSample.FontFamily = new FontFamily("Segoe UI");
        ImagePreviewSurface.Visibility = Visibility.Collapsed;
        FontPreviewSurface.Visibility = Visibility.Collapsed;
        MediaPreviewSurface.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        ShellHost.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
    }

    private void ShowOnly(UIElement element)
    {
        ImagePreviewSurface.Visibility = ReferenceEquals(element, ImagePreviewSurface) ? Visibility.Visible : Visibility.Collapsed;
        FontPreviewSurface.Visibility = ReferenceEquals(element, FontPreviewSurface) ? Visibility.Visible : Visibility.Collapsed;
        MediaPreviewSurface.Visibility = ReferenceEquals(element, MediaPreviewSurface) ? Visibility.Visible : Visibility.Collapsed;
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
            case PreviewContentKind.Media when content.MediaPath is not null:
                _mediaRequestId = requestId;
                _mediaFallbackText = content.Text;
                PreviewMedia.Source = new Uri(content.MediaPath, UriKind.Absolute);
                MediaPlayPauseButton.Content = "▶";
                MediaMuteButton.Content = "🔊";
                MediaTimeText.Text = "00:00 / 00:00";
                ShowOnly(MediaPreviewSurface);
                break;
            default:
                PreviewText.Text = content.Text ?? string.Empty;
                ShowOnly(PreviewText);
                break;
        }
    }

    private void PreviewMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_mediaRequestId != Volatile.Read(ref _previewRequestId) ||
            MediaPreviewSurface.Visibility != Visibility.Visible)
        {
            ResetMedia();
            return;
        }

        _mediaDuration = PreviewMedia.NaturalDuration.HasTimeSpan
            ? PreviewMedia.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        MediaPositionSlider.Maximum = Math.Max(1, _mediaDuration.TotalSeconds);
        AudioPlaceholder.Visibility = PreviewMedia.NaturalVideoWidth == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateMediaPosition();
    }

    private void PreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        _mediaPlaying = false;
        _mediaTimer.Stop();
        PreviewMedia.Position = TimeSpan.Zero;
        MediaPlayPauseButton.Content = "▶";
        UpdateMediaPosition();
    }

    private void PreviewMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_mediaRequestId != Volatile.Read(ref _previewRequestId))
        {
            return;
        }

        var fallbackText = _mediaFallbackText ?? "The installed Windows media codecs could not open this file.";
        ResetMedia();
        PreviewText.Text = fallbackText;
        PreviewSource.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Translate("Preview source: {0}"),
            LocalizationManager.Translate("Media metadata"));
        ShowOnly(PreviewText);
    }

    private void MediaPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewMedia.Source is null)
        {
            return;
        }

        if (_mediaPlaying)
        {
            PreviewMedia.Pause();
            _mediaTimer.Stop();
            MediaPlayPauseButton.Content = "▶";
        }
        else
        {
            PreviewMedia.Play();
            _mediaTimer.Start();
            MediaPlayPauseButton.Content = "⏸";
        }

        _mediaPlaying = !_mediaPlaying;
    }

    private void MediaMuteButton_Click(object sender, RoutedEventArgs e)
    {
        PreviewMedia.IsMuted = !PreviewMedia.IsMuted;
        MediaMuteButton.Content = PreviewMedia.IsMuted ? "🔇" : "🔊";
    }

    private void MediaPositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mediaDuration > TimeSpan.Zero)
        {
            PreviewMedia.Position = TimeSpan.FromSeconds(
                Math.Clamp(MediaPositionSlider.Value, 0, _mediaDuration.TotalSeconds));
            UpdateMediaPosition();
        }
    }

    private void MediaTimer_Tick(object? sender, EventArgs e)
    {
        if (_mediaRequestId != Volatile.Read(ref _previewRequestId) ||
            MediaPreviewSurface.Visibility != Visibility.Visible)
        {
            ResetMedia();
            return;
        }

        UpdateMediaPosition();
    }

    private void UpdateMediaPosition()
    {
        var position = PreviewMedia.Position;
        MediaPositionSlider.Value = Math.Clamp(
            position.TotalSeconds,
            0,
            Math.Max(1, MediaPositionSlider.Maximum));
        MediaTimeText.Text = $"{FormatMediaTime(position)} / {FormatMediaTime(_mediaDuration)}";
    }

    private void ResetMedia()
    {
        _mediaTimer.Stop();
        _mediaPlaying = false;
        try
        {
            PreviewMedia.Stop();
            PreviewMedia.Close();
        }
        catch (InvalidOperationException)
        {
        }

        PreviewMedia.Source = null;
        PreviewMedia.IsMuted = false;
        _mediaRequestId = 0;
        _mediaDuration = TimeSpan.Zero;
        _mediaFallbackText = null;
        MediaPositionSlider.Value = 0;
        MediaPositionSlider.Maximum = 1;
        MediaPlayPauseButton.Content = "▶";
        MediaMuteButton.Content = "🔊";
        MediaTimeText.Text = "00:00 / 00:00";
        AudioPlaceholder.Visibility = Visibility.Collapsed;
    }

    internal static string FormatMediaTime(TimeSpan value)
    {
        var bounded = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return bounded.TotalHours >= 1
            ? bounded.ToString(@"h\:mm\:ss")
            : bounded.ToString(@"mm\:ss");
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
