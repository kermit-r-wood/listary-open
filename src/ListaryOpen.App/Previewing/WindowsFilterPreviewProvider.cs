using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal sealed class WindowsFilterPreviewProvider : IFilePreviewProvider
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.WindowsFilter);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            temporaryPath = CreateCapturedCopy(context, cancellationToken);
            if (temporaryPath is null)
            {
                return null;
            }
            using var filter = NativeFilterSession.TryLoad(temporaryPath);
            if (filter is null)
            {
                return null;
            }
            var text = WindowsFilterTextExtractor.Extract(filter, cancellationToken);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : PreviewContent.ForText("Windows IFilter text", text);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ExternalException or InvalidDataException or
                DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string? CreateCapturedCopy(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        using var source = new FileStream(
            context.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var capturedLength = source.Length;
        if (capturedLength <= 0 || capturedLength > MaximumFileBytes)
        {
            return null;
        }
        var directory = Path.Combine(Path.GetTempPath(), "ListaryOpen", "FilterCopies");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"{Guid.NewGuid():N}{context.Extension.ToLowerInvariant()}");
        try
        {
            using var target = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            var buffer = new byte[64 * 1024];
            long remaining = capturedLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0)
                {
                    throw new EndOfStreamException("The source changed while preparing its filter preview.");
                }
                target.Write(buffer, 0, count);
                remaining -= count;
            }
            target.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            throw;
        }
    }
}

internal static class WindowsFilterTextExtractor
{
    internal const int MaximumOutputCharacters = 120_000;
    private const int MaximumChunks = 4096;
    private const int MaximumTextCalls = 8192;
    private const int MaximumTextCallsPerChunk = 1024;
    private const int TextBufferCharacters = 4096;
    private const int FilterEndOfChunks = unchecked((int)0x80041700);
    private const int FilterNoMoreText = unchecked((int)0x80041701);
    private const int FilterNoText = unchecked((int)0x80041705);
    private const int FilterEmbeddingUnavailable = unchecked((int)0x80041707);
    private const int FilterLinkUnavailable = unchecked((int)0x80041708);
    private const int FilterLastText = 0x00041709;
    private const uint ChunkText = 1;
    internal const uint InitializationFlags =
        1 | 2 | 4 | 8 | 16 | 32 | 64 | 2048;

    internal static string? Extract(
        IWindowsFilterSession filter,
        CancellationToken cancellationToken)
    {
        var init = filter.Init(InitializationFlags);
        if (init != 0)
        {
            return null;
        }
        var builder = new StringBuilder();
        uint previousChunkId = 0;
        var textCalls = 0;
        for (var chunkIndex = 0; chunkIndex < MaximumChunks; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = filter.GetChunk(out var chunk);
            if (result == FilterEndOfChunks)
            {
                return Finish(builder);
            }
            if (result is FilterEmbeddingUnavailable or FilterLinkUnavailable)
            {
                continue;
            }
            if (result != 0)
            {
                throw new InvalidDataException(
                    $"The registered IFilter returned an unexpected chunk result: 0x{result:X8}.");
            }
            if (chunk.Id == 0 || chunk.Id <= previousChunkId ||
                chunk.BreakType > 4 || chunk.Flags is not 1 and not 2)
            {
                throw new InvalidDataException("The registered IFilter returned an invalid chunk descriptor.");
            }
            previousChunkId = chunk.Id;
            if ((chunk.Flags & ChunkText) == 0)
            {
                continue;
            }
            if (builder.Length > 0 && chunk.BreakType != 0)
            {
                builder.AppendLine();
            }
            var chunkTextCalls = 0;
            while (builder.Length < MaximumOutputCharacters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++textCalls > MaximumTextCalls)
                {
                    throw new InvalidDataException("The registered IFilter exceeded its text-call budget.");
                }
                if (++chunkTextCalls > MaximumTextCallsPerChunk)
                {
                    throw new InvalidDataException(
                        "The registered IFilter exceeded its per-chunk text-call budget.");
                }
                result = filter.GetText(
                    TextBufferCharacters,
                    out var characters,
                    out var text);
                if (characters > TextBufferCharacters)
                {
                    throw new InvalidDataException("The registered IFilter returned an invalid text length.");
                }
                if (result is 0 or FilterLastText)
                {
                    AppendBounded(builder, text);
                }
                if (result is FilterNoMoreText or FilterNoText or FilterLastText)
                {
                    break;
                }
                if (result != 0)
                {
                    throw new InvalidDataException(
                        $"The registered IFilter returned an unexpected text result: 0x{result:X8}.");
                }
                if (characters == 0)
                {
                    throw new InvalidDataException("The registered IFilter made no text progress.");
                }
            }
            if (builder.Length >= MaximumOutputCharacters)
            {
                return Finish(builder, truncated: true);
            }
        }
        throw new InvalidDataException("The registered IFilter exceeded its chunk budget.");
    }

    private static string? Finish(StringBuilder builder, bool truncated = false)
    {
        var text = builder.ToString().Replace('\0', ' ').Trim();
        if (text.Length == 0)
        {
            return null;
        }
        return truncated ? text + Environment.NewLine + "… filter text truncated" : text;
    }

    private static void AppendBounded(StringBuilder builder, string value)
    {
        var remaining = MaximumOutputCharacters - builder.Length;
        if (remaining > 0)
        {
            builder.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        }
    }
}

internal interface IWindowsFilterSession : IDisposable
{
    int Init(uint flags);
    int GetChunk(out WindowsFilterChunk chunk);
    int GetText(uint capacity, out uint characters, out string text);
}

internal readonly record struct WindowsFilterChunk(
    uint Id,
    uint BreakType,
    uint Flags);

internal sealed class NativeFilterSession : IWindowsFilterSession
{
    private IFilter? _filter;
    private bool _uninitializeCom;

    private NativeFilterSession(IFilter filter, bool uninitializeCom)
    {
        _filter = filter;
        _uninitializeCom = uninitializeCom;
    }

    internal static NativeFilterSession? TryLoad(string path)
    {
        const int RpcChangedMode = unchecked((int)0x80010106);
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0 && initialized != RpcChangedMode)
        {
            return null;
        }
        var uninitialize = initialized is 0 or 1;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var result = LoadIFilter(path, IntPtr.Zero, out pointer);
            if (result != 0 || pointer == IntPtr.Zero)
            {
                if (uninitialize)
                {
                    CoUninitialize();
                }
                return null;
            }
            var filter = (IFilter)Marshal.GetObjectForIUnknown(pointer);
            return new NativeFilterSession(filter, uninitialize);
        }
        catch
        {
            if (uninitialize)
            {
                CoUninitialize();
            }
            throw;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }
    }

    public int Init(uint flags) =>
        _filter!.Init(flags, 0, IntPtr.Zero, out _);

    public int GetChunk(out WindowsFilterChunk chunk)
    {
        var result = _filter!.GetChunk(out var native);
        chunk = new WindowsFilterChunk(native.IdChunk, native.BreakType, native.Flags);
        return result;
    }

    public int GetText(uint capacity, out uint characters, out string text)
    {
        var buffer = Marshal.AllocHGlobal(checked((int)capacity * sizeof(char)));
        try
        {
            characters = capacity;
            var result = _filter!.GetText(ref characters, buffer);
            text = characters <= capacity
                ? Marshal.PtrToStringUni(buffer, checked((int)characters)) ?? string.Empty
                : string.Empty;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        var filter = Interlocked.Exchange(ref _filter, null);
        if (filter is not null && Marshal.IsComObject(filter))
        {
            Marshal.FinalReleaseComObject(filter);
        }
        if (_uninitializeCom)
        {
            _uninitializeCom = false;
            CoUninitialize();
        }
    }

    [DllImport("query.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int LoadIFilter(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr outer,
        out IntPtr filter);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

[ComImport]
[Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFilter
{
    [PreserveSig]
    int Init(uint flags, uint attributeCount, IntPtr attributes, out uint filterFlags);

    [PreserveSig]
    int GetChunk(out NativeStatChunk chunk);

    [PreserveSig]
    int GetText(
        ref uint bufferCharacters,
        IntPtr buffer);

    [PreserveSig]
    int GetValue(out IntPtr value);

    [PreserveSig]
    int BindRegion(
        NativeFilterRegion region,
        ref Guid interfaceId,
        out IntPtr value);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStatChunk
{
    internal uint IdChunk;
    internal uint BreakType;
    internal uint Flags;
    internal uint Locale;
    internal NativeFullPropertySpec Attribute;
    internal uint IdChunkSource;
    internal uint StartSource;
    internal uint LengthSource;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFullPropertySpec
{
    internal Guid PropertySet;
    internal NativePropertySpec Property;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePropertySpec
{
    internal uint Kind;
    internal UIntPtr Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFilterRegion
{
    internal uint Chunk;
    internal uint Start;
    internal uint Extent;
}
