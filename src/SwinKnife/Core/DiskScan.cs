using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace SwinKnife.Core;

public sealed class DiskNode(string name, DiskNode? parent, bool isDir)
{
    public string Name { get; internal set; } = name;
    public DiskNode? Parent { get; internal set; } = parent;
    public List<DiskNode>? Children { get; } = isDir ? new List<DiskNode>() : null;
    public bool IsDir { get; } = isDir;
    public long Size { get; internal set; }
    public long Files { get; internal set; }
    public DateTime Modified { get; internal set; }
    public string Ext { get; internal set; } = "";

    public string FullPath
    {
        get
        {
            var parts = new Stack<string>();
            for (var n = this; n != null; n = n.Parent) parts.Push(n.Name);
            return Path.Combine(parts.ToArray());
        }
    }
}

public sealed record ScanResult(DiskNode Root, Dictionary<string, (long size, long count)> Extensions, int Errors, int CloudOnly, bool Cancelled);

public static class DiskScan
{
    private const int ReparsePoint = 0x400;
    private const int CloudOnlyMask = 0x400000 | 0x40000 | 0x1000; // RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN | OFFLINE

    private readonly record struct Entry(string Name, bool IsDir, long Length, int Attributes, DateTime Modified);

    /// <summary>
    /// Scansione cartella per cartella con più thread in parallelo (funziona su qualsiasi disco, senza privilegi).
    /// Ogni cartella è letta da un solo thread, che è anche l'unico a modificarne l'elenco dei figli.
    /// </summary>
    public static ScanResult Scan(string rootPath, bool skipCloud, Action<string> progress, CancellationToken ct)
    {
        var root = new DiskNode(Path.GetFullPath(rootPath), null, true);
        try { root.Modified = Directory.GetLastWriteTime(rootPath); } catch { }
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var queue = new ConcurrentStack<(DiskNode node, string path)>();
        queue.Push((root, root.Name));
        long files = 0, total = 0;
        int errors = 0, cloud = 0, pending = 1;
        var current = root.Name;
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref pending) > 0 && !ct.IsCancellationRequested)
            {
                if (!queue.TryPop(out var job))
                {
                    spin.SpinOnce();
                    continue;
                }
                spin.Reset();
                var (node, path) = job;
                current = path;
                try
                {
                    var en = new FileSystemEnumerable<Entry>(path,
                        (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, (int)e.Attributes, e.LastWriteTimeUtc.LocalDateTime), opts);
                    long localFiles = 0, localSize = 0;
                    foreach (var e in en)
                    {
                        if ((e.Attributes & ReparsePoint) != 0 && (e.Attributes & CloudOnlyMask) == 0) continue; // junction/link: evito doppi conteggi
                        if (e.IsDir)
                        {
                            var child = new DiskNode(e.Name, node, true) { Modified = e.Modified };
                            node.Children!.Add(child);
                            Interlocked.Increment(ref pending);
                            queue.Push((child, Path.Combine(path, e.Name)));
                        }
                        else
                        {
                            var size = e.Length;
                            if (skipCloud && (e.Attributes & CloudOnlyMask) != 0)
                            {
                                size = 0;
                                Interlocked.Increment(ref cloud);
                            }
                            var dot = e.Name.LastIndexOf('.');
                            var x = dot > 0 ? string.Intern(e.Name[dot..].ToLowerInvariant()) : "";
                            node.Children!.Add(new DiskNode(e.Name, node, false) { Size = size, Modified = e.Modified, Ext = x });
                            localFiles++;
                            localSize += size;
                        }
                    }
                    Interlocked.Add(ref files, localFiles);
                    Interlocked.Add(ref total, localSize);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
                Interlocked.Decrement(ref pending);
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        while (!threads.All(t => t.Join(250)))
            progress(L.T($"{Util.Number(Interlocked.Read(ref files))} file · {Util.HumanSize(Interlocked.Read(ref total))} · {current}"));
        return new ScanResult(root, Summarize(root), errors, cloud, ct.IsCancellationRequested);
    }

    /// <summary>Calcola dimensioni e numero di file delle cartelle, ordina i figli e restituisce le statistiche per estensione.</summary>
    public static Dictionary<string, (long size, long count)> Summarize(DiskNode root)
    {
        var ext = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<DiskNode>();
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            dirs.Add(d);
            foreach (var c in d.Children!)
            {
                if (c.IsDir) stack.Push(c);
                else ext[c.Ext] = ext.TryGetValue(c.Ext, out var v) ? (v.Item1 + c.Size, v.Item2 + 1) : (c.Size, 1);
            }
        }
        // ogni cartella compare prima dei suoi figli: al contrario si sommano dal basso verso l'alto
        for (var i = dirs.Count - 1; i >= 0; i--)
        {
            var d = dirs[i];
            long size = 0, count = 0;
            foreach (var c in d.Children!)
            {
                size += c.Size;
                count += c.IsDir ? c.Files : 1;
            }
            d.Size = size;
            d.Files = count;
        }
        Parallel.ForEach(dirs, d => d.Children!.Sort((a, b) => b.Size.CompareTo(a.Size)));
        return ext;
    }

    public static void Remove(DiskNode node)
    {
        var parent = node.Parent;
        if (parent == null) return;
        parent.Children!.Remove(node);
        var files = node.IsDir ? node.Files : 1;
        for (var p = parent; p != null; p = p.Parent)
        {
            p.Size -= node.Size;
            p.Files -= files;
        }
        node.Parent = null;
    }

    // ------------------------------------------------------------------ cestino
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Sposta un file o una cartella nel Cestino di Windows.</summary>
    public static void SendToRecycleBin(string path) => SendToRecycleBin([path]);

    /// <summary>Sposta più file nel Cestino con un'unica operazione.</summary>
    public static void SendToRecycleBin(IEnumerable<string> paths)
    {
        var path = string.Join('\0', paths);
        if (path.Length == 0) return;
        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var r = SHFileOperation(ref op);
        if (r != 0 || op.fAnyOperationsAborted != 0) throw new IOException(L.T($"Impossibile spostare nel Cestino (codice {r})."));
    }

    // ------------------------------------------------------------------ treemap
    public static List<(double x, double y, double w, double h)> Squarify(IReadOnlyList<double> sizes, double x, double y, double w, double h)
    {
        var n = sizes.Count;
        var rects = new List<(double, double, double, double)>(n);
        var total = sizes.Sum();
        if (total <= 0 || w <= 0 || h <= 0)
        {
            for (var k = 0; k < n; k++) rects.Add((x, y, 0, 0));
            return rects;
        }
        var scale = w * h / total;
        var areas = sizes.Select(s => s * scale).ToArray();
        var i = 0;
        while (i < n)
        {
            var shortSide = Math.Min(w, h);
            if (shortSide <= 0)
            {
                for (; i < n; i++) rects.Add((x, y, 0, 0));
                break;
            }
            var s = areas[i];
            var rmax = areas[i];
            var worst = s > 0 ? Math.Max(shortSide * shortSide * rmax / (s * s), s * s / (shortSide * shortSide * areas[i])) : 1e9;
            var j = i + 1;
            while (j < n)
            {
                var s2 = s + areas[j];
                var rmin = areas[j];
                var nw = rmin > 0 ? Math.Max(shortSide * shortSide * rmax / (s2 * s2), s2 * s2 / (shortSide * shortSide * rmin)) : 1e9;
                if (nw > worst) break;
                s = s2;
                worst = nw;
                j++;
            }
            if (w >= h)
            {
                var thick = s / h;
                var yy = y;
                for (var k = i; k < j; k++)
                {
                    var hh = areas[k] / thick;
                    rects.Add((x, yy, thick, hh));
                    yy += hh;
                }
                x += thick;
                w -= thick;
            }
            else
            {
                var thick = s / w;
                var xx = x;
                for (var k = i; k < j; k++)
                {
                    var ww = areas[k] / thick;
                    rects.Add((xx, y, ww, thick));
                    xx += ww;
                }
                y += thick;
                h -= thick;
            }
            i = j;
        }
        return rects;
    }

    public static readonly (byte r, byte g, byte b)[] Palette =
    [
        (66, 133, 244), (234, 67, 53), (52, 168, 83), (251, 188, 5), (171, 71, 188), (0, 172, 193),
        (255, 112, 67), (158, 157, 36), (92, 107, 192), (240, 98, 146), (0, 137, 123), (141, 110, 99),
    ];
    public static readonly (byte r, byte g, byte b) OtherColor = (150, 150, 150);
    private static readonly (byte r, byte g, byte b) DirColor = (80, 80, 84);

    public static Dictionary<string, int> ColorIndex(Dictionary<string, (long size, long count)> ext) =>
        ext.OrderByDescending(kv => kv.Value.size).Take(Palette.Length).Select((kv, i) => (kv.Key, i))
            .ToDictionary(x => x.Key, x => x.i, StringComparer.OrdinalIgnoreCase);

    public sealed class Treemap
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Pixels { get; init; }          // BGRA
        public required int[] Ids { get; init; }              // indice in Nodes per ogni pixel
        public required short[] ExtIds { get; init; }         // indice colore estensione (-1 = altro)
        public required List<DiskNode> Nodes { get; init; }
        public required Dictionary<DiskNode, (double x, double y, double w, double h)> Rects { get; init; }
        public required DiskNode Root { get; init; }
    }

    /// <summary>Treemap "a cuscino" (algoritmo di van Wijk/van de Wetering, come WinDirStat).</summary>
    public static Treemap? Render(DiskNode root, int W, int H, Dictionary<string, int> colors, CancellationToken ct)
    {
        const double h0 = 0.38, factor = 0.91, ambient = 0.15;
        var lx = -1 / Math.Sqrt(102);
        var ly = -1 / Math.Sqrt(102);
        var lz = 10 / Math.Sqrt(102);
        var n = W * H;
        var s1x = new float[n];
        var s2x = new float[n];
        var s1y = new float[n];
        var s2y = new float[n];
        var col = new int[n];
        var ids = new int[n];
        var extIds = new short[n];
        Array.Fill(ids, -1);
        Array.Fill(extIds, (short)-1);
        var nodes = new List<DiskNode>();
        var rects = new Dictionary<DiskNode, (double, double, double, double)>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(DiskNode node, double x, double y, double w, double h, double a, double b, double c, double d, double ht)>();
        stack.Push((root, 0, 0, W, H, 0, 0, 0, 0, h0));
        var count = 0;
        while (stack.Count > 0)
        {
            if (++count % 4000 == 0 && ct.IsCancellationRequested) return null;
            var (node, x, y, w, h, a, b, c, d, ht) = stack.Pop();
            int px0 = (int)Math.Round(x), px1 = (int)Math.Round(x + w), py0 = (int)Math.Round(y), py1 = (int)Math.Round(y + h);
            if (px1 <= px0 || py1 <= py0) continue;
            rects[node] = (x, y, w, h);
            // creste del cuscino su x e y
            var na = a + 4 * ht * (2 * x + w) / w;
            var nb = b - 4 * ht / w;
            var nc = c + 4 * ht * (2 * y + h) / h;
            var nd = d - 4 * ht / h;
            var idx = nodes.Count;
            nodes.Add(node);
            int color;
            short ext = -1;
            if (node.IsDir) color = Pack(DirColor);
            else if (colors.TryGetValue(node.Ext, out var ci))
            {
                color = Pack(Palette[ci]);
                ext = (short)ci;
            }
            else color = Pack(OtherColor);
            for (var yy = py0; yy < py1; yy++)
            {
                var row = yy * W;
                for (var xx = px0; xx < px1; xx++)
                {
                    var p = row + xx;
                    s1x[p] = (float)na;
                    s2x[p] = (float)nb;
                    s1y[p] = (float)nc;
                    s2y[p] = (float)nd;
                    col[p] = color;
                    ids[p] = idx;
                    extIds[p] = ext;
                }
            }
            if (node.IsDir && node.Children!.Count > 0 && (px1 - px0) * (py1 - py0) >= 2)
            {
                var kids = node.Children.Where(k => k.Size > 0).ToList();
                var rs = Squarify(kids.Select(k => (double)k.Size).ToList(), x, y, w, h);
                for (var k = 0; k < kids.Count; k++)
                {
                    var r = rs[k];
                    if (r.w > 0 && r.h > 0) stack.Push((kids[k], r.x, r.y, r.w, r.h, na, nb, nc, nd, ht * factor));
                }
            }
        }
        var px = new byte[n * 4];
        Parallel.For(0, H, yy =>
        {
            var fy = yy + 0.5;
            for (var xx = 0; xx < W; xx++)
            {
                var p = yy * W + xx;
                var fx = xx + 0.5;
                var nx = -(2 * s2x[p] * fx + s1x[p]);
                var ny = -(2 * s2y[p] * fy + s1y[p]);
                var cosa = (nx * lx + ny * ly + lz) / Math.Sqrt(nx * nx + ny * ny + 1);
                var intensity = (ambient + Math.Max(cosa, 0) * (1 - ambient)) * 1.25;
                var cc = col[p];
                px[p * 4] = (byte)Math.Min(255, (cc & 0xFF) * intensity);
                px[p * 4 + 1] = (byte)Math.Min(255, ((cc >> 8) & 0xFF) * intensity);
                px[p * 4 + 2] = (byte)Math.Min(255, ((cc >> 16) & 0xFF) * intensity);
                px[p * 4 + 3] = 255;
            }
        });
        return new Treemap { Width = W, Height = H, Pixels = px, Ids = ids, ExtIds = extIds, Nodes = nodes, Rects = rects, Root = root };
    }

    private static int Pack((byte r, byte g, byte b) c) => (c.r << 16) | (c.g << 8) | c.b;
}
