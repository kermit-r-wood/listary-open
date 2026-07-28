using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Claunia.PropertyList;
using K4os.Compression.LZ4.Streams;
using ListaryOpen.App;
using ListaryOpen.App.Previewing;
using OpenMcdf;
using Parquet.Serialization;

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
    public async Task TextProviderConvertsXhtmlWithoutRunningScriptsOrKeepingMarkup()
    {
        using var temporary = new TemporaryFile("sample.xhtml");
        await File.WriteAllTextAsync(
            temporary.Path,
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>Safe heading</h1><script>bad()</script><p>Body &amp; text</p></body></html>");

        var preview = await new TextPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Safe heading", preview?.Text);
        Assert.Contains("Body & text", preview?.Text);
        Assert.DoesNotContain("bad()", preview?.Text);
        Assert.DoesNotContain("<html", preview?.Text);
    }

    [Fact]
    public async Task EmailProviderDecodesMultipartHeadersAndDoesNotRenderAttachments()
    {
        using var temporary = new TemporaryFile("message.eml");
        await File.WriteAllTextAsync(temporary.Path,
            """
            From: sender@example.test
            To: receiver@example.test
            Subject: =?UTF-8?B?5rWL6K+V6YKu5Lu2?=
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="outer"

            --outer
            Content-Type: multipart/alternative; boundary="inner"

            --inner
            Content-Type: text/plain; charset=UTF-8
            Content-Transfer-Encoding: quoted-printable

            Safe=20body
            --inner
            Content-Type: text/html; charset=UTF-8

            <html><script>bad()</script><p>HTML body</p></html>
            --inner--
            --outer
            Content-Type: application/octet-stream; name="secret.txt"
            Content-Disposition: attachment; filename="secret.txt"
            Content-Transfer-Encoding: base64

            U0VDUkVUX0FUVEFDSE1FTlRfQ09OVEVOVA==
            --outer--
            """.ReplaceLineEndings("\r\n"));

        var preview = await new EmailPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Safe MIME preview", preview?.Source);
        Assert.Contains("Subject: 测试邮件", preview?.Text);
        Assert.Contains("Safe body", preview?.Text);
        Assert.Contains("secret.txt", preview?.Text);
        Assert.DoesNotContain("SECRET_ATTACHMENT_CONTENT", preview?.Text);
        Assert.DoesNotContain("bad()", preview?.Text);
    }

    [Fact]
    public async Task MailboxProviderListsHeadersWithoutAttachmentPayloads()
    {
        using var temporary = new TemporaryFile("messages.mbox");
        await File.WriteAllTextAsync(
            temporary.Path,
            """
            From first@example.test Sun Jul 26 10:00:00 2026
            From: first@example.test
            Date: Sun, 26 Jul 2026 10:00:00 +0000
            Subject: First message
            Content-Type: application/octet-stream
            Content-Transfer-Encoding: base64

            U0VDUkVUX1BBWUxPQUQ=
            From second@example.test Sun Jul 26 11:00:00 2026
            From: second@example.test
            Subject: Second message

            This body must not be summarized.
            """.ReplaceLineEndings("\n"));

        var preview = await new EmailPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Mailbox summary", preview?.Source);
        Assert.Contains("First message", preview?.Text);
        Assert.Contains("Second message", preview?.Text);
        Assert.DoesNotContain("SECRET_PAYLOAD", preview?.Text);
        Assert.DoesNotContain("This body must not be summarized", preview?.Text);
    }

    [Fact]
    public async Task EmlxProviderIgnoresTheLeadingByteCount()
    {
        using var temporary = new TemporaryFile("message.emlx");
        await File.WriteAllTextAsync(
            temporary.Path,
            "123\r\nFrom: sender@example.test\r\nSubject: EMLX preview\r\n\r\nSafe body");

        var preview = await new EmailPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Safe MIME preview", preview?.Source);
        Assert.Contains("Subject: EMLX preview", preview?.Text);
        Assert.Contains("Safe body", preview?.Text);
    }

    [Fact]
    public async Task OutlookMessageProviderExtractsSubjectAndBodyFromCompoundFile()
    {
        using var temporary = new TemporaryFile("message.msg");
        CreateMinimalOutlookMessage(
            temporary.Path,
            "MSG preview subject",
            "Safe MSG body");

        var preview = await new OutlookMessagePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Safe Outlook message", preview?.Source);
        Assert.Contains("Subject: MSG preview subject", preview?.Text);
        Assert.Contains("Safe MSG body", preview?.Text);
        Assert.Contains("never saved, opened, or rendered", preview?.Text);
    }

    [Fact]
    public async Task OutlookMessageProviderDoesNotParseOversizedFiles()
    {
        using var temporary = new TemporaryFile("large.oft");
        await using (var stream = new FileStream(temporary.Path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(33L * 1024 * 1024);
        }

        var preview = await new OutlookMessagePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Outlook message metadata", preview?.Source);
        Assert.Contains("Content extraction is limited to 32.0 MB", preview?.Text);
    }

    [Fact]
    public void OutlookStoreFolderSummaryListsOnlyFolderMetadata()
    {
        var root = new FakeOutlookStoreFolderNode(
            "Root",
            12,
            3,
            new FakeOutlookStoreFolderNode("Inbox", 8, 2),
            new FakeOutlookStoreFolderNode("Archive", 4, 1));

        var summary = OutlookStoreFolderSummary.Build(
            root,
            "Outlook Personal Folders (Unicode)",
            1024,
            CancellationToken.None);

        Assert.Contains("Type: Outlook Personal Folders (Unicode)", summary);
        Assert.Contains("- Root [messages: 12, unread: 3]", summary);
        Assert.Contains("- Inbox [messages: 8, unread: 2]", summary);
        Assert.Contains("- Archive [messages: 4, unread: 1]", summary);
        Assert.Contains("subjects, bodies, recipients, and attachments were not read", summary);
        Assert.DoesNotContain("Safe message body", summary);
    }

    [Fact]
    public async Task CompoundDocumentProviderListsDirectoryWithoutOpeningStreamPayloads()
    {
        using var temporary = new TemporaryFile("sample.one");
        using (var root = RootStorage.Create(temporary.Path, OpenMcdf.Version.V3, StorageModeFlags.Transacted))
        {
            using (var payload = root.CreateStream("DocumentContent"))
            {
                payload.Write(Encoding.UTF8.GetBytes("secret OneNote body"));
            }

            var section = root.CreateStorage("Sections");
            using (var metadata = section.CreateStream("Metadata"))
            {
                metadata.Write(new byte[] { 1, 2, 3, 4 });
            }

            root.Commit();
        }

        var preview = await new CompoundDocumentPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Compound document structure", preview?.Source);
        Assert.Contains("DocumentContent", preview?.Text);
        Assert.Contains("Sections/Metadata", preview?.Text);
        Assert.DoesNotContain("secret OneNote body", preview?.Text);
        Assert.Contains("stream payloads were not opened", preview?.Text);
    }

    [Fact]
    public async Task ChmProviderListsItsfDirectoryWithoutDecompressingTopics()
    {
        using var temporary = new TemporaryFile("sample.chm");
        const int itsfLength = 0x60;
        const int itspLength = 0x54;
        const int blockLength = 0x1000;
        var directoryOffset = itsfLength;
        var directoryLength = itspLength + blockLength;
        var dataOffset = directoryOffset + directoryLength;
        var file = new byte[dataOffset + 32];
        "ITSF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), itsfLength);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0x48), checked((ulong)directoryOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0x50), checked((ulong)directoryLength));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(0x58), checked((ulong)dataOffset));

        var directory = new byte[directoryLength];
        "ITSP"u8.CopyTo(directory);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(8), itspLength);
        BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(0x10), blockLength);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(0x1C), -1);
        BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(0x20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(0x28), 1);
        var block = new byte[blockLength];
        "PMGL"u8.CopyTo(block);
        var entries = new List<byte>();
        AddEntry("#SYSTEM", 0, 0, 18);
        AddEntry("home.html", 1, 0, 42);
        entries.CopyTo(block, 0x14);
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(4), checked((uint)(blockLength - 0x14 - entries.Count)));
        block.CopyTo(directory, itspLength);
        directory.CopyTo(file, directoryOffset);
        await File.WriteAllBytesAsync(temporary.Path, file);
        Assert.Equal("ITSF", Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal("ITSP", Encoding.ASCII.GetString(file, directoryOffset, 4));
        Assert.Equal("PMGL", Encoding.ASCII.GetString(file, directoryOffset + itspLength, 4));
        Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(directoryOffset + 0x28)));

        var preview = await new ChmPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("CHM structure and directory", preview?.Source);
        Assert.Contains("ITSF version: 3", preview?.Text);
        Assert.Contains("home.html", preview?.Text);
        Assert.Contains("#SYSTEM", preview?.Text);
        Assert.Contains("Entries shown: 2", preview?.Text);
        Assert.Contains("MSCompressed/LZX topic payloads were not decompressed", preview?.Text);

        void AddEntry(string path, ulong section, ulong offset, ulong length)
        {
            entries.AddRange(Cword((ulong)Encoding.UTF8.GetByteCount(path)));
            entries.AddRange(Encoding.UTF8.GetBytes(path));
            entries.AddRange(Cword(section));
            entries.AddRange(Cword(offset));
            entries.AddRange(Cword(length));
        }

        static byte[] Cword(ulong value)
        {
            Span<byte> groups = stackalloc byte[10];
            var count = 0;
            do
            {
                groups[count++] = (byte)(value & 0x7F);
                value >>= 7;
            } while (value != 0);
            var result = new byte[count];
            for (var index = 0; index < count; index++)
            {
                var group = groups[count - index - 1];
                result[index] = index == count - 1 ? group : (byte)(group | 0x80);
            }
            return result;
        }
    }

    [Fact]
    public async Task PostScriptProviderReadsDscAndLiteralTextWithoutExecutingCode()
    {
        using var temporary = new TemporaryFile("sample.eps");
        await File.WriteAllTextAsync(
            temporary.Path,
            "%!PS-Adobe-3.0 EPSF-3.0\n" +
            "%%Title: (Safe illustration)\n" +
            "%%Creator: Preview test\n" +
            "%%Pages: 2\n" +
            "%%BoundingBox: 0 0 640 480\n" +
            "%%Page: 1 1\n" +
            "(Hello \\(PostScript\\)) show\n" +
            "%%Page: 2 2\n" +
            "(Second page) show\n" +
            "systemdict /file known { (must not run) } if\n");

        var preview = await new PostScriptPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("PostScript structure and text", preview?.Source);
        Assert.Contains("Title: Safe illustration", preview?.Text);
        Assert.Contains("Pages (DSC): 2", preview?.Text);
        Assert.Contains("Page markers: 2", preview?.Text);
        Assert.Contains("Bounding box: 0 0 640 480", preview?.Text);
        Assert.Contains("Hello (PostScript)", preview?.Text);
        Assert.Contains("must not run", preview?.Text);
        Assert.Contains("was not interpreted or executed", preview?.Text);
    }

    [Fact]
    public async Task PostScriptProviderReadsBinaryEpsPostScriptSectionOnly()
    {
        using var temporary = new TemporaryFile("binary.eps");
        var payload = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%Title: Binary wrapper\n");
        var file = new byte[12 + payload.Length];
        new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 }.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), checked((uint)payload.Length));
        payload.CopyTo(file, 12);
        await File.WriteAllBytesAsync(temporary.Path, file);

        var preview = await new PostScriptPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Container: binary EPS wrapper", preview?.Text);
        Assert.Contains("Title: Binary wrapper", preview?.Text);
    }

    [Fact]
    public async Task LhaProviderListsLevelZeroHeadersWithoutReadingPayloads()
    {
        using var temporary = new TemporaryFile("sample.lzh");
        var path = Encoding.ASCII.GetBytes("hello.txt");
        var payload = Encoding.ASCII.GetBytes("secret");
        const int headerSize = 32;
        var bytes = new byte[2 + headerSize + payload.Length];
        bytes[0] = headerSize;
        Encoding.ASCII.GetBytes("-lh0-").CopyTo(bytes, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(7), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(11), (uint)payload.Length);
        bytes[19] = 0x20;
        bytes[20] = 0;
        bytes[21] = checked((byte)path.Length);
        path.CopyTo(bytes, 22);
        // Leave the optional data CRC at zero and identify the producer as ASCII.
        bytes[33] = (byte)'A';
        var checksum = 0;
        for (var index = 2; index < 2 + headerSize; index++)
        {
            checksum = (checksum + bytes[index]) & 0xff;
        }
        bytes[1] = checked((byte)checksum);
        payload.CopyTo(bytes, 2 + headerSize);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new LhaArchivePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("LHA archive directory", preview?.Source);
        Assert.Contains("hello.txt", preview?.Text);
        Assert.Contains("-lh0-", preview?.Text);
        Assert.DoesNotContain("secret", preview?.Text);
        Assert.Contains("payloads were not opened or decompressed", preview?.Text);
    }

    [Fact]
    public async Task LhaProviderListsLevelTwoExtendedNames()
    {
        using var temporary = new TemporaryFile("level2.lha");
        const int headerSize = 33;
        var payload = Encoding.ASCII.GetBytes("body");
        var bytes = new byte[headerSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, headerSize);
        Encoding.ASCII.GetBytes("-lh0-").CopyTo(bytes, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(7), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(11), (uint)payload.Length);
        bytes[19] = 0x20;
        bytes[20] = 2;
        bytes[23] = (byte)'A';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), 7);
        bytes[26] = 1;
        Encoding.ASCII.GetBytes("note").CopyTo(bytes, 27);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(31), 0);
        payload.CopyTo(bytes, headerSize);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new LhaArchivePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("LHA archive directory", preview?.Source);
        Assert.Contains("note", preview?.Text);
        Assert.DoesNotContain("body", preview?.Text);
    }

    [Fact]
    public async Task LhaProviderListsLevelOneExtendedNames()
    {
        using var temporary = new TemporaryFile("level1.lzh");
        const int baseHeaderBytes = 36;
        const int extensionBytes = 7; // type + four-byte name + next size
        var payload = Encoding.ASCII.GetBytes("data");
        var bytes = new byte[baseHeaderBytes + extensionBytes + payload.Length];
        bytes[0] = 34; // level-1 header size excludes its first two bytes
        Encoding.ASCII.GetBytes("-lh0-").CopyTo(bytes, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(7), (uint)(extensionBytes + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(11), (uint)payload.Length);
        bytes[19] = 0x20;
        bytes[20] = 1;
        bytes[21] = 0; // extended filename follows
        bytes[24] = (byte)'A';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), extensionBytes);
        bytes[36] = 1;
        Encoding.ASCII.GetBytes("note").CopyTo(bytes, 37);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(41), 0);
        payload.CopyTo(bytes, baseHeaderBytes + extensionBytes);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new LhaArchivePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("LHA archive directory", preview?.Source);
        Assert.Contains("note", preview?.Text);
        Assert.DoesNotContain("data", preview?.Text);
    }

    [Fact]
    public async Task DjvuProviderReadsPageInfoAndPlainTextWithoutDecodingImages()
    {
        using var temporary = new TemporaryFile("sample.djvu");
        var info = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(info, 640);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2), 480);
        info[4] = 25;
        var textBytes = Encoding.UTF8.GetBytes("DjVu OCR text");
        var text = new byte[3 + textBytes.Length + 1];
        text[0] = 0;
        text[1] = 0;
        text[2] = checked((byte)textBytes.Length);
        textBytes.CopyTo(text, 3);
        text[^1] = 1;
        var page = Combine(
            "DJVU"u8.ToArray(),
            Chunk("INFO", info),
            Chunk("TXTa", text));
        var file = Combine(
            "AT&T"u8.ToArray(),
            Chunk("FORM", page));
        await File.WriteAllBytesAsync(temporary.Path, file);

        var preview = await new DjvuPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("DjVu structure and text", preview?.Source);
        Assert.Contains("Pages: 1", preview?.Text);
        Assert.Contains("640 × 480", preview?.Text);
        Assert.Contains("DjVu OCR text", preview?.Text);
        Assert.Contains("Image, JB2, annotation, and BZZ-compressed payloads were not decoded", preview?.Text);

        static byte[] Chunk(string id, byte[] payload)
        {
            using var stream = new MemoryStream();
            stream.Write(Encoding.ASCII.GetBytes(id));
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)payload.Length));
            stream.Write(length);
            stream.Write(payload);
            if ((payload.Length & 1) != 0)
            {
                stream.WriteByte(0);
            }
            return stream.ToArray();
        }

        static byte[] Combine(params byte[][] parts)
        {
            using var stream = new MemoryStream();
            foreach (var part in parts)
            {
                stream.Write(part);
            }
            return stream.ToArray();
        }
    }

    [Fact]
    public void OutlookStoreFolderSummaryBoundsFolderCountAndOutput()
    {
        var children = Enumerable.Range(0, OutlookStoreFolderSummary.MaximumFolders + 100)
            .Select(index => new FakeOutlookStoreFolderNode(
                $"Folder-{index:D4}-{new string('x', 700)}",
                index,
                index / 2));
        var root = new FakeOutlookStoreFolderNode("Root", 0, 0, children.ToArray());

        var summary = OutlookStoreFolderSummary.Build(
            root,
            "Outlook Offline Storage (Unicode)",
            1024,
            CancellationToken.None);

        Assert.Contains("folder listing truncated by the folder/output budget", summary);
        Assert.True(summary.Length <= OutlookStoreFolderSummary.MaximumOutputCharacters + 3);
    }

    [Fact]
    public void OutlookStoreFolderSummaryHonorsCancellation()
    {
        var root = new FakeOutlookStoreFolderNode("Root", 0, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => OutlookStoreFolderSummary.Build(
            root,
            "Outlook Personal Folders (Unicode)",
            1,
            cancellation.Token));
    }

    [Fact]
    public async Task OutlookStoreProviderRejectsInvalidPstAndLeavesMetadataFallbackAvailable()
    {
        using var temporary = new TemporaryFile("invalid.pst");
        await File.WriteAllBytesAsync(temporary.Path, Encoding.ASCII.GetBytes("!BDN\0\0\0\0\0\0\u0017\0"));

        var context = Context(temporary.Path);
        var provider = new OutlookStorePreviewProvider();

        Assert.True(provider.CanPreview(context));
        Assert.Null(await provider.LoadAsync(context, CancellationToken.None));
        Assert.True(PreviewFormatRegistry.Supports(".pst", PreviewFallback.Opaque));
    }

    [Fact]
    public async Task WindowsEventLogProviderRejectsInvalidEvtxAndLeavesMetadataFallbackAvailable()
    {
        using var temporary = new TemporaryFile("invalid.evtx");
        await File.WriteAllBytesAsync(temporary.Path, new byte[1024]);

        var context = Context(temporary.Path);
        var provider = new WindowsEventLogPreviewProvider();

        Assert.True(provider.CanPreview(context));
        Assert.Null(await provider.LoadAsync(context, CancellationToken.None));
        Assert.True(PreviewFormatRegistry.Supports(".evtx", PreviewFallback.Opaque));
    }

    [Fact]
    public async Task EbookProviderExtractsPalmDocText()
    {
        using var temporary = new TemporaryFile("sample.mobi");
        var text = "PalmDOC preview text";
        await File.WriteAllBytesAsync(temporary.Path, CreatePalmDoc(text));

        var preview = await new EbookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Ebook metadata and text", preview?.Source);
        Assert.Contains("Sample Book", preview?.Text);
        Assert.Contains("PalmDOC LZ77", preview?.Text);
        Assert.Contains(text, preview?.Text);
    }

    [Fact]
    public async Task EbookProviderStreamsRecordsFromBooksLargerThanTheOldSixteenMiBLimit()
    {
        using var temporary = new TemporaryFile("large.azw");
        var content = CreateLargePalmDocHeader("Large book preview");
        await using (var stream = new FileStream(temporary.Path, FileMode.Create, FileAccess.Write))
        {
            await stream.WriteAsync(content);
            stream.SetLength(32L * 1024 * 1024);
        }

        var preview = await new EbookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Ebook metadata and text", preview?.Source);
        Assert.Contains("Large Sample", preview?.Text);
        Assert.Contains("Large book preview", preview?.Text);
    }

    [Fact]
    public async Task EbookProviderHonorsDeclaredTextLength()
    {
        using var temporary = new TemporaryFile("bounded.mobi");
        var bytes = CreatePalmDoc("helloSECRET");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(98), 5);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new EbookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("hello", preview?.Text);
        Assert.DoesNotContain("SECRET", preview?.Text);
    }

    [Fact]
    public async Task EbookProviderRejectsPalmDocBackReferencesAcrossRecordBoundaries()
    {
        using var temporary = new TemporaryFile("invalid.mobi");
        await File.WriteAllBytesAsync(temporary.Path, CreateCrossRecordPalmDoc());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EbookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None));
    }

    [Fact]
    public async Task KfxProviderListsKfxZipMembersWithoutOpeningPayloads()
    {
        using var temporary = new TemporaryFile("sample.kfx");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("book/manifestion");
            archive.CreateEntry("book/contention");
        }

        var preview = await new KfxPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("KFX-ZIP structure", preview?.Source);
        Assert.Contains("book/manifestion", preview?.Text);
        Assert.Contains("payloads not opened", preview?.Text);
    }

    [Fact]
    public async Task KfxProviderSamplesRawIonPrintableMetadataWithoutDecodingIt()
    {
        using var temporary = new TemporaryFile("raw.kfx");
        await File.WriteAllBytesAsync(
            temporary.Path,
            [0x01, 0x02, 0x93, 0x10, (byte)'t', (byte)'i', (byte)'t', (byte)'l', (byte)'e', (byte)'=',
             (byte)'K', (byte)'F', (byte)'X', (byte)' ', (byte)'p', (byte)'r', (byte)'e', (byte)'v', (byte)'i', (byte)'e', (byte)'w']);

        var preview = await new KfxPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("KFX structure", preview?.Source);
        Assert.Contains("title=KFX preview", preview?.Text);
        Assert.Contains("were not decrypted or interpreted", preview?.Text);
    }

    [Fact]
    public async Task AccessProviderReadsJetHeaderNamesWithoutOpeningRows()
    {
        using var temporary = new TemporaryFile("sample.accdb");
        var bytes = new byte[4096 * 2];
        Encoding.ASCII.GetBytes("Standard ACE DB").CopyTo(bytes, 4);
        Encoding.Unicode.GetBytes("Customers").CopyTo(bytes, 256);
        Encoding.Unicode.GetBytes("Orders").CopyTo(bytes, 280);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new AccessDatabasePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Access database structure", preview?.Source);
        Assert.Contains("Engine marker: Standard ACE DB", preview?.Text);
        Assert.Contains("Customers", preview?.Text);
        Assert.Contains("Orders", preview?.Text);
        Assert.Contains("Rows, schema pages", preview?.Text);
    }

    [Fact]
    public async Task TorrentProviderReadsBencodedMetadataWithoutContactingTrackers()
    {
        using var temporary = new TemporaryFile("sample.torrent");
        var torrent = Encoding.ASCII.GetBytes(
            "d8:announce14:http://tracker4:infod4:name9:Test Book6:lengthi123e12:piece lengthi16384e6:pieces20:12345678901234567890ee");
        await File.WriteAllBytesAsync(temporary.Path, torrent);

        var preview = await new TorrentPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("BitTorrent metadata", preview?.Source);
        Assert.Contains("Tracker: http://tracker", preview?.Text);
        Assert.Contains("Name: Test Book", preview?.Text);
        Assert.Contains("Piece length: 16384", preview?.Text);
        Assert.Contains("Length: 123", preview?.Text);
        Assert.Contains("Piece hashes: 1", preview?.Text);
        Assert.Contains("Tracker requests were not made", preview?.Text);
        Assert.DoesNotContain("12345678901234567890", preview?.Text);
    }

    [Fact]
    public async Task PacketCaptureProviderReadsPcapRecordHeadersWithoutDisplayingPayloads()
    {
        using var temporary = new TemporaryFile("sample.pcap");
        var bytes = new byte[24 + 16 + 4];
        bytes[0] = 0xD4; bytes[1] = 0xC3; bytes[2] = 0xB2; bytes[3] = 0xA1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 65_535);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 12), 4);
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(bytes.AsSpan(40));
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new PacketCapturePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("PCAP packet capture structure", preview?.Source);
        Assert.Contains("PCAP version: 2.4", preview?.Text);
        Assert.Contains("Records scanned: 1", preview?.Text);
        Assert.Contains("Captured bytes declared: 4", preview?.Text);
        Assert.DoesNotContain("DE AD BE EF", preview?.Text);
        Assert.Contains("payloads were not decoded", preview?.Text);
    }

    [Fact]
    public async Task PacketCaptureProviderReadsPcapNgEnhancedPacketCounts()
    {
        using var temporary = new TemporaryFile("sample.pcapng");
        var bytes = new byte[28 + 36];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(), 0x0A0D0D0A);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x1A2B3C4D);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 36);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new PacketCapturePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("PCAPNG packet capture structure", preview?.Source);
        Assert.Contains("Blocks scanned: 2", preview?.Text);
        Assert.Contains("Enhanced packets: 1", preview?.Text);
        Assert.Contains("Captured bytes declared: 4", preview?.Text);
    }

    [Fact]
    public async Task PacketCaptureProviderReadsBigEndianPcapNgHeaders()
    {
        using var temporary = new TemporaryFile("big-endian.pcapng");
        var bytes = new byte[28 + 32];
        bytes[0] = 0x0A; bytes[1] = 0x0D; bytes[2] = 0x0D; bytes[3] = 0x0A;
        bytes[7] = 28;
        bytes[8] = 0x1A; bytes[9] = 0x2B; bytes[10] = 0x3C; bytes[11] = 0x4D;
        bytes[13] = 1;
        bytes[27] = 28;
        bytes[28 + 3] = 6;
        bytes[28 + 7] = 32;
        bytes[28 + 31] = 32;
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new PacketCapturePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("PCAPNG packet capture structure", preview?.Source);
        Assert.Contains("Blocks scanned: 2", preview?.Text);
        Assert.Contains("Enhanced packets: 1", preview?.Text);
    }

    [Fact]
    public async Task DocumentProviderExtractsGlyphTextFromXpsWithoutRenderingMarkup()
    {
        using var temporary = new TemporaryFile("sample.xps");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Documents/1/Pages/1.fpage");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(
                """<FixedPage xmlns="http://schemas.microsoft.com/xps/2005/06"><Glyphs UnicodeString="XPS preview text" /></FixedPage>""");
        }

        var preview = await new DocumentPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Document text", preview?.Source);
        Assert.Contains("XPS preview text", preview?.Text);
        Assert.DoesNotContain("<Glyphs", preview?.Text);
    }

    [Fact]
    public async Task DocumentProviderExtractsTextFromVisioOpenPackaging()
    {
        using var temporary = new TemporaryFile("diagram.vsdx");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("visio/pages/page1.xml");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("<PageContents><Shapes><Shape><Text>Visio page text</Text></Shape></Shapes></PageContents>");
        }

        var preview = await new DocumentPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Document text", preview?.Source);
        Assert.Contains("Visio page text", preview?.Text);
    }

    [Theory]
    [InlineData("book.fb2", false)]
    [InlineData("book.fb2.zip", true)]
    [InlineData("book.fbz", true)]
    public async Task FictionBookProviderExtractsTextButSkipsEmbeddedBinary(
        string fileName,
        bool zipped)
    {
        using var temporary = new TemporaryFile(fileName);
        const string source =
            """<?xml version="1.0"?><FictionBook><description><title-info><book-title>Sample FB2</book-title></title-info></description><body><section><p>Readable chapter</p></section></body><binary id="cover">U0VDUkVUX0JJTkFSWQ==</binary></FictionBook>""";
        if (zipped)
        {
            using var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("book.fb2");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(source);
        }
        else
        {
            await File.WriteAllTextAsync(temporary.Path, source);
        }

        var preview = await new FictionBookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("FictionBook text", preview?.Source);
        Assert.Contains("Sample FB2", preview?.Text);
        Assert.Contains("Readable chapter", preview?.Text);
        Assert.DoesNotContain("U0VDUkVUX0JJTkFSWQ", preview?.Text);
    }

    [Fact]
    public async Task FictionBookEmptyBinaryDoesNotHideFollowingBodyAndOutputIsBounded()
    {
        using var temporary = new TemporaryFile("bounded.fb2");
        await File.WriteAllTextAsync(
            temporary.Path,
            $"<FictionBook><binary/><body><p>visible {new string('x', 200_000)}</p></body></FictionBook>");

        var preview = await new FictionBookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("visible", preview?.Text);
        Assert.True(preview?.Text?.Length <= 120_002);
    }

    [Fact]
    public async Task FictionBookZipRejectsExcessiveCentralDirectoryEntries()
    {
        using var temporary = new TemporaryFile("many.fbz");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 4097; index++)
            {
                archive.CreateEntry($"{index:D4}.txt");
            }
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FictionBookPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None));
    }

    [Fact]
    public void CompoundFictionBookZipDoesNotPreferTheSystemArchiveHandler()
    {
        var context = new PreviewContext(
            @"C:\books\sample.fb2.zip",
            "sample.fb2.zip",
            ".zip",
            100,
            DateTimeOffset.UtcNow);

        Assert.False(PreviewCoordinator.ShouldPreferSystemPreview(context));
    }

    [Theory]
    [InlineData("sample.woff", 0x774F4646u, false)]
    [InlineData("sample.woff2", 0x774F4632u, true)]
    public async Task WebFontProviderReadsBoundedContainerMetadata(
        string name,
        uint signature,
        bool isWoff2)
    {
        using var temporary = new TemporaryFile(name);
        var header = new byte[isWoff2 ? 64 : 68];
        BinaryPrimitives.WriteUInt32BigEndian(header, signature);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)header.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 32);
        if (isWoff2)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 4);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), 1);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(26), 2);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(22), 2);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(44), 0x6E616D65);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(48), 64);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(52), 4);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 4);
        }
        await File.WriteAllBytesAsync(temporary.Path, header);

        var preview = await new WebFontPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Web font metadata", preview?.Source);
        Assert.Contains("TrueType", preview?.Text);
        Assert.Contains("Tables: 1", preview?.Text);
        Assert.Contains("Version: 1.2", preview?.Text);
    }

    [Fact]
    public async Task WebFontProviderReadsWoffFamilyAndFullName()
    {
        using var temporary = new TemporaryFile("identified.woff");
        await File.WriteAllBytesAsync(
            temporary.Path,
            CreateWoffWithNames("Listary Sans", "Listary Sans Regular"));

        var preview = await new WebFontPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Web font metadata", preview?.Source);
        Assert.Contains("Family: Listary Sans", preview?.Text);
        Assert.Contains("Full name: Listary Sans Regular", preview?.Text);
    }

    [Fact]
    public async Task WebFontProviderReadsCompressedWoffNameTable()
    {
        using var temporary = new TemporaryFile("compressed.woff");
        await File.WriteAllBytesAsync(
            temporary.Path,
            CreateWoffWithNames("Compressed Sans", "Compressed Sans Regular", compress: true));

        var preview = await new WebFontPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Contains("Family: Compressed Sans", preview?.Text);
        Assert.Contains("Full name: Compressed Sans Regular", preview?.Text);
    }

    [Fact]
    public async Task WebFontProviderRejectsTableOverlappingItsDirectory()
    {
        using var temporary = new TemporaryFile("overlap.woff");
        var bytes = CreateWoffWithNames("Hidden Sans", "Hidden Sans Regular");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(48), 44);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new WebFontPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Null(preview);
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
    public async Task ArchiveProviderTreatsCbzAsAZipComicAndBoundsLongNames()
    {
        using var temporary = new TemporaryFile("sample.cbz");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 220; index++)
            {
                archive.CreateEntry($"{index:D3}-{new string('x', 700)}.png");
            }
        }

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("ZIP archive", preview?.Text);
        Assert.Contains("Entries: 220", preview?.Text);
        Assert.Contains("20 more entries", preview?.Text);
        Assert.True(preview?.Text?.Length < 125_000);
    }

    [Fact]
    public void RegisteredFormatsHaveCanonicalKeysAndExplicitNonTextFallbacks()
    {
        Assert.NotEmpty(PreviewFormatRegistry.All);
        foreach (var (extension, format) in PreviewFormatRegistry.All)
        {
            Assert.StartsWith(".", extension);
            Assert.Equal(extension.ToLowerInvariant(), extension);
            if (format.IsBinary)
            {
                Assert.False((format.Fallbacks & PreviewFallback.Text) != 0, extension);
            }
        }
        Assert.DoesNotContain(
            PreviewFormatRegistry.All,
            pair => pair.Value.BuiltInCapability == PreviewCapability.None);
    }

    [Theory]
    [InlineData(".png", "Raster", "RenderedContent", false)]
    [InlineData(".docx", "Document", "ExtractedContent", true)]
    [InlineData(".pdf", "Pdf", "Metadata", true)]
    [InlineData(".cbz", "Archive", "ExtractedContent", true)]
    [InlineData(".opus", "Media", "Metadata", true)]
    [InlineData(".m4b", "Media", "Metadata", true)]
    [InlineData(".cr3", "ShellThumbnail", "Thumbnail", true)]
    [InlineData(".raf", "ShellThumbnail", "Thumbnail", true)]
    [InlineData(".xps", "Document", "ExtractedContent", true)]
    [InlineData(".chm", "ChmDocument", "ExtractedContent", true)]
    [InlineData(".fb2", "FictionBook", "ExtractedContent", false)]
    [InlineData(".dot", "Document", "Metadata", true)]
    [InlineData(".xls", "Document", "ExtractedContent", true)]
    [InlineData(".xlt", "Document", "ExtractedContent", true)]
    [InlineData(".msg", "OutlookMessage", "ExtractedContent", true)]
    [InlineData(".oft", "OutlookMessage", "ExtractedContent", true)]
    [InlineData(".msix", "Archive", "ExtractedContent", true)]
    [InlineData(".vsdx", "Document", "ExtractedContent", true)]
    [InlineData(".jp2", "Raster", "RenderedContent", true)]
    [InlineData(".dds", "Raster", "RenderedContent", true)]
    [InlineData(".eps", "PostScriptVector", "ExtractedContent", true)]
    [InlineData(".eot", "WebFont", "Metadata", true)]
    [InlineData(".kfx", "Ebook", "ExtractedContent", true)]
    [InlineData(".sqlite", "Database", "ExtractedContent", false)]
    [InlineData(".accdb", "Database", "ExtractedContent", true)]
    [InlineData(".zipx", "Archive", "ExtractedContent", true)]
    [InlineData(".dxf", "Model", "ExtractedContent", true)]
    [InlineData(".torrent", "Opaque", "ExtractedContent", true)]
    [InlineData(".pcap", "Opaque", "ExtractedContent", true)]
    [InlineData(".ics", "Pim", "ExtractedContent", false)]
    [InlineData(".pst", "OutlookStore", "ExtractedContent", true)]
    [InlineData(".evtx", "EventLog", "ExtractedContent", true)]
    [InlineData(".one", "CompoundDocument", "ExtractedContent", true)]
    [InlineData(".lzh", "LhaArchive", "ExtractedContent", true)]
    [InlineData(".djvu", "DjvuDocument", "ExtractedContent", true)]
    [InlineData(".br", "Archive", "ExtractedContent", true)]
    [InlineData(".svgz", "Svg", "ExtractedContent", false)]
    [InlineData(".dwg", "Opaque", "Metadata", true)]
    [InlineData(".class", "Opaque", "ExtractedContent", false)]
    [InlineData(".wasm", "Opaque", "ExtractedContent", false)]
    [InlineData(".dmp", "Opaque", "ExtractedContent", true)]
    [InlineData(".msu", "Archive", "ExtractedContent", true)]
    [InlineData(".wim", "Archive", "ExtractedContent", true)]
    [InlineData(".esd", "Archive", "ExtractedContent", true)]
    [InlineData(".parquet", "Opaque", "ExtractedContent", true)]
    [InlineData(".webarchive", "WebArchive", "ExtractedContent", true)]
    [InlineData(".lz4", "Archive", "ExtractedContent", true)]
    [InlineData(".vdi", "Opaque", "ExtractedContent", true)]
    [InlineData(".vmdk", "Opaque", "ExtractedContent", true)]
    [InlineData(".qcow2", "Opaque", "ExtractedContent", true)]
    [InlineData(".dmg", "Opaque", "ExtractedContent", true)]
    [InlineData(".vhd", "VirtualDisk", "Metadata", true)]
    [InlineData(".vhdx", "VirtualDisk", "Metadata", true)]
    [InlineData(".zstd", "Archive", "ExtractedContent", true)]
    [InlineData(".exe", "Executable", "Metadata", false)]
    public void RegistryRoutesCommonFormatsToTheExpectedFallback(
        string extension,
        string fallbackName,
        string capabilityName,
        bool preferSystem)
    {
        var fallback = Enum.Parse<PreviewFallback>(fallbackName);
        var capability = Enum.Parse<PreviewCapability>(capabilityName);
        Assert.True(PreviewFormatRegistry.TryGet(extension, out var format));
        Assert.Equal(capability, format.BuiltInCapability);
        Assert.Equal(preferSystem, format.PreferSystem);
        Assert.True(PreviewFormatRegistry.Supports(extension.ToUpperInvariant(), fallback));
        Assert.False(PreviewFormatRegistry.CanAttemptText(extension));
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".dot")]
    [InlineData(".ppt")]
    [InlineData(".pot")]
    [InlineData(".pps")]
    public void LegacyWordAndPowerPointCanUseAnInstalledWindowsFilter(string extension)
    {
        Assert.True(PreviewFormatRegistry.Supports(extension, PreviewFallback.WindowsFilter));
        Assert.True(PreviewFormatRegistry.Supports(extension, PreviewFallback.Document));
    }

    [Fact]
    public void WindowsFilterExtractorCollectsBoundedTextChunks()
    {
        using var filter = new FakeWindowsFilterSession(
            new WindowsFilterChunk(1, 0, 1),
            "First filtered paragraph",
            new WindowsFilterChunk(2, 3, 1),
            "Second filtered paragraph");

        var text = WindowsFilterTextExtractor.Extract(filter, CancellationToken.None);

        Assert.Contains("First filtered paragraph", text);
        Assert.Contains("Second filtered paragraph", text);
        Assert.True(text?.Length <= WindowsFilterTextExtractor.MaximumOutputCharacters + 64);
        Assert.Equal(WindowsFilterTextExtractor.InitializationFlags, filter.InitializationFlags);
        Assert.Equal(16u | 32u, filter.InitializationFlags & (16u | 32u));
        Assert.Equal(0u, filter.InitializationFlags & (128u | 512u));
        Assert.NotEqual(0u, filter.InitializationFlags & 2048u);
    }

    [Fact]
    public void WindowsFilterExtractorRejectsNonIncreasingChunkIds()
    {
        using var filter = new FakeWindowsFilterSession(
            new WindowsFilterChunk(2, 0, 1),
            "first",
            new WindowsFilterChunk(2, 0, 1),
            "duplicate");

        Assert.Throws<InvalidDataException>(() =>
            WindowsFilterTextExtractor.Extract(filter, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void WindowsFilterExtractorRejectsInvalidChunkFlags(uint flags)
    {
        using var filter = new FakeWindowsFilterSession(
            new WindowsFilterChunk(1, 0, flags),
            "invalid");

        Assert.Throws<InvalidDataException>(() =>
            WindowsFilterTextExtractor.Extract(filter, CancellationToken.None));
    }

    [Fact]
    public void WindowsFilterExtractorRejectsUnknownPositiveChunkResult()
    {
        using var filter = new FakeWindowsFilterSession
        {
            ChunkResultOverride = 1
        };

        Assert.Throws<InvalidDataException>(() =>
            WindowsFilterTextExtractor.Extract(filter, CancellationToken.None));
    }

    [Fact]
    public void WindowsFilterExtractorRejectsTextLengthBeyondTheSuppliedBuffer()
    {
        using var filter = new FakeWindowsFilterSession(
            new WindowsFilterChunk(1, 0, 1),
            "text")
        {
            ReturnedCharacterCountOverride = 4097
        };

        Assert.Throws<InvalidDataException>(() =>
            WindowsFilterTextExtractor.Extract(filter, CancellationToken.None));
    }

    [Fact]
    public void WindowsFilterExtractorRejectsSuccessfulTextWithoutProgress()
    {
        using var filter = new FakeWindowsFilterSession(
            new WindowsFilterChunk(1, 0, 1),
            "text")
        {
            ReturnSuccessfulTextWithoutProgress = true
        };

        Assert.Throws<InvalidDataException>(() =>
            WindowsFilterTextExtractor.Extract(filter, CancellationToken.None));
    }

    [Fact]
    public void WindowsFilterExtractorHonorsCancellationBeforeCallingTheFilter()
    {
        using var filter = new FakeWindowsFilterSession(
            new WindowsFilterChunk(1, 0, 1),
            "text");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WindowsFilterTextExtractor.Extract(filter, cancellation.Token));
        Assert.Equal(0, filter.ChunkCalls);
    }

    [Fact]
    public void WindowsFilterNativeStructuresMatchTheX64Abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(16, Marshal.SizeOf<NativePropertySpec>());
        Assert.Equal(32, Marshal.SizeOf<NativeFullPropertySpec>());
        Assert.Equal(64, Marshal.SizeOf<NativeStatChunk>());
        Assert.Equal(16, Marshal.OffsetOf<NativeStatChunk>(
            nameof(NativeStatChunk.Attribute)).ToInt32());
        Assert.Equal(12, Marshal.SizeOf<NativeFilterRegion>());
    }

    [Fact]
    public void WindowsImageMetadataReaderSummarizesBoundedImageFields()
    {
        const string xml = """
            <WIM>
              <IMAGE INDEX="1">
                <NAME>Windows 11 Pro</NAME>
                <DESCRIPTION>Deployment image</DESCRIPTION>
                <TOTALBYTES>1073741824</TOTALBYTES>
                <WINDOWS>
                  <ARCH>9</ARCH>
                  <PRODUCTNAME>Microsoft Windows 11</PRODUCTNAME>
                  <EDITIONID>Professional</EDITIONID>
                  <INSTALLATIONTYPE>Client</INSTALLATIONTYPE>
                  <VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>22631</BUILD><SPBUILD>1</SPBUILD></VERSION>
                  <LANGUAGES><LANGUAGE>en-US</LANGUAGE><LANGUAGE>zh-CN</LANGUAGE></LANGUAGES>
                </WINDOWS>
              </IMAGE>
            </WIM>
            """;

        var summary = WindowsImageMetadataReader.Parse(xml, ".wim", CancellationToken.None);

        Assert.Contains("Windows Imaging Format", summary);
        Assert.Contains("Windows 11 Pro", summary);
        Assert.Contains("Architecture: x64", summary);
        Assert.Contains("Version: 10.0.22631.1", summary);
        Assert.Contains("Languages: en-US, zh-CN", summary);
        Assert.Contains("1.0 GB", summary);
        Assert.Contains("no image was mounted, applied, or extracted", summary);
    }

    [Fact]
    public void WindowsImageMetadataReaderRejectsExternalEntities()
    {
        const string xml = """
            <!DOCTYPE WIM [<!ENTITY external SYSTEM "file:///C:/Windows/win.ini">]>
            <WIM><IMAGE INDEX="1"><NAME>&external;</NAME></IMAGE></WIM>
            """;

        Assert.Throws<XmlException>(() =>
            WindowsImageMetadataReader.Parse(xml, ".wim", CancellationToken.None));
    }

    [Fact]
    public void WindowsImageMetadataReaderCapsTheImageCountAndOutput()
    {
        var xml = "<WIM>" + string.Concat(Enumerable.Range(1, 65)
            .Select(index => $"<IMAGE INDEX=\"{index}\"><NAME>Image {index}</NAME></IMAGE>")) +
            "</WIM>";

        var summary = WindowsImageMetadataReader.Parse(xml, ".esd", CancellationToken.None);

        Assert.Contains("Electronic Software Download image", summary);
        Assert.Contains("Images shown: 64+", summary);
        Assert.Contains("Image 64", summary);
        Assert.DoesNotContain("Image 65", summary);
        Assert.True(summary.Length <= 120_512);
    }

    [Fact]
    public void WindowsImageProviderAcceptsOnlyWimAndEsd()
    {
        var provider = new WindowsImagePreviewProvider();

        Assert.True(provider.CanPreview(new PreviewContext(
            @"C:\sample.wim", "sample.wim", ".wim", 1, DateTime.UtcNow)));
        Assert.True(provider.CanPreview(new PreviewContext(
            @"C:\sample.ESD", "sample.ESD", ".ESD", 1, DateTime.UtcNow)));
        Assert.False(provider.CanPreview(new PreviewContext(
            @"C:\sample.iso", "sample.iso", ".iso", 1, DateTime.UtcNow)));
    }

    [Fact]
    public void WindowsImageListingStateRejectsEscapesAndStopsAtItsEntryBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "ListaryOpenListingState");
        var state = new WindowsImageListingState(
            root,
            CancellationToken.None,
            maximumEntries: 2,
            maximumMessages: 10,
            maximumPathCharacters: 32,
            maximumOutputCharacters: 1024,
            timeout: TimeSpan.FromMinutes(1));
        state.BeginImage(1);

        Assert.True(state.Add(Path.Combine(root, "Windows", "System32")));
        Assert.True(state.Add(Path.Combine(root, "Users", "preview.txt")));
        Assert.False(state.Add(Path.Combine(root, "third.txt")));
        Assert.True(state.AbortedByBudget);

        var listing = state.Build(imagesTruncated: false);
        Assert.Contains(@"Windows\System32", listing);
        Assert.Contains(@"Users\preview.txt", listing);
        Assert.DoesNotContain("third.txt", listing);
        Assert.Contains("listing truncated", listing);

        var escape = new WindowsImageListingState(
            root,
            CancellationToken.None,
            maximumEntries: 2,
            maximumMessages: 10,
            maximumPathCharacters: 32,
            maximumOutputCharacters: 1024,
            timeout: TimeSpan.FromMinutes(1));
        Assert.True(escape.Add(Path.GetFullPath(Path.Combine(root, "..", "secret.txt"))));
        Assert.Null(escape.Build(imagesTruncated: false));
    }

    [Theory]
    [InlineData("legacy.xls")]
    [InlineData("template.xlt")]
    public async Task DocumentProviderExtractsBoundedLegacyExcelCells(string fileName)
    {
        using var temporary = new TemporaryFile(fileName);
        await File.WriteAllBytesAsync(temporary.Path, CreateLegacyXlsFixture());

        var preview = await new DocumentPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Legacy Excel cells", preview?.Source);
        Assert.Contains("Sheet: Sheet1", preview?.Text);
        Assert.Contains("10x10", preview?.Text);
        Assert.Contains("Cached cell values only", preview?.Text);
        Assert.Contains("are not executed", preview?.Text);
    }

    [Fact]
    public async Task DocumentProviderSafelyDegradesForDamagedLegacyExcel()
    {
        using var temporary = new TemporaryFile("damaged.xls");
        await File.WriteAllBytesAsync(
            temporary.Path,
            [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 1, 2, 3]);

        var preview = await new DocumentPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Legacy Excel metadata", preview?.Source);
        Assert.Contains("encrypted, damaged, or uses an unsupported", preview?.Text);
    }

    [Fact]
    public async Task DocumentProviderRejectsOpenXmlDisguisedAsLegacyExcel()
    {
        using var temporary = new TemporaryFile("disguised.xls");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("xl/sharedStrings.xml");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("<sst><si><t>must not be decompressed</t></si></sst>");
        }

        var preview = await new DocumentPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Legacy Excel metadata", preview?.Source);
        Assert.Contains("does not contain a supported BIFF2–BIFF8", preview?.Text);
        Assert.DoesNotContain("must not be decompressed", preview?.Text);
    }

    [Fact]
    public async Task DocumentProviderDoesNotParseOversizedLegacyExcel()
    {
        using var temporary = new TemporaryFile("oversized.xls");
        await using (var stream = File.Create(temporary.Path))
        {
            stream.SetLength(33L * 1024 * 1024);
        }

        var preview = await new DocumentPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Legacy Excel metadata", preview?.Source);
        Assert.Contains("limited to 32.0 MB", preview?.Text);
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
    public async Task ParquetProviderReadsOnlyTheCompactMetadataFooter()
    {
        using var temporary = new TemporaryFile("schema.parquet");
        var footer = CreateMinimalParquetFooter();
        await using (var stream = new FileStream(temporary.Path, FileMode.Create, FileAccess.Write))
        {
            await stream.WriteAsync("PAR1"u8.ToArray());
            stream.SetLength(32L * 1024 * 1024);
            stream.Position = stream.Length - footer.Length - 8;
            await stream.WriteAsync(footer);
            var trailer = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(trailer, checked((uint)footer.Length));
            "PAR1"u8.CopyTo(trailer.AsSpan(4));
            await stream.WriteAsync(trailer);
        }

        var preview = await new ParquetPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Parquet schema", preview?.Source);
        Assert.Contains("Rows: 42", preview?.Text);
        Assert.Contains("Row groups: 0", preview?.Text);
        Assert.Contains("Created by: ListaryOpen test", preview?.Text);
        Assert.Contains("• schema", preview?.Text);
        Assert.Contains("• name — BYTE_ARRAY, optional, UTF8", preview?.Text);
        Assert.Contains("row values were not opened", preview?.Text);
    }

    [Fact]
    public async Task ParquetProviderRejectsFooterLengthsOutsideTheFile()
    {
        using var temporary = new TemporaryFile("invalid.parquet");
        var bytes = new byte[12];
        "PAR1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 9);
        "PAR1"u8.CopyTo(bytes.AsSpan(8));
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParquetPreviewProvider().LoadAsync(
                Context(temporary.Path),
                CancellationToken.None));
    }

    [Fact]
    public async Task ParquetProviderReadsFooterWrittenByIndependentLibrary()
    {
        using var temporary = new TemporaryFile("independent.parquet");
        var rows = new[]
        {
            new ParquetFixtureRow
            {
                Name = "alpha",
                EventTime = new DateTime(2026, 7, 26, 3, 0, 0, DateTimeKind.Utc),
                Value = 1.25m
            },
            new ParquetFixtureRow
            {
                Name = null,
                EventTime = new DateTime(2026, 7, 26, 4, 0, 0, DateTimeKind.Utc),
                Value = 2.50m
            }
        };
        await ParquetSerializer.SerializeAsync(rows, temporary.Path);

        var preview = await new ParquetPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Parquet schema", preview?.Source);
        Assert.Contains("Rows: 2", preview?.Text);
        Assert.Contains("Row groups: 1", preview?.Text);
        Assert.Contains("Name", preview?.Text);
        Assert.Contains("EventTime", preview?.Text);
        Assert.Contains("Value", preview?.Text);
    }

    [Fact]
    public async Task WebArchiveProviderReadsXmlMainResourceAndIgnoresSubresources()
    {
        using var temporary = new TemporaryFile("page.webarchive");
        var html = Encoding.UTF8.GetBytes(
            "<html><script>bad()</script><h1>Archived heading</h1><p>Visible body</p></html>");
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("SECRET_SUBRESOURCE"));
        await File.WriteAllTextAsync(
            temporary.Path,
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <plist version="1.0"><dict>
               <key>WebMainResource</key><dict>
                 <key>WebResourceURL</key><string>https://example.test/page</string>
                 <key>WebResourceMIMEType</key><string>text/html</string>
                 <key>WebResourceTextEncodingName</key><string>UTF-8</string>
                 <key>WebResourceData</key><data>{Convert.ToBase64String(html)}</data>
               </dict>
               <key>WebSubresources</key><array><dict>
                 <key>WebResourceData</key><data>{secret}</data>
               </dict></array>
             </dict></plist>
             """);

        var preview = await new WebArchivePreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Safe WebArchive", preview?.Source);
        Assert.Contains("Archived heading", preview?.Text);
        Assert.Contains("Visible body", preview?.Text);
        Assert.DoesNotContain("bad()", preview?.Text);
        Assert.DoesNotContain("SECRET_SUBRESOURCE", preview?.Text);
    }

    [Fact]
    public async Task WebArchiveProviderReadsBinaryPlistMainResource()
    {
        using var temporary = new TemporaryFile("binary.webarchive");
        var main = new NSDictionary();
        main.Add("WebResourceURL", new NSString("https://example.test/binary"));
        main.Add("WebResourceMIMEType", new NSString("text/plain"));
        main.Add("WebResourceTextEncodingName", new NSString("UTF-8"));
        main.Add("WebResourceData", new NSData(Encoding.UTF8.GetBytes("Binary plist body")));
        var root = new NSDictionary();
        root.Add("WebMainResource", main);
        await using (var output = File.Create(temporary.Path))
        {
            PropertyListParser.SaveAsBinary(root, output);
        }

        var preview = await new WebArchivePreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Contains("Binary plist body", preview?.Text);
        Assert.Contains("https://example.test/binary", preview?.Text);
    }

    [Fact]
    public async Task WebArchiveProviderRechecksTheOpenedFileLength()
    {
        using var temporary = new TemporaryFile("oversized.webarchive");
        await using (var stream = File.Create(temporary.Path))
        {
            stream.SetLength(5L * 1024 * 1024);
        }
        var context = new PreviewContext(
            temporary.Path,
            "oversized.webarchive",
            ".webarchive",
            16,
            DateTimeOffset.UtcNow);

        var preview = await new WebArchivePreviewProvider().LoadAsync(
            context,
            CancellationToken.None);

        Assert.Equal("WebArchive metadata", preview?.Source);
        Assert.Contains("limited to 4.0 MB", preview?.Text);
    }

    [Fact]
    public async Task WebArchiveProviderAcceptsParameterizedXhtmlMimeType()
    {
        using var temporary = new TemporaryFile("parameterized.webarchive");
        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("<p>Parameterized XHTML</p>"));
        await File.WriteAllTextAsync(
            temporary.Path,
            $"""
            <?xml version="1.0"?>
            <plist><dict><key>WebMainResource</key><dict>
              <key>WebResourceMIMEType</key><string>application/xhtml+xml; charset=utf-8</string>
              <key>WebResourceTextEncodingName</key><string>UTF-8</string>
              <key>WebResourceData</key><data>{encoded}</data>
            </dict></dict></plist>
            """);

        var preview = await new WebArchivePreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Contains("Parameterized XHTML", preview?.Text);
        Assert.DoesNotContain("<p>", preview?.Text);
    }

    [Fact]
    public async Task ArchiveProviderReadsLz4FrameTextAndTarListing()
    {
        using var textFile = new TemporaryFile("sample.lz4");
        await WriteLz4Async(textFile.Path, Encoding.UTF8.GetBytes("LZ4 preview text"));
        using var tarFile = new TemporaryFile("sample.tar.lz4");
        using var tarBytes = new MemoryStream();
        using (var writer = new TarWriter(tarBytes, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "folder/readme.txt")
            {
                DataStream = new MemoryStream("hello"u8.ToArray())
            });
        }
        await WriteLz4Async(tarFile.Path, tarBytes.ToArray());

        var textPreview = await new ArchivePreviewProvider().LoadAsync(
            Context(textFile.Path), CancellationToken.None);
        var tarPreview = await new ArchivePreviewProvider().LoadAsync(
            Context(tarFile.Path), CancellationToken.None);

        Assert.Contains("LZ4 preview text", textPreview?.Text);
        Assert.Contains("folder/readme.txt", tarPreview?.Text);
        Assert.Contains("Decompressed scan budget: 64 MiB", tarPreview?.Text);
    }

    [Fact]
    public async Task VirtualDiskProviderValidatesVhdFooterAndReportsSizes()
    {
        using var temporary = new TemporaryFile("disk.vhd");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalVhd());

        var preview = await new VirtualDiskPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("VHD metadata", preview?.Source);
        Assert.Contains("Disk type: Fixed", preview?.Text);
        Assert.Contains("Current virtual size: 8.0 MB", preview?.Text);
        Assert.Contains("Footer checksum: valid", preview?.Text);
        Assert.Contains("was not mounted or attached", preview?.Text);
    }

    [Fact]
    public async Task VirtualDiskProviderChoosesNewestValidVhdxHeader()
    {
        using var temporary = new TemporaryFile("disk.vhdx");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalVhdx());

        var preview = await new VirtualDiskPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("VHDX metadata", preview?.Source);
        Assert.Contains("Active header sequence: 2", preview?.Text);
        Assert.Contains("Log length: 1.0 MB", preview?.Text);
        Assert.Contains("was not mounted or attached", preview?.Text);
    }

    [Fact]
    public async Task VirtualDiskProviderDoesNotTrustInvalidVhdFooterFields()
    {
        using var temporary = new TemporaryFile("corrupt.vhd");
        var bytes = CreateMinimalVhd();
        bytes[64] ^= 0x01;
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new VirtualDiskPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Contains("No VHD footer passed", preview?.Text);
        Assert.DoesNotContain("Current virtual size", preview?.Text);
        Assert.DoesNotContain("Identifier:", preview?.Text);
    }

    [Fact]
    public async Task BinaryStructureProviderReadsJavaClassPoolWithoutExecutingBytecode()
    {
        using var temporary = new TemporaryFile("Example.class");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalClassFile());

        var preview = await new BinaryStructurePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Java class structure", preview?.Source);
        Assert.Contains("Class version: 61.0", preview?.Text);
        Assert.Contains("This class: com.example.Test", preview?.Text);
        Assert.Contains("Super class: java.lang.Object", preview?.Text);
        Assert.Contains("Methods: 0", preview?.Text);
        Assert.Contains("was not loaded, linked, executed", preview?.Text);
    }

    [Fact]
    public async Task BinaryStructureProviderListsWasmSectionsWithoutInstantiatingCode()
    {
        using var temporary = new TemporaryFile("module.wasm");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalWasmFile());

        var preview = await new BinaryStructurePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("WebAssembly module structure", preview?.Source);
        Assert.Contains("Binary version: 1", preview?.Text);
        Assert.Contains("Custom section names: debug", preview?.Text);
        Assert.Contains("was not instantiated, executed", preview?.Text);
    }

    [Fact]
    public async Task BinaryStructureProviderReadsMinidumpDirectoryWithoutOpeningMemory()
    {
        using var temporary = new TemporaryFile("sample.dmp");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalMiniDump());

        var preview = await new BinaryStructurePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Windows minidump structure", preview?.Source);
        Assert.Contains("Streams declared: 2", preview?.Text);
        Assert.Contains("Modules: 1", preview?.Text);
        Assert.Contains("Exception stream: present", preview?.Text);
        Assert.Contains("memory, module paths, symbols", preview?.Text);
    }

    [Theory]
    [InlineData("disk.vdi", "VirtualBox VDI structure", "Virtual disk size: 64.0 MB")]
    [InlineData("disk.qcow2", "QEMU QCOW structure", "Format version: 3")]
    [InlineData("image.dmg", "Apple UDIF disk image structure", "Segments: 1")]
    public async Task BinaryStructureProviderReadsVirtualDiskHeadersWithoutMounting(
        string fileName,
        string source,
        string expectedText)
    {
        using var temporary = new TemporaryFile(fileName);
        var bytes = fileName.EndsWith(".vdi", StringComparison.OrdinalIgnoreCase)
            ? CreateMinimalVdi()
            : fileName.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase)
                ? CreateMinimalQcow()
                : CreateMinimalDmg();
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new BinaryStructurePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal(source, preview?.Source);
        Assert.Contains(expectedText, preview?.Text);
        Assert.Contains("not mounted", preview?.Text);
    }

    [Fact]
    public async Task BinaryStructureProviderReadsVmdkDescriptorTextWithoutResolvingBackingFiles()
    {
        using var temporary = new TemporaryFile("disk.vmdk");
        await File.WriteAllTextAsync(temporary.Path,
            "# Disk DescriptorFile\nversion=1\nCID=fffffffe\ncreateType=\"monolithicSparse\"\nRW 131072 SPARSE \"disk.vmdk\"\n");

        var preview = await new BinaryStructurePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("VMware VMDK structure", preview?.Source);
        Assert.Contains("createType=\"monolithicSparse\"", preview?.Text);
        Assert.Contains("no disk was mounted", preview?.Text);
    }

    [Fact]
    public void VirtualDiskCrc32CMatchesTheStandardCheckVector()
    {
        Assert.Equal(0xE3069283u, VirtualDiskPreviewProvider.Crc32C("123456789"u8));
    }

    [Theory]
    [InlineData("sample.docm", "word/document.xml")]
    [InlineData("sample.xlsm", "xl/sharedStrings.xml")]
    [InlineData("sample.pptm", "ppt/slides/slide1.xml")]
    public async Task DocumentProviderExtractsTextFromCommonOpenXmlAliases(
        string fileName,
        string contentPath)
    {
        using var temporary = new TemporaryFile(fileName);
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(contentPath);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("<root><p>Alias document preview</p></root>");
        }

        var preview = await new DocumentPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Document text", preview?.Source);
        Assert.Contains("Alias document preview", preview?.Text);
    }

    [Theory]
    [InlineData("sample.jar")]
    [InlineData("sample.apk")]
    [InlineData("sample.appx")]
    [InlineData("sample.msix")]
    [InlineData("sample.nupkg")]
    [InlineData("sample.vsix")]
    public async Task ArchiveProviderListsCommonZipContainerFormats(string fileName)
    {
        using var temporary = new TemporaryFile(fileName);
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("folder/readme.txt");
        }

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("ZIP archive", preview?.Text);
        Assert.Contains("folder/readme.txt", preview?.Text);
    }

    [Fact]
    public async Task ArchiveProviderListsZipxCentralDirectoryWithoutOpeningPayloads()
    {
        using var temporary = new TemporaryFile("sample.zipx");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("folder/readme.txt");
        }

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("ZIPX archive", preview?.Text);
        Assert.Contains("folder/readme.txt", preview?.Text);
    }

    [Fact]
    public async Task MaffProviderExtractsOnlyBoundedMainPageText()
    {
        using var temporary = new TemporaryFile("saved.maff");
        using (var archive = ZipFile.Open(temporary.Path, ZipArchiveMode.Create))
        {
            var page = archive.CreateEntry("tab/index.html");
            await using var writer = new StreamWriter(page.Open());
            await writer.WriteAsync(
                "<html><script>bad()</script><h1>Saved page</h1><img src=\"https://example.test/a.png\"></html>");
        }

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("MAFF web archive", preview?.Text);
        Assert.Contains("Saved page", preview?.Text);
        Assert.DoesNotContain("bad()", preview?.Text);
        Assert.DoesNotContain("https://", preview?.Text);
    }

    [Fact]
    public async Task ArchiveProviderListsSevenZipEntriesWithoutExtracting()
    {
        using var temporary = new TemporaryFile("sample.7z");
        await File.WriteAllBytesAsync(
            temporary.Path,
            Convert.FromBase64String(
                "N3q8ryccAARFYv/9EwAAAAAAAABiAAAAAAAAAFY/f5oBAA5hcmNoaXZlIHByZXZpZXcAAQQGAAEJEwAHCwEAASEhAQAMDwAICgHzTdWhAAAFARkMAAAAAAAAAAAAAAAAERcAcgBlAGEAZABtAGUALgB0AHgAdAAAABkEAAAAABQKAQBJnDiYnRzdARUGAQAgAAAAAAA="));

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("7Z archive", preview?.Text);
        Assert.Contains("readme.txt", preview?.Text);
        Assert.Contains("Entries shown: 1", preview?.Text);
    }

    [Fact]
    public async Task ArchiveProviderSamplesBzip2ContentWithABoundedReader()
    {
        using var temporary = new TemporaryFile("sample.bz2");
        await File.WriteAllBytesAsync(
            temporary.Path,
            Convert.FromBase64String(
                "QlpoOTFBWSZTWbpbh3UAAAGfgEAAEAAQAAAQIiZZgCAAIpo0YYhAAAZXHjZCEl0nYawXckU4UJC6W4d1"));

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("BZ2 compressed stream", preview?.Text);
        Assert.Contains("BZip2 preview sample", preview?.Text);
        Assert.True(preview?.Text?.Length < 70_000);
    }

    [Fact]
    public async Task ArchiveProviderSamplesBrotliContentWithABoundedReader()
    {
        using var temporary = new TemporaryFile("sample.br");
        await using (var output = File.Create(temporary.Path))
        await using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize))
        {
            await brotli.WriteAsync(Encoding.UTF8.GetBytes("Brotli preview sample"));
        }

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("BR compressed stream", preview?.Text);
        Assert.Contains("Brotli preview sample", preview?.Text);
        Assert.True(preview?.Text?.Length < 70_000);
    }

    [Fact]
    public async Task SvgProviderReadsGzipCompressedSvg()
    {
        using var temporary = new TemporaryFile("sample.svgz");
        await using (var output = File.Create(temporary.Path))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><text>Compressed SVG</text></svg>"));
        }

        var preview = await new SvgPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Safe SVG", preview?.Source);
        Assert.Contains("20 × 10", preview?.Text);
        Assert.Contains("Compressed SVG", preview?.Text);
    }

    [Theory]
    [InlineData("sample.pst", "!BDN\0\0\0\0\0\0\u0017\0", "Outlook Personal Folders (Unicode)")]
    [InlineData("sample.pcap", "\u00D4\u00C3\u00B2\u00A1", "Packet capture")]
    [InlineData("Example.class", "\u00CA\u00FE\u00BA\u00BE\0\0\0=", "Java class file (major version 61)")]
    [InlineData("module.wasm", "\0asm\u0001\0\0\0", "WebAssembly module (version 1)")]
    [InlineData("drawing.dwg", "AC1027", "AutoCAD drawing (AC1027)")]
    [InlineData("disk.qcow2", "QFI\u00FB\0\0\0\u0003", "QCOW virtual disk (version 3)")]
    [InlineData("data.parquet", "PAR1", "Apache Parquet data file")]
    public async Task OpaqueProviderRecognizesCommonBinaryHeaders(
        string fileName,
        string latin1Header,
        string expectedDescription)
    {
        using var temporary = new TemporaryFile(fileName);
        await File.WriteAllBytesAsync(temporary.Path, Encoding.Latin1.GetBytes(latin1Header));

        var preview = await new OpaqueFilePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("File metadata", preview?.Source);
        Assert.Contains(expectedDescription, preview?.Text);
        Assert.Contains("not executed", preview?.Text);
    }

    [Theory]
    [InlineData("disk.vhd", "conectix", "VHD virtual disk")]
    [InlineData("image.dmg", "koly", "Apple UDIF disk image")]
    public async Task OpaqueProviderRecognizesVirtualDiskFooters(
        string fileName,
        string footer,
        string expectedDescription)
    {
        using var temporary = new TemporaryFile(fileName);
        var bytes = new byte[1024];
        Encoding.ASCII.GetBytes(footer).CopyTo(bytes, bytes.Length - 512);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new OpaqueFilePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains(expectedDescription, preview?.Text);
    }

    [Fact]
    public async Task GenericDbFallsBackToOpaqueMetadataWhenItIsNotSqlite()
    {
        using var temporary = new TemporaryFile("unknown.db");
        await File.WriteAllBytesAsync(temporary.Path, [0x00, 0x01, 0x02, 0x03]);
        var context = Context(temporary.Path);

        var database = await new DatabasePreviewProvider().LoadAsync(context, CancellationToken.None);
        var opaque = await new OpaqueFilePreviewProvider().LoadAsync(context, CancellationToken.None);

        Assert.Null(database);
        Assert.True(PreviewFormatRegistry.Supports(".db", PreviewFallback.Opaque));
        Assert.Equal("File metadata", opaque?.Source);
    }

    [Fact]
    public async Task DbfProviderReadsSchemaWithoutOpeningMemoFilesOrRows()
    {
        using var temporary = new TemporaryFile("table.dbf");
        var header = new byte[65];
        header[0] = 0x03;
        header[1] = 126;
        header[2] = 7;
        header[3] = 26;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 65);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 11);
        Encoding.ASCII.GetBytes("NAME").CopyTo(header, 32);
        header[43] = (byte)'C';
        header[48] = 10;
        header[64] = 0x0D;
        await File.WriteAllBytesAsync(temporary.Path, header);

        var preview = await new DatabasePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("DBF schema", preview?.Source);
        Assert.Contains("dBASE III", preview?.Text);
        Assert.Contains("NAME — C, width 10", preview?.Text);
        Assert.Contains("row values are never opened", preview?.Text);
    }

    [Fact]
    public async Task MultipartArchiveProviderFindsVolumesWithoutCombiningThem()
    {
        using var temporary = new TemporaryFile("backup.7z.002");
        await File.WriteAllBytesAsync(temporary.Path, new byte[20]);
        await File.WriteAllBytesAsync(Path.Combine(temporary.DirectoryPath, "backup.7z.001"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(temporary.DirectoryPath, "backup.7z.003"), new byte[30]);

        var preview = await new MultipartArchivePreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Multipart archive metadata", preview?.Source);
        Assert.Contains("Volumes found: 3", preview?.Text);
        Assert.Contains("backup.7z.001", preview?.Text);
        Assert.Contains("Combined size shown: 60 B", preview?.Text);
        Assert.True(PreviewCoordinator.ShouldPreferSystemPreview(Context(temporary.Path)));
    }

    [Fact]
    public async Task CalendarProviderUnfoldsEventsAndIgnoresAttachmentUris()
    {
        using var temporary = new TemporaryFile("meeting.ics");
        await File.WriteAllTextAsync(
            temporary.Path,
            """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            SUMMARY:Project
             review
            DTSTART:20260726T100000Z
            LOCATION:Room 1
            ATTACH:https://example.test/secret
            DESCRIPTION:Line one\nLine two
            END:VEVENT
            END:VCALENDAR
            """.ReplaceLineEndings("\r\n"));

        var preview = await new PimPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Calendar events", preview?.Source);
        Assert.Contains("SUMMARY: Projectreview", preview?.Text);
        Assert.Contains("Line one", preview?.Text);
        Assert.Contains("Line two", preview?.Text);
        Assert.DoesNotContain("https://", preview?.Text);
    }

    [Fact]
    public async Task ContactProviderShowsCommonFieldsAndDecodesEscapes()
    {
        using var temporary = new TemporaryFile("person.vcf");
        await File.WriteAllTextAsync(
            temporary.Path,
            "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Example Person\r\nORG:Example\\, Inc.\r\nEMAIL:test@example.test\r\nEND:VCARD\r\n");

        var preview = await new PimPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Contact cards", preview?.Source);
        Assert.Contains("FN: Example Person", preview?.Text);
        Assert.Contains("ORG: Example, Inc.", preview?.Text);
        Assert.Contains("EMAIL: test@example.test", preview?.Text);
    }

    [Fact]
    public async Task WindowsContactProviderShowsWhitelistedFieldsWithoutPhotoPayloads()
    {
        using var temporary = new TemporaryFile("person.contact");
        await File.WriteAllTextAsync(
            temporary.Path,
            """
            <Contact xmlns="http://schemas.microsoft.com/Contact">
              <FormattedName>Example Person</FormattedName>
              <Company>Example Company</Company>
              <EmailAddress>person@example.test</EmailAddress>
              <Photo>SECRET_PHOTO_PAYLOAD</Photo>
            </Contact>
            """);

        var preview = await new PimPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Windows Contact", preview?.Source);
        Assert.Contains("Example Person", preview?.Text);
        Assert.Contains("person@example.test", preview?.Text);
        Assert.DoesNotContain("SECRET_PHOTO_PAYLOAD", preview?.Text);
    }

    [Fact]
    public async Task LdifProviderDecodesTextButIgnoresExternalValueUris()
    {
        using var temporary = new TemporaryFile("directory.ldif");
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes("测试用户"));
        await File.WriteAllTextAsync(
            temporary.Path,
            $"dn: cn=test,dc=example,dc=test\r\ncn:: {encodedName}\r\nmail: test@example.test\r\nphoto:< file:///secret.bin\r\n\r\n");

        var preview = await new PimPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("LDAP contacts", preview?.Source);
        Assert.Contains("测试用户", preview?.Text);
        Assert.Contains("test@example.test", preview?.Text);
        Assert.DoesNotContain("file://", preview?.Text);
    }

    [Fact]
    public async Task PreviewWorkerProcessReturnsTextBeforeNormalApplicationStartup()
    {
        using var temporary = new TemporaryFile("isolated.ics");
        await File.WriteAllTextAsync(
            temporary.Path,
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Isolated preview\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var executable = GetRepositoryPath(
            "src",
            "ListaryOpen.PreviewHost",
            "bin",
            configuration,
            "net8.0-windows",
            "ListaryOpen.PreviewHost.exe");
        Assert.True(File.Exists(executable), executable);

        var preview = await PreviewWorkerClient.LoadAsync(
            executable,
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal("Calendar events", preview?.Source);
        Assert.Contains("Isolated preview", preview?.Text);
    }

    [Fact]
    public async Task ArchiveProviderListsTarBzip2EntriesThroughADecompressedBudget()
    {
        using var temporary = new TemporaryFile("sample.tar.bz2");
        await File.WriteAllBytesAsync(
            temporary.Path,
            Convert.FromBase64String(
                "QlpoOTFBWSZTWchu1q8AADNbgckAQAF7gAEAZkaeQABABACgAFRnpGTRgAAeoJSCaekHqZA0BaX0nBzcIGLKM05IDknaG8IglYg/B6rR1aQzMdCDl8klwnd9h7jBkjYGw55FwXckU4UJDIbtavA="));

        var preview = await new ArchivePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("BZ2 TAR archive", preview?.Text);
        Assert.Contains("readme.txt", preview?.Text);
        Assert.Contains("Decompressed scan budget: 64 MiB", preview?.Text);
    }

    [Fact]
    public async Task CabinetReaderListsEntriesWithoutExtracting()
    {
        using var temporary = new TemporaryFile("sample.cab");
        await File.WriteAllBytesAsync(
            temporary.Path,
            Convert.FromBase64String(
                "TVNDRgAAAABeAAAAAAAAACwAAAAAAAAAAwEBAAEAAAD8AwAARwAAAAEAAQALAAAAAAAAAAAA+lwuTSAAcmVhZG1lLnR4dABKM44qDwALAENLS05MUigoSi3LTC0HAA=="));

        var preview = CabinetPreviewReader.Read(temporary.Path, CancellationToken.None);

        Assert.Contains("CAB archive", preview);
        Assert.Contains("Entries shown: 1", preview);
        Assert.Contains("readme.txt", preview);
        Assert.False(File.Exists(Path.Combine(temporary.DirectoryPath, "readme.txt")));
    }

    [Fact]
    public async Task IsoReaderPrefersJolietAndListsUnicodeNames()
    {
        using var temporary = new TemporaryFile("sample.iso");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalJolietIso());

        var preview = IsoPreviewReader.Read(temporary.Path, CancellationToken.None);

        Assert.Contains("ISO 9660 / Joliet image", preview);
        Assert.Contains("Entries shown: 1", preview);
        Assert.Contains("预览.TXT", preview);
    }

    [Fact]
    public async Task DatabaseProviderRetainsHeaderMetadataForMalformedSqlite()
    {
        using var temporary = new TemporaryFile("sample.sqlite3");
        var header = new byte[100];
        "SQLite format 3\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16, 2), 4096);
        header[18] = 1;
        header[19] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), 42);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60, 4), 7);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(68, 4), 0x12345678);
        await File.WriteAllBytesAsync(temporary.Path, header);

        var preview = await new DatabasePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("SQLite metadata", preview?.Source);
        Assert.Contains("Page size: 4096 bytes", preview?.Text);
        Assert.Contains("Database pages: 42", preview?.Text);
        Assert.Contains("Text encoding: UTF-8", preview?.Text);
        Assert.Contains("User version: 7", preview?.Text);
        Assert.Contains("Application ID: 0x12345678", preview?.Text);
        Assert.Contains("Header metadata remains available", preview?.Text);
    }

    [Fact]
    public async Task DatabaseProviderReadsOnlySqliteSchemaAndNeverUserRows()
    {
        using var temporary = new TemporaryFile("schema.sqlite");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={temporary.Path};Mode=ReadWriteCreate;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT NOT NULL);
                CREATE INDEX ix_notes_body ON notes(body);
                CREATE VIEW note_ids AS SELECT id FROM notes;
                INSERT INTO notes(body) VALUES ('SECRET_USER_ROW');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var before = new FileInfo(temporary.Path);
        var beforeLength = before.Length;
        var beforeWrite = before.LastWriteTimeUtc;

        var preview = await new DatabasePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        var after = new FileInfo(temporary.Path);
        Assert.Contains("table notes", preview?.Text);
        Assert.Contains("index ix_notes_body", preview?.Text);
        Assert.Contains("view note_ids", preview?.Text);
        Assert.DoesNotContain("SECRET_USER_ROW", preview?.Text);
        Assert.Contains("User rows are never read", preview?.Text);
        Assert.Equal(beforeLength, after.Length);
        Assert.Equal(beforeWrite, after.LastWriteTimeUtc);
    }

    [Fact]
    public async Task DatabaseProviderRejectsNonSqliteDbWithoutTreatingItAsText()
    {
        using var temporary = new TemporaryFile("sample.db");
        await File.WriteAllTextAsync(temporary.Path, "not a SQLite database");
        var context = Context(temporary.Path);

        Assert.Null(await new DatabasePreviewProvider().LoadAsync(context, CancellationToken.None));
        Assert.False(new TextPreviewProvider().CanPreview(context));
    }

    [Fact]
    public async Task InternetShortcutDisplaysButNeverOpensARedactedTarget()
    {
        using var temporary = new TemporaryFile("sample.url");
        await File.WriteAllTextAsync(
            temporary.Path,
            "[InternetShortcut]\nURL=https://user:secret@example.test/path\nIconFile=C:\\icon.ico");

        var preview = await new ShortcutPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Internet shortcut", preview?.Source);
        Assert.Contains("https://***@example.test/path", preview?.Text);
        Assert.DoesNotContain("user:secret", preview?.Text);
        Assert.Contains("never opened", preview?.Text);
    }

    [Fact]
    public async Task WindowsShortcutParsesBoundedStringDataWithoutResolvingTarget()
    {
        using var temporary = new TemporaryFile("sample.lnk");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalShellLink());

        var preview = await new ShortcutPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Windows shortcut", preview?.Source);
        Assert.Contains("Target: ..\\target.exe", preview?.Text);
        Assert.Contains("Arguments: --safe-preview", preview?.Text);
        Assert.Contains("never resolved or opened", preview?.Text);
    }

    [Fact]
    public async Task ModelProviderSummarizesObjGeometryWithoutLoadingMaterials()
    {
        using var temporary = new TemporaryFile("sample.obj");
        await File.WriteAllTextAsync(
            temporary.Path,
            "mtllib remote.mtl\nv 0 0 0\nv 2 0 0\nv 0 3 0\nf 1 2 3\n");

        var preview = await new ModelPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Wavefront OBJ", preview?.Text);
        Assert.Contains("Vertices: 3", preview?.Text);
        Assert.Contains("Faces/triangles: 1", preview?.Text);
        Assert.Contains("Dimensions: 2 × 3 × 0", preview?.Text);
        Assert.Contains("External material libraries ignored: 1", preview?.Text);
    }

    [Fact]
    public async Task ModelProviderSummarizesBinaryStlWithinTriangleBudget()
    {
        using var temporary = new TemporaryFile("sample.stl");
        var stl = new byte[134];
        BinaryPrimitives.WriteUInt32LittleEndian(stl.AsSpan(80), 1);
        WriteSingle(stl, 96, 0); WriteSingle(stl, 100, 0); WriteSingle(stl, 104, 0);
        WriteSingle(stl, 108, 4); WriteSingle(stl, 112, 0); WriteSingle(stl, 116, 0);
        WriteSingle(stl, 120, 0); WriteSingle(stl, 124, 5); WriteSingle(stl, 128, 0);
        await File.WriteAllBytesAsync(temporary.Path, stl);

        var preview = await new ModelPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Binary STL", preview?.Text);
        Assert.Contains("Faces/triangles: 1", preview?.Text);
        Assert.Contains("Dimensions: 4 × 5 × 0", preview?.Text);
    }

    [Fact]
    public async Task ModelProviderSummarizesGltfWithoutFollowingExternalUris()
    {
        using var temporary = new TemporaryFile("sample.gltf");
        await File.WriteAllTextAsync(
            temporary.Path,
            """{"asset":{"version":"2.0"},"scenes":[{}],"nodes":[{}],"meshes":[{}],"buffers":[{"uri":"https://example.test/model.bin"}]}""");

        var preview = await new ModelPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("glTF JSON", preview?.Text);
        Assert.Contains("Meshes: 1", preview?.Text);
        Assert.Contains("External resources ignored: 1", preview?.Text);
        Assert.DoesNotContain("https://", preview?.Text);
    }

    [Fact]
    public async Task ModelProviderSummarizesDxfEntitiesAndLayers()
    {
        using var temporary = new TemporaryFile("sample.dxf");
        await File.WriteAllTextAsync(
            temporary.Path,
            "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\nLayer A\n0\nCIRCLE\n8\nLayer B\n0\nENDSEC\n0\nEOF\n");

        var preview = await new ModelPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Drawing Exchange Format", preview?.Text);
        Assert.Contains("Layers shown: 2", preview?.Text);
        Assert.Contains("LINE: 1", preview?.Text);
        Assert.Contains("CIRCLE: 1", preview?.Text);
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
    public async Task PdfPageProviderRendersARealFirstPageWithinPixelBounds()
    {
        using var temporary = new TemporaryFile("render.pdf");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalPdf());

        var preview = await new PdfPagePreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal(PreviewContentKind.Image, preview!.Kind);
        Assert.Equal("Built-in PDF page 1/1", preview.Source);
        Assert.NotNull(preview.Image);
        Assert.InRange(preview.Image!.PixelWidth, 1, 1600);
        Assert.InRange(preview.Image.PixelHeight, 1, 1600);
        Assert.True((long)preview.Image.PixelWidth * preview.Image.PixelHeight <= 8_000_000);
    }

    [Fact]
    public void PdfRenderSizePreservesAspectRatioAndRejectsInvalidPages()
    {
        Assert.Equal((1600, 800), PdfPagePreviewProvider.CalculateRenderSize(3200, 1600));
        Assert.Throws<InvalidDataException>(() => PdfPagePreviewProvider.CalculateRenderSize(0, 100));
        Assert.Throws<InvalidDataException>(() => PdfPagePreviewProvider.CalculateRenderSize(float.NaN, 100));
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

        Assert.Equal(PreviewContentKind.Media, preview?.Kind);
        Assert.Equal(temporary.Path, preview?.MediaPath);
        Assert.Contains("2 channel(s), 44100 Hz, 16 bit", preview?.Text);
        Assert.Contains("00:00:01", preview?.Text);
    }

    [Fact]
    public async Task MediaProviderReadsMp4MovieAndTrackMetadataWithoutReadingMediaPayload()
    {
        using var temporary = new TemporaryFile("sample.mp4");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalMp4());

        var preview = await new MediaPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal(PreviewContentKind.Media, preview?.Kind);
        Assert.Contains("Major brand: isom", preview?.Text);
        Assert.Contains("Duration: 00:00:12.500", preview?.Text);
        Assert.Contains("Track 1: video, codec avc1, 1920×1080", preview?.Text);
    }

    [Theory]
    [InlineData("sample.webm", "webm")]
    [InlineData("sample.mkv", "matroska")]
    [InlineData("sample.mka", "matroska")]
    public async Task MediaProviderReadsEbmlInfoAndTracksWithoutReadingClusters(
        string fileName,
        string docType)
    {
        using var temporary = new TemporaryFile(fileName);
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalEbml(docType));

        var preview = await new MediaPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Equal(PreviewContentKind.Media, preview?.Kind);
        Assert.Contains($"Container: EBML / {docType}", preview?.Text);
        Assert.Contains("Title: Bounded preview", preview?.Text);
        Assert.Contains("Duration: 00:00:12.500", preview?.Text);
        Assert.Contains("Video — V_VP9 — Main [eng] — 1920×1080", preview?.Text);
    }

    [Fact]
    public async Task MediaProviderSeeksPastKnownClusterToLaterEbmlMetadata()
    {
        using var temporary = new TemporaryFile("late-metadata.mkv");
        await File.WriteAllBytesAsync(
            temporary.Path,
            CreateMinimalEbml("matroska", clusterFirst: true));

        var preview = await new MediaPreviewProvider().LoadAsync(
            Context(temporary.Path),
            CancellationToken.None);

        Assert.Contains("Title: Bounded preview", preview?.Text);
        Assert.Contains("Video — V_VP9", preview?.Text);
    }

    [Fact]
    public async Task MediaProviderReadsOpusHeadersCommentsAndGranuleDuration()
    {
        using var temporary = new TemporaryFile("sample.opus");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalOpus());

        var preview = await new MediaPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("Codec: Opus", preview?.Text);
        Assert.Contains("2 channel(s), 48000 Hz", preview?.Text);
        Assert.Contains("Title: Preview song", preview?.Text);
        Assert.Contains("Duration: 00:00:02.000", preview?.Text);
    }

    [Fact]
    public async Task MediaProviderReadsAiffCommonChunk()
    {
        using var temporary = new TemporaryFile("sample.aiff");
        await File.WriteAllBytesAsync(temporary.Path, CreateMinimalAiff());

        var preview = await new MediaPreviewProvider().LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Contains("2 channel(s), 44100 Hz, 16 bit", preview?.Text);
        Assert.Contains("Duration: 00:00:01.000", preview?.Text);
    }

    [Fact]
    public async Task MediaProviderReadsMidiAndAacHeaders()
    {
        using var midi = new TemporaryFile("sample.mid");
        var midiBytes = new byte[14];
        "MThd"u8.CopyTo(midiBytes);
        BinaryPrimitives.WriteUInt32BigEndian(midiBytes.AsSpan(4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(midiBytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(midiBytes.AsSpan(10), 3);
        BinaryPrimitives.WriteUInt16BigEndian(midiBytes.AsSpan(12), 480);
        await File.WriteAllBytesAsync(midi.Path, midiBytes);
        using var aac = new TemporaryFile("sample.aac");
        await File.WriteAllBytesAsync(aac.Path, [0xFF, 0xF1, 0x50, 0x80, 0x0C, 0x80, 0x00]);

        var midiPreview = await new MediaPreviewProvider().LoadAsync(Context(midi.Path), CancellationToken.None);
        var aacPreview = await new MediaPreviewProvider().LoadAsync(Context(aac.Path), CancellationToken.None);

        Assert.Contains("MIDI format: 1", midiPreview?.Text);
        Assert.Contains("Tracks: 3", midiPreview?.Text);
        Assert.Contains("480 ticks per quarter note", midiPreview?.Text);
        Assert.Contains("AAC profile 2", aacPreview?.Text);
        Assert.Contains("2 channel(s), 44100 Hz", aacPreview?.Text);
        Assert.Contains("First ADTS frame: 100 bytes", aacPreview?.Text);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(65, "01:05")]
    [InlineData(3661, "1:01:01")]
    public void MediaTimeFormattingIsStable(int seconds, string expected)
    {
        Assert.Equal(expected, FilePreviewPane.FormatMediaTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void PreviewPaneDefinesManualAccessibleMediaControls()
    {
        var xaml = File.ReadAllText(GetRepositoryPath(
            "src", "ListaryOpen.App", "FilePreviewPane.xaml"));

        Assert.Contains("x:Name=\"MediaPreviewSurface\"", xaml);
        Assert.Contains("x:Name=\"PreviewMedia\"", xaml);
        Assert.Contains("LoadedBehavior=\"Manual\"", xaml);
        Assert.Contains("UnloadedBehavior=\"Manual\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PreviewMediaPlayPause\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PreviewMediaPosition\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"PreviewMediaMute\"", xaml);
    }

    [Fact]
    public async Task RasterImageProviderLoadsPngAsImageContent()
    {
        var directory = Directory.CreateTempSubdirectory("ListaryOpenImagePreview");
        try
        {
            var path = Path.Combine(directory.FullName, "sample.png");
            // 1x1 PNG
            await File.WriteAllBytesAsync(
                path,
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

            var preview = await new RasterImagePreviewProvider()
                .LoadAsync(Context(path), CancellationToken.None);

            Assert.NotNull(preview);
            Assert.Equal(PreviewContentKind.Image, preview!.Kind);
            Assert.NotNull(preview.Image);
            Assert.True(preview.Image!.PixelWidth >= 1);
            Assert.True(preview.Image.PixelHeight >= 1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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

    [Fact]
    public async Task WebFontProviderReadsEotNamesAndEmbeddingFlagsWithoutUnpackingFontData()
    {
        using var temporary = new TemporaryFile("sample.eot");
        var style = Encoding.Unicode.GetBytes("Bold");
        var versionName = Encoding.Unicode.GetBytes("Version 1.0");
        var fullName = Encoding.Unicode.GetBytes("Preview EOT");
        var fontData = new byte[] { 0x00, 0x01, 0x00, 0x00, 0xDE, 0xAD };
        var eotSize = 34 + (style.Length + 2) + (2 + versionName.Length + 2) +
            (2 + fullName.Length + 2) + fontData.Length;
        var bytes = new byte[eotSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)eotSize));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)fontData.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x00000001);
        bytes[26] = 1;
        bytes[27] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 700);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), checked((ushort)style.Length));
        var position = 34;
        style.CopyTo(bytes, position);
        position += style.Length + 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(position), checked((ushort)versionName.Length));
        position += 2;
        versionName.CopyTo(bytes, position);
        position += versionName.Length + 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(position), checked((ushort)fullName.Length));
        position += 2;
        fullName.CopyTo(bytes, position);
        position += fullName.Length + 2;
        fontData.CopyTo(bytes, position);
        await File.WriteAllBytesAsync(temporary.Path, bytes);

        var preview = await new WebFontPreviewProvider()
            .LoadAsync(Context(temporary.Path), CancellationToken.None);

        Assert.Equal("Embedded OpenType metadata", preview?.Source);
        Assert.Contains("Full name: Preview EOT", preview?.Text);
        Assert.Contains("Style: Bold", preview?.Text);
        Assert.Contains("Weight: 700", preview?.Text);
        Assert.Contains("Italic: yes", preview?.Text);
        Assert.Contains("not decompressed, decrypted, installed, or rendered", preview?.Text);
    }

    private static PreviewContext Context(string path)
    {
        var info = new FileInfo(path);
        return new PreviewContext(path, info.Name, info.Extension, info.Length, info.LastWriteTimeUtc);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ListaryOpen.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
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

    private static byte[] CreateMinimalMp4()
    {
        static byte[] Atom(string type, params byte[][] payloads)
        {
            var payloadLength = payloads.Sum(payload => payload.Length);
            var bytes = new byte[8 + payloadLength];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)bytes.Length));
            Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
            var offset = 8;
            foreach (var payload in payloads)
            {
                payload.CopyTo(bytes, offset);
                offset += payload.Length;
            }
            return bytes;
        }

        var fileType = new byte[12];
        Encoding.ASCII.GetBytes("isom").CopyTo(fileType, 0);
        BinaryPrimitives.WriteUInt32BigEndian(fileType.AsSpan(4), 512);
        Encoding.ASCII.GetBytes("isom").CopyTo(fileType, 8);

        var movieHeader = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(movieHeader.AsSpan(12), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(movieHeader.AsSpan(16), 12_500);

        var trackHeader = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(76), 1920u << 16);
        BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(80), 1080u << 16);

        var handler = new byte[12];
        Encoding.ASCII.GetBytes("vide").CopyTo(handler, 8);

        var sampleDescription = new byte[44];
        BinaryPrimitives.WriteUInt32BigEndian(sampleDescription.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(sampleDescription.AsSpan(8), 36);
        Encoding.ASCII.GetBytes("avc1").CopyTo(sampleDescription, 12);
        BinaryPrimitives.WriteUInt16BigEndian(sampleDescription.AsSpan(40), 1920);
        BinaryPrimitives.WriteUInt16BigEndian(sampleDescription.AsSpan(42), 1080);

        var sampleTable = Atom("stbl", Atom("stsd", sampleDescription));
        var media = Atom("mdia", Atom("hdlr", handler), Atom("minf", sampleTable));
        var track = Atom("trak", Atom("tkhd", trackHeader), media);
        var movie = Atom("moov", Atom("mvhd", movieHeader), track);
        return [.. Atom("ftyp", fileType), .. Atom("mdat", new byte[1024]), .. movie];
    }

    private static byte[] CreateMinimalOpus()
    {
        static byte[] Page(byte[] packet, ulong granule, uint sequence)
        {
            var bytes = new byte[28 + packet.Length];
            "OggS"u8.CopyTo(bytes);
            bytes[4] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(6), granule);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), sequence);
            bytes[26] = 1;
            bytes[27] = checked((byte)packet.Length);
            packet.CopyTo(bytes, 28);
            return bytes;
        }

        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48_000);

        var vendor = Encoding.UTF8.GetBytes("ListaryOpen");
        var comment = Encoding.UTF8.GetBytes("TITLE=Preview song");
        var tags = new byte[8 + 4 + vendor.Length + 4 + 4 + comment.Length];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), checked((uint)vendor.Length));
        vendor.CopyTo(tags, 12);
        var offset = 12 + vendor.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(offset + 4), checked((uint)comment.Length));
        comment.CopyTo(tags, offset + 8);

        return [
            .. Page(head, 0, 0),
            .. Page(tags, 0, 1),
            .. Page([0], 96_312, 2)
        ];
    }

    private static byte[] CreateMinimalEbml(string docType, bool clusterFirst = false)
    {
        var header = EbmlElement(0x1A45DFA3, EbmlElement(0x4282, Encoding.UTF8.GetBytes(docType)));
        var duration = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(duration, BitConverter.DoubleToInt64Bits(12_500d));
        var info = EbmlElement(
            0x1549A966,
            EbmlElement(0x2AD7B1, [0x0F, 0x42, 0x40]),
            EbmlElement(0x4489, duration),
            EbmlElement(0x7BA9, "Bounded preview"u8.ToArray()));
        var video = EbmlElement(
            0xE0,
            EbmlElement(0xB0, [0x07, 0x80]),
            EbmlElement(0xBA, [0x04, 0x38]));
        var entry = EbmlElement(
            0xAE,
            EbmlElement(0x83, [1]),
            EbmlElement(0x86, "V_VP9"u8.ToArray()),
            EbmlElement(0x536E, "Main"u8.ToArray()),
            EbmlElement(0x22B59C, "eng"u8.ToArray()),
            video);
        var tracks = EbmlElement(0x1654AE6B, entry);
        var cluster = EbmlElement(0x1F43B675, new byte[64]);
        var segment = clusterFirst
            ? EbmlElement(0x18538067, cluster, info, tracks)
            : EbmlElement(0x18538067, info, tracks, cluster);
        return [.. header, .. segment];
    }

    private static byte[] EbmlElement(ulong id, params byte[][] payloadParts)
    {
        var payload = payloadParts.SelectMany(bytes => bytes).ToArray();
        if (payload.Length > 16_382)
        {
            throw new InvalidOperationException("The test EBML helper only supports two-byte sizes.");
        }
        var idBytes = new List<byte>();
        var started = false;
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            var value = (byte)(id >> shift);
            if (value != 0 || started)
            {
                started = true;
                idBytes.Add(value);
            }
        }
        var sizeBytes = payload.Length <= 126
            ? new[] { (byte)(0x80 | payload.Length) }
            : new[] { (byte)(0x40 | (payload.Length >> 8)), (byte)payload.Length };
        return [.. idBytes, .. sizeBytes, .. payload];
    }

    private static byte[] CreateMinimalAiff()
    {
        var bytes = new byte[38];
        "FORM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 30);
        "AIFFCOMM"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 18);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(22), 44_100);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(26), 16);
        new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }.CopyTo(bytes, 28);
        return bytes;
    }

    private static byte[] CreateMinimalPdf()
    {
        var builder = new StringBuilder("%PDF-1.4\n%");
        builder.Append((char)0xE2).Append((char)0xE3).Append((char)0xCF).Append((char)0xD3).Append('\n');
        var offsets = new List<int> { 0 };
        void AddObject(int number, string body)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(builder.ToString()));
            builder.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        AddObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AddObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AddObject(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        const string content = "BT /F1 20 Tf 20 50 Td (Preview) Tj ET";
        AddObject(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        AddObject(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var xrefOffset = Encoding.Latin1.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static async Task WriteLz4Async(string path, byte[] content)
    {
        await using var output = File.Create(path);
        await using var encoder = LZ4Stream.Encode(output, leaveOpen: false);
        await encoder.WriteAsync(content);
    }

    private static byte[] CreateMinimalClassFile()
    {
        var bytes = new List<byte>();
        AddU4(0xCAFEBABE);
        AddU2(0);
        AddU2(61);
        AddU2(5);
        bytes.Add(7); AddU2(2);
        AddUtf8("com/example/Test");
        bytes.Add(7); AddU2(4);
        AddUtf8("java/lang/Object");
        AddU2(0x0021); // public, super
        AddU2(1); AddU2(3);
        AddU2(0); // interfaces
        AddU2(0); // fields
        AddU2(0); // methods
        AddU2(0); // attributes
        return bytes.ToArray();

        void AddU2(ushort value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        void AddU4(uint value)
        {
            bytes.Add((byte)(value >> 24));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        void AddUtf8(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            bytes.Add(1);
            AddU2(checked((ushort)encoded.Length));
            bytes.AddRange(encoded);
        }
    }

    private static byte[] CreateMinimalWasmFile()
    {
        var bytes = new List<byte> { 0, 0x61, 0x73, 0x6D, 1, 0, 0, 0 };
        bytes.Add(0); // custom section
        bytes.Add(6); // payload length
        bytes.Add(5); // custom name length
        bytes.AddRange("debug"u8.ToArray());
        return bytes.ToArray();
    }

    private static byte[] CreateMinimalMiniDump()
    {
        var bytes = new byte[64];
        "MDMP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x0000A793);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1_700_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 4); // ModuleListStream
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), 6); // ExceptionStream
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), 60);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 1);
        return bytes;
    }

    private static byte[] CreateMinimalVdi()
    {
        var bytes = new byte[0x1A0];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x40), 0xBEDA107F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x44), 0x00010001);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x48), 0x180);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x4C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x50), 0);
        Encoding.Unicode.GetBytes("Test VDI").CopyTo(bytes, 0x54);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x170), 64L * 1024 * 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x178), 1024 * 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x184), 3);
        return bytes;
    }

    private static byte[] CreateMinimalQcow()
    {
        var bytes = new byte[72];
        new byte[] { 0x51, 0x46, 0x49, 0xFB }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 16);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), 32UL * 1024 * 1024 * 1024);
        return bytes;
    }

    private static byte[] CreateMinimalDmg()
    {
        var bytes = new byte[512];
        "koly"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 512);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), 64UL * 1024 * 1024);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), 1);
        return bytes;
    }

    private static byte[] CreateLegacyXlsFixture()
    {
        const string compressedBase64 =
            "H4sIAAAAAAACCu1be4hUVRj/ztx75967j3nsKzWdplnDdXdddsfW3RLdNdIeIIkaCq5t6zqmuDkyu4JB0JRJ/WFhKPSPYUIPH9GDoiALVugPhaQIJIr+WCP8K4iiQEGdvvPdc8+cOztXXEEoPN/lnu/+vvP4/c7ccx/nzMz33yWnjn0y5yJU2HIw4HrJhagSY7jP9kECML9U4oe+n4V7Sdv/ylwHT2TUgt+bz9vnJvHQALgIEfjYPIMpwK+4D8FuiAM8nt8+siu9Yvfu3Ngzhdyu9G21h0jDCOMaJnHgLcMjBkcwGsMxyKMNlDZS+hGV+4rSZZizF7d7lq6f3y/G7cbIIJV7jdIMpTHgLX5BdX6mSA/MgbN8DL9wkFHFqxZbAQXYASMwdptz3w7NTZkdYGE/WSt0Qitu3diBD9IZWIonZhFURlNmL160s6oV3wRrIQdbYXNIxT6w8bNVKnbhHsam5qXMFeBAKrxqGHOwkRy0I2LDWKQd0tME8rinIjzXr5tBn5mWM0hpyhyGheirEA1jhTCacl6QJBj3KfZgvf5qFGqXw6imlwlSDuAW1rc8dEBvOPGNelhZYjpp9b7OhXfgLrys5mdaO1tbu4cXLh1q88HQwpR5L7wLcwP5m9bmtm4OFsrAezCvXKirO9gOYl5qAbyPUipKVbQmivbBccgCDPN4e9pnFnCoTRD7gcyiDOUODmPVLjiBIwSrqvWClSpqDMBJeDBI5mkL8pE0hXJgoNzEEjiFd4IyqV9/WuWKmlPQRDeNf0pp5Vk9meZx5sf/vrl4ZIZxuAPjDKp9zqe88pcr4ydC4idD4sdD4u60+KGICYmiUeI+WYySbyia5BuLNnkoAvmmolV6mp6g+/G5l3B5C1FYtz2Xm+g5ACbGGZxhDu5YpZ/BVTgNj2AZvKoxF0bzYz2eW+y5Xs/1ee4BC5+d3Xt7uoXvET4r/GLh7xe+V/glwvcJ3y+8aC8r2suK9rKivaxoLyvay4r2sqK9bF8JH6gOZByAOuzAZvS8M2+hfwX9qNOMe/kUNgv/VA0m3vtQIvA+VBtpkoV5mW/x83s9BlBPbxB1mG7F9yN+nKQrJYHkV0/8+cPqLWsGhineTvEOSl+iSBHKCu7jZwpK8CLmnDGbhKR9VPplSo/xmx6WYLSZyvibGij7J7EsthqJSaVe6kQS4rgkXuTjFGNVYpEqMaNKzKwSs6rEolVidpWYUyXmVsSuiT7FKfVQBBGTyKTz4KMoIkMiB5EpEKNWLIlcRDGBIpQXlYghsiXifI5EBiJXIs5eI5GFqFYirqVOIhtRvUROgJ1riQtkkBZbIp6XEMikPEcipmgxSWeNRIaixSSddRJZihaTdMYkshUtJukss3MtSYEs0uJKxBR2i7TUSmQo7BZpqZfIUtgt0hKXyFbYLdJSZudaGgSKkpYaiXheo0A25dVKxBQtNumsl8hQtNikMy6RpWixSWdSIlvRYpPOMjvX0iSQQ1rqJGIKu0NaYhIZCrtDWhISWQq7Q1oaJLIVdoe0lNm5lmaBXNJSLxFT2F3SEpfIUNhd0pKUyFLYXdLSKJGtsLukpczOtbQg+hEnLidxYH8Gq/CN8QDtfro80gCf03LEoHL/m+dd465YnsAj9w8ktqBP3gK13cCu4+3Vojlw0PiHObX/6F9XntieOPWGAx0LPv2pG2NHcLdF/qD3dgCP0t0VYD3NygGfht6D93kAGlOv0jwd4DDdrwF+w0opUZdbStTfM54r+JiPsNU7Rgv58fy2ifTKvaO5Me+8H748u+3L04wfF7P7rrQuOscMfRq1adOmTZs2bdq0adOmTZu2W57/Ry6cv3Ck6+7EoTdx/t955UM+//8Gyosta8Bbi90I3jcFfN7P19bGxPx9Qsz7+TpAC61le+sBB8T6wNGQ9QA+n7/UXEttgWizmk8lPB18PcL7LgPqEl6TKVFsQ76wc5xnjVOrTJ9wbdq0adOmTZs2bdq0adN2RxoTc3BDzL8t+t2UN6fm/+twxc8oasWcvZ5+Oe/N+3l+Unznz+f5/i/XWsR6AM+/hvt1/TeL/6ythTxuE/ib1pWwC30BnpvR+GnB1SO/LT6O0o63ljTpZa9Syx5s//qSe/YX5v9fiNsGZC/ATthCOnbOePziv0CY2p+bruj9JBLH+zrYA8/iNkJ9fww/hW2kiUcm8D8YeYyEWxvyM3H93Cy/E+B/GBlGSUOOzsDM9PTfQv8TCv+/vWIIaAA2AAA=";
        using var compressed = new MemoryStream(Convert.FromBase64String(compressedBase64));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CreateMinimalVhd()
    {
        var footer = new byte[512];
        "conectix"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(16), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(24), 60);
        "LOPN"u8.CopyTo(footer.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(32), 0x00010000);
        "Wi2k"u8.CopyTo(footer.AsSpan(36));
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(40), 8 * 1024 * 1024);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(48), 8 * 1024 * 1024);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(60), 2);
        new Guid("11223344-5566-7788-99aa-bbccddeeff00")
            .TryWriteBytes(footer.AsSpan(68, 16), bigEndian: true, out _);
        uint sum = 0;
        foreach (var value in footer)
        {
            sum += value;
        }
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), ~sum);
        return footer;
    }

    private static byte[] CreateMinimalVhdx()
    {
        var bytes = new byte[2 * 1024 * 1024];
        "vhdxfile"u8.CopyTo(bytes);
        WriteHeader(bytes.AsSpan(64 * 1024, 4096), 1);
        WriteHeader(bytes.AsSpan(128 * 1024, 4096), 2);
        return bytes;

        static void WriteHeader(Span<byte> header, ulong sequence)
        {
            "head"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], sequence);
            new Guid("11111111-2222-3333-4444-555555555555").TryWriteBytes(header[16..32]);
            new Guid("66666666-7777-8888-9999-aaaaaaaaaaaa").TryWriteBytes(header[32..48]);
            new Guid("bbbbbbbb-cccc-dddd-eeee-ffffffffffff").TryWriteBytes(header[48..64]);
            BinaryPrimitives.WriteUInt16LittleEndian(header[64..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[66..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[68..], 1024 * 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 1024 * 1024);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], CalculateCrc32C(header));
        }

        static uint CalculateCrc32C(ReadOnlySpan<byte> data)
        {
            var copy = data.ToArray();
            copy.AsSpan(4, 4).Clear();
            var crc = uint.MaxValue;
            foreach (var value in copy)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0);
                }
            }
            return ~crc;
        }
    }

    private static byte[] CreateMinimalParquetFooter()
    {
        var bytes = new List<byte>
        {
            0x15, 0x02,       // version (field 1, i32) = 1
            0x19, 0x2C,       // schema (field 2), list of two structs
            0x48, 0x06        // root name (field 4, binary), length 6
        };
        bytes.AddRange("schema"u8.ToArray());
        bytes.AddRange([0x15, 0x02, 0x00]); // one child, end root struct
        bytes.AddRange([
            0x15, 0x0C,       // physical type BYTE_ARRAY
            0x25, 0x02,       // optional repetition
            0x18, 0x04        // name, length 4
        ]);
        bytes.AddRange("name"u8.ToArray());
        bytes.AddRange([
            0x25, 0x00, 0x00, // converted type UTF8, end leaf struct
            0x16, 0x54,       // rows (field 3, i64) = 42
            0x19, 0x0C,       // zero row groups
            0x28, 0x10        // created_by (field 6), length 16
        ]);
        bytes.AddRange("ListaryOpen test"u8.ToArray());
        bytes.Add(0x00);
        return bytes.ToArray();
    }

    private static byte[] CreateWoffWithNames(
        string family,
        string fullName,
        bool compress = false)
    {
        var familyBytes = Encoding.BigEndianUnicode.GetBytes(family);
        var fullNameBytes = Encoding.BigEndianUnicode.GetBytes(fullName);
        const int nameHeaderAndRecords = 30;
        var nameTable = new byte[nameHeaderAndRecords + familyBytes.Length + fullNameBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(nameTable.AsSpan(4), nameHeaderAndRecords);
        WriteNameRecord(nameTable.AsSpan(6), 1, familyBytes.Length, 0);
        WriteNameRecord(nameTable.AsSpan(18), 4, fullNameBytes.Length, familyBytes.Length);
        familyBytes.CopyTo(nameTable, nameHeaderAndRecords);
        fullNameBytes.CopyTo(nameTable, nameHeaderAndRecords + familyBytes.Length);

        byte[] storedTable;
        if (compress)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(nameTable);
            }
            storedTable = compressed.ToArray();
        }
        else
        {
            storedTable = nameTable;
        }
        var paddedStoredLength = (storedTable.Length + 3) & ~3;
        var paddedNameLength = (nameTable.Length + 3) & ~3;
        var fileLength = 64 + paddedStoredLength;
        var bytes = new byte[fileLength];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x774F4646);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), checked((uint)fileLength));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), checked((uint)(28 + paddedNameLength)));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 1);
        "name"u8.CopyTo(bytes.AsSpan(44));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(48), 64);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(52), checked((uint)storedTable.Length));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), checked((uint)nameTable.Length));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60), CalculateChecksum(nameTable));
        storedTable.CopyTo(bytes, 64);
        return bytes;

        static void WriteNameRecord(Span<byte> record, ushort nameId, int length, int offset)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record, 3);
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(record[4..], 0x0409);
            BinaryPrimitives.WriteUInt16BigEndian(record[6..], nameId);
            BinaryPrimitives.WriteUInt16BigEndian(record[8..], checked((ushort)length));
            BinaryPrimitives.WriteUInt16BigEndian(record[10..], checked((ushort)offset));
        }

        static uint CalculateChecksum(ReadOnlySpan<byte> data)
        {
            uint checksum = 0;
            Span<byte> word = stackalloc byte[4];
            for (var offset = 0; offset < data.Length; offset += 4)
            {
                word.Clear();
                data.Slice(offset, Math.Min(4, data.Length - offset)).CopyTo(word);
                checksum = unchecked(checksum + BinaryPrimitives.ReadUInt32BigEndian(word));
            }
            return checksum;
        }
    }

    private static void CreateMinimalOutlookMessage(
        string path,
        string subject,
        string body)
    {
        using var root = RootStorage.Create(path, OpenMcdf.Version.V3, StorageModeFlags.Transacted);
        root.CLSID = new Guid("00020D0B-0000-0000-C000-000000000046");
        using (var properties = root.CreateStream("__properties_version1.0"))
        {
            properties.Write(new byte[32]);
        }
        WriteUnicodeStream(root, "__substg1.0_0037001F", subject);
        WriteUnicodeStream(root, "__substg1.0_1000001F", body);
        root.Commit();

        static void WriteUnicodeStream(RootStorage root, string name, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value + '\0');
            using var stream = root.CreateStream(name);
            stream.Write(bytes);
        }
    }

    private static byte[] CreatePalmDoc(string text)
    {
        var record0Offset = 94;
        var record1Offset = record0Offset + 16;
        var textBytes = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[record1Offset + textBytes.Length];
        Encoding.Latin1.GetBytes("Sample Book").CopyTo(bytes, 0);
        "BOOK"u8.CopyTo(bytes.AsSpan(60));
        "MOBI"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(76), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(78), (uint)record0Offset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(86), (uint)record1Offset);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record0Offset + 4), (uint)textBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset + 8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset + 10), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset + 12), 0);
        textBytes.CopyTo(bytes, record1Offset);
        return bytes;
    }

    private static byte[] CreateLargePalmDocHeader(string text)
    {
        const int record0Offset = 102;
        const int record1Offset = record0Offset + 16;
        var textBytes = Encoding.UTF8.GetBytes(text);
        var record2Offset = record1Offset + textBytes.Length;
        var bytes = new byte[record2Offset];
        Encoding.Latin1.GetBytes("Large Sample").CopyTo(bytes, 0);
        "BOOK"u8.CopyTo(bytes.AsSpan(60));
        "MOBI"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(76), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(78), record0Offset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(86), record1Offset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(94), (uint)record2Offset);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record0Offset + 4), (uint)textBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset + 8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset + 10), 4096);
        textBytes.CopyTo(bytes, record1Offset);
        return bytes;
    }

    private static byte[] CreateCrossRecordPalmDoc()
    {
        const int record0Offset = 102;
        const int record1Offset = record0Offset + 16;
        const int record2Offset = record1Offset + 1;
        var bytes = new byte[record2Offset + 2];
        Encoding.Latin1.GetBytes("Invalid records").CopyTo(bytes, 0);
        "BOOK"u8.CopyTo(bytes.AsSpan(60));
        "MOBI"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(76), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(78), record0Offset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(86), record1Offset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(94), record2Offset);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(record0Offset + 4), 4);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record0Offset + 8), 2);
        bytes[record1Offset] = (byte)'A';
        bytes[record2Offset] = 0x80;
        bytes[record2Offset + 1] = 0x08;
        return bytes;
    }

    private static byte[] CreateMinimalJolietIso()
    {
        const int sectorSize = 2048;
        var image = new byte[22 * sectorSize];
        WriteVolumeDescriptor(image.AsSpan(16 * sectorSize, sectorSize), type: 1, joliet: false);
        WriteVolumeDescriptor(image.AsSpan(17 * sectorSize, sectorSize), type: 2, joliet: true);
        var terminator = image.AsSpan(18 * sectorSize, sectorSize);
        terminator[0] = 255;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;

        var directory = image.AsSpan(20 * sectorSize, sectorSize);
        var offset = 0;
        offset += WriteIsoDirectoryRecord(directory[offset..], 20, sectorSize, directory: true, [0]);
        offset += WriteIsoDirectoryRecord(directory[offset..], 20, sectorSize, directory: true, [1]);
        var name = Encoding.BigEndianUnicode.GetBytes("预览.TXT;1");
        WriteIsoDirectoryRecord(directory[offset..], 21, 5, directory: false, name);
        "hello"u8.CopyTo(image.AsSpan(21 * sectorSize));
        return image;

        static void WriteVolumeDescriptor(Span<byte> descriptor, byte type, bool joliet)
        {
            descriptor[0] = type;
            "CD001"u8.CopyTo(descriptor[1..]);
            descriptor[6] = 1;
            if (joliet)
            {
                descriptor[88] = (byte)'%';
                descriptor[89] = (byte)'/';
                descriptor[90] = (byte)'E';
            }

            WriteBothEndian16(descriptor[128..], sectorSize);
            WriteBothEndian32(descriptor[80..], 22);
            WriteIsoDirectoryRecord(descriptor[156..], 20, sectorSize, directory: true, [0]);
        }

        static int WriteIsoDirectoryRecord(
            Span<byte> target,
            uint extent,
            uint length,
            bool directory,
            ReadOnlySpan<byte> name)
        {
            var recordLength = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
            target[0] = checked((byte)recordLength);
            target[1] = 0;
            WriteBothEndian32(target[2..], extent);
            WriteBothEndian32(target[10..], length);
            target[25] = directory ? (byte)2 : (byte)0;
            WriteBothEndian16(target[28..], 1);
            target[32] = checked((byte)name.Length);
            name.CopyTo(target[33..]);
            return recordLength;
        }

        static void WriteBothEndian16(Span<byte> target, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target, checked((ushort)value));
            BinaryPrimitives.WriteUInt16BigEndian(target[2..], checked((ushort)value));
        }

        static void WriteBothEndian32(Span<byte> target, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
            BinaryPrimitives.WriteUInt32BigEndian(target[4..], value);
        }
    }

    private static byte[] CreateMinimalShellLink()
    {
        const uint flags = 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80;
        var strings = new[]
        {
            "Preview shortcut",
            @"..\target.exe",
            @"C:\Work",
            "--safe-preview",
            @"C:\target.exe,0"
        };
        var length = 76 + strings.Sum(value => 2 + Encoding.Unicode.GetByteCount(value));
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x4C);
        new Guid("00021401-0000-0000-C000-000000000046").ToByteArray().CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 1234);
        var offset = 76;
        foreach (var value in strings)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value.Length));
            offset += 2;
            offset += Encoding.Unicode.GetBytes(value, bytes.AsSpan(offset));
        }

        return bytes;
    }

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));

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

    private sealed class FakeOutlookStoreFolderNode : IOutlookStoreFolderNode
    {
        public FakeOutlookStoreFolderNode(
            string name,
            int contentCount,
            int unreadCount,
            params FakeOutlookStoreFolderNode[] children)
        {
            DisplayName = name;
            ContentCount = contentCount;
            ContentUnreadCount = unreadCount;
            Children = children;
        }

        public string DisplayName { get; }

        public int ContentCount { get; }

        public int ContentUnreadCount { get; }

        public IEnumerable<IOutlookStoreFolderNode> Children { get; }
    }

    private sealed class FakeWindowsFilterSession : IWindowsFilterSession
    {
        private const int EndOfChunks = unchecked((int)0x80041700);
        private const int NoMoreText = unchecked((int)0x80041701);
        private readonly Queue<(WindowsFilterChunk Chunk, string Text)> _chunks = new();
        private string? _currentText;
        private bool _textReturned;

        public uint InitializationFlags { get; private set; }

        public int ChunkCalls { get; private set; }

        public int? ChunkResultOverride { get; init; }

        public uint? ReturnedCharacterCountOverride { get; init; }

        public bool ReturnSuccessfulTextWithoutProgress { get; init; }

        public FakeWindowsFilterSession(params object[] values)
        {
            for (var index = 0; index + 1 < values.Length; index += 2)
            {
                _chunks.Enqueue(((WindowsFilterChunk)values[index], (string)values[index + 1]));
            }
        }

        public int Init(uint flags)
        {
            InitializationFlags = flags;
            return 0;
        }

        public int GetChunk(out WindowsFilterChunk chunk)
        {
            ChunkCalls++;
            if (ChunkResultOverride is int result)
            {
                chunk = default;
                return result;
            }
            if (!_chunks.TryDequeue(out var next))
            {
                chunk = default;
                return EndOfChunks;
            }
            chunk = next.Chunk;
            _currentText = next.Text;
            _textReturned = false;
            return 0;
        }

        public int GetText(uint capacity, out uint characters, out string text)
        {
            if (ReturnSuccessfulTextWithoutProgress)
            {
                characters = 0;
                text = string.Empty;
                return 0;
            }
            if (_textReturned || _currentText is null)
            {
                characters = 0;
                text = string.Empty;
                return NoMoreText;
            }
            var count = Math.Min(checked((int)capacity), _currentText.Length);
            text = _currentText[..count];
            characters = ReturnedCharacterCountOverride ?? checked((uint)count);
            _textReturned = true;
            return 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ParquetFixtureRow
    {
        public string? Name { get; set; }

        public DateTime EventTime { get; set; }

        public decimal Value { get; set; }
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
