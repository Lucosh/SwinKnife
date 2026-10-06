using System.Formats.Tar;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SwinKnife.Core;

public enum ArchiveFormat { Zip, SevenZip, TarGz }

/// <summary>Il file è protetto da password (assente o sbagliata).</summary>
public sealed class ArchivePasswordException(string message) : Exception(message);

public sealed record ArchiveSummary(string Type, int Files, long Size, bool Encrypted, string? SingleRoot);

/// <summary>Creazione ed estrazione di archivi compressi.</summary>
public static class Archives
{
    public static readonly HashSet<string> Extensions =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst", ".jar", ".apk", ".cbz", ".cbr", ".epub", ".xpi"];

    public static bool IsArchive(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Formati che SharpCompress legge solo in streaming (tar compressi, file .gz singoli).</summary>
    private static bool UseReader(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".gz" or ".tgz" or ".bz2" or ".tbz2" or ".xz" or ".txz" or ".zst" or ".tar";

    /// <summary>Nome dell'archivio senza estensioni (anche doppie, tipo .tar.gz).</summary>
    public static string Stem(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static bool IsPasswordError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var m = e.Message;
            if (e.GetType().Name.Contains("Crypto") || m.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("encrypt", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ lettura
    public static ArchiveSummary Inspect(string path, string? password)
    {
        if (UseReader(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var tar = Path.GetFileName(path).Contains(".tar.", StringComparison.OrdinalIgnoreCase) || ext is ".tgz" or ".tbz2" or ".txz" or ".tar";
            return new ArchiveSummary(tar ? "TAR" + (ext == ".tar" ? "" : " compresso") : ext.TrimStart('.').ToUpperInvariant(), -1, -1, false, null);
        }
        try
        {
            using var archive = ArchiveFactory.Open(path, new ReaderOptions { Password = password });
            var files = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var roots = archive.Entries.Select(e => (e.Key ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var single = roots.Count == 1 && archive.Entries.Any(e => (e.Key ?? "").Replace('\\', '/').Contains('/')) ? roots[0] : null;
            return new ArchiveSummary(archive.Type.ToString().ToUpperInvariant(), files.Count, files.Sum(e => e.Size), files.Any(e => e.IsEncrypted), single);
        }
        catch (Exception ex) when (IsPasswordError(ex))
        {
            // 7z con nomi cifrati: senza password non si vede nemmeno l'elenco
            throw new ArchivePasswordException(password == null ? L.T("L'archivio è protetto da password.") : L.T("Password errata."));
        }
    }

    // ------------------------------------------------------------------ estrazione
    /// <summary>Estrae l'archivio nella cartella indicata (che viene creata). Restituisce il numero di file estratti.</summary>
    public static int Extract(string path, string dest, string? password, Action<double, string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true, PreserveFileTime = true };
        var count = 0;
        try
        {
            if (TarCompression(path) is { } wrap)
            {
                using var fs = File.OpenRead(path);
                using var input = wrap(fs);
                count = ExtractTar(input, dest, () => fs.Position / (double)Math.Max(1, fs.Length), progress, ct);
            }
            else if (UseReader(path))
            {
                using var fs = File.OpenRead(path);
                using var reader = ReaderFactory.Open(fs, new ReaderOptions { Password = password });
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.Entry.IsDirectory) continue;
                    var key = reader.Entry.Key;
                    progress(fs.Position / (double)Math.Max(1, fs.Length), key ?? "");
                    if (string.IsNullOrEmpty(key)) reader.WriteEntryToFile(Path.Combine(dest, Stem(path)), options);
                    else reader.WriteEntryToDirectory(dest, options);
                    count++;
                }
            }
            else
            {
                using var archive = ArchiveFactory.Open(path, new ReaderOptions { Password = password });
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                var total = Math.Max(1, entries.Sum(e => e.Size));
                long done = 0;
                if (archive.IsSolid)
                {
                    // negli archivi "solid" (7z, rar) leggere in sequenza è molto più veloce
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (reader.Entry.IsDirectory) continue;
                        progress(done / (double)total, reader.Entry.Key ?? "");
                        reader.WriteEntryToDirectory(dest, options);
                        done += reader.Entry.Size;
                        count++;
                    }
                }
                else
                    foreach (var e in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress(done / (double)total, e.Key ?? "");
                        e.WriteToDirectory(dest, options);
                        done += e.Size;
                        count++;
                    }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (IsPasswordError(ex) || password != null && ex.GetType().Name.Contains("DataError")))
        {
            // con una password sbagliata 7z segnala solo "dati corrotti"
            throw new ArchivePasswordException(password == null ? L.T("L'archivio è protetto da password.") : L.T("Password errata."));
        }
        progress(1, "");
        return count;
    }

    /// <summary>Per .tar, .tar.gz e .tgz uso il lettore di .NET, che gestisce anche i formati PAX e GNU.</summary>
    private static Func<Stream, Stream>? TarCompression(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar")) return s => s;
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return s => new GZipStream(s, CompressionMode.Decompress);
        return null;
    }

    private static int ExtractTar(Stream input, string dest, Func<double> position, Action<double, string> progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(dest);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        using var reader = new TarReader(input);
        var count = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(Path.Combine(root, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
            // protezione dai percorsi che escono dalla cartella di destinazione ("../")
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(full);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    progress(position(), entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    entry.ExtractToFile(full, true);
                    count++;
                    break;
            }
        }
        return count;
    }

    // ------------------------------------------------------------------ creazione
    /// <summary>Elenco (file, nome nell'archivio) per file e cartelle scelti.</summary>
    private static List<(string file, string entry)> Collect(IEnumerable<string> items)
    {
        var list = new List<(string, string)>();
        foreach (var item in items)
        {
            if (File.Exists(item)) list.Add((item, Path.GetFileName(item)));
            else if (Directory.Exists(item))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(item).TrimEnd('\\')) ?? "";
                foreach (var f in Directory.EnumerateFiles(item, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    list.Add((f, Path.GetRelativePath(parent, f).Replace('\\', '/')));
            }
        }
        return list;
    }

    public static void Create(string archive, IReadOnlyList<string> items, ArchiveFormat format, int level, string? password,
        Action<double, string> progress, CancellationToken ct)
    {
        if (format == ArchiveFormat.SevenZip)
        {
            var exe = SevenZip.Find() ?? throw new InvalidOperationException(L.T("7-Zip non disponibile."));
            if (File.Exists(archive)) File.Delete(archive);
            SevenZip.Create(exe, archive, items, Math.Clamp(level, 0, 9), password, p => progress(p, ""), ct);
            return;
        }
        var files = Collect(items);
        var total = Math.Max(1, files.Sum(f => SafeLength(f.file)));
        long done = 0;
        var tmp = archive + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            {
                if (format == ArchiveFormat.Zip) WriteZip(fs, files, level, password, ref done, total, progress, ct);
                else WriteTarGz(fs, files, level, ref done, total, progress, ct);
            }
            File.Move(tmp, archive, true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static long SafeLength(string f)
    {
        try { return new FileInfo(f).Length; }
        catch { return 0; }
    }

    private static void WriteZip(Stream output, List<(string file, string entry)> files, int level, string? password, ref long done, long total,
        Action<double, string> progress, CancellationToken ct)
    {
        using var zip = new ZipOutputStream(output) { IsStreamOwner = false };
        zip.SetLevel(Math.Clamp(level, 0, 9));
        if (!string.IsNullOrEmpty(password)) zip.Password = password;
        var buffer = new byte[1 << 20];
        foreach (var (file, name) in files)
        {
            ct.ThrowIfCancellationRequested();
            progress(done / (double)total, name);
            var fi = new FileInfo(file);
            var entry = new ZipEntry(name) { DateTime = fi.LastWriteTime, Size = fi.Length, IsUnicodeText = true };
            if (!string.IsNullOrEmpty(password)) entry.AESKeySize = 256;
            zip.PutNextEntry(entry);
            using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.Write(buffer, 0, n);
                    done += n;
                    progress(done / (double)total, name);
                }
            }
            zip.CloseEntry();
        }
        zip.Finish();
    }

    private static void WriteTarGz(Stream output, List<(string file, string entry)> files, int level, ref long done, long total,
        Action<double, string> progress, CancellationToken ct)
    {
        var compression = level <= 1 ? CompressionLevel.Fastest : level >= 8 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        if (level == 0) compression = CompressionLevel.NoCompression;
        using var gz = new GZipStream(output, compression, true);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax, false);
        foreach (var (file, name) in files)
        {
            ct.ThrowIfCancellationRequested();
            progress(done / (double)total, name);
            tar.WriteEntry(file, name);
            done += SafeLength(file);
        }
    }
}
