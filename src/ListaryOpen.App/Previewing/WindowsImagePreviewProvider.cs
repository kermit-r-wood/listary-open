using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace ListaryOpen.App.Previewing;

internal sealed class WindowsImagePreviewProvider : IFilePreviewProvider
{
    private const long MaximumContainerBytes = 64L * 1024 * 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        context.Extension.Equals(".wim", StringComparison.OrdinalIgnoreCase) ||
        context.Extension.Equals(".esd", StringComparison.OrdinalIgnoreCase);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.SizeBytes <= 0 || context.SizeBytes > MaximumContainerBytes ||
                !HasWimSignature(context.FullPath))
            {
                return null;
            }

            using var handle = NativeMethods.WIMCreateFile(
                context.FullPath,
                desiredAccess: 0x80000000,
                creationDisposition: 3,
                flagsAndAttributes: 0,
                compressionType: 0,
                out _);
            if (handle.IsInvalid)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!NativeMethods.WIMGetImageInformation(handle, out buffer, out var byteCount))
                {
                    return null;
                }
                var xml = WindowsImageMetadataReader.DecodeXml(buffer, byteCount);
                var summary = WindowsImageMetadataReader.Parse(
                    xml,
                    Path.GetExtension(context.FullPath),
                    cancellationToken);
                var listing = WindowsImageDirectoryReader.TryRead(
                    handle,
                    WindowsImageMetadataReader.CountImages(xml),
                    cancellationToken);
                if (!string.IsNullOrEmpty(listing))
                {
                    summary += Environment.NewLine + listing;
                }
                return PreviewContent.ForText("Windows image metadata", summary);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NativeMethods.LocalFree(buffer);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or XmlException or
                DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or Win32Exception)
        {
            return null;
        }
    }

    private static bool HasWimSignature(string path)
    {
        Span<byte> signature = stackalloc byte[8];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(signature) == signature.Length &&
            signature.SequenceEqual("MSWIM\0\0\0"u8);
    }
}

internal static class WindowsImageMetadataReader
{
    internal const int MaximumXmlBytes = 4 * 1024 * 1024;
    private const int MaximumImages = 64;
    private const int MaximumFieldCharacters = 1024;
    private const int MaximumOutputCharacters = 120_000;

    internal static string DecodeXml(IntPtr buffer, uint byteCount)
    {
        if (buffer == IntPtr.Zero || byteCount < 2 || byteCount > MaximumXmlBytes ||
            (byteCount & 1) != 0)
        {
            throw new InvalidDataException("The Windows image metadata buffer is invalid.");
        }

        var characters = checked((int)byteCount / sizeof(char));
        var xml = Marshal.PtrToStringUni(buffer, characters) ?? string.Empty;
        return xml.TrimEnd('\0', '\uFEFF').TrimStart('\uFEFF');
    }

    internal static string Parse(
        string xml,
        string extension,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaximumXmlBytes / sizeof(char))
        {
            throw new InvalidDataException("The Windows image metadata XML is invalid.");
        }

        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes / sizeof(char)
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("WIM", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Windows image metadata root is invalid.");
        }

        var images = root.Elements()
            .Where(element => element.Name.LocalName.Equals("IMAGE", StringComparison.OrdinalIgnoreCase))
            .Take(MaximumImages + 1)
            .ToArray();
        if (images.Length == 0)
        {
            throw new InvalidDataException("The Windows image has no image metadata.");
        }

        var shown = Math.Min(images.Length, MaximumImages);
        var builder = new StringBuilder()
            .AppendLine(extension.Equals(".esd", StringComparison.OrdinalIgnoreCase)
                ? "Electronic Software Download image"
                : "Windows Imaging Format")
            .Append("Images shown: ").Append(shown);
        if (images.Length > MaximumImages)
        {
            builder.Append('+');
        }
        builder.AppendLine().AppendLine();

        for (var index = 0; index < shown; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = images[index];
            builder.Append("Image ")
                .Append(Sanitize(image.Attribute("INDEX")?.Value) ?? (index + 1).ToString())
                .AppendLine();
            AppendField(builder, "Name", Value(image, "DISPLAYNAME") ?? Value(image, "NAME"));
            AppendField(builder, "Description",
                Value(image, "DISPLAYDESCRIPTION") ?? Value(image, "DESCRIPTION"));
            AppendField(builder, "Edition", Value(image, "WINDOWS", "EDITIONID"));
            AppendField(builder, "Product", Value(image, "WINDOWS", "PRODUCTNAME"));
            AppendField(builder, "Architecture", FormatArchitecture(Value(image, "WINDOWS", "ARCH")));
            AppendField(builder, "Version", FormatVersion(image));
            AppendField(builder, "Languages", FormatLanguages(image));
            AppendField(builder, "Installation", Value(image, "WINDOWS", "INSTALLATIONTYPE"));
            AppendField(builder, "Size", FormatBytes(Value(image, "TOTALBYTES")));
            builder.AppendLine();
            if (builder.Length >= MaximumOutputCharacters)
            {
                builder.Length = MaximumOutputCharacters;
                builder.AppendLine().AppendLine("… image metadata truncated");
                break;
            }
        }

        builder.AppendLine("Metadata queried read-only through Windows Imaging API; no image was mounted, applied, or extracted.");
        return builder.ToString();
    }

    internal static int CountImages(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes / sizeof(char)
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        return document.Root?.Elements()
            .Count(element => element.Name.LocalName.Equals(
                "IMAGE",
                StringComparison.OrdinalIgnoreCase)) ?? 0;
    }

    private static string? Value(XElement parent, params string[] path)
    {
        XElement? current = parent;
        foreach (var name in path)
        {
            current = current?.Elements().FirstOrDefault(
                element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        return Sanitize(current?.Value);
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new string(value
            .Where(character => !char.IsControl(character) || character is '\t' or '\r' or '\n')
            .ToArray())
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return sanitized.Length <= MaximumFieldCharacters
            ? sanitized
            : sanitized[..(MaximumFieldCharacters - 1)] + "…";
    }

    private static string? FormatArchitecture(string? value) => value switch
    {
        "0" => "x86",
        "5" => "ARM",
        "6" => "Itanium",
        "9" => "x64",
        "12" => "ARM64",
        null => null,
        _ => value
    };

    private static string? FormatVersion(XElement image)
    {
        var version = image.Elements().FirstOrDefault(
            element => element.Name.LocalName.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase))?
            .Elements().FirstOrDefault(
                element => element.Name.LocalName.Equals("VERSION", StringComparison.OrdinalIgnoreCase));
        if (version is null)
        {
            return null;
        }

        var parts = new[] { "MAJOR", "MINOR", "BUILD", "SPBUILD" }
            .Select(name => Value(version, name))
            .Where(value => value is not null)
            .ToArray();
        return parts.Length == 0 ? null : string.Join('.', parts);
    }

    private static string? FormatLanguages(XElement image)
    {
        var languages = image.Descendants()
            .Where(element => element.Name.LocalName.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase))
            .Select(element => Sanitize(element.Value))
            .Where(value => value is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        return languages.Length == 0 ? null : string.Join(", ", languages);
    }

    private static string? FormatBytes(string? value) =>
        ulong.TryParse(value, out var bytes)
            ? FilePreviewPane.FormatSize(checked((long)Math.Min(bytes, long.MaxValue)))
            : value;

    private static void AppendField(StringBuilder builder, string label, string? value)
    {
        if (value is not null)
        {
            builder.Append("  ").Append(label).Append(": ").AppendLine(value);
        }
    }
}

internal static class WindowsImageDirectoryReader
{
    private const int MaximumImages = 2;
    private const int MaximumEntries = 200;
    private const int MaximumMessages = 10_000;
    private const int MaximumPathCharacters = 1024;
    private const int MaximumOutputCharacters = 120_000;
    private static readonly TimeSpan SoftTimeout = TimeSpan.FromSeconds(3);

    internal static string? TryRead(
        SafeWimHandle wim,
        int imageCount,
        CancellationToken cancellationToken)
    {
        if (imageCount <= 0)
        {
            return null;
        }

        var root = Path.Combine(Path.GetTempPath(), "ListaryOpen", "WimPreview");
        Directory.CreateDirectory(root);
        var temporaryPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryPath);
        try
        {
            if (!NativeMethods.WIMSetTemporaryPath(wim, temporaryPath))
            {
                return null;
            }

            var state = new WindowsImageListingState(
                temporaryPath,
                cancellationToken,
                MaximumEntries,
                MaximumMessages,
                MaximumPathCharacters,
                MaximumOutputCharacters,
                SoftTimeout);
            var stateHandle = GCHandle.Alloc(state);
            GCHandle callbackHandle = default;
            var releaseHandles = true;
            try
            {
                NativeMethods.WimMessageCallback callback = OnMessage;
                callbackHandle = GCHandle.Alloc(callback);
                var callbackIndex = NativeMethods.WIMRegisterMessageCallback(
                    wim,
                    callback,
                    GCHandle.ToIntPtr(stateHandle));
                if (callbackIndex == uint.MaxValue)
                {
                    return null;
                }

                try
                {
                    var shownImages = Math.Min(imageCount, MaximumImages);
                    for (var index = 1; index <= shownImages; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var image = NativeMethods.WIMLoadImage(wim, checked((uint)index));
                        if (image.IsInvalid)
                        {
                            return null;
                        }

                        state.BeginImage(index);
                        var applied = NativeMethods.WIMApplyImage(
                            image,
                            temporaryPath,
                            0x08 | 0x10 | 0x20 | 0x100);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!applied && !state.AbortedByBudget)
                        {
                            return null;
                        }
                        if (state.AbortedByBudget)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    releaseHandles = NativeMethods.WIMUnregisterMessageCallback(wim, callback);
                    GC.KeepAlive(callback);
                }
            }
            finally
            {
                if (releaseHandles)
                {
                    if (callbackHandle.IsAllocated)
                    {
                        callbackHandle.Free();
                    }
                    stateHandle.Free();
                }
            }

            return state.Build(imageCount > MaximumImages);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException or Win32Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                var resolvedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                var resolvedTemporary = Path.GetFullPath(temporaryPath);
                if (resolvedTemporary.StartsWith(
                    resolvedRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(resolvedTemporary, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static uint OnMessage(
        uint message,
        UIntPtr parameter1,
        IntPtr parameter2,
        IntPtr userData)
    {
        try
        {
            var state = (WindowsImageListingState?)GCHandle.FromIntPtr(userData).Target;
            if (state is null || message != 0x9479)
            {
                return 0;
            }

            var path = Marshal.PtrToStringUni((IntPtr)parameter1);
            return state.Add(path) ? 0u : uint.MaxValue;
        }
        catch
        {
            return uint.MaxValue;
        }
    }
}

internal sealed class WindowsImageListingState
{
    private readonly string _targetPath;
    private readonly CancellationToken _cancellationToken;
    private readonly int _maximumEntries;
    private readonly int _maximumMessages;
    private readonly int _maximumPathCharacters;
    private readonly int _maximumOutputCharacters;
    private readonly TimeSpan _timeout;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly StringBuilder _builder = new();
    private int _entries;
    private int _messages;

    internal WindowsImageListingState(
        string targetPath,
        CancellationToken cancellationToken,
        int maximumEntries,
        int maximumMessages,
        int maximumPathCharacters,
        int maximumOutputCharacters,
        TimeSpan timeout)
    {
        _targetPath = Path.GetFullPath(targetPath);
        _cancellationToken = cancellationToken;
        _maximumEntries = maximumEntries;
        _maximumMessages = maximumMessages;
        _maximumPathCharacters = maximumPathCharacters;
        _maximumOutputCharacters = maximumOutputCharacters;
        _timeout = timeout;
    }

    internal bool AbortedByBudget { get; private set; }

    internal void BeginImage(int index)
    {
        lock (_builder)
        {
            _builder.Append("Image ").Append(index).AppendLine(" entries:");
        }
    }

    internal bool Add(string? path)
    {
        lock (_builder)
        {
            _messages++;
            if (_cancellationToken.IsCancellationRequested ||
                _stopwatch.Elapsed >= _timeout ||
                _messages > _maximumMessages ||
                _entries >= _maximumEntries ||
                _builder.Length >= _maximumOutputCharacters)
            {
                AbortedByBudget = true;
                return false;
            }

            var sanitized = SanitizePath(path);
            if (sanitized is null)
            {
                return true;
            }
            _entries++;
            _builder.Append("  ").AppendLine(sanitized);
            return true;
        }
    }

    internal string? Build(bool imagesTruncated)
    {
        lock (_builder)
        {
            if (_entries == 0)
            {
                return null;
            }
            if (AbortedByBudget || imagesTruncated)
            {
                _builder.AppendLine("… image directory listing truncated");
            }
            _builder.AppendLine("Directory listing used WIM_FLAG_NO_APPLY; no image files were created.");
            return _builder.ToString();
        }
    }

    private string? SanitizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(_targetPath, path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (relative.Equals(".", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return null;
        }

        var sanitized = new string(relative
            .Where(character => !char.IsControl(character))
            .ToArray());
        return sanitized.Length <= _maximumPathCharacters
            ? sanitized
            : sanitized[..(_maximumPathCharacters - 1)] + "…";
    }
}

internal static class NativeMethods
{
    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWimHandle WIMCreateFile(
        string path,
        uint desiredAccess,
        uint creationDisposition,
        uint flagsAndAttributes,
        uint compressionType,
        out uint creationResult);

    [DllImport("wimgapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WIMGetImageInformation(
        SafeWimHandle image,
        out IntPtr imageInformation,
        out uint imageInformationBytes);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WIMSetTemporaryPath(
        SafeWimHandle wim,
        string temporaryPath);

    [DllImport("wimgapi.dll", SetLastError = true)]
    internal static extern SafeWimHandle WIMLoadImage(
        SafeWimHandle wim,
        uint imageIndex);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint WimMessageCallback(
        uint message,
        UIntPtr parameter1,
        IntPtr parameter2,
        IntPtr userData);

    [DllImport("wimgapi.dll", SetLastError = true)]
    internal static extern uint WIMRegisterMessageCallback(
        SafeWimHandle wim,
        WimMessageCallback callback,
        IntPtr userData);

    [DllImport("wimgapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WIMUnregisterMessageCallback(
        SafeWimHandle wim,
        WimMessageCallback callback);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WIMApplyImage(
        SafeWimHandle image,
        string targetPath,
        uint applyFlags);

    [DllImport("wimgapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WIMCloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class SafeWimHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeWimHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.WIMCloseHandle(handle);
}
