using System.Buffers.Binary;
using System.Text;

namespace SwinKnife.Core;

/// <summary>File eliminato ritrovato leggendo le strutture del file system (con nome e cartella originali).</summary>
public sealed class DeletedFile
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public List<(long offset, long length)> Extents { get; } = new();
    public byte[]? Resident { get; init; }
    public double OverwrittenFraction { get; set; }

    public string State => OverwrittenFraction <= 0 ? L.T("Buono") : OverwrittenFraction >= 0.999 ? L.T("Sovrascritto") : L.T("In parte sovrascritto");
    public string Ext => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string FullPath => Folder.Length == 0 ? Name : Folder + "\\" + Name;
}

/// <summary>
/// Ricerca dei file eliminati di recente su FAT12/16/32, exFAT e NTFS (sola lettura).
/// Quando un file viene cancellato la sua voce resta nella directory (FAT/exFAT) o nella MFT (NTFS)
/// finché non viene riutilizzata: da lì si recuperano nome, cartella, dimensione e posizione dei dati.
/// </summary>
public static class FsRecovery
{
    public static string? Detect(IByteSource src)
    {
        var bs = src.Read(0, 512);
        if (bs.Length < 512 || bs[510] != 0x55 || bs[511] != 0xAA) return null;
        var oem = Encoding.ASCII.GetString(bs, 3, 8);
        if (oem == "NTFS    ") return "NTFS";
        if (oem == "EXFAT   ") return "exFAT";
        if (Encoding.ASCII.GetString(bs, 0x52, 5) == "FAT32") return "FAT32";
        if (Encoding.ASCII.GetString(bs, 0x36, 4) == "FAT1") return "FAT";
        // FAT senza etichetta: controllo la coerenza del BPB
        var bps = U16(bs, 11);
        if (bps is 512 or 1024 or 2048 or 4096 && bs[13] != 0 && U16(bs, 14) != 0 && bs[16] is 1 or 2)
            return U16(bs, 22) == 0 ? "FAT32" : "FAT";
        return null;
    }

    public static List<DeletedFile> Scan(IByteSource src, Action<string> progress, CancellationToken ct) => Detect(src) switch
    {
        "NTFS" => new Ntfs(src).Scan(progress, ct),
        "exFAT" => new ExFat(src).Scan(progress, ct),
        "FAT32" or "FAT" => new Fat(src).Scan(progress, ct),
        _ => throw new NotSupportedException(L.T("File system non riconosciuto: usa la scansione profonda.")),
    };

    /// <summary>Scrive il contenuto del file recuperato.</summary>
    public static void Extract(IByteSource src, DeletedFile f, Stream dst, CancellationToken ct = default)
    {
        if (f.Resident != null)
        {
            dst.Write(f.Resident, 0, (int)Math.Min(f.Resident.Length, f.Size));
            return;
        }
        var remaining = f.Size;
        foreach (var (offset, length) in f.Extents)
        {
            var take = Math.Min(length, remaining);
            long pos = 0;
            while (pos < take)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(4 << 20, take - pos);
                var chunk = offset < 0 ? new byte[n] : src.Read(offset + pos, n); // offset < 0: area sparsa (zeri)
                if (chunk.Length == 0) return;
                dst.Write(chunk);
                pos += chunk.Length;
            }
            remaining -= take;
            if (remaining <= 0) break;
        }
    }

    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));

    private static DateTime? DosDate(uint ts)
    {
        var date = ts >> 16;
        var time = ts & 0xFFFF;
        try
        {
            return new DateTime((int)(1980 + (date >> 9)), (int)((date >> 5) & 15), (int)(date & 31),
                (int)(time >> 11), (int)((time >> 5) & 63), (int)((time & 31) * 2));
        }
        catch
        {
            return null;
        }
    }

    private static bool PlausibleName(string n) =>
        n.Length > 0 && n.Length < 256 && n.All(c => c >= 32 && c != 0xFFFF && "\\/:*?\"<>|".IndexOf(c) < 0);

    // ====================================================================== FAT12/16/32
    private sealed class Fat
    {
        private readonly IByteSource _src;
        private readonly int _bits;
        private readonly long _fatOffset;
        private readonly long _dataOffset;
        private readonly int _clusterSize;
        private readonly long _clusterCount;
        private readonly uint _rootCluster;
        private readonly long _rootOffset;
        private readonly int _rootSize;
        private readonly byte[]? _fat;

        public Fat(IByteSource src)
        {
            _src = src;
            var bs = src.Read(0, 512);
            int bps = U16(bs, 11), spc = bs[13], reserved = U16(bs, 14), fats = bs[16], rootEntries = U16(bs, 17);
            long total = U16(bs, 19) != 0 ? U16(bs, 19) : U32(bs, 32);
            long fatSectors = U16(bs, 22) != 0 ? U16(bs, 22) : U32(bs, 36);
            _clusterSize = bps * spc;
            _fatOffset = (long)reserved * bps;
            var rootSectors = (rootEntries * 32 + bps - 1) / bps;
            _rootOffset = _fatOffset + fats * fatSectors * bps;
            _rootSize = rootEntries * 32;
            _dataOffset = _rootOffset + (long)rootSectors * bps;
            _clusterCount = (total - (_dataOffset / bps)) / spc;
            _bits = U16(bs, 22) == 0 ? 32 : _clusterCount < 4085 ? 12 : 16;
            _rootCluster = _bits == 32 ? U32(bs, 44) : 0;
            var fatBytes = fatSectors * bps;
            if (fatBytes <= 256L << 20) _fat = src.Read(_fatOffset, (int)fatBytes);
        }

        private uint Next(uint c)
        {
            long off = _bits switch { 12 => c + c / 2, 16 => c * 2L, _ => c * 4L };
            byte[] b;
            int o;
            if (_fat != null)
            {
                if (off + 4 > _fat.Length) return 0x0FFFFFFF;
                b = _fat;
                o = (int)off;
            }
            else
            {
                b = _src.Read(_fatOffset + off, 4);
                o = 0;
                if (b.Length < 4) return 0x0FFFFFFF;
            }
            return _bits switch
            {
                12 => (c & 1) == 0 ? (uint)(U16(b, o) & 0xFFF) : (uint)(U16(b, o) >> 4),
                16 => U16(b, o),
                _ => U32(b, o) & 0x0FFFFFFF,
            };
        }

        private bool IsEnd(uint v) => v >= (_bits switch { 12 => 0xFF8u, 16 => 0xFFF8u, _ => 0x0FFFFFF8u }) || v < 2;
        private long Offset(uint c) => _dataOffset + (c - 2L) * _clusterSize;
        private bool Valid(uint c) => c >= 2 && c < _clusterCount + 2;

        private byte[] ReadChain(uint start, int maxClusters)
        {
            var ms = new MemoryStream();
            var seen = new HashSet<uint>();
            for (var c = start; Valid(c) && seen.Add(c) && seen.Count <= maxClusters; c = Next(c))
            {
                ms.Write(_src.Read(Offset(c), _clusterSize));
                if (IsEnd(Next(c))) break;
            }
            return ms.ToArray();
        }

        public List<DeletedFile> Scan(Action<string> progress, CancellationToken ct)
        {
            var result = new List<DeletedFile>();
            var queue = new Queue<(byte[] data, string path, bool deleted)>();
            var visited = new HashSet<uint>();
            var root = _bits == 32 ? ReadChain(_rootCluster, 65536) : _src.Read(_rootOffset, _rootSize);
            queue.Enqueue((root, "", false));
            var dirs = 0;
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (data, path, parentDeleted) = queue.Dequeue();
                if (++dirs % 50 == 0) progress(L.T($"Cartelle esaminate: {dirs} · file eliminati trovati: {result.Count}"));
                var lfn = new List<string>();
                for (var i = 0; i + 32 <= data.Length; i += 32)
                {
                    var b0 = data[i];
                    if (b0 == 0x00) break;
                    var attr = data[i + 11];
                    if (attr == 0x0F)
                    {
                        var chars = new StringBuilder();
                        foreach (var (o, n) in new[] { (1, 5), (14, 6), (28, 2) })
                            for (var k = 0; k < n; k++)
                            {
                                var ch = (char)U16(data, i + o + k * 2);
                                if (ch == 0 || ch == 0xFFFF) goto done;
                                chars.Append(ch);
                            }
                        done:
                        lfn.Add(chars.ToString());
                        continue;
                    }
                    var deleted = b0 == 0xE5 || parentDeleted;
                    if ((attr & 0x08) != 0 || (attr & 0xC0) != 0)
                    {
                        lfn.Clear();
                        continue;
                    }
                    var shortName = ShortName(data, i);
                    lfn.Reverse();
                    var longName = string.Concat(lfn);
                    lfn.Clear();
                    var name = PlausibleName(longName) ? longName : shortName;
                    if (name is "." or ".." || !PlausibleName(name))
                    {
                        if (parentDeleted && !PlausibleName(name)) break; // dati non più di directory
                        continue;
                    }
                    uint first = U16(data, i + 26);
                    if (_bits == 32) first |= (uint)U16(data, i + 20) << 16;
                    var size = U32(data, i + 28);
                    var isDir = (attr & 0x10) != 0;
                    if (isDir)
                    {
                        if (!Valid(first) || !visited.Add(first)) continue;
                        var sub = deleted ? _src.Read(Offset(first), _clusterSize * 4) : ReadChain(first, 65536);
                        queue.Enqueue((sub, path.Length == 0 ? name : path + "\\" + name, deleted));
                        continue;
                    }
                    if (!deleted || size == 0 || !Valid(first)) continue;
                    var clusters = (size + _clusterSize - 1) / _clusterSize;
                    var f = new DeletedFile { Name = name, Folder = path, Size = size, Modified = DosDate(((uint)U16(data, i + 24) << 16) | U16(data, i + 22)) };
                    // dopo la cancellazione la catena FAT è azzerata: i dati si leggono come contigui
                    f.Extents.Add((Offset(first), clusters * (long)_clusterSize));
                    var used = 0;
                    for (var k = 0; k < clusters && k < 100000; k++)
                    {
                        var c = (uint)(first + k);
                        if (!Valid(c) || Next(c) != 0) used++;
                    }
                    f.OverwrittenFraction = clusters > 0 ? used / (double)Math.Min(clusters, 100000) : 0;
                    result.Add(f);
                }
            }
            return result;
        }

        private static string ShortName(byte[] d, int i)
        {
            var raw = Encoding.Latin1.GetString(d, i, 11);
            var first = d[i] == 0xE5 ? "_" : d[i] == 0x05 ? "å" : raw[..1];
            var name = (first + raw[1..8]).TrimEnd();
            var ext = raw[8..11].TrimEnd();
            if ((d[i + 12] & 0x08) != 0) name = name.ToLowerInvariant();
            if ((d[i + 12] & 0x10) != 0) ext = ext.ToLowerInvariant();
            return ext.Length > 0 ? $"{name}.{ext}" : name;
        }
    }

    // ====================================================================== exFAT
    private sealed class ExFat
    {
        private readonly IByteSource _src;
        private readonly long _fatOffset;
        private readonly long _heapOffset;
        private readonly int _clusterSize;
        private readonly uint _clusterCount;
        private readonly uint _rootCluster;
        private byte[]? _bitmap;

        public ExFat(IByteSource src)
        {
            _src = src;
            var bs = src.Read(0, 512);
            var bps = 1 << bs[0x6C];
            _clusterSize = bps << bs[0x6D];
            _fatOffset = (long)U32(bs, 0x50) * bps;
            _heapOffset = (long)U32(bs, 0x58) * bps;
            _clusterCount = U32(bs, 0x5C);
            _rootCluster = U32(bs, 0x60);
        }

        private long Offset(uint c) => _heapOffset + (c - 2L) * _clusterSize;
        private bool Valid(uint c) => c >= 2 && c < _clusterCount + 2;
        private uint Next(uint c) => U32(_src.Read(_fatOffset + c * 4L, 4), 0);

        private bool Allocated(uint c)
        {
            if (_bitmap == null) return false;
            var bit = c - 2;
            return bit / 8 < _bitmap.Length && (_bitmap[bit / 8] & (1 << (int)(bit % 8))) != 0;
        }

        private byte[] Read(uint first, ulong length, bool noFatChain, int maxClusters = 65536)
        {
            var clusters = (int)Math.Min(maxClusters, (long)((length + (ulong)_clusterSize - 1) / (ulong)_clusterSize));
            if (noFatChain) return _src.Read(Offset(first), (int)Math.Min((long)clusters * _clusterSize, 256L << 20));
            var ms = new MemoryStream();
            var seen = new HashSet<uint>();
            for (var c = first; Valid(c) && seen.Add(c) && seen.Count <= clusters; c = Next(c))
                ms.Write(_src.Read(Offset(c), _clusterSize));
            return ms.ToArray();
        }

        public List<DeletedFile> Scan(Action<string> progress, CancellationToken ct)
        {
            var result = new List<DeletedFile>();
            var queue = new Queue<(byte[] data, string path, bool deleted)>();
            var root = Read(_rootCluster, 64UL << 20, false);
            queue.Enqueue((root, "", false));
            var visited = new HashSet<uint> { _rootCluster };
            var dirs = 0;
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (data, path, parentDeleted) = queue.Dequeue();
                if (++dirs % 50 == 0) progress(L.T($"Cartelle esaminate: {dirs} · file eliminati trovati: {result.Count}"));
                for (var i = 0; i + 32 <= data.Length; i += 32)
                {
                    var type = data[i];
                    if (type == 0x00) break;
                    var code = type & 0x7F;
                    if (type == 0x81 && path.Length == 0 && _bitmap == null) // bitmap di allocazione
                    {
                        _bitmap = _src.Read(Offset(U32(data, i + 20)), (int)Math.Min(U64(data, i + 24), 512UL << 20));
                        continue;
                    }
                    if (code != 0x05) continue;
                    var secondary = data[i + 1];
                    if (secondary < 2 || i + (secondary + 1) * 32 > data.Length) continue;
                    var deleted = (type & 0x80) == 0 || parentDeleted;
                    var attrs = U16(data, i + 4);
                    var modified = DosDate(U32(data, i + 12));
                    var s = i + 32;
                    if ((data[s] & 0x7F) != 0x40) continue;
                    var flags = data[s + 1];
                    var nameLen = data[s + 3];
                    var length = U64(data, s + 24);
                    var first = U32(data, s + 20);
                    var name = new StringBuilder();
                    for (var k = 2; k <= secondary; k++)
                    {
                        var e = i + k * 32;
                        if ((data[e] & 0x7F) != 0x41) break;
                        for (var c = 0; c < 15 && name.Length < nameLen; c++) name.Append((char)U16(data, e + 2 + c * 2));
                    }
                    i += secondary * 32;
                    var fileName = name.ToString();
                    if (!PlausibleName(fileName)) continue;
                    var noChain = (flags & 2) != 0;
                    if ((attrs & 0x10) != 0)
                    {
                        if (!Valid(first) || !visited.Add(first)) continue;
                        var sub = Read(first, Math.Max(length, (ulong)_clusterSize), noChain || deleted, deleted ? 64 : 65536);
                        queue.Enqueue((sub, path.Length == 0 ? fileName : path + "\\" + fileName, deleted));
                        continue;
                    }
                    if (!deleted || length == 0 || !Valid(first)) continue;
                    var f = new DeletedFile { Name = fileName, Folder = path, Size = (long)length, Modified = modified };
                    var clusters = (long)((length + (ulong)_clusterSize - 1) / (ulong)_clusterSize);
                    // file contiguo (caso più comune) oppure catena FAT ancora leggibile
                    var chain = new List<uint>();
                    if (!noChain)
                    {
                        var seen = new HashSet<uint>();
                        for (var c = first; Valid(c) && seen.Add(c) && chain.Count < clusters; c = Next(c)) chain.Add(c);
                    }
                    if (chain.Count == clusters && clusters > 1 && chain.Zip(chain.Skip(1)).Any(p => p.Second != p.First + 1))
                        foreach (var c in chain) f.Extents.Add((Offset(c), _clusterSize));
                    else
                        f.Extents.Add((Offset(first), clusters * _clusterSize));
                    var used = 0;
                    for (var k = 0; k < Math.Min(clusters, 100000); k++)
                        if (Allocated((uint)(first + k))) used++;
                    f.OverwrittenFraction = used / (double)Math.Max(1, Math.Min(clusters, 100000));
                    result.Add(f);
                }
            }
            return result;
        }
    }

    // ====================================================================== NTFS
    private sealed class Ntfs
    {
        private readonly IByteSource _src;
        private readonly long _clusterSize;
        private readonly int _recordSize;
        private readonly long _mftOffset;

        private sealed record Entry(string Name, ulong Parent, bool IsDir);

        public Ntfs(IByteSource src)
        {
            _src = src;
            var bs = src.Read(0, 512);
            int bps = U16(bs, 11), spc = bs[13];
            if (spc > 128) spc = 1 << (256 - spc);
            _clusterSize = (long)bps * spc;
            _mftOffset = (long)U64(bs, 0x30) * _clusterSize;
            var cpr = (sbyte)bs[0x40];
            _recordSize = cpr > 0 ? (int)(cpr * _clusterSize) : 1 << -cpr;
        }

        private bool Fixup(byte[] r)
        {
            if (r.Length < 48 || r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E') return false;
            int usaOff = U16(r, 4), usaCount = U16(r, 6);
            if (usaCount < 2 || usaOff + usaCount * 2 > r.Length) return false;
            var stride = r.Length / (usaCount - 1);
            var usn = U16(r, usaOff);
            for (var k = 1; k < usaCount; k++)
            {
                var pos = k * stride - 2;
                if (pos + 2 > r.Length || U16(r, pos) != usn) return false;
                r[pos] = r[usaOff + k * 2];
                r[pos + 1] = r[usaOff + k * 2 + 1];
            }
            return true;
        }

        private static List<(long lcn, long len)> Runs(byte[] r, int pos, int end)
        {
            var runs = new List<(long, long)>();
            long lcn = 0;
            while (pos < end && r[pos] != 0)
            {
                int lenSize = r[pos] & 0xF, offSize = r[pos] >> 4;
                if (lenSize == 0 || lenSize > 8 || offSize > 8 || pos + 1 + lenSize + offSize > end) break;
                long len = 0;
                for (var k = 0; k < lenSize; k++) len |= (long)r[pos + 1 + k] << (8 * k);
                if (offSize == 0)
                {
                    runs.Add((-1, len)); // sparso
                }
                else
                {
                    long off = 0;
                    for (var k = 0; k < offSize; k++) off |= (long)r[pos + 1 + lenSize + k] << (8 * k);
                    if ((r[pos + lenSize + offSize] & 0x80) != 0) off -= 1L << (8 * offSize); // segno
                    lcn += off;
                    runs.Add((lcn, len));
                }
                pos += 1 + lenSize + offSize;
            }
            return runs;
        }

        private sealed class Parsed
        {
            public string? Name;
            public int NameSpace = -1;
            public ulong Parent;
            public DateTime? Modified;
            public long Size;
            public byte[]? Resident;
            public List<(long lcn, long len)>? Runs;
        }

        private static Parsed? Parse(byte[] r)
        {
            var p = new Parsed();
            int pos = U16(r, 20);
            var used = (int)Math.Min(U32(r, 24), (uint)r.Length);
            while (pos + 16 <= used)
            {
                var type = U32(r, pos);
                if (type == 0xFFFFFFFF) break;
                var len = (int)U32(r, pos + 4);
                if (len < 16 || pos + len > used) break;
                var nonResident = r[pos + 8] != 0;
                var nameLen = r[pos + 9];
                if (type == 0x30 && !nonResident)
                {
                    var vo = pos + U16(r, pos + 20);
                    var ns = r[vo + 65];
                    var n = r[vo + 64];
                    // preferisco il nome Win32 (1) o Win32+DOS (3) a quello DOS 8.3 (2)
                    var score = ns == 2 ? 0 : ns == 0 ? 1 : 2;
                    if (score > p.NameSpace && vo + 66 + n * 2 <= r.Length)
                    {
                        p.NameSpace = score;
                        p.Name = Encoding.Unicode.GetString(r, vo + 66, n * 2);
                        p.Parent = U64(r, vo) & 0xFFFFFFFFFFFF;
                        try { p.Modified = DateTime.FromFileTime((long)U64(r, vo + 16)); } catch { }
                    }
                }
                else if (type == 0x80 && nameLen == 0)
                {
                    if (!nonResident)
                    {
                        var vl = (int)U32(r, pos + 16);
                        var vo = pos + U16(r, pos + 20);
                        if (vo + vl <= r.Length)
                        {
                            p.Resident = r.AsSpan(vo, vl).ToArray();
                            p.Size = vl;
                        }
                    }
                    else if (U64(r, pos + 16) == 0) // primo segmento (VCN 0)
                    {
                        p.Size = (long)U64(r, pos + 48);
                        p.Runs = Runs(r, pos + U16(r, pos + 32), pos + len);
                    }
                }
                pos += len;
            }
            return p;
        }

        public List<DeletedFile> Scan(Action<string> progress, CancellationToken ct)
        {
            var rec0 = _src.Read(_mftOffset, _recordSize);
            if (!Fixup(rec0)) throw new InvalidDataException(L.T("MFT non leggibile."));
            var mftRuns = Parse(rec0)?.Runs ?? throw new InvalidDataException(L.T("MFT non leggibile."));
            var entries = new Dictionary<ulong, Entry>();
            var deleted = new List<(ulong rec, Parsed p)>();
            byte[]? bitmap = null;
            ulong recNo = 0;
            var total = mftRuns.Sum(r => r.len) * _clusterSize / _recordSize;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (lcn, len) in mftRuns)
            {
                var bytes = len * _clusterSize;
                for (long done = 0; done < bytes; done += 4 << 20)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = lcn < 0 ? [] : _src.Read(lcn * _clusterSize + done, (int)Math.Min(4 << 20, bytes - done));
                    for (var o = 0; o + _recordSize <= chunk.Length; o += _recordSize, recNo++)
                    {
                        var r = chunk.AsSpan(o, _recordSize).ToArray();
                        if (!Fixup(r) || U64(r, 32) != 0) continue; // record vuoto o di estensione
                        var flags = U16(r, 22);
                        var p = Parse(r);
                        if (p?.Name == null) continue;
                        var isDir = (flags & 2) != 0;
                        if (isDir || (flags & 1) != 0) entries[recNo] = new Entry(p.Name, p.Parent, isDir);
                        if (recNo == 6 && p.Runs != null) // $Bitmap: cluster occupati del volume
                        {
                            var ms = new MemoryStream();
                            foreach (var (bl, bn) in p.Runs.Where(x => x.lcn >= 0)) ms.Write(_src.Read(bl * _clusterSize, (int)Math.Min(bn * _clusterSize, 256L << 20)));
                            bitmap = ms.ToArray();
                        }
                        if ((flags & 1) == 0 && !isDir && p.Size > 0) deleted.Add((recNo, p));
                    }
                    if (sw.ElapsedMilliseconds > 300)
                    {
                        sw.Restart();
                        progress(L.T($"Record MFT esaminati: {Util.Number((long)recNo)} di {Util.Number(total)} · eliminati: {deleted.Count}"));
                    }
                }
            }
            var result = new List<DeletedFile>();
            foreach (var (_, p) in deleted)
            {
                var f = new DeletedFile { Name = p.Name!, Folder = PathOf(entries, p.Parent), Size = p.Size, Modified = p.Modified, Resident = p.Resident };
                if (p.Runs != null)
                {
                    long total2 = 0, used = 0;
                    foreach (var (lcn, len) in p.Runs)
                    {
                        f.Extents.Add((lcn < 0 ? -1 : lcn * _clusterSize, len * _clusterSize));
                        if (lcn < 0 || bitmap == null) continue;
                        for (long c = lcn; c < lcn + Math.Min(len, 100000); c++)
                        {
                            total2++;
                            if (c / 8 < bitmap.Length && (bitmap[c / 8] & (1 << (int)(c % 8))) != 0) used++;
                        }
                    }
                    f.OverwrittenFraction = total2 > 0 ? used / (double)total2 : 0;
                }
                else if (p.Resident == null) continue;
                result.Add(f);
            }
            return result;
        }

        private static string PathOf(Dictionary<ulong, Entry> entries, ulong parent)
        {
            var parts = new List<string>();
            var seen = new HashSet<ulong>();
            while (parent != 5 && seen.Add(parent) && parts.Count < 64)
            {
                if (!entries.TryGetValue(parent, out var e))
                {
                    parts.Add(L.T("(cartella sconosciuta)"));
                    break;
                }
                parts.Add(e.Name);
                parent = e.Parent;
            }
            parts.Reverse();
            return string.Join("\\", parts);
        }
    }
}
