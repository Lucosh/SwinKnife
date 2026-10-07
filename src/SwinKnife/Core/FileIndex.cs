using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SwinKnife.Core;

/// <summary>
/// Indice dei nomi di tutti i file di un'unità, per la ricerca istantanea (come "Everything").
/// Da amministratore su NTFS si legge la MFT e si resta aggiornati col registro delle modifiche (USN journal);
/// altrimenti si scansionano le cartelle e si seguono i cambiamenti con FileSystemWatcher.
/// </summary>
public sealed class VolumeIndex : IDisposable
{
    public const byte InUse = 1, Dir = 2;
    public string Root { get; }
    public bool Fast { get; private set; }               // costruito dalla MFT
    public int Count => _count;                          // posizioni nella tabella (comprese quelle vuote)
    /// <summary>File e cartelle presenti davvero.</summary>
    public int Entries
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _count; i++) if (Flags[i] != 0 && Name[i] != null) n++;
            return n;
        }
    }
    public DateTime Built { get; private set; }

    internal string?[] Name = [];
    internal int[] Parent = [];
    internal ushort[] Seq = [];
    internal long[] Size = [];                           // -1 = da rileggere (file modificato)
    internal long[] Modified = [];                       // FILETIME
    internal byte[] Flags = [];
    private int _count;
    private int _rootIndex;
    private readonly object _lock = new();
    private readonly Dictionary<int, string> _overlayPath = new();          // voci aggiunte da FileSystemWatcher: percorso completo
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private SafeFileHandle? _volume;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _liveCts;
    private ulong _journalId;
    private long _nextUsn;

    public event Action? Changed;

    private VolumeIndex(string root) => Root = root;

    // ------------------------------------------------------------------ costruzione
    public static VolumeIndex Build(string root, Action<string> progress, CancellationToken ct)
    {
        var v = new VolumeIndex(root);
        if (MftScan.CanUse(root))
        {
            try
            {
                v.BuildFromMft(progress, ct);
                return v;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppInfo.Log(ex, "Indice veloce (MFT) di " + root);
            }
        }
        v.BuildFromScan(progress, ct);
        return v;
    }

    private void BuildFromMft(Action<string> progress, CancellationToken ct)
    {
        var (h, bulk) = MftScan.Open(Root);
        using (bulk)
        {
            QueryJournal(h);
            var t = MftScan.ReadTable(h, bulk, progress, ct);
            var n = t.Flags.Length;
            Ensure(n + 4096); // margine per i file creati dopo (numeri di record nuovi)
            for (var i = 0; i < n; i++)
            {
                Name[i] = t.Name[i];
                Parent[i] = (int)t.Parent[i];
                Seq[i] = t.Seq[i];
                Size[i] = t.Size[i];
                Modified[i] = t.Modified[i];
                Flags[i] = t.Name[i] != null ? t.Flags[i] : (byte)0;
            }
            _count = n;
            _rootIndex = 5; // record 5 = cartella radice
            Name[5] = null;
            // voci con una cartella padre non valida (eliminata e riusata) non si mostrano
            for (var i = 0; i < n; i++)
                if (Flags[i] != 0 && i != _rootIndex && (Parent[i] >= n || (Flags[Parent[i]] & Dir) == 0 || (t.ParentSeq[i] != 0 && Seq[Parent[i]] != t.ParentSeq[i])))
                    Flags[i] = 0;
        }
        _volume = h;
        Fast = true;
        Built = DateTime.Now;
        StartJournal();
    }

    private void BuildFromScan(Action<string> progress, CancellationToken ct)
    {
        var scan = DiskScan.Scan(Root, false, progress, ct);
        ct.ThrowIfCancellationRequested();
        Ensure((int)Math.Min(int.MaxValue - 1, scan.Root.Files * 13 / 10 + 4096));
        _rootIndex = Add(null, -1, 0, 0, Dir);
        var stack = new Stack<(DiskNode node, int index)>();
        stack.Push((scan.Root, _rootIndex));
        while (stack.Count > 0)
        {
            var (node, idx) = stack.Pop();
            foreach (var c in node.Children!)
            {
                var k = Add(c.Name, idx, c.IsDir ? 0 : c.Size, c.Modified == default ? 0 : c.Modified.ToFileTime(), c.IsDir ? Dir : (byte)0);
                if (c.IsDir) stack.Push((c, k));
            }
        }
        Built = DateTime.Now;
        StartWatcher();
    }

    private void Ensure(int capacity)
    {
        if (capacity <= Name.Length) return;
        Array.Resize(ref Name, capacity);
        Array.Resize(ref Parent, capacity);
        Array.Resize(ref Seq, capacity);
        Array.Resize(ref Size, capacity);
        Array.Resize(ref Modified, capacity);
        Array.Resize(ref Flags, capacity);
    }

    private int Add(string? name, int parent, long size, long modified, byte flags)
    {
        if (_count >= Name.Length) Ensure(Math.Max(1024, Name.Length * 3 / 2));
        var k = _count++;
        Name[k] = name;
        Parent[k] = parent;
        Size[k] = size;
        Modified[k] = modified;
        Flags[k] = (byte)(flags | InUse);
        return k;
    }

    // ------------------------------------------------------------------ lettura
    public bool IsDir(int i) => (Flags[i] & Dir) != 0;

    public string PathOf(int i)
    {
        if (_overlayPath.TryGetValue(i, out var p)) return p;
        var parts = new List<string>();
        var guard = 0;
        for (var k = i; k >= 0 && k != _rootIndex && guard++ < 256; k = Parent[k])
        {
            if (Name[k] is not { } n) break;
            parts.Add(n);
            if (Parent[k] == k) break;
        }
        parts.Reverse();
        return Root + string.Join('\\', parts);
    }

    public string FolderOf(int i) => Path.GetDirectoryName(PathOf(i)) ?? Root;

    public bool IsDeleted(string path) => _deleted.Count > 0 && _deleted.Contains(path);

    /// <summary>Dimensione aggiornata (se il file è cambiato dopo l'indicizzazione la rilegge dal disco).</summary>
    public long SizeOf(int i)
    {
        if (IsDir(i)) return -1;
        if (Size[i] >= 0) return Size[i];
        try
        {
            var fi = new FileInfo(PathOf(i));
            Size[i] = fi.Exists ? fi.Length : 0;
            Modified[i] = fi.Exists ? fi.LastWriteTime.ToFileTime() : Modified[i];
        }
        catch { Size[i] = 0; }
        return Size[i];
    }

    public DateTime ModifiedOf(int i)
    {
        try { return Modified[i] > 0 ? DateTime.FromFileTime(Modified[i]) : default; }
        catch { return default; }
    }

    // ------------------------------------------------------------------ aggiornamento dal registro USN (NTFS, amministratore)
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4, FSCTL_READ_USN_JOURNAL = 0x000900bb;

    private void QueryJournal(SafeFileHandle h)
    {
        var data = new byte[64];
        if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, null, 0, data, data.Length, out _, IntPtr.Zero)) return; // registro disattivato
        _journalId = BinaryPrimitives.ReadUInt64LittleEndian(data);
        _nextUsn = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
    }

    private void StartJournal()
    {
        if (_volume == null || _journalId == 0) return;
        _liveCts = new CancellationTokenSource();
        var ct = _liveCts.Token;
        new Thread(() =>
        {
            var input = new byte[40];
            var output = new byte[1 << 16];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var changed = false;
                    while (true)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(input, _nextUsn);
                        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF);  // tutti i motivi
                        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), _journalId);
                        if (!DeviceIoControl(_volume, FSCTL_READ_USN_JOURNAL, input, input.Length, output, output.Length, out var got, IntPtr.Zero) || got <= 8) break;
                        var next = BinaryPrimitives.ReadInt64LittleEndian(output);
                        lock (_lock) changed |= ApplyUsn(output.AsSpan(8, got - 8));
                        if (next == _nextUsn) break;
                        _nextUsn = next;
                    }
                    if (changed) Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    AppInfo.Log(ex, "Registro USN " + Root);
                    return;
                }
                ct.WaitHandle.WaitOne(1500);
            }
        }) { IsBackground = true, Name = "USN " + Root }.Start();
    }

    private bool ApplyUsn(ReadOnlySpan<byte> buf)
    {
        var changed = false;
        for (var o = 0; o + 60 <= buf.Length;)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]);
            if (len < 60 || o + len > buf.Length) break;
            var r = buf.Slice(o, len);
            o += len;
            if (BinaryPrimitives.ReadUInt16LittleEndian(r[4..]) != 2) continue; // solo USN_RECORD_V2
            var frn = BinaryPrimitives.ReadUInt64LittleEndian(r[8..]);
            var parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
            var reason = BinaryPrimitives.ReadUInt32LittleEndian(r[40..]);
            var attrs = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(r[56..]), nameOff = BinaryPrimitives.ReadUInt16LittleEndian(r[58..]);
            var rec = (int)(frn & 0xFFFFFFFFFFFF);
            if (rec < 0 || nameOff + nameLen > r.Length) continue;
            if (rec >= Name.Length) Ensure(Math.Max(rec + 1024, Name.Length * 5 / 4));
            if (rec >= _count) _count = rec + 1;
            if ((reason & 0x200) != 0) // FILE_DELETE
            {
                Flags[rec] = 0;
                changed = true;
            }
            else if ((reason & (0x100 | 0x2000)) != 0) // FILE_CREATE, RENAME_NEW_NAME
            {
                Name[rec] = Encoding.Unicode.GetString(r.Slice(nameOff, nameLen));
                Parent[rec] = (int)(parentFrn & 0xFFFFFFFFFFFF);
                Seq[rec] = (ushort)(frn >> 48);
                Flags[rec] = (byte)(InUse | ((attrs & 0x10) != 0 ? Dir : 0));
                Size[rec] = (attrs & 0x10) != 0 ? 0 : -1;
                Modified[rec] = BinaryPrimitives.ReadInt64LittleEndian(r[32..]);
                changed = true;
            }
            else if ((reason & 0x7) != 0 && Flags[rec] != 0) // DATA_OVERWRITE/EXTEND/TRUNCATION
            {
                Size[rec] = -1;
                Modified[rec] = BinaryPrimitives.ReadInt64LittleEndian(r[32..]);
            }
        }
        return changed;
    }

    // ------------------------------------------------------------------ aggiornamento con FileSystemWatcher
    private void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, e) => Added(e.FullPath);
            _watcher.Deleted += (_, e) => Removed(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Removed(e.OldFullPath);
                Added(e.FullPath);
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Controllo modifiche " + Root);
        }
    }

    private void Added(string path)
    {
        lock (_lock)
        {
            _deleted.Remove(path);
            var isDir = Directory.Exists(path);
            long size = 0, mod = 0;
            try
            {
                if (!isDir)
                {
                    var fi = new FileInfo(path);
                    size = fi.Length;
                    mod = fi.LastWriteTime.ToFileTime();
                }
            }
            catch { }
            var k = Add(Path.GetFileName(path), -2, size, mod, isDir ? Dir : (byte)0);
            _overlayPath[k] = path;
        }
        Changed?.Invoke();
    }

    private void Removed(string path)
    {
        lock (_lock) _deleted.Add(path);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _liveCts?.Cancel();
        _watcher?.Dispose();
        _volume?.Dispose();
    }

    // ------------------------------------------------------------------ ricerca
    public void Match(SearchQuery q, ConcurrentBag<SearchHit> hits, ref int total, int max, CancellationToken ct)
    {
        var n = _count;
        var local = 0;
        var found = new ConcurrentBag<int>();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, n, 65536), new ParallelOptions { CancellationToken = ct }, range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
            {
                if (Flags[i] == 0 || Name[i] is not { } name) continue;
                if (!q.Matches(name, (Flags[i] & Dir) != 0)) continue;
                if (Interlocked.Increment(ref local) <= max) found.Add(i);
            }
        });
        Interlocked.Add(ref total, local);
        foreach (var i in found) hits.Add(new SearchHit(this, i));
    }
}

public readonly record struct SearchHit(VolumeIndex Volume, int Index);

/// <summary>
/// Testo cercato: parole da trovare tutte nel nome (in qualsiasi ordine), "frase esatta", -parola da escludere,
/// caratteri jolly * e ?, ext:pdf;docx per le estensioni. Più un filtro per tipo.
/// </summary>
public sealed class SearchQuery
{
    public enum Kind { All, Folders, Documents, Images, Audio, Video, Archives, Programs }

    private static readonly Dictionary<Kind, HashSet<string>> Types = new()
    {
        [Kind.Documents] = Set("pdf doc docx odt rtf txt md xls xlsx xlsm ods csv ppt pptx odp epub xps html htm json xml"),
        [Kind.Images] = Set("jpg jpeg png gif bmp webp heic heif avif tif tiff svg ico raw cr2 cr3 nef arw dng psd jxl"),
        [Kind.Audio] = Set("mp3 wav flac aac m4a ogg opus wma aiff mid"),
        [Kind.Video] = Set("mp4 mkv avi mov wmv webm m4v mpg mpeg flv 3gp ts"),
        [Kind.Archives] = Set("zip 7z rar tar gz tgz bz2 xz iso cab"),
        [Kind.Programs] = Set("exe msi bat cmd ps1 lnk appx msix"),
    };

    private static HashSet<string> Set(string s) => new(s.Split(' '), StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _include = new(), _exclude = new();
    private readonly List<Regex> _wild = new();
    private readonly List<(string text, int mode)> _simpleWild = new(); // 1 = finisce con, 2 = inizia con
    private readonly HashSet<string>? _ext;
    private readonly Kind _kind;

    public bool IsEmpty => _include.Count == 0 && _wild.Count == 0 && _simpleWild.Count == 0 && _ext == null && _kind == Kind.All;

    public SearchQuery(string text, Kind kind)
    {
        _kind = kind;
        foreach (Match m in Regex.Matches(text, "-?\"[^\"]*\"|\\S+"))
        {
            var t = m.Value;
            var neg = t.StartsWith('-') && t.Length > 1;
            if (neg) t = t[1..];
            t = t.Trim('"');
            if (t.Length == 0) continue;
            if (t.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                _ext ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in t[4..].Split(';', ',')) if (e.Trim('.', ' ').Length > 0) _ext.Add(e.Trim('.', ' '));
            }
            else if (t.IndexOfAny(['*', '?']) >= 0 && !neg)
            {
                // i casi più comuni senza espressioni regolari: *.pdf, report*, *fattura*
                var core = t.Trim('*');
                if (core.Length > 0 && core.IndexOfAny(['*', '?']) < 0)
                {
                    if (t.StartsWith('*') && t.EndsWith('*')) _include.Add(core);
                    else if (t.StartsWith('*')) _simpleWild.Add((core, 1));
                    else if (t.EndsWith('*')) _simpleWild.Add((core, 2));
                    else _include.Add(core);
                    continue;
                }
                _wild.Add(new Regex("^" + Regex.Escape(t).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }
            else (neg ? _exclude : _include).Add(t);
        }
    }

    public bool Matches(string name, bool isDir)
    {
        if (_kind == Kind.Folders && !isDir) return false;
        foreach (var t in _include) if (name.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
        foreach (var t in _exclude) if (name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if ((_ext != null || (_kind != Kind.All && _kind != Kind.Folders)) && isDir) return false;
        if (_ext != null || Types.ContainsKey(_kind))
        {
            var dot = name.LastIndexOf('.');
            if (dot < 0) return false;
            var ext = name[(dot + 1)..];
            if (_ext != null && !_ext.Contains(ext)) return false;
            if (Types.TryGetValue(_kind, out var set) && !set.Contains(ext)) return false;
        }
        foreach (var (t, mode) in _simpleWild)
            if (mode == 1 ? !name.EndsWith(t, StringComparison.OrdinalIgnoreCase) : !name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var w in _wild) if (!w.IsMatch(name)) return false;
        return true;
    }
}

/// <summary>Gli indici di tutte le unità locali, costruiti alla prima ricerca e tenuti aggiornati.</summary>
public static class FileIndexes
{
    private static readonly List<VolumeIndex> Volumes = new();
    private static Task? _building;
    public static bool Ready { get; private set; }
    public static bool Fast => Volumes.Count > 0 && Volumes.All(v => v.Fast);
    public static event Action? Changed;
    public static IReadOnlyList<VolumeIndex> All => Volumes;
    public static int TotalCount => Volumes.Sum(v => v.Entries);

    public static Task EnsureAsync(Action<string> progress)
    {
        return _building ??= Task.Run(() =>
        {
            var drives = DriveInfo.GetDrives().Where(d =>
            {
                try { return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable; }
                catch { return false; }
            }).ToList();
            foreach (var d in drives)
            {
                try
                {
                    progress(L.T($"Indicizzazione di {d.Name}…"));
                    var v = VolumeIndex.Build(d.Name, m => progress($"{d.Name}  {m}"), CancellationToken.None);
                    v.Changed += () => Changed?.Invoke();
                    lock (Volumes) Volumes.Add(v);
                }
                catch (Exception ex)
                {
                    AppInfo.Log(ex, "Indicizzazione " + d.Name);
                }
            }
            Ready = true;
        });
    }

    /// <summary>Rifà l'indice da capo (es. dopo essere diventati amministratore o aver collegato un'unità).</summary>
    public static Task RebuildAsync(Action<string> progress)
    {
        lock (Volumes)
        {
            foreach (var v in Volumes) v.Dispose();
            Volumes.Clear();
        }
        Ready = false;
        _building = null;
        return EnsureAsync(progress);
    }

    public static (List<SearchHit> hits, int total) Search(SearchQuery q, int max, CancellationToken ct)
    {
        var bag = new ConcurrentBag<SearchHit>();
        var total = 0;
        List<VolumeIndex> vols;
        lock (Volumes) vols = Volumes.ToList();
        foreach (var v in vols) v.Match(q, bag, ref total, max, ct);
        return (bag.ToList(), total);
    }
}
