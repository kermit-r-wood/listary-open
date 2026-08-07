# Preview format support

The preview pipeline distinguishes built-in capability from optional Windows preview
handlers. A registered extension is not automatically a promise of built-in rendering.
For rich documents and codec-dependent media, ListaryOpen tries the installed Windows
handler first and then uses the listed safe fallback.

| Family | Common extensions | Built-in fallback | Windows handler |
| --- | --- | --- | --- |
| Text and source | extensionless text, txt, log, ini, cfg, csv, tsv, json, xml, yaml, toml, subtitles (srt/vtt/ass), playlists (m3u/m3u8/pls) and source-code extensions | Bounded plain text; UTF-8, UTF-16 and GBK detection | Not required |
| Calendar and contacts | ics, ifb, vcs, vcf, vcard, Windows contact, ldif | Bounded unfolded event/contact fields; Windows Contact XML and LDIF values are whitelisted; binary photos and external value URIs are ignored | Not required |
| Markup and email | md, markdown, html, htm, shtml, xht, xhtml, eml, emlx, mbox, mht, mhtml | Safe plain-text conversion; bounded multipart MIME and RFC 2047 decoding; MBOX headers are summarized without indexing message bodies; attachments listed but never opened; no scripts or external resources | Not required |
| Outlook messages and templates | msg, oft | Bounded sender, recipient, subject, body and attachment-name extraction in the isolated preview worker; attachments are never saved, opened or rendered; files over 32 MiB degrade to metadata | Preferred for layout |
| Raster images | apng, bmp, cur, dib, gif, ico, jpeg, jpg, png, tif, tiff | Rendered image | Not required |
| Codec/design images | avif, heic, heif, jp2, j2k, jpf, webp, jxr, wdp, hdp, dds, tga, exr | WIC/WPF decode or Windows thumbnail | Preferred |
| Camera/design images | arw, cr2, cr3, dng, nef, orf, pef, raf, raw, rw2, sr2, srw, x3f, psd, psb | Windows thumbnail when available | Required for full preview |
| Vector images | svg, svgz | Safe structure and bounded source preview, including gzip-wrapped SVG; scripts and external references are not loaded | Not required |
| PostScript/design vectors | ai, eps, ps | Bounded DSC metadata, page/bounding-box structure, and literal text strings are shown; PostScript code is never interpreted or executed | Preferred for rendered content |
| Fonts | eot, otf, ttc, ttf, woff, woff2 | Typeface specimen for desktop fonts; validated bounded container metadata for web fonts; WOFF1 family/full-name/version/manufacturer/license extraction; EOT version, names, weight and embedding flags are read without unpacking font data | Not required |
| PDF | pdf | First page rendered by bundled PDFium, bounded to 1600px/8MP; metadata fallback | Preferred for interactive pages |
| Modern Office | docx/docm/dotx/dotm, xlsx/xlsm/xltx/xltm, pptx/pptm/potx/potm/ppsx/ppsm | Bounded OpenXML text extraction | Preferred for layout |
| Legacy Excel | xls, xlt | BIFF2–BIFF8 sheet/cell preview in the isolated worker with a captured 32 MiB input length and bounded emitted rows, cells and text; cached values are shown without executing formulas or macros, and links or embedded objects are not extracted | Preferred for layout |
| Legacy Word and PowerPoint | doc/dot, ppt/pot/pps | When a compatible x64 Windows IFilter is registered, bounded text extraction runs in the isolated worker against a captured temporary copy with embedded/link traversal disabled; otherwise signature and size metadata | Preferred/required for document content |
| Outlook mail stores | pst/ost | Read-only folder tree with stored message and unread counts in the isolated worker; message subjects, bodies, recipients and attachments are deliberately not read; locked, corrupt, password-protected, oversized or unsupported stores fall back to signature/size metadata | Preferred for layout |
| Legacy compound documents | wps/et/dps, one, pub | For CFB-backed files, bounded read-only storage/stream directory listing without opening stream payloads; non-CFB or malformed files fall back to signature/size metadata | Preferred for structure; required for rendered document content |
| OpenDocument | odt, ods, odp, odg | Bounded XML text extraction | Preferred for layout |
| Visio | vsdx | Bounded page XML text extraction | Preferred for layout |
| Fixed-layout documents | xps, oxps | Bounded glyph text extraction from the package; markup is not rendered | Preferred for layout |
| Help documents | chm | Bounded ITSF/ITSP header and uncompressed PMGL directory listing with virtual paths, sections, offsets and lengths; MSCompressed/LZX topic payloads are not decompressed or rendered | Preferred for structure; required for topic rendering |
| DjVu scanned documents | djv, djvu | Bounded IFF/FORM structure, page count/dimensions, and plain `TXTa` OCR text; `TXTz`/BZZ, image, JB2, and annotations are reported but not decoded | Preferred for rendered content |
| E-books | epub, mobi, azw, azw3, prc, kfx, fb2, fbz, fb2.zip | Bounded package, FictionBook or PalmDOC text extraction; KFX-ZIP central-directory listing and raw Ion printable metadata samples; large MOBI files use random record reads instead of whole-file allocation; encrypted/DRM and HUFF/CDIC books safely degrade without decryption | Preferred for layout |
| ZIP containers and Windows app packages | zip, zipx, cbz, 3mf, jar, war, ear, apk, aab, appx/appxbundle, msix/msixbundle, nupkg, snupkg, vsix, xpi, ipa, kmz | Bounded central directory and entry listing; ZIPX directory enumeration does not open payload streams | Preferred when installed |
| Other package containers | pages, numbers, key, sketch, maff, olm, ova | Bounded ZIP/TAR package listing; MAFF extracts only bounded `index.html` text and never loads resources | Preferred when installed |
| TAR/GZip | tar, tgz, tar.gz, gz | Bounded entry listing or decompressed text sample | Preferred when installed |
| Other archives/images | 7z, rar, cbr, cb7, ace, arc, arj | Bounded entry listing via SharpCompress; no extraction | Preferred when installed |
| Multipart archives | 7z.001, zip.001, partNN.rar, rNN, zNN | Bounded sibling-volume metadata; volumes are never combined or extracted | Preferred when installed |
| Cabinet and disc images | cab, msu, ISO9660/Joliet iso | Bounded read-only entry listing; no extraction | Preferred when installed |
| Compressed streams | br, bz2, xz, zst/zstd, lz/lzip, lz4, Unix compress Z and common tar aliases including tar.lz4/tlz4 | Bounded decompressed text/binary sample; LZ4 frame decoding and all raw or compressed TAR listings have explicit compressed-input and 64 MiB scan budgets | Preferred when installed |
| Windows deployment images | wim, esd | Bounded image names, descriptions, editions, architectures, versions, languages and sizes plus up to 200 paths from the first two images through the read-only Windows Imaging API; directory enumeration uses `WIM_FLAG_NO_APPLY`, and images are never mounted or extracted; signature/size metadata fallback when the host API cannot read the format | Preferred |
| Audio | wav, flac, mp3, aac/adts, aif/aiff, amr, ape, m4a, m4b, mid/midi, mka, oga/ogg, opus, wma | Play/pause, seek and mute with installed Windows codecs; bounded built-in metadata covers WAV, FLAC, MP3, ISO-BMFF audio, Opus/Vorbis comments and duration, AIFF common chunks, MIDI headers, AAC ADTS headers and MKA EBML tracks | Built-in player first |
| Video | 3gp, avi, flv, m2ts, mts, m4v, mkv, mov, mp4, mpeg, mpg, ts, vob, webm, wmv | Play/pause, seek and mute with installed Windows codecs; MP4/MOV atom and MKV/WebM EBML metadata includes duration, tracks, codecs and dimensions without reading frame/cluster payloads | Preferred |
| Databases | db, sqlite, sqlite3, dbf, mdb, accdb, mde, accde | SQLite header plus bounded read-only `sqlite_schema` declarations; bounded dBASE schema metadata without opening rows or memo sidecars; Jet/ACE Access marker, page count and UTF-16 name samples; generic DB and encrypted/compiled Access files safely degrade; user rows, macros and attachments are never queried | Preferred for Access content |
| Shortcuts | lnk, url | Bounded target/argument metadata; credentials redacted; target never resolved or opened | Not required |
| 3D and exchange CAD | stl, obj, ply, gltf, glb, dxf | Bounded geometry/scene/entity statistics; external resources ignored | Preferred for DXF |
| Windows binaries | exe, dll, sys, msi | Version, architecture and signature metadata; never executed | Not required |
| Windows Event Logs | evtx | Bounded newest-event metadata (timestamp, provider, id, level, task, opcode and record id) in the isolated worker; event payloads, message resources and rendered text are not loaded | Preferred |
| Safari WebArchive | webarchive | XML and binary plist parsing in the isolated worker with a 4 MiB container and 1 MiB main-resource budget; only the textual main resource is decoded, HTML is converted to plain text, and subresources/scripts/links are never loaded | Preferred for layout |
| VHD and VHDX virtual disks | vhd, vhdx | Bounded footer/header metadata; VHD footer checksum and VHDX redundant-header CRC32C are validated; disks are never mounted or attached | Preferred when installed |
| VirtualBox VDI images | vdi | Bounded VDI header, version, image type, virtual size, block size, allocation count and description; never mounted or attached | Preferred when installed |
| VMware VMDK images | vmdk | Bounded sparse-header geometry or descriptor text; extents and backing files are never mounted, opened, or resolved | Preferred when installed |
| QEMU QCOW images | qcow, qcow2 | Bounded QCOW version, cluster size, virtual size and backing-file descriptor; never mounted or opened | Preferred when installed |
| Apple disk images | dmg | Bounded UDIF footer/version, data-fork range, flags and segment count; never mounted or decompressed | Preferred when installed |
| Windows crash dumps | dmp, mdmp | Bounded minidump stream-directory validation with module/thread counts, exception/system-info presence and timestamp; memory, symbols and payloads are never opened | Preferred when installed |
| BitTorrent and packet captures | torrent, pcap, pcapng | Bounded torrent bencode metadata (name, lengths, pieces and file paths) or PCAP/PCAPNG block/record lengths; trackers are never contacted and packet/piece payloads are not opened | Preferred |
| LHA/LZH archives | lha, lzh | Level 0/1/2 directory headers, paths (including extended Unicode names), methods, and declared packed/original sizes are listed with bounded header scanning; compressed payloads are never opened or decompressed | Preferred when installed |
| Java and WebAssembly binaries | class, wasm | Bounded class constant-pool/member structure or Wasm section and custom-name listing; bytecode is never loaded, linked, instantiated, executed, or decompiled | Preferred when installed |
| Developer and analytic binaries | Avro, Arrow/Feather, ORC, Parquet, sav, sas7bdat, dta | Bounded signature/version metadata; Parquet additionally reads only its capped Compact-Thrift footer to show schema, row count, row groups and producer—column pages and row values are never read | Preferred when installed |
| Proprietary CAD | dwg | Bounded AutoCAD version-signature metadata; drawing content is not decoded | Required for rendered content |

All built-in readers are read-only, bounded, and must degrade without executing file
content. Archive, package, MIME, ebook, model, database, shortcut, Outlook-store and
structured PIM text parsers, including MSG/OFT compound-file decoding, run in
`ListaryOpen.PreviewHost.exe`; the main process terminates the
entire worker process tree after five seconds. This is a lifetime/resource isolation
boundary rather than a Windows privilege sandbox. The canonical extension and
compound-name policy is defined in `PreviewFormatRegistry`.

Legacy Office IFilter availability and extracted text depend on the filters installed
for the application's x64 process. ListaryOpen does not install Office filters or
change filter registrations or file associations. A missing, incompatible, timed-out,
or crashed filter safely falls back to metadata; worker termination limits lifetime

and resource exposure but does not provide a privilege sandbox.

PST/OST folder previews use the managed `XstReader.Api` reader (MS-PL) without any
Outlook or Office components. The reader is intentionally used only for the root
folder, child folders, and their stored count properties: enumerating messages can
process signed/encrypted content and attachments, so it is not part of the built-in
preview contract. The worker opens stores read-only and uses a 64 GiB input cap,
500-folder/32-level/120,000-character output budgets, and the normal five-second
worker lifetime limit.
