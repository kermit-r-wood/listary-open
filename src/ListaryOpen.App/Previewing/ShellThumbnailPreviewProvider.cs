using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ListaryOpen.App.Previewing;

internal sealed class ShellThumbnailPreviewProvider : IFilePreviewProvider
{
    private const uint BiggerSizeOk = 0x00000001;
    private const uint ThumbnailOnly = 0x00000008;
    private const uint ResizeToFit = 0x00000020;
    private static readonly Guid ShellItemImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.ShellThumbnail);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        var bitmap = await Task.Run(
            () => TryLoadThumbnail(context.FullPath),
            cancellationToken).ConfigureAwait(false);
        return bitmap is null ? null : PreviewContent.ForImage("Windows thumbnail", bitmap);
    }

    private static BitmapSource? TryLoadThumbnail(string path)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = ShellItemImageFactoryId;
            var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out factory);
            if (result < 0 || factory is null)
            {
                return null;
            }

            var size = new NativeSize(1200, 1200);
            result = factory.GetImage(size, BiggerSizeOk | ThumbnailOnly | ResizeToFit, out bitmapHandle);
            if (result < 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (factory is not null && Marshal.IsComObject(factory))
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmapHandle);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory shellItem);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
