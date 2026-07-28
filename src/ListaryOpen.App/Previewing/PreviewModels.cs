using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ListaryOpen.App.Previewing;

internal enum PreviewContentKind
{
    Text,
    Image,
    Font,
    Media
}

internal sealed record PreviewContent(
    PreviewContentKind Kind,
    string Source,
    string? Text = null,
    BitmapSource? Image = null,
    FontFamily? FontFamily = null,
    string? Heading = null,
    string? MediaPath = null)
{
    internal static PreviewContent ForText(string source, string text) =>
        new(PreviewContentKind.Text, source, Text: text);

    internal static PreviewContent ForImage(string source, BitmapSource image) =>
        new(PreviewContentKind.Image, source, Image: image);

    internal static PreviewContent ForFont(string source, FontFamily fontFamily, string heading) =>
        new(PreviewContentKind.Font, source, FontFamily: fontFamily, Heading: heading);

    internal static PreviewContent ForMedia(string source, string path, string fallbackText) =>
        new(PreviewContentKind.Media, source, Text: fallbackText, MediaPath: path);
}

internal sealed record PreviewContext(
    string FullPath,
    string Name,
    string Extension,
    long SizeBytes,
    DateTimeOffset LastWriteTime);

internal interface IFilePreviewProvider
{
    bool CanPreview(PreviewContext context);

    Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken);
}
