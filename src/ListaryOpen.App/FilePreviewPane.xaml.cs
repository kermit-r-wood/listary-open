using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.App;

public partial class FilePreviewPane : UserControl
{
    private const int MaximumTextCharacters = 120_000;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".config", ".cpp", ".cs", ".css", ".csv", ".h", ".htm", ".html",
        ".ini", ".js", ".json", ".log", ".md", ".ps1", ".py", ".rs", ".sql", ".txt",
        ".xaml", ".xml", ".yaml", ".yml"
    };

    private CancellationTokenSource? _previewCancellation;

    public FilePreviewPane()
    {
        InitializeComponent();
        Unloaded += (_, _) => Clear();
    }

    internal async Task ShowPreviewAsync(FileRecord? record)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var cancellationToken = _previewCancellation.Token;
        ShellHost.ClearPreview();
        ResetContent();

        if (record is null)
        {
            PreviewName.Text = string.Empty;
            PreviewPath.Text = string.Empty;
            PreviewDetails.Text = string.Empty;
            EmptyMessage.Text = LocalizationManager.Translate("Select a result to preview");
            FileIcon.SetPath(PreviewIcon, null);
            return;
        }

        PreviewName.Text = record.Name;
        PreviewPath.Text = record.FullPath;
        PreviewDetails.Text = CreateDetails(record);
        FileIcon.SetIsDirectory(PreviewIcon, record.IsDirectory);
        FileIcon.SetPath(PreviewIcon, record.FullPath);
        EmptyMessage.Text = LocalizationManager.Translate(record.IsDirectory ? "Folder" : "Preview is not available");
        if (record.IsDirectory || !File.Exists(record.FullPath))
        {
            return;
        }

        var extension = Path.GetExtension(record.Name);
        try
        {
            if (ImageExtensions.Contains(extension))
            {
                var bitmap = await Task.Run(() => LoadBitmap(record.FullPath), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PreviewImage.Source = bitmap;
                ShowOnly(ImagePreviewSurface);
                return;
            }

            if (TextExtensions.Contains(extension))
            {
                var text = await ReadTextPreviewAsync(record.FullPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (text is not null)
                {
                    PreviewText.Text = text;
                    ShowOnly(PreviewText);
                    return;
                }
            }

            await Task.Delay(100, cancellationToken);
            if (ShellHost.TryPreview(record.FullPath))
            {
                ShowOnly(ShellHost);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            EmptyMessage.Text = LocalizationManager.Translate("Could not load preview");
        }
    }

    internal void Clear()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        ShellHost.ClearPreview();
        ResetContent();
    }

    private void ResetContent()
    {
        PreviewImage.Source = null;
        PreviewText.Text = string.Empty;
        ImagePreviewSurface.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Collapsed;
        ShellHost.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
    }

    private void ShowOnly(UIElement element)
    {
        ImagePreviewSurface.Visibility = ReferenceEquals(element, ImagePreviewSurface) ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Visibility = ReferenceEquals(element, PreviewText) ? Visibility.Visible : Visibility.Collapsed;
        ShellHost.Visibility = ReferenceEquals(element, ShellHost) ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Collapsed;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1000;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task<string?> ReadTextPreviewAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16_384,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaximumTextCharacters];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (buffer.AsSpan(0, count).Contains('\0'))
        {
            return null;
        }

        var text = new string(buffer, 0, count);
        return reader.EndOfStream ? text : text + Environment.NewLine + "…";
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
