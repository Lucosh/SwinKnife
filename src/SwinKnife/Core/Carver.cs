using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace SwinKnife.Core;

public sealed record FoundFile(long Offset, long Size, string Ext, string Category, bool Complete);

/// <summary>
/// Recupero per "carving": cerca le firme dei formati all'inizio di ogni settore e ricostruisce la lunghezza
/// del file analizzandone la struttura. Funziona su FAT32, exFAT, NTFS e supporti formattati (senza nomi originali).
/// </summary>
public static partial class Carver
{
    private const int SectorSize = 512;
    private const int ChunkSize = 8 << 20;
    private const long MiB = 1 << 20;

    private delegate (long size, string ext, bool complete)? Parser(IByteSource src, long off);

    private sealed record Signature(byte[] Magic, int Offset, Parser Parse, string Category);

    public static readonly Dictionary<string, string> Categories = new()
    {
        [L.T("Immagini")] = L.T("Foto e immagini (JPG, PNG, GIF, BMP, HEIC, RAW, TIFF)"),
        [L.T("Documenti")] = L.T("Documenti (PDF, Word, Excel, PowerPoint, OpenDocument)"),
        [L.T("Audio")] = L.T("Audio (MP3, WAV, OGG, M4A)"),
        [L.T("Video")] = L.T("Video (MP4, MOV, AVI, 3GP)"),
        [L.T("Archivi")] = L.T("Archivi (ZIP, 7z)"),
    };

    private static readonly Dictionary<string, string> ExtCategory = new()
    {
        ["avi"] = L.T("Video"), ["webp"] = L.T("Immagini"), ["heic"] = L.T("Immagini"), ["avif"] = L.T("Immagini"), ["cr3"] = L.T("Immagini"),
        ["m4a"] = L.T("Audio"), ["ogv"] = L.T("Video"), ["zip"] = L.T("Archivi"), ["jar"] = L.T("Archivi"), ["apk"] = L.T("Archivi"), ["epub"] = L.T("Documenti"),
    };

    private static readonly HashSet<string> InexactEnd = ["tif", "cr2", "nef", "arw", "dng", "orf", "pef"];

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static readonly Signature[] Signatures =
    [
        new([0xFF, 0xD8, 0xFF], 0, ParseJpeg, L.T("Immagini")),
        new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, ParsePng, L.T("Immagini")),
        new(B("GIF87a"), 0, ParseGif, L.T("Immagini")),
        new(B("GIF89a"), 0, ParseGif, L.T("Immagini")),
        new(B("BM"), 0, ParseBmp, L.T("Immagini")),
        new([0x49, 0x49, 0x2A, 0x00], 0, ParseTiff, L.T("Immagini")),
        new([0x4D, 0x4D, 0x00, 0x2A], 0, ParseTiff, L.T("Immagini")),
        new(B("%PDF-"), 0, ParsePdf, L.T("Documenti")),
        new([0x50, 0x4B, 0x03, 0x04], 0, ParseZip, L.T("Documenti")),
        new([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], 0, ParseOle, L.T("Documenti")),
        new(B("ID3"), 0, ParseMp3, L.T("Audio")),
        new(B("RIFF"), 0, ParseRiff, L.T("Audio")),
        new(B("OggS"), 0, ParseOgg, L.T("Audio")),
        new(B("ftyp"), 4, ParseIsoBmff, L.T("Video")),
        new([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, Parse7z, L.T("Archivi")),
    ];

    private static ushort U16(byte[] b, int o, bool le = true) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o, bool le = true) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o, bool le = true) => le ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));

    // ================================================================ JPEG
    private const long JpegMax = 80 * MiB;

    private static long? ScanEntropy(IByteSource src, long off, long pos)
    {
        while (pos < JpegMax)
        {
            var chunk = src.Read(off + pos, 256 * 1024);
            if (chunk.Length < 2) return null;
            var i = 0;
            while (true)
            {
                i = Array.IndexOf(chunk, (byte)0xFF, i);
                if (i < 0)
                {
                    pos += chunk.Length;
                    break;
                }
                if (i + 1 >= chunk.Length)
                {
                    pos += i;
                    break;
                }
                var n = chunk[i + 1];
                if (n == 0x00 || n is >= 0xD0 and <= 0xD7)
                {
                    i += 2;
                    continue;
                }
                if (n == 0xFF)
                {
                    i += 1;
                    continue;
                }
                return pos + i;
            }
        }
        return null;
    }

    public static (long size, string ext, bool complete)? ParseJpeg(IByteSource src, long off)
    {
        long pos = 2;
        bool sof = false, sos = false;
        while (pos < JpegMax)
        {
            var h = src.Read(off + pos, 4);
            if (h.Length < 2 || h[0] != 0xFF) return null;
            var m = h[1];
            if (m == 0xFF)
            {
                pos++;
                continue;
            }
            if (m == 0x01 || m is >= 0xD0 and <= 0xD7)
            {
                pos += 2;
                continue;
            }
            if (m == 0xD8) return null;
            if (m == 0xD9) return sos ? (pos + 2, "jpg", sof) : null;
            if (h.Length < 4) return null;
            var len = (h[2] << 8) | h[3];
            if (len < 2) return null;
            if (m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) sof = true;
            pos += 2 + len;
            if (m == 0xDA)
            {
                sos = true;
                var next = ScanEntropy(src, off, pos);
                if (next == null) return null;
                pos = next.Value;
            }
        }
        return null;
    }

    // ================================================================ PNG / GIF / BMP
    private static (long, string, bool)? ParsePng(IByteSource src, long off)
    {
        long pos = 8;
        var first = true;
        while (pos < 200 * MiB)
        {
            var h = src.Read(off + pos, 8);
            if (h.Length < 8) return null;
            var len = U32(h, 0, false);
            if (len > 0x7FFFFFFF || !h.AsSpan(4, 4).ToArray().All(c => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) return null;
            var type = Encoding.ASCII.GetString(h, 4, 4);
            if (first && type != "IHDR") return null;
            first = false;
            pos += 12 + len;
            if (type == "IEND") return (pos, "png", true);
        }
        return null;
    }

    private static long? SkipSubBlocks(IByteSource src, long off, long pos)
    {
        while (true)
        {
            var b = src.Read(off + pos, 1);
            if (b.Length == 0) return null;
            pos++;
            if (b[0] == 0) return pos;
            pos += b[0];
            if (pos > 100 * MiB) return null;
        }
    }

    private static (long, string, bool)? ParseGif(IByteSource src, long off)
    {
        var h = src.Read(off, 13);
        if (h.Length < 13) return null;
        long pos = 13;
        if ((h[10] & 0x80) != 0) pos += 3 * (2 << (h[10] & 7));
        var frames = 0;
        while (pos < 100 * MiB)
        {
            var b = src.Read(off + pos, 1);
            if (b.Length == 0) return null;
            long? next;
            switch (b[0])
            {
                case 0x3B:
                    return frames > 0 ? (pos + 1, "gif", true) : null;
                case 0x21:
                    next = SkipSubBlocks(src, off, pos + 2);
                    break;
                case 0x2C:
                {
                    var d = src.Read(off + pos, 10);
                    if (d.Length < 10) return null;
                    pos += 10;
                    if ((d[9] & 0x80) != 0) pos += 3 * (2 << (d[9] & 7));
                    next = SkipSubBlocks(src, off, pos + 1);
                    frames++;
                    break;
                }
                default:
                    return null;
            }
            if (next == null) return null;
            pos = next.Value;
        }
        return null;
    }

    private static (long, string, bool)? ParseBmp(IByteSource src, long off)
    {
        var h = src.Read(off, 54);
        if (h.Length < 54) return null;
        long size = U32(h, 2);
        var dataOff = U32(h, 10);
        var hsize = U32(h, 14);
        if (U32(h, 6) != 0 || hsize is not (12 or 40 or 52 or 56 or 64 or 108 or 124)) return null;
        if (size < 54 || size > 300 * MiB || dataOff < 14 + hsize || dataOff >= size) return null;
        if (hsize >= 40)
        {
            var w = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(18));
            var hh = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(22)));
            var planes = U16(h, 26);
            var bpp = U16(h, 28);
            var comp = U32(h, 30);
            if (planes != 1 || bpp is not (1 or 4 or 8 or 16 or 24 or 32) || w <= 0 || w > 50000 || hh == 0) return null;
            if (comp == 0)
            {
                var expected = dataOff + ((long)w * bpp + 31) / 32 * 4 * hh;
                if (size < expected - 4 || size > expected + 1024) return null;
            }
        }
        return (size, "bmp", true);
    }

    // ================================================================ TIFF / RAW
    private static readonly Dictionary<int, int> TiffTypeSize = new()
    {
        [1] = 1, [2] = 1, [3] = 2, [4] = 4, [5] = 8, [6] = 1, [7] = 1, [8] = 2, [9] = 4, [10] = 8, [11] = 4, [12] = 8, [13] = 4, [16] = 8,
    };

    private static (long, string, bool)? ParseTiff(IByteSource src, long off)
    {
        const long max = 300 * MiB;
        var h = src.Read(off, 16);
        if (h.Length < 16) return null;
        var le = h[0] == (byte)'I';
        var first = U32(h, 4, le);
        if (first < 8 || first > 64 * MiB) return null;
        long end = 8;
        var hasData = false;
        var make = "";
        var dng = false;
        var queue = new Stack<long>();
        queue.Push(first);
        var seen = new HashSet<long>();
        while (queue.Count > 0 && seen.Count < 64)
        {
            var ifd = queue.Pop();
            if (!seen.Add(ifd) || ifd < 8 || ifd >= max) continue;
            var cb = src.Read(off + ifd, 2);
            if (cb.Length < 2) continue;
            var n = U16(cb, 0, le);
            if (n == 0 || n > 1000)
            {
                if (ifd == first) return null;
                continue;
            }
            var ent = src.Read(off + ifd + 2, n * 12 + 4);
            if (ent.Length < n * 12 + 4) continue;
            end = Math.Max(end, ifd + 2 + n * 12 + 4);
            var vals = new Dictionary<int, List<long>>();
            for (var k = 0; k < n; k++)
            {
                var e = k * 12;
                int tag = U16(ent, e, le), type = U16(ent, e + 2, le);
                long cnt = U32(ent, e + 4, le);
                var sz = TiffTypeSize.GetValueOrDefault(type) * cnt;
                if (sz > 4)
                {
                    var voff = U32(ent, e + 8, le);
                    if (voff + sz < max) end = Math.Max(end, voff + sz);
                }
                if (tag == 0xC612) dng = true;
                if (tag == 0x010F && type == 2)
                {
                    var raw = sz <= 4 ? ent.AsSpan(e + 8, (int)Math.Min(cnt, 4)).ToArray() : src.Read(off + U32(ent, e + 8, le), (int)Math.Min(cnt, 64));
                    make = Encoding.ASCII.GetString(raw).ToUpperInvariant();
                }
                if (tag is 0x111 or 0x117 or 0x144 or 0x145 or 0x201 or 0x202 or 0x14A or 0x8769 && type is 3 or 4 or 13)
                {
                    cnt = Math.Min(cnt, 100000);
                    var itemSize = type == 3 ? 2 : 4;
                    var bytes = (int)(itemSize * cnt);
                    var raw = bytes <= 4 ? ent.AsSpan(e + 8, 4).ToArray() : src.Read(off + U32(ent, e + 8, le), bytes);
                    if (raw.Length < bytes) continue;
                    var list = new List<long>((int)cnt);
                    for (var j = 0; j < cnt; j++) list.Add(type == 3 ? U16(raw, j * 2, le) : U32(raw, j * 4, le));
                    vals[tag] = list;
                }
            }
            foreach (var (o, c) in new[] { (0x111, 0x117), (0x144, 0x145), (0x201, 0x202) })
            {
                if (!vals.TryGetValue(o, out var offs) || !vals.TryGetValue(c, out var counts)) continue;
                for (var j = 0; j < Math.Min(offs.Count, counts.Count); j++)
                {
                    if (offs[j] + counts[j] is > 0 and < max)
                    {
                        end = Math.Max(end, offs[j] + counts[j]);
                        hasData = true;
                    }
                }
            }
            foreach (var t in new[] { 0x14A, 0x8769 })
                if (vals.TryGetValue(t, out var subs))
                    foreach (var s in subs) queue.Push(s);
            var next = U32(ent, n * 12, le);
            if (next != 0) queue.Push(next);
        }
        if (!hasData) return null;
        var ext = h[8] == 'C' && h[9] == 'R' ? "cr2" : dng ? "dng" : make.StartsWith("NIKON") ? "nef" : make.StartsWith("SONY") ? "arw"
            : make.StartsWith("OLYMPUS") ? "orf" : make.StartsWith("PENTAX") ? "pef" : "tif";
        return (end, ext, false);
    }

    // ================================================================ PDF
    [GeneratedRegex(@"^\s*(\d+\s+\d+\s+obj|xref)")]
    private static partial Regex PdfContinuation();

    private static readonly byte[] PdfEof = B("%%EOF");

    private static (long, string, bool)? ParsePdf(IByteSource src, long off)
    {
        var head = Encoding.Latin1.GetString(src.Read(off, 8));
        if (!Regex.IsMatch(head, @"^%PDF-[12]\.\d")) return null;
        long pos = 0;
        while (pos < 1024 * MiB)
        {
            var chunk = src.Read(off + pos, (int)MiB);
            if (chunk.Length < 5) return null;
            var start = 0;
            while (true)
            {
                var i = chunk.AsSpan(start).IndexOf(PdfEof);
                if (i < 0) break;
                i += start;
                var end = pos + i + 5;
                var tail = src.Read(off + end, 2);
                if (tail.Length >= 2 && tail[0] == '\r' && tail[1] == '\n') end += 2;
                else if (tail.Length >= 1 && tail[0] is (byte)'\r' or (byte)'\n') end += 1;
                if (PdfContinuation().IsMatch(Encoding.Latin1.GetString(src.Read(off + end, 64))))
                {
                    start = i + 5;
                    continue;
                }
                return (end, "pdf", true);
            }
            pos += chunk.Length - 4;
        }
        return null;
    }

    // ================================================================ ZIP / Office / ODF
    private static readonly byte[] Eocd = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] CentralDir = [0x50, 0x4B, 0x01, 0x02];

    private static string ZipKind(IByteSource src, long off, string firstName, byte[] h, long cdOff, long cdSize)
    {
        if (firstName == "mimetype")
        {
            var mt = Encoding.ASCII.GetString(src.Read(off + 30 + firstName.Length + U16(h, 28), (int)Math.Min(U32(h, 18), 100)));
            if (mt.Contains("opendocument.text")) return "odt";
            if (mt.Contains("opendocument.spreadsheet")) return "ods";
            if (mt.Contains("opendocument.presentation")) return "odp";
            if (mt.Contains("epub+zip")) return "epub";
        }
        var cd = cdOff == 0xFFFFFFFF ? "" : Encoding.Latin1.GetString(src.Read(off + cdOff, (int)Math.Min(cdSize, 4 * MiB)));
        if (cd.Contains("word/document.xml")) return "docx";
        if (cd.Contains("xl/workbook.xml")) return "xlsx";
        if (cd.Contains("ppt/presentation.xml")) return "pptx";
        if (cd.Contains("AndroidManifest.xml")) return "apk";
        if (cd.Contains("META-INF/MANIFEST.MF")) return "jar";
        return "zip";
    }

    private static (long, string, bool)? ParseZip(IByteSource src, long off)
    {
        var h = src.Read(off, 30);
        if (h.Length < 30) return null;
        var nameLen = U16(h, 26);
        if (nameLen is 0 or > 1024 || U16(h, 4) > 100) return null;
        var firstName = Encoding.Latin1.GetString(src.Read(off + 30, nameLen));
        long pos = 30;
        while (pos < 2048 * MiB)
        {
            var chunk = src.Read(off + pos, (int)MiB);
            if (chunk.Length < 22) return null;
            var start = 0;
            while (true)
            {
                var i = chunk.AsSpan(start).IndexOf(Eocd);
                if (i < 0) break;
                i += start;
                var eocd = pos + i;
                var e = src.Read(off + eocd, 22);
                if (e.Length == 22)
                {
                    long cdSize = U32(e, 12), cdOff = U32(e, 16);
                    var clen = U16(e, 20);
                    var ok = cdOff == 0xFFFFFFFF || (cdOff + cdSize == eocd && src.Read(off + cdOff, 4).AsSpan().SequenceEqual(CentralDir));
                    if (ok) return (eocd + 22 + clen, ZipKind(src, off, firstName, h, cdOff, cdSize), true);
                }
                start = i + 1;
            }
            pos += chunk.Length - 3;
        }
        return null;
    }

    // ================================================================ OLE (doc/xls/ppt)
    private static (long, string, bool)? ParseOle(IByteSource src, long off)
    {
        var h = src.Read(off, 512);
        if (h.Length < 512 || h[0x1C] != 0xFE || h[0x1D] != 0xFF) return null;
        var shift = U16(h, 0x1E);
        if (shift is not (9 or 12)) return null;
        var ssz = 1 << shift;
        var numFat = U32(h, 0x2C);
        var firstDir = U32(h, 0x30);
        var difatNext = U32(h, 0x44);
        var numDifat = U32(h, 0x48);
        if (numFat is 0 or > 50000) return null;
        var fat = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var v = U32(h, 0x4C + 4 * i);
            if (v < 0xFFFFFFFA) fat.Add(v);
        }
        var per = ssz / 4;
        var guard = 0;
        while (difatNext < 0xFFFFFFFA && fat.Count < numFat && guard++ <= numDifat)
        {
            var d = src.Read(off + (difatNext + 1L) * ssz, ssz);
            if (d.Length < ssz) break;
            for (var i = 0; i < per - 1; i++)
            {
                var v = U32(d, i * 4);
                if (v < 0xFFFFFFFA) fat.Add(v);
            }
            difatNext = U32(d, (per - 1) * 4);
        }
        long maxUsed = -1;
        for (var idx = 0; idx < Math.Min(fat.Count, numFat); idx++)
        {
            var d = src.Read(off + (fat[idx] + 1L) * ssz, ssz);
            if (d.Length < ssz) break;
            for (var j = per - 1; j >= 0; j--)
            {
                if (U32(d, j * 4) != 0xFFFFFFFF)
                {
                    maxUsed = Math.Max(maxUsed, (long)idx * per + j);
                    break;
                }
            }
        }
        if (maxUsed < 0) return null;
        var dir = src.Read(off + (firstDir + 1L) * ssz, ssz);
        var ext = "ole";
        foreach (var (key, e) in new[] { ("WordDocument", "doc"), ("Workbook", "xls"), ("Book", "xls"), ("PowerPoint Document", "ppt"), ("__substg1.0_", "msg") })
        {
            if (dir.AsSpan().IndexOf(Encoding.Unicode.GetBytes(key)) >= 0)
            {
                ext = e;
                break;
            }
        }
        return ((maxUsed + 2) * ssz, ext, true);
    }

    // ================================================================ MP3
    private static readonly int[][] Bitrates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],   // MPEG1 L1
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],      // MPEG1 L2
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],       // MPEG1 L3
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],      // MPEG2 L1
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],           // MPEG2 L2/L3
    ];

    public static int MpegFrameLength(byte[] h, int o = 0)
    {
        if (h.Length < o + 4 || h[o] != 0xFF || (h[o + 1] & 0xE0) != 0xE0) return 0;
        var ver = (h[o + 1] >> 3) & 3;
        var layer = (h[o + 1] >> 1) & 3;
        var bri = h[o + 2] >> 4;
        var sri = (h[o + 2] >> 2) & 3;
        var pad = (h[o + 2] >> 1) & 1;
        if (ver == 1 || layer == 0 || bri is 0 or 15 || sri == 3) return 0;
        var mpeg1 = ver == 3;
        var table = mpeg1 ? layer switch { 3 => 0, 2 => 1, _ => 2 } : layer == 3 ? 3 : 4;
        var br = Bitrates[table][bri] * 1000;
        var sr = (ver switch { 3 => new[] { 44100, 48000, 32000 }, 2 => [22050, 24000, 16000], _ => [11025, 12000, 8000] })[sri];
        if (layer == 3) return (12 * br / sr + pad) * 4;
        if (layer == 1 && !mpeg1) return 72 * br / sr + pad;
        return 144 * br / sr + pad;
    }

    private static (long, string, bool)? ParseMp3(IByteSource src, long off)
    {
        var h = src.Read(off, 10);
        if (h.Length < 10 || h[3] is not (2 or 3 or 4) || h[6..10].Any(b => (b & 0x80) != 0)) return null;
        long size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
        var pos = 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);
        var window = src.Read(off + pos, 64 * 1024);
        var i = 0;
        while (true)
        {
            i = Array.IndexOf(window, (byte)0xFF, i);
            if (i < 0 || i + 4 > window.Length) return null;
            var fl = MpegFrameLength(window, i);
            if (fl > 0 && MpegFrameLength(src.Read(off + pos + i + fl, 4)) > 0) break;
            i++;
        }
        pos += i;
        var frames = 0;
        while (pos < 200 * MiB)
        {
            var fh = src.Read(off + pos, 4);
            var fl = MpegFrameLength(fh);
            if (fl > 0)
            {
                pos += fl;
                frames++;
                continue;
            }
            if (fh.Length >= 3 && fh[0] == 'T' && fh[1] == 'A' && fh[2] == 'G') pos += 128;
            break;
        }
        return frames >= 10 ? (pos, "mp3", true) : null;
    }

    // ================================================================ RIFF / MP4 / OGG / 7z
    private static (long, string, bool)? ParseRiff(IByteSource src, long off)
    {
        var h = src.Read(off, 20);
        if (h.Length < 20) return null;
        var size = U32(h, 4) + 8L;
        var ext = Encoding.ASCII.GetString(h, 8, 4) switch { "WAVE" => "wav", "AVI " => "avi", "WEBP" => "webp", _ => null };
        if (ext == null || size < 20 || h[12..16].Any(c => c is < 32 or >= 127)) return null;
        return (size, ext, true);
    }

    private static readonly HashSet<string> Boxes = ["ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "pnot", "pdin",
        "moof", "mfra", "sidx", "styp", "prft", "emsg", "PICT", "udta", "junk", "beam"];

    private static (long, string, bool)? ParseIsoBmff(IByteSource src, long off)
    {
        var h = src.Read(off, 16);
        if (h.Length < 16 || Encoding.ASCII.GetString(h, 4, 4) != "ftyp") return null;
        var size0 = U32(h, 0, false);
        if (size0 is < 8 or > 4096) return null;
        var brand = Encoding.ASCII.GetString(h, 8, 4);
        var ext = brand switch
        {
            "qt  " => "mov",
            "heic" or "heix" or "hevc" or "mif1" or "msf1" or "heim" or "heis" => "heic",
            "avif" or "avis" => "avif",
            "M4A " or "M4B " or "M4P " => "m4a",
            "crx " => "cr3",
            _ when brand.StartsWith("3g") => "3gp",
            _ => "mp4",
        };
        long pos = 0;
        var seen = new HashSet<string>();
        while (pos < 16L * 1024 * MiB)
        {
            var bh = src.Read(off + pos, 16);
            if (bh.Length < 8) break;
            long sz = U32(bh, 0, false);
            var type = Encoding.ASCII.GetString(bh, 4, 4);
            if (!Boxes.Contains(type)) break;
            if (sz == 1)
            {
                if (bh.Length < 16) break;
                sz = (long)U64(bh, 8, false);
            }
            else if (sz == 0) break;
            if (sz < 8) break;
            seen.Add(type);
            pos += sz;
        }
        if (seen.Count < 2) return null;
        return (pos, ext, seen.Contains("moov") || seen.Contains("meta"));
    }

    private static (long, string, bool)? ParseOgg(IByteSource src, long off)
    {
        var first = Encoding.Latin1.GetString(src.Read(off, 128));
        var ext = first.Contains("OpusHead") ? "opus" : first.Contains("theora") ? "ogv" : "ogg";
        long pos = 0;
        var pages = 0;
        while (pos < 500 * MiB)
        {
            var h = src.Read(off + pos, 27);
            if (h.Length < 27 || h[0] != 'O' || h[1] != 'g' || h[2] != 'g' || h[3] != 'S') break;
            var nseg = h[26];
            var segs = src.Read(off + pos + 27, nseg);
            if (segs.Length < nseg) break;
            pos += 27 + nseg + segs.Sum(b => (long)b);
            pages++;
            if ((h[5] & 4) != 0) return (pos, ext, true);
        }
        return pages > 2 ? (pos, ext, false) : null;
    }

    private static (long, string, bool)? Parse7z(IByteSource src, long off)
    {
        var h = src.Read(off, 32);
        if (h.Length < 32) return null;
        var nextOff = U64(h, 12);
        var nextSize = U64(h, 20);
        if (nextSize == 0 || nextSize > 256 * (ulong)MiB) return null;
        var end = 32 + nextOff + nextSize;
        if (end > 64UL * 1024 * (ulong)MiB) return null;
        return ((long)end, "7z", true);
    }

    // ================================================================ scansione
    public static void Scan(IByteSource src, ISet<string> categories, long minSize, Action<FoundFile> onFound,
        Action<long> progress, CancellationToken ct)
    {
        var sigs = Signatures.Where(s => categories.Contains(s.Category) ||
                                         (categories.Count > 0 && (s.Parse == ParseRiff || s.Parse == ParseZip || s.Parse == ParseIsoBmff))).ToArray();
        if (sigs.Length == 0) return;
        long pos = 0, skipUntil = 0;
        while (pos < src.Size && !ct.IsCancellationRequested)
        {
            var data = src.Read(pos, ChunkSize);
            var n = data.Length / SectorSize;
            if (n == 0) break;
            for (var s = 0; s < n; s++)
            {
                var o = pos + (long)s * SectorSize;
                if (o < skipUntil) continue;
                var baseIdx = s * SectorSize;
                foreach (var sig in sigs)
                {
                    if (!data.AsSpan(baseIdx + sig.Offset, sig.Magic.Length).SequenceEqual(sig.Magic)) continue;
                    (long size, string ext, bool complete)? res;
                    try { res = sig.Parse(src, o); }
                    catch { res = null; }
                    if (res is not { } r || r.size <= 0 || o + r.size > src.Size) continue;
                    var cat = ExtCategory.GetValueOrDefault(r.ext, sig.Category);
                    if (!categories.Contains(cat) || r.size < minSize) continue;
                    onFound(new FoundFile(o, r.size, r.ext, cat, r.complete));
                    if (r.complete && !InexactEnd.Contains(r.ext)) skipUntil = o + r.size;
                    break;
                }
            }
            pos += (long)n * SectorSize;
            if (skipUntil > pos) pos = skipUntil / SectorSize * SectorSize;
            progress(pos);
        }
    }

    /// <summary>Estrae la JPEG incorporata più grande da un file RAW (anteprima di CR2, NEF, ARW...).</summary>
    public static byte[]? LargestEmbeddedJpeg(byte[] data)
    {
        var src = new MemorySource(data);
        (int pos, long size)? best = null;
        var span = data.AsSpan();
        var i = 0;
        while (true)
        {
            var k = span[i..].IndexOf(new byte[] { 0xFF, 0xD8, 0xFF });
            if (k < 0) break;
            i += k;
            if (ParseJpeg(src, i) is { } r && (best == null || r.size > best.Value.size)) best = (i, r.size);
            i += 3;
        }
        return best is { } b ? data.AsSpan(b.pos, (int)b.size).ToArray() : null;
    }
}
