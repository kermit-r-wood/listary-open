using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ListaryOpen.App.Previewing;

internal sealed class PdfPagePreviewProvider : IFilePreviewProvider
{
    private const int MaximumDimension = 1600;
    private const long MaximumPixels = 8_000_000;
    private const int BgraBitmapFormat = 4;
    private const int RenderAnnotations = 1;
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Pdf);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => RenderFirstPage(context.FullPath, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RenderGate.Release();
        }
    }

    private static PreviewContent? RenderFirstPage(string path, CancellationToken cancellationToken)
    {
        PdfiumNative.EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var document = PdfiumNative.FPDF_LoadDocument(path, null);
        if (document == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pageCount = PdfiumNative.FPDF_GetPageCount(document);
            if (pageCount <= 0)
            {
                return null;
            }

            var page = PdfiumNative.FPDF_LoadPage(document, 0);
            if (page == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var pageWidth = PdfiumNative.FPDF_GetPageWidthF(page);
                var pageHeight = PdfiumNative.FPDF_GetPageHeightF(page);
                var (width, height) = CalculateRenderSize(pageWidth, pageHeight);
                cancellationToken.ThrowIfCancellationRequested();
                var stride = checked(width * 4);
                var pixels = new byte[checked(stride * height)];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var bitmap = PdfiumNative.FPDFBitmap_CreateEx(
                        width,
                        height,
                        BgraBitmapFormat,
                        handle.AddrOfPinnedObject(),
                        stride);
                    if (bitmap == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        PdfiumNative.FPDF_RenderPageBitmap(
                            bitmap,
                            page,
                            0,
                            0,
                            width,
                            height,
                            0,
                            RenderAnnotations);
                    }
                    finally
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bitmap);
                    }
                }
                finally
                {
                    handle.Free();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var image = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);
                image.Freeze();
                return PreviewContent.ForImage($"Built-in PDF page 1/{pageCount}", image);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    internal static (int Width, int Height) CalculateRenderSize(float pageWidth, float pageHeight)
    {
        if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight) ||
            pageWidth <= 0 || pageHeight <= 0)
        {
            throw new InvalidDataException("The PDF page has invalid dimensions.");
        }

        var scale = Math.Min(1d, MaximumDimension / Math.Max(pageWidth, pageHeight));
        var width = Math.Max(1, checked((int)Math.Round(pageWidth * scale)));
        var height = Math.Max(1, checked((int)Math.Round(pageHeight * scale)));
        if ((long)width * height > MaximumPixels)
        {
            var pixelScale = Math.Sqrt(MaximumPixels / ((double)width * height));
            width = Math.Max(1, checked((int)Math.Floor(width * pixelScale)));
            height = Math.Max(1, checked((int)Math.Floor(height * pixelScale)));
        }

        return (width, height);
    }
}

internal static class PdfiumNative
{
    private static readonly object InitializationGate = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitializationGate)
        {
            if (!_initialized)
            {
                FPDF_InitLibrary();
                _initialized = true;
            }
        }
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_InitLibrary();

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_ClosePage(IntPtr page);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBitmap_CreateEx(
        int width,
        int height,
        int format,
        IntPtr firstScan,
        int stride);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFBitmap_FillRect(
        IntPtr bitmap,
        int left,
        int top,
        int width,
        int height,
        uint color);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
}
