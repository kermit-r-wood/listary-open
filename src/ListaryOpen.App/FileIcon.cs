using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ListaryOpen.App;

internal static class FileIcon
{
    private const int MaximumCachedIcons = 512;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;

    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> IconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ShellIconConcurrency = new(initialCount: 4, maxCount: 4);

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path",
        typeof(string),
        typeof(FileIcon),
        new PropertyMetadata(null, OnIconRequestChanged));

    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.RegisterAttached(
        "IsDirectory",
        typeof(bool),
        typeof(FileIcon),
        new PropertyMetadata(false, OnIconRequestChanged));

    private static readonly DependencyPropertyKey HasIconPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "HasIcon",
            typeof(bool),
            typeof(FileIcon),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasIconProperty = HasIconPropertyKey.DependencyProperty;

    public static void SetPath(DependencyObject element, string? value) =>
        element.SetValue(PathProperty, value);

    public static string? GetPath(DependencyObject element) =>
        (string?)element.GetValue(PathProperty);

    public static void SetIsDirectory(DependencyObject element, bool value) =>
        element.SetValue(IsDirectoryProperty, value);

    public static bool GetIsDirectory(DependencyObject element) =>
        (bool)element.GetValue(IsDirectoryProperty);

    public static bool GetHasIcon(DependencyObject element) =>
        (bool)element.GetValue(HasIconProperty);

    private static void OnIconRequestChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Image image)
        {
            return;
        }

        image.Source = null;
        image.SetValue(HasIconPropertyKey, false);
        _ = image.Dispatcher.BeginInvoke(new Action(() => LoadIconAsync(image)));
    }

    private static async void LoadIconAsync(Image image)
    {
        var path = GetPath(image);
        var isDirectory = GetIsDirectory(image);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var cacheKey = GetCacheKey(path, isDirectory);
            if (IconCache.Count >= MaximumCachedIcons && !IconCache.ContainsKey(cacheKey))
            {
                IconCache.Clear();
            }

            var iconTask = IconCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<ImageSource?>>(
                    () => LoadShellIconThrottledAsync(path, isDirectory),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var source = await iconTask.Value;
            if (string.Equals(GetPath(image), path, StringComparison.OrdinalIgnoreCase) &&
                GetIsDirectory(image) == isDirectory)
            {
                image.Source = source;
                image.SetValue(HasIconPropertyKey, source is not null);
            }
        }
        catch (Exception)
        {
            // The generic XAML glyph remains visible when the shell cannot provide an icon.
        }
    }

    private static async Task<ImageSource?> LoadShellIconThrottledAsync(string path, bool isDirectory)
    {
        await ShellIconConcurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LoadShellIcon(path, isDirectory)).ConfigureAwait(false);
        }
        finally
        {
            ShellIconConcurrency.Release();
        }
    }

    internal static string GetCacheKey(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (isDirectory)
        {
            return "folder";
        }

        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension is ".exe" or ".lnk" or ".ico"
            ? $"path:{System.IO.Path.GetFullPath(path)}"
            : $"extension:{extension}";
    }

    internal static ImageSource? LoadShellIcon(string path, bool isDirectory)
    {
        var useActualFile = !isDirectory &&
            System.IO.Path.GetExtension(path) is { } extension &&
            (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)) &&
            File.Exists(path);
        var lookupPath = useActualFile
            ? path
            : isDirectory
                ? "folder"
                : $"file{System.IO.Path.GetExtension(path)}";
        var flags = ShgfiIcon | ShgfiSmallIcon;
        if (!useActualFile)
        {
            flags |= ShgfiUseFileAttributes;
        }

        var fileInfo = new ShellFileInfo();
        var result = SHGetFileInfo(
            lookupPath,
            isDirectory ? FileAttributeDirectory : FileAttributeNormal,
            ref fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            flags);
        if (result == IntPtr.Zero || fileInfo.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.Icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            source.Freeze();
            return source;
        }
        finally
        {
            _ = DestroyIcon(fileInfo.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
