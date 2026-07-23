using System.IO.Compression;
using System.Text;
using ListaryOpen.App.Previewing;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class PreviewProviderTests
{
    [Fact]
    public void TextDecoderRecognizesBomlessUtf16AndGbk()
    {
        Assert.Equal("hello", PreviewTextReader.Decode(Encoding.Unicode.GetBytes("hello")));
        Assert.Equal("你好", PreviewTextReader.Decode([0xC4, 0xE3, 0xBA, 0xC3]));
        Assert.Null(PreviewTextReader.Decode([0, 1, 2, 3, 4, 5]));
    }

    [Fact]
    public async Task TextProviderPreviewsAnExtensionlessTextFile()
    {
        using var temporary = new TemporaryFile("README");
        await File.WriteAllTextAsync(temporary.Path, "extensionless preview");
        var provider = new TextPreviewProvider();

        var preview = await provider.LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal(PreviewContentKind.Text, preview?.Kind);
        Assert.Contains("extensionless preview", preview?.Text);
    }

    [Fact]
    public async Task TextProviderRendersMarkdownWithoutFollowingLinksOrLoadingImages()
    {
        using var temporary = new TemporaryFile("sample.md");
        await File.WriteAllTextAsync(
            temporary.Path,
            "# Heading\n\n[Visible text](https://example.test) ![Alternative](https://example.test/image.png)");

        var preview = await new TextPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Heading", preview?.Text);
        Assert.Contains("Visible text", preview?.Text);
        Assert.Contains("Alternative", preview?.Text);
        Assert.DoesNotContain("https://", preview?.Text);
    }

    [Fact]
    public async Task ArchiveProviderListsZipEntriesWithoutExtractingThem()
    {
        using var temporary = new TemporaryFile("sample.zip");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("folder/readme.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("hello");
        }

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("ZIP archive", preview?.Text);
        Assert.Contains("folder/readme.txt", preview?.Text);
    }

    [Fact]
    public async Task DocumentProviderExtractsTextFromOpenXmlPackages()
    {
        using var temporary = new TemporaryFile("sample.docx");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("word/document.xml");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(
                "<?xml version=\"1.0\"?><w:document xmlns:w=\"urn:test\"><w:p><w:r><w:t>Document preview</w:t></w:r></w:p></w:document>");
        }

        var preview = await new DocumentPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Document text", preview?.Source);
        Assert.Contains("Document preview", preview?.Text);
    }

    [Fact]
    public async Task PdfProviderReturnsSafeMetadataWhenNativePreviewIsUnavailable()
    {
        using var temporary = new TemporaryFile("sample.pdf");
        await File.WriteAllTextAsync(
            temporary.Path,
            "%PDF-1.7\n1 0 obj << /Type /Page >> endobj\n<< /Title (Preview title) /Author (ListaryOpen) >>\n%%EOF",
            Encoding.Latin1);

        var preview = await new PdfPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Detected pages: 1", preview?.Text);
        Assert.Contains("Preview title", preview?.Text);
        Assert.Contains("ListaryOpen", preview?.Text);
    }

    [Fact]
    public async Task SvgProviderBlocksExternalResourcesAndReportsStructure()
    {
        using var temporary = new TemporaryFile("sample.svg");
        await File.WriteAllTextAsync(
            temporary.Path,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><image href=\"https://example.test/a.png\"/><path d=\"M0 0\"/></svg>");

        var preview = await new SvgPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Dimensions: 100 × 50", preview?.Text);
        Assert.Contains("External references blocked: 1", preview?.Text);
    }

    [Fact]
    public async Task MediaProviderReadsWaveMetadata()
    {
        using var temporary = new TemporaryFile("sample.wav");
        await File.WriteAllBytesAsync(temporary.Path, CreateWaveHeader());

        var preview = await new MediaPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("2 channel(s), 44100 Hz, 16 bit", preview?.Text);
        Assert.Contains("00:00:01", preview?.Text);
    }

    [Fact]
    public async Task CoordinatorCachesCompletedPreviewResults()
    {
        using var temporary = new TemporaryFile("cache.test");
        await File.WriteAllTextAsync(temporary.Path, "cache");
        var provider = new CountingProvider();
        var coordinator = new PreviewCoordinator([provider]);
        var context = Context(temporary.Path);

        await coordinator.LoadAsync(context, CancellationToken.None);
        await coordinator.LoadAsync(context, CancellationToken.None);

        Assert.Equal(1, provider.LoadCount);
    }

    [Fact]
    public async Task ExecutableProviderReadsPeMetadataWithoutExecutingTheFile()
    {
        var path = typeof(PreviewProviderTests).Assembly.Location;

        var preview = await new ExecutablePreviewProvider().LoadAsync(Context(path), CancellationToken.None);

        Assert.Contains("Architecture:", preview?.Text);
        Assert.Contains("Managed metadata: yes", preview?.Text);
        Assert.Contains("never executed", preview?.Text);
    }

    [Fact]
    public async Task FontProviderBuildsATypefaceSampleFromAFontFile()
    {
        var fontsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Fonts");
        var path = Directory.EnumerateFiles(fontsDirectory, "*.ttf").First();

        var preview = await new FontPreviewProvider().LoadAsync(Context(path), CancellationToken.None);

        Assert.Equal(PreviewContentKind.Font, preview?.Kind);
        Assert.NotNull(preview?.FontFamily);
        Assert.False(string.IsNullOrWhiteSpace(preview?.Heading));
    }

    private static PreviewContext Context(string path)
    {
        var info = new FileInfo(path);
        return new PreviewContext(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private static byte[] CreateWaveHeader()
    {
        var bytes = new byte[44 + 176_400];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 22);
        BitConverter.GetBytes(44_100).CopyTo(bytes, 24);
        BitConverter.GetBytes(176_400).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)4).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.GetBytes(176_400).CopyTo(bytes, 40);
        return bytes;
    }

    private sealed class CountingProvider : IFilePreviewProvider
    {
        public int LoadCount { get; private set; }

        public bool CanPreview(PreviewContext context) => true;

        public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult<PreviewContent?>(PreviewContent.ForText("test", "cached"));
        }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string name)
        {
            var directory = Directory.CreateTempSubdirectory("ListaryOpenPreview");
            DirectoryPath = directory.FullName;
            Path = System.IO.Path.Combine(DirectoryPath, name);
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
