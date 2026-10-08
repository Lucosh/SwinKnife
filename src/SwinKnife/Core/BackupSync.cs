namespace SwinKnife.Core;

public sealed record BackupResult(int Copied, int Deleted, int Skipped, long Bytes);

/// <summary>Copia/sincronizza una cartella in un'altra: solo i file nuovi o modificati, con mirror facoltativo.</summary>
public static class BackupSync
{
    public static BackupResult Run(string src, string dst, bool mirror, Action<double, string> progress, CancellationToken ct)
    {
        src = Path.GetFullPath(src);
        dst = Path.GetFullPath(dst);
        Directory.CreateDirectory(dst);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        var files = Directory.EnumerateFiles(src, "*", opts).ToList();
        var total = Math.Max(1, files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, skipped = 0;
        long bytes = 0;
        var done = 0;

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src, f);
            seen.Add(rel);
            var target = Path.Combine(dst, rel);
            progress(done++ / (double)total, rel);
            try
            {
                var sfi = new FileInfo(f);
                var tfi = new FileInfo(target);
                if (tfi.Exists && tfi.Length == sfi.Length && tfi.LastWriteTimeUtc >= sfi.LastWriteTimeUtc) { skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
                File.SetLastWriteTimeUtc(target, sfi.LastWriteTimeUtc);
                copied++;
                bytes += sfi.Length;
            }
            catch { skipped++; }
        }

        var deleted = 0;
        if (mirror)
        {
            foreach (var tf in Directory.EnumerateFiles(dst, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(dst, tf);
                if (!seen.Contains(rel)) { try { File.Delete(tf); deleted++; } catch { } }
            }
            // rimuove le cartelle rimaste vuote
            foreach (var dir in Directory.EnumerateDirectories(dst, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        progress(1, "");
        return new BackupResult(copied, deleted, skipped, bytes);
    }
}
