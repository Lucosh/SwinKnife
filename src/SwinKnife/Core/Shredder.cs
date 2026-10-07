using System.Security.Cryptography;

namespace SwinKnife.Core;

/// <summary>
/// Eliminazione sicura: il contenuto viene sovrascritto prima di cancellare il file e il nome viene cambiato,
/// così né il Cestino né i programmi di recupero lo possono ritrovare.
/// Sugli SSD la sovrascrittura non è garantita al 100% (il disco può scrivere altrove), ma insieme al TRIM è comunque efficace.
/// </summary>
public static class Shredder
{
    private const int Block = 1 << 20;

    public static long TotalSize(IEnumerable<string> paths) =>
        paths.Sum(p => Directory.Exists(p)
            ? new DirectoryInfo(p).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Sum(f => f.Length)
            : File.Exists(p) ? new FileInfo(p).Length : 0);

    /// <summary>Elimina file e cartelle; progress riceve i byte sovrascritti finora.</summary>
    public static (int files, List<string> errors) Shred(IEnumerable<string> paths, int passes, Action<long, string> progress, CancellationToken ct)
    {
        long done = 0;
        var count = 0;
        var errors = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var f in Directory.EnumerateFiles(p, "*", opts).ToList())
                    Try(f);
                // cartelle dalla più profonda, rinominate prima di essere rimosse
                foreach (var d in Directory.EnumerateDirectories(p, "*", opts).OrderByDescending(d => d.Length).Append(p))
                {
                    try { Directory.Delete(RenameRandom(d, true)); }
                    catch (Exception ex) { errors.Add($"{d}: {ex.Message}"); }
                }
            }
            else if (File.Exists(p)) Try(p);
        }
        return (count, errors);

        void Try(string f)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ShredFile(f, passes, n => progress(done += n, f), ct);
                count++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{f}: {ex.Message}");
            }
        }
    }

    private static void ShredFile(string path, int passes, Action<long> written, CancellationToken ct)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, Block, FileOptions.WriteThrough))
        {
            var len = fs.Length;
            var buf = new byte[Block];
            for (var pass = 0; pass < passes; pass++)
            {
                fs.Position = 0;
                for (long pos = 0; pos < len; pos += Block)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(Block, len - pos);
                    // casuale nei passaggi dispari, zeri negli altri (come il metodo DoD a 3 passaggi)
                    if (pass % 2 == 0) RandomNumberGenerator.Fill(buf.AsSpan(0, n));
                    else Array.Clear(buf, 0, n);
                    fs.Write(buf, 0, n);
                    if (pass == passes - 1) written(n);
                }
                fs.Flush(true);
            }
            fs.SetLength(0);
        }
        // anche la data e il nome non devono dire niente
        File.SetCreationTime(path, new DateTime(2000, 1, 1));
        File.SetLastWriteTime(path, new DateTime(2000, 1, 1));
        File.Delete(RenameRandom(path, false));
    }

    private static string RenameRandom(string path, bool dir)
    {
        var parent = Path.GetDirectoryName(path)!;
        for (var i = 0; i < 3; i++)
        {
            var name = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            var target = Path.Combine(parent, name);
            try
            {
                if (dir) Directory.Move(path, target);
                else File.Move(path, target);
                path = target;
            }
            catch { break; }
        }
        return path;
    }

    /// <summary>
    /// Riempie lo spazio libero dell'unità con un file di zeri e poi lo elimina: i file cancellati in passato
    /// non sono più recuperabili. Lascia sempre un margine libero per non bloccare Windows.
    /// </summary>
    public static void WipeFreeSpace(string drive, Action<double, string> progress, CancellationToken ct)
    {
        var info = new DriveInfo(drive);
        const long margin = 512L << 20;
        var target = info.AvailableFreeSpace - margin;
        if (target <= 0) return;
        var dir = Path.Combine(info.RootDirectory.FullName, "SwinKnife-pulizia-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var buf = new byte[8 << 20];
            long done = 0;
            var part = 0;
            while (done < target)
            {
                // file da 2 GB al massimo (FAT32 non ne accetta di più grandi)
                using var fs = new FileStream(Path.Combine(dir, $"{part++}.tmp"), FileMode.Create, FileAccess.Write, FileShare.None, buf.Length, FileOptions.WriteThrough);
                long inFile = 0;
                while (inFile < (2L << 30) - buf.Length && done < target)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(buf.Length, target - done);
                    try { fs.Write(buf, 0, n); }
                    catch (IOException) { done = target; break; } // disco pieno prima del previsto
                    inFile += n;
                    done += n;
                    progress(done / (double)target, L.T($"{Util.HumanSize(done)} di {Util.HumanSize(target)}"));
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
