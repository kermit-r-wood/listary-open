using System.Collections.Frozen;

namespace ListaryOpen.App.Previewing;

internal enum PreviewCategory
{
    Text, Image, Vector, Font, Document, Ebook, Archive, Audio, Video, Cad, Binary
}

internal enum PreviewCapability
{
    None, Metadata, Thumbnail, ExtractedContent, RenderedContent
}

[Flags]
internal enum PreviewFallback
{
    None = 0,
    Text = 1 << 0,
    Raster = 1 << 1,
    Svg = 1 << 2,
    Font = 1 << 3,
    Document = 1 << 4,
    Archive = 1 << 5,
    Pdf = 1 << 6,
    Media = 1 << 7,
    Executable = 1 << 8,
    ShellThumbnail = 1 << 9,
    Database = 1 << 10,
    Shortcut = 1 << 11,
    Model = 1 << 12,
    Email = 1 << 13,
    Ebook = 1 << 14,
    WebFont = 1 << 15,
    FictionBook = 1 << 16,
    MultipartArchive = 1 << 17,
    Pim = 1 << 18,
    Opaque = 1 << 19,
    OutlookMessage = 1 << 20,
    WebArchive = 1 << 21,
    VirtualDisk = 1 << 22,
    WindowsFilter = 1 << 23,
    OutlookStore = 1 << 24,
    EventLog = 1 << 25,
    CompoundDocument = 1 << 26,
    LhaArchive = 1 << 27,
    DjvuDocument = 1 << 28,
    PostScriptVector = 1 << 29,
    ChmDocument = 1 << 30
}

internal sealed record PreviewFormat(
    PreviewCategory Category,
    PreviewCapability BuiltInCapability,
    bool PreferSystem,
    bool IsBinary,
    PreviewFallback Fallbacks);

internal static class PreviewFormatRegistry
{
    private static readonly FrozenDictionary<string, PreviewFormat> Formats = CreateFormats();

    internal static IReadOnlyDictionary<string, PreviewFormat> All => Formats;

    internal static bool TryGet(string extension, out PreviewFormat format) =>
        Formats.TryGetValue(Normalize(extension), out format!);

    internal static bool Supports(string extension, PreviewFallback fallback) =>
        TryGet(extension, out var format) && (format.Fallbacks & fallback) != 0;

    internal static bool ShouldPreferSystem(string extension) =>
        TryGet(extension, out var format) && format.PreferSystem;

    internal static bool ShouldPreferSystem(string extension, string name) =>
        !IsFictionBookZip(name) &&
        (IsMultipartArchive(name) || ShouldPreferSystem(extension));

    internal static bool IsFictionBookZip(string name) =>
        name.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMultipartArchive(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            name,
            @"(?ix)(?:\.(?:7z|zip)\.\d{3}|\.part\d+\.rar|\.[rz]\d{2})$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    internal static bool CanAttemptText(string extension) =>
        !TryGet(extension, out var format) || (format.Fallbacks & PreviewFallback.Text) != 0;

    private static FrozenDictionary<string, PreviewFormat> CreateFormats()
    {
        var formats = new Dictionary<string, PreviewFormat>(StringComparer.OrdinalIgnoreCase);
        Add(formats, [".apng", ".bmp", ".cur", ".dib", ".emf", ".gif", ".ico", ".jfif",
            ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".wmf"],
            PreviewCategory.Image, PreviewCapability.RenderedContent, false, true, PreviewFallback.Raster);
        Add(formats, [".avif", ".dds", ".exr", ".hdp", ".heic", ".heif", ".j2k", ".jp2",
            ".jpf", ".jxr", ".tga", ".wdp", ".webp"], PreviewCategory.Image,
            PreviewCapability.RenderedContent, true, true, PreviewFallback.Raster | PreviewFallback.ShellThumbnail);
        Add(formats, [".arw", ".cr2", ".cr3", ".dng", ".nef", ".orf", ".pef", ".psb",
            ".psd", ".raf", ".raw", ".rw2", ".sr2", ".srw", ".x3f"],
            PreviewCategory.Image, PreviewCapability.Thumbnail, true, true, PreviewFallback.ShellThumbnail);
        Add(formats, [".svg"], PreviewCategory.Vector, PreviewCapability.ExtractedContent, false, false, PreviewFallback.Svg);
        Add(formats, [".svgz"], PreviewCategory.Vector, PreviewCapability.ExtractedContent, false, true, PreviewFallback.Svg);
        Add(formats, [".ai", ".eps", ".ps"], PreviewCategory.Vector,
            PreviewCapability.ExtractedContent, true, true,
            PreviewFallback.PostScriptVector | PreviewFallback.Document);
        Add(formats, [".otf", ".ttc", ".ttf"], PreviewCategory.Font, PreviewCapability.RenderedContent, false, true, PreviewFallback.Font);
        Add(formats, [".woff", ".woff2"], PreviewCategory.Font, PreviewCapability.Metadata, false, true, PreviewFallback.WebFont);
        Add(formats, [".eot"], PreviewCategory.Font, PreviewCapability.Metadata, true, true, PreviewFallback.WebFont | PreviewFallback.Opaque);

        Add(formats, [".xls", ".xlt"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Document);
        Add(formats, [".doc", ".dot", ".pot", ".pps", ".ppt"], PreviewCategory.Document,
            PreviewCapability.Metadata, true, true,
            PreviewFallback.WindowsFilter | PreviewFallback.Document);
        Add(formats, [".dps", ".et", ".wps", ".one", ".pub"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true,
            PreviewFallback.CompoundDocument | PreviewFallback.Document);
        Add(formats, [".msg", ".oft"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.OutlookMessage);
        Add(formats, [".docm", ".docx", ".dotm", ".dotx", ".odg", ".odp", ".ods", ".odt",
            ".potm", ".potx", ".ppsm", ".ppsx", ".pptm", ".pptx", ".rtf",
            ".vsdx", ".xlsm", ".xlsx", ".xltm", ".xltx", ".xps", ".oxps"],
            PreviewCategory.Document, PreviewCapability.ExtractedContent, true, true, PreviewFallback.Document);
        Add(formats, [".ost", ".pst"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.OutlookStore | PreviewFallback.Opaque);
        Add(formats, [".webarchive"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.WebArchive);
        Add(formats, [".chm"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true,
            PreviewFallback.ChmDocument | PreviewFallback.Document);
        Add(formats, [".djv", ".djvu"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.DjvuDocument);
        Add(formats, [".epub"], PreviewCategory.Ebook, PreviewCapability.ExtractedContent, true, true, PreviewFallback.Document);
        Add(formats, [".azw", ".azw3", ".mobi", ".prc"], PreviewCategory.Ebook,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Ebook);
        Add(formats, [".kfx"], PreviewCategory.Ebook,
            PreviewCapability.ExtractedContent, true, true,
            PreviewFallback.Ebook | PreviewFallback.Archive | PreviewFallback.Document);
        Add(formats, [".fb2"], PreviewCategory.Ebook,
            PreviewCapability.ExtractedContent, false, false, PreviewFallback.FictionBook);
        Add(formats, [".fbz"], PreviewCategory.Ebook,
            PreviewCapability.ExtractedContent, false, true, PreviewFallback.FictionBook);
        Add(formats, [".pdf"], PreviewCategory.Document, PreviewCapability.Metadata, true, true,
            PreviewFallback.Pdf | PreviewFallback.ShellThumbnail);

        Add(formats, [".3mf", ".aab", ".apk", ".appx", ".appxbundle", ".cbz", ".ear", ".ipa",
            ".jar", ".key", ".kmz", ".maff", ".msix", ".msixbundle", ".numbers", ".nupkg", ".olm", ".pages",
            ".sketch", ".snupkg", ".vsix", ".war", ".xpi", ".zip"],
            PreviewCategory.Archive, PreviewCapability.ExtractedContent, true, true, PreviewFallback.Archive);
        Add(formats, [".cbt", ".ova", ".tar", ".tgz", ".gz"], PreviewCategory.Archive,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Archive);
        Add(formats, [".7z", ".ace", ".arc", ".arj", ".cab", ".cb7", ".cbr", ".iso", ".msu", ".rar"], PreviewCategory.Archive,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Archive);
        Add(formats, [".br", ".bz2", ".lzip", ".lz", ".taz", ".tb2", ".tbr", ".tbz", ".tbz2",
            ".tlz", ".tlz4", ".tz", ".tz2", ".txz", ".tzst", ".tzstd", ".xz", ".z", ".zst", ".zstd", ".lz4"],
            PreviewCategory.Archive,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Archive);
        Add(formats, [".zipx"],
            PreviewCategory.Archive, PreviewCapability.ExtractedContent, true, true, PreviewFallback.Archive);
        Add(formats, [".esd", ".wim"],
            PreviewCategory.Archive, PreviewCapability.ExtractedContent, true, true, PreviewFallback.Archive);
        Add(formats, [".lha", ".lzh"],
            PreviewCategory.Archive, PreviewCapability.ExtractedContent, true, true,
            PreviewFallback.LhaArchive | PreviewFallback.Opaque);

        Add(formats, [".aac", ".adts", ".aif", ".aiff", ".amr", ".ape", ".flac", ".m4a", ".m4b", ".mid",
            ".midi", ".mka", ".mp3", ".oga", ".ogg", ".opus", ".wav", ".wma"],
            PreviewCategory.Audio, PreviewCapability.Metadata, true, true, PreviewFallback.Media | PreviewFallback.ShellThumbnail);
        Add(formats, [".3gp", ".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4",
            ".mpeg", ".mpg", ".mts", ".ts", ".vob", ".webm", ".wmv"],
            PreviewCategory.Video, PreviewCapability.Thumbnail, true, true, PreviewFallback.Media | PreviewFallback.ShellThumbnail);
        Add(formats, [".dxf"], PreviewCategory.Cad, PreviewCapability.ExtractedContent, true, false, PreviewFallback.Model);
        Add(formats, [".glb", ".gltf", ".obj", ".ply", ".stl"], PreviewCategory.Cad,
            PreviewCapability.ExtractedContent, false, true, PreviewFallback.Model);
        Add(formats, [".dwg"], PreviewCategory.Cad, PreviewCapability.Metadata, true, true, PreviewFallback.Opaque);

        Add(formats, [".dll", ".exe", ".msi", ".sys"], PreviewCategory.Binary,
            PreviewCapability.Metadata, false, true, PreviewFallback.Executable);
        Add(formats, [".lnk"], PreviewCategory.Binary,
            PreviewCapability.Metadata, false, true, PreviewFallback.Shortcut);
        Add(formats, [".url"], PreviewCategory.Text,
            PreviewCapability.Metadata, false, false, PreviewFallback.Shortcut);
        Add(formats, [".db"], PreviewCategory.Binary,
            PreviewCapability.Metadata, false, true, PreviewFallback.Database | PreviewFallback.Opaque);
        Add(formats, [".evtx"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.EventLog | PreviewFallback.Opaque);
        Add(formats, [".dbf", ".sqlite", ".sqlite3"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, false, true, PreviewFallback.Database);
        Add(formats, [".class", ".wasm"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, false, true, PreviewFallback.Opaque);
        Add(formats, [".dmp", ".mdmp"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Opaque);
        Add(formats, [".vdi", ".vmdk", ".qcow", ".qcow2", ".dmg"],
            PreviewCategory.Binary, PreviewCapability.ExtractedContent, true, true, PreviewFallback.Opaque);
        Add(formats, [".avro", ".arrow", ".feather", ".orc", ".sav", ".sas7bdat", ".dta"],
            PreviewCategory.Binary, PreviewCapability.Metadata, true, true, PreviewFallback.Opaque);
        Add(formats, [".pcap", ".pcapng", ".torrent"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Opaque);
        Add(formats, [".accdb", ".accde", ".mdb", ".mde"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, true, true,
            PreviewFallback.Database | PreviewFallback.Opaque);
        Add(formats, [".vhd", ".vhdx"], PreviewCategory.Binary,
            PreviewCapability.Metadata, true, true, PreviewFallback.VirtualDisk);
        Add(formats, [".parquet"], PreviewCategory.Binary,
            PreviewCapability.ExtractedContent, true, true, PreviewFallback.Opaque);
        Add(formats, [".eml", ".emlx", ".mbox", ".mht", ".mhtml"], PreviewCategory.Document,
            PreviewCapability.ExtractedContent, false, false, PreviewFallback.Email);
        Add(formats, [".contact", ".ics", ".ifb", ".ldif", ".vcard", ".vcf", ".vcs"], PreviewCategory.Text,
            PreviewCapability.ExtractedContent, false, false, PreviewFallback.Pim);
        Add(formats, [".ass", ".htm", ".html", ".m3u", ".m3u8",
            ".markdown", ".md", ".ovf", ".pls", ".shtm", ".shtml", ".srt", ".vtt", ".xht", ".xhtml"], PreviewCategory.Text,
            PreviewCapability.ExtractedContent, false, false, PreviewFallback.Text);
        return formats.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void Add(
        Dictionary<string, PreviewFormat> formats,
        IEnumerable<string> extensions,
        PreviewCategory category,
        PreviewCapability capability,
        bool preferSystem,
        bool isBinary,
        PreviewFallback fallbacks)
    {
        foreach (var extension in extensions)
        {
            var normalized = Normalize(extension);
            if (!formats.TryAdd(normalized, new PreviewFormat(category, capability, preferSystem, isBinary, fallbacks)))
            {
                throw new InvalidOperationException($"Duplicate preview format registration: {normalized}");
            }
        }
    }

    private static string Normalize(string extension) =>
        string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
}
