using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwinKnife.Core;

/// <summary>
/// Analisi veloce di un volume NTFS leggendo direttamente la Master File Table, come WinDirStat 2 e WizTree:
/// invece di aprire una cartella alla volta si legge di seguito la tabella che descrive tutti i file del disco.
/// Un disco intero richiede pochi secondi. Serve l'accesso in lettura al volume, quindi i privilegi di amministratore.
/// </summary>
public static unsafe class MftScan
{
    private const int RootRecord = 5;
    private const int ChunkSize = 4 << 20;

    /// <summary>True se la cartella sta su un volume NTFS locale e l'app ha i privilegi per leggerlo direttamente.</summary>
    public static bool CanUse(string path) => IsNtfsVolume(path) && RawSource.IsAdmin();

    public static bool IsNtfsVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not { Length: 3 } || root[1] != ':') return false; // niente percorsi di rete
            var d = new DriveInfo(root);
            return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable && d.DriveFormat == "NTFS";
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);

    /// <summary>Dati di un record della MFT che servono per l'albero.</summary>
    internal sealed class Table(int n)
    {
        public readonly byte[] Flags = new byte[n];        // 1 = in uso, 2 = cartella
        public readonly ushort[] Seq = new ushort[n];
        public readonly uint[] Parent = new uint[n];
        public readonly ushort[] ParentSeq = new ushort[n];
        public readonly string?[] Name = new string?[n];
        public readonly long[] Size = new long[n];
        public readonly long[] Modified = new long[n];
        public readonly long[] Allocated = new long[n];     // spazio realmente occupato sul disco
        public readonly bool[] Cloud = new bool[n];         // segnaposto di OneDrive & co. (tag di reparse "cloud")
        public readonly ConcurrentBag<(uint rec, uint parent, ushort parentSeq, string name)> Links = new(); // nomi in più (hard link)
    }

    public static ScanResult Scan(string path, bool skipCloud, Action<string> progress, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(full)!;
        var (h, bulk) = Open(volume);
        using (h)
        using (bulk)
            return ScanHandle(h, bulk, full, volume, skipCloud, progress, ct);
    }

    /// <summary>Apre il volume due volte: con cache (letture sparse) e senza (lettura in blocco della MFT).</summary>
    internal static (SafeFileHandle h, SafeFileHandle bulk) Open(string volume)
    {
        var h = CreateFile($@"\\.\{volume[..2]}", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            throw err == 5 ? new UnauthorizedAccessException(L.T("Accesso negato: servono i privilegi di amministratore."))
                           : new IOException(L.T($"Impossibile aprire l'unità {volume[..2]} (errore {err})."));
        }
        // per la lettura in blocco: senza cache di sistema (FILE_FLAG_NO_BUFFERING), più veloce e non svuota la cache
        var bulk = CreateFile($@"\\.\{volume[..2]}", 0x80000000, 3, IntPtr.Zero, 3, 0x20000000, IntPtr.Zero);
        if (bulk.IsInvalid)
        {
            bulk.Dispose();
            bulk = CreateFile($@"\\.\{volume[..2]}", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }
        return (h, bulk);
    }

    /// <summary>Analizza un volume già aperto (o un'immagine disco NTFS); <paramref name="volume"/> è la radice da mostrare, es. "C:\".</summary>
    private static ScanResult ScanHandle(SafeFileHandle h, SafeFileHandle bulk, string full, string volume, bool skipCloud, Action<string> progress, CancellationToken ct)
    {
        var t = ReadTable(h, bulk, progress, ct);
        progress(L.T("Costruzione dell'albero…"));
        return Build(t, full, volume, skipCloud, ct);
    }

    /// <summary>Legge tutta la MFT: per ogni record nome, cartella padre, dimensione e data.</summary>
    internal static Table ReadTable(SafeFileHandle h, SafeFileHandle bulk, Action<string> progress, CancellationToken ct)
    {
        var boot = ReadAt(h, 0, 512);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ") throw new InvalidDataException(L.T("Il volume non è NTFS."));
        int bps = U16(boot, 11), spc = boot[13];
        if (spc > 128) spc = 1 << (256 - spc);
        long cluster = (long)bps * spc;
        var cpr = (sbyte)boot[0x40];
        var recordSize = cpr > 0 ? (int)(cpr * cluster) : 1 << -cpr;
        var mftOffset = (long)U64(boot, 0x30) * cluster;

        // record 0 = $MFT: dice dove si trova (anche frammentata) e quanto è grande la tabella
        var rec0 = ReadAt(h, mftOffset, Align(recordSize, bps));
        if (!Fixup(rec0.AsSpan(0, recordSize), bps)) throw new InvalidDataException(L.T("MFT non leggibile."));
        var (mftRuns, mftSize) = MftRuns(h, rec0.AsSpan(0, recordSize).ToArray(), cluster, recordSize, bps);
        var count = (int)Math.Min(mftSize / recordSize, int.MaxValue - 1);
        var t = new Table(count);

        progress(L.T("Lettura della tabella dei file (MFT)…"));
        var jobs = new List<(long offset, int len, long firstRecord)>();
        long vcnRecords = 0;
        foreach (var (lcn, len) in mftRuns)
        {
            var bytes = len * cluster;
            if (lcn >= 0)
                for (long done = 0; done < bytes; done += ChunkSize)
                {
                    var firstRec = vcnRecords + done / recordSize;
                    if (firstRec >= count) break;
                    jobs.Add((lcn * cluster + done, (int)Math.Min(ChunkSize, bytes - done), firstRec));
                }
            vcnRecords += bytes / recordSize;
        }
        // più letture contemporanee (gli SSD NVMe rendono al massimo con molte richieste in coda);
        // ogni blocco viene analizzato dallo stesso thread che lo ha letto
        long doneJobs = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = ct }, job =>
        {
            var (offset, len, first) = job;
            var ptr = NativeMemory.AlignedAlloc((nuint)len, 4096); // senza cache di sistema servono buffer allineati
            try
            {
                var buf = new Span<byte>(ptr, len);
                if (!ReadFully(bulk, buf, offset)) buf.Clear(); // settori illeggibili: i record restano vuoti
                var n = (int)Math.Min(len / recordSize, count - first);
                for (var i = 0; i < n; i++) ParseRecord(buf.Slice(i * recordSize, recordSize), (int)(first + i), bps, t);
            }
            finally
            {
                NativeMemory.AlignedFree(ptr);
            }
            var d = Interlocked.Increment(ref doneJobs);
            if (sw.ElapsedMilliseconds > 250)
                lock (sw)
                {
                    if (sw.ElapsedMilliseconds <= 250) return;
                    sw.Restart();
                    progress(L.T($"Lettura della tabella dei file (MFT): {d * 100 / jobs.Count} %"));
                }
        });

        return t;
    }

    private static int Align(int n, int to) => (n + to - 1) / to * to;

    private static byte[] ReadAt(SafeFileHandle h, long offset, int count)
    {
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = RandomAccess.Read(h, buf.AsSpan(got), offset + got);
            if (n <= 0) break;
            got += n;
        }
        return buf;
    }

    private static bool ReadFully(SafeFileHandle h, Span<byte> buf, long offset)
    {
        try
        {
            var got = 0;
            while (got < buf.Length)
            {
                var n = RandomAccess.Read(h, buf[got..], offset + got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Applica l'"update sequence array" (gli ultimi 2 byte di ogni settore sono salvati a parte).</summary>
    private static bool Fixup(Span<byte> r, int sector)
    {
        if (r.Length < 48 || r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E') return false;
        int usaOff = U16(r, 4), usaCount = U16(r, 6);
        if (usaCount < 2 || usaOff + usaCount * 2 > r.Length) return false;
        var stride = r.Length / (usaCount - 1); // sempre 512 byte, anche sui dischi con settori da 4K
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

    private static List<(long lcn, long len)> Runs(ReadOnlySpan<byte> r, int pos, int end)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        while (pos < end && r[pos] != 0)
        {
            int lenSize = r[pos] & 0xF, offSize = r[pos] >> 4;
            if (lenSize == 0 || lenSize > 8 || offSize > 8 || pos + 1 + lenSize + offSize > end) break;
            long len = 0;
            for (var k = 0; k < lenSize; k++) len |= (long)r[pos + 1 + k] << (8 * k);
            if (offSize == 0) runs.Add((-1, len));
            else
            {
                long off = 0;
                for (var k = 0; k < offSize; k++) off |= (long)r[pos + 1 + lenSize + k] << (8 * k);
                if ((r[pos + lenSize + offSize] & 0x80) != 0) off -= 1L << (8 * offSize);
                lcn += off;
                runs.Add((lcn, len));
            }
            pos += 1 + lenSize + offSize;
        }
        return runs;
    }

    /// <summary>Posizione sul disco della MFT; se è molto frammentata l'elenco continua in altri record ($ATTRIBUTE_LIST).</summary>
    private static (List<(long lcn, long len)> runs, long size) MftRuns(SafeFileHandle h, byte[] rec0, long cluster, int recordSize, int sector)
    {
        var segments = new List<(long vcn, List<(long, long)> runs)>();
        long size = 0;
        List<(long vcn, uint rec)> others = [];
        void Collect(ReadOnlySpan<byte> r, bool isBase)
        {
            int pos = U16(r, 20);
            var used = (int)Math.Min(U32(r, 24), (uint)r.Length);
            while (pos + 16 <= used)
            {
                var type = U32(r, pos);
                if (type == 0xFFFFFFFF) break;
                var len = (int)U32(r, pos + 4);
                if (len < 16 || pos + len > used) break;
                var nonResident = r[pos + 8] != 0;
                if (type == 0x80 && r[pos + 9] == 0 && nonResident)
                {
                    var vcn = (long)U64(r, pos + 16);
                    if (vcn == 0) size = (long)U64(r, pos + 48);
                    segments.Add((vcn, Runs(r, pos + U16(r, pos + 32), pos + len)));
                }
                else if (type == 0x20 && isBase)
                {
                    // $ATTRIBUTE_LIST: voci di $DATA che stanno in altri record
                    byte[] list;
                    if (!nonResident)
                    {
                        var vo = pos + U16(r, pos + 20);
                        list = r.Slice(vo, (int)U32(r, pos + 16)).ToArray();
                    }
                    else
                    {
                        var ms = new MemoryStream();
                        foreach (var (lcn, n) in Runs(r, pos + U16(r, pos + 32), pos + len))
                            if (lcn >= 0) ms.Write(ReadAt(h, lcn * cluster, (int)(n * cluster)));
                        list = ms.ToArray().AsSpan(0, (int)Math.Min(ms.Length, (long)U64(r, pos + 48))).ToArray();
                    }
                    for (var o = 0; o + 26 <= list.Length;)
                    {
                        var et = U32(list, o);
                        var el = U16(list, o + 4);
                        if (el == 0) break;
                        var rec = (uint)(U64(list, o + 16) & 0xFFFFFFFFFFFF);
                        if (et == 0x80 && list[o + 6] == 0 && rec != 0) others.Add(((long)U64(list, o + 8), rec));
                        o += el;
                    }
                }
                pos += len;
            }
        }
        Collect(rec0, true);
        foreach (var (_, rec) in others.DistinctBy(o => o.rec))
        {
            // i record di estensione stanno nei primi frammenti, già noti
            var known = segments.OrderBy(s => s.vcn).SelectMany(s => s.runs).ToList();
            var byteOff = (long)rec * recordSize;
            long vcnStart = 0;
            foreach (var (lcn, len) in known)
            {
                if (byteOff < (vcnStart + len) * cluster)
                {
                    if (lcn < 0) break;
                    var abs = lcn * cluster + (byteOff - vcnStart * cluster);
                    var aligned = abs / sector * sector;
                    var raw = ReadAt(h, aligned, Align((int)(abs - aligned) + recordSize, sector));
                    var r = raw.AsSpan((int)(abs - aligned), recordSize);
                    if (Fixup(r, sector)) Collect(r, false);
                    break;
                }
                vcnStart += len;
            }
        }
        var all = segments.OrderBy(s => s.vcn).SelectMany(s => s.runs).ToList();
        if (all.Count == 0 || size <= 0) throw new InvalidDataException(L.T("MFT non leggibile."));
        return (all, size);
    }

    private static void ParseRecord(Span<byte> r, int recNo, int sector, Table t)
    {
        if (r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E') return;
        var flags = U16(r, 22);
        if ((flags & 1) == 0) return; // record libero o di un file eliminato
        if (!Fixup(r, sector)) return;
        var baseRec = U64(r, 32) & 0xFFFFFFFFFFFF;
        var owner = baseRec != 0 ? (int)baseRec : recNo; // i record di estensione aggiungono dati al record base
        if ((uint)owner >= (uint)t.Flags.Length) return;
        if (baseRec == 0)
        {
            t.Flags[recNo] = (byte)(1 | ((flags & 2) != 0 ? 2 : 0));
            t.Seq[recNo] = U16(r, 16);
        }

        int pos = U16(r, 20);
        var used = (int)Math.Min(U32(r, 24), (uint)r.Length);
        string? best = null;
        uint bestParent = 0;
        ushort bestParentSeq = 0;
        List<(uint, ushort, string, int)>? names = null;
        while (pos + 16 <= used)
        {
            var type = U32(r, pos);
            if (type == 0xFFFFFFFF) break;
            var len = (int)U32(r, pos + 4);
            if (len < 16 || pos + len > used) break;
            var nonResident = r[pos + 8] != 0;
            if (type == 0x10 && !nonResident) // $STANDARD_INFORMATION
            {
                var vo = pos + U16(r, pos + 20);
                if (vo + 16 <= used) t.Modified[owner] = (long)U64(r, vo + 8);
            }
            else if (type == 0x30 && !nonResident) // $FILE_NAME
            {
                var vo = pos + U16(r, pos + 20);
                int n = r[vo + 64], ns = r[vo + 65];
                if (vo + 66 + n * 2 <= used)
                {
                    var parentRef = U64(r, vo);
                    var name = Encoding.Unicode.GetString(r.Slice(vo + 66, n * 2));
                    names ??= new List<(uint, ushort, string, int)>(2);
                    names.Add(((uint)(parentRef & 0xFFFFFFFFFFFF), (ushort)(parentRef >> 48), name, ns));
                }
            }
            else if (type == 0x80 && r[pos + 9] == 0) // $DATA senza nome = contenuto del file
            {
                if (!nonResident) t.Size[owner] = U32(r, pos + 16);
                else if (U64(r, pos + 16) == 0)
                {
                    t.Size[owner] = (long)U64(r, pos + 48);
                    // file sparsi o compressi: il totale effettivamente allocato è più avanti
                    var compressed = (U16(r, pos + 12) & 0x8001) != 0 && U16(r, pos + 32) >= 72;
                    t.Allocated[owner] = (long)U64(r, pos + (compressed ? 64 : 40));
                }
            }
            else if (type == 0xC0 && !nonResident) // $REPARSE_POINT
            {
                var vo = pos + U16(r, pos + 20);
                if (vo + 4 <= used) t.Cloud[owner] = (U32(r, vo) & 0xFFFF0FFF) == 0x9000001A; // IO_REPARSE_TAG_CLOUD_*
            }
            pos += len;
        }
        if (names == null) return;
        // un nome per cartella: preferisco il nome lungo (Win32/POSIX) a quello DOS 8.3
        foreach (var (parent, pseq, name, ns) in names)
        {
            if (ns == 2 && names.Any(o => o.Item1 == parent && o.Item4 != 2)) continue;
            if (best == null && baseRec == 0)
            {
                best = name;
                bestParent = parent;
                bestParentSeq = pseq;
            }
            else t.Links.Add(((uint)owner, parent, pseq, name));
        }
        if (best != null)
        {
            t.Name[recNo] = best;
            t.Parent[recNo] = bestParent;
            t.ParentSeq[recNo] = bestParentSeq;
        }
    }

    private static ScanResult Build(Table t, string full, string volume, bool skipCloud, CancellationToken ct)
    {
        var n = t.Flags.Length;
        var nodes = new DiskNode?[n];
        var root = new DiskNode(volume, null, true);
        if (RootRecord < n)
        {
            nodes[RootRecord] = root;
            root.Modified = DateTime.FromFileTime(Math.Max(0, t.Modified[RootRecord]));
        }
        bool ValidParent(uint p, ushort seq) => p < n && (t.Flags[p] & 3) == 3 && (seq == 0 || t.Seq[p] == seq);
        // prima tutte le cartelle, poi i collegamenti padre-figlio
        for (var i = 0; i < n; i++)
            if ((t.Flags[i] & 3) == 3 && i != RootRecord && t.Name[i] != null)
                nodes[i] = new DiskNode(t.Name[i]!, null, true) { Modified = FileTime(t.Modified[i]) };
        var cloud = 0;
        // una sola stringa per estensione (milioni di file, poche centinaia di estensioni)
        var exts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();
        DiskNode? MakeFile(int i, string name)
        {
            var size = t.Size[i];
            if (skipCloud && t.Cloud[i] && t.Allocated[i] < size)
            {
                // solo-cloud (o scaricato in parte): conta quanto occupa davvero sul PC
                size = t.Allocated[i];
                cloud++;
            }
            var dot = name.LastIndexOf('.');
            var x = "";
            if (dot > 0 && !exts.TryGetValue(name.AsSpan(dot), out x!))
            {
                x = name[dot..].ToLowerInvariant();
                exts.Dictionary[x] = x;
            }
            return new DiskNode(name, null, false) { Size = size, Modified = FileTime(t.Modified[i]), Ext = x };
        }
        void Attach(DiskNode child, DiskNode parent)
        {
            child.Parent = parent;
            parent.Children!.Add(child);
        }
        for (var i = 0; i < n; i++)
        {
            if (i % 200000 == 0) ct.ThrowIfCancellationRequested();
            if ((t.Flags[i] & 1) == 0 || i == RootRecord || t.Name[i] == null || !ValidParent(t.Parent[i], t.ParentSeq[i])) continue;
            var parent = nodes[t.Parent[i]];
            if (parent == null) continue;
            if ((t.Flags[i] & 2) != 0)
            {
                if (nodes[i] != null && nodes[i] != parent) Attach(nodes[i]!, parent);
            }
            else Attach(MakeFile(i, t.Name[i]!)!, parent);
        }
        foreach (var (rec, parentRec, pseq, name) in t.Links)
        {
            if ((t.Flags[rec] & 3) != 1 || !ValidParent(parentRec, pseq) || nodes[parentRec] is not { } parent) continue;
            Attach(MakeFile((int)rec, name)!, parent);
        }

        // cartella scelta dentro al volume
        var start = root;
        foreach (var part in full[volume.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            start = start.Children!.FirstOrDefault(c => c.IsDir && c.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DirectoryNotFoundException(full);
        }
        if (start != root)
        {
            start.Parent = null;
            start.Name = full.TrimEnd('\\');
        }
        var ext = DiskScan.Summarize(start);
        return new ScanResult(start, ext, 0, cloud, false);
    }

    private static DateTime FileTime(long ft)
    {
        try { return ft > 0 ? DateTime.FromFileTime(ft) : default; }
        catch { return default; }
    }
}
