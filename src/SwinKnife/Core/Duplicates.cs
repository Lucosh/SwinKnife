using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ImageMagick;

namespace SwinKnife.Core;

public sealed class DupFile : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ulong Hash { get; set; }
    public int Group { get; set; }
    public string GroupLabel { get; set; } = "";

    private bool _delete;
    public bool Delete
    {
        get => _delete;
        set
        {
            if (_delete == value) return;
            _delete = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Delete)));
        }
    }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string SizeText => Util.HumanSize(Size);
    public string ModifiedText => Util.Date(Modified);
    public string Resolution => Width > 0 ? $"{Width} × {Height}" : "";
    public long Pixels => (long)Width * Height;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Ricerca di file identici (stesso contenuto) e di foto simili (hash percettivo).</summary>
public static class Duplicates
{
    private const FileAttributes CloudOnly = (FileAttributes)0x400000 | (FileAttributes)0x40000 | FileAttributes.Offline;

    public static readonly HashSet<string> ImageExts =
        [".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".bmp", ".tif", ".tiff", ".gif", ".avif", ".jfif"];

    /// <summary>Elenca i file delle cartelle, saltando quelli solo nel cloud (scaricarli sarebbe lento e costoso).</summary>
    private static List<DupFile> Enumerate(IEnumerable<string> roots, long minSize, Func<string, bool>? filter, Action<string> progress, CancellationToken ct, out int cloudSkipped)
    {
        var result = new List<DupFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Temporary,
        };
        cloudSkipped = 0;
        foreach (var root in roots)
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles("*", options); }
            catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((f.Attributes & CloudOnly) != 0)
                    {
                        cloudSkipped++;
                        continue;
                    }
                    if (f.Length < minSize || (filter != null && !filter(f.Extension))) continue;
                    if (!seen.Add(f.FullName)) continue;
                    result.Add(new DupFile { Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime });
                    if (result.Count % 500 == 0) progress(L.T($"{Util.Number(result.Count)} file trovati…"));
                }
                catch { }
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ file identici
    public static List<List<DupFile>> FindIdentical(IReadOnlyList<string> roots, long minSize, Action<string> progress, CancellationToken ct, out int cloudSkipped)
    {
        progress(L.T("Ricerca dei file…"));
        var files = Enumerate(roots, Math.Max(1, minSize), null, progress, ct, out cloudSkipped);
        // 1) stessa dimensione
        var bySize = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
        var candidates = bySize.Sum(g => g.Count());
        var done = 0;
        var groups = new List<List<DupFile>>();
        var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
        foreach (var sizeGroup in bySize.OrderByDescending(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();
            // 2) stesso inizio e fine (lettura veloce), 3) stesso contenuto completo
            var partial = new Dictionary<DupFile, string>();
            Parallel.ForEach(sizeGroup, po, f =>
            {
                var h = TryHash(f.Path, f.Size, partialOnly: true);
                lock (partial) partial[f] = h;
            });
            foreach (var pg in sizeGroup.Where(f => partial[f].Length > 0).GroupBy(f => partial[f]).Where(g => g.Count() > 1))
            {
                List<DupFile> list;
                if (sizeGroup.Key <= 128 * 1024) list = pg.ToList(); // il primo passaggio ha già letto tutto il file
                else
                {
                    var full = new Dictionary<DupFile, string>();
                    Parallel.ForEach(pg, po, f =>
                    {
                        var h = TryHash(f.Path, f.Size, partialOnly: false);
                        lock (full) full[f] = h;
                    });
                    foreach (var fg in pg.Where(f => full[f].Length > 0).GroupBy(f => full[f]).Where(g => g.Count() > 1))
                        groups.Add(fg.ToList());
                    continue;
                }
                groups.Add(list);
            }
            done += sizeGroup.Count();
            progress(L.T($"Confronto dei contenuti: {done * 100 / Math.Max(1, candidates)}% ({Util.Number(groups.Count)} gruppi di doppioni)"));
        }
        return groups.OrderByDescending(g => g[0].Size * (g.Count - 1)).ToList();
    }

    private static string TryHash(string path, long size, bool partialOnly)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buf = new byte[1 << 20];
            if (partialOnly)
            {
                // primi e ultimi 64 KB
                const int chunk = 64 * 1024;
                if (size <= 2 * chunk)
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) md5.AppendData(buf, 0, n);
                }
                else
                {
                    fs.ReadExactly(buf, 0, chunk);
                    md5.AppendData(buf, 0, chunk);
                    fs.Seek(-chunk, SeekOrigin.End);
                    fs.ReadExactly(buf, 0, chunk);
                    md5.AppendData(buf, 0, chunk);
                }
            }
            else
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) md5.AppendData(buf, 0, n);
            }
            return Convert.ToHexString(md5.GetHashAndReset());
        }
        catch
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ foto simili
    /// <summary>Raggruppa le foto che si somigliano (distanza di Hamming tra gli hash ≤ soglia).</summary>
    public static List<List<DupFile>> FindSimilarPhotos(IReadOnlyList<string> roots, int threshold, Action<string> progress, CancellationToken ct, out int cloudSkipped)
    {
        progress(L.T("Ricerca delle foto…"));
        var files = Enumerate(roots, 4 * 1024, ext => ImageExts.Contains(ext.ToLowerInvariant()), progress, ct, out cloudSkipped);
        var done = 0;
        var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        var ok = new List<DupFile>();
        Parallel.ForEach(files, po, f =>
        {
            if (PerceptualHash(f)) lock (ok) ok.Add(f);
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0) progress(L.T($"Analisi delle foto: {n} di {files.Count}…"));
        });
        progress(L.T("Confronto delle foto…"));
        // union-find sulle coppie abbastanza vicine
        var parent = Enumerable.Range(0, ok.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        for (var i = 0; i < ok.Count; i++)
        {
            if (i % 500 == 0) ct.ThrowIfCancellationRequested();
            var hi = ok[i].Hash;
            for (var j = i + 1; j < ok.Count; j++)
                if (BitOperations.PopCount(hi ^ ok[j].Hash) <= threshold)
                {
                    var a = Find(i);
                    var b = Find(j);
                    if (a != b) parent[a] = b;
                }
        }
        return Enumerable.Range(0, ok.Count).GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => g.Select(i => ok[i]).OrderByDescending(f => f.Pixels).ThenByDescending(f => f.Size).ToList())
            .OrderByDescending(g => g.Skip(1).Sum(f => f.Size)).ToList();
    }

    private const int N = 32;

    /// <summary>Coseni della DCT 32×32 (solo le prime 8 frequenze servono all'hash).</summary>
    private static readonly double[,] Cos = BuildCos();

    private static double[,] BuildCos()
    {
        var c = new double[8, N];
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < N; x++)
                c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * N));
        return c;
    }

    /// <summary>pHash: segno delle basse frequenze della DCT rispetto alla mediana.</summary>
    private static ulong DctHash(double[] gray)
    {
        // DCT separabile: prima sulle righe, poi sulle colonne
        var rows = new double[N, 8];
        for (var y = 0; y < N; y++)
            for (var u = 0; u < 8; u++)
            {
                double s = 0;
                for (var x = 0; x < N; x++) s += gray[y * N + x] * Cos[u, x];
                rows[y, u] = s;
            }
        var coeffs = new double[64];
        for (var v = 0; v < 8; v++)
            for (var u = 0; u < 8; u++)
            {
                double s = 0;
                for (var y = 0; y < N; y++) s += rows[y, u] * Cos[v, y];
                coeffs[v * 8 + u] = s;
            }
        // la componente continua (luminosità media) non conta
        var median = coeffs.Skip(1).OrderBy(x => x).ElementAt(31);
        ulong hash = 0;
        for (var i = 1; i < 64; i++)
            if (coeffs[i] > median) hash |= 1UL << i;
        return hash;
    }

    /// <summary>Hash percettivo a 64 bit: resiste a ridimensionamenti, ricompressione e ritocchi di luce e colore.</summary>
    private static bool PerceptualHash(DupFile f)
    {
        try
        {
            var info = new MagickImageInfo(f.Path);
            f.Width = (int)info.Width;
            f.Height = (int)info.Height;
            var settings = new MagickReadSettings();
            // per i JPEG la decodifica ridotta è molto più veloce
            settings.SetDefine(MagickFormat.Jpeg, "size", "256x256");
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
            using var img = new MagickImage(f.Path, settings);
            img.AutoOrient();
            // foto ruotate dall'EXIF: le dimensioni originali vanno scambiate
            if (f.Width != f.Height && img.Width != img.Height && (img.Width > img.Height) != (f.Width > f.Height))
                (f.Width, f.Height) = (f.Height, f.Width);
            img.Alpha(AlphaOption.Remove);
            img.ColorSpace = ColorSpace.Gray;
            img.Resize(new MagickGeometry(N, N) { IgnoreAspectRatio = true });
            using var pixels = img.GetPixels();
            var data = pixels.ToByteArray(PixelMapping.RGB)!;
            var gray = new double[N * N];
            for (var i = 0; i < gray.Length; i++) gray[i] = data[i * 3];
            f.Hash = DctHash(gray);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ selezione automatica
    public enum KeepRule { Best, Oldest, Newest, ShortestPath }

    /// <summary>Segna da eliminare tutti i file del gruppo tranne quello da tenere.</summary>
    public static void AutoSelect(List<DupFile> group, KeepRule rule)
    {
        var keep = rule switch
        {
            KeepRule.Oldest => group.OrderBy(f => f.Modified).First(),
            KeepRule.Newest => group.OrderByDescending(f => f.Modified).First(),
            KeepRule.ShortestPath => group.OrderBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
            _ => group.OrderByDescending(f => f.Pixels).ThenByDescending(f => f.Size).ThenBy(f => f.Modified).First(),
        };
        foreach (var f in group) f.Delete = f != keep;
    }
}
