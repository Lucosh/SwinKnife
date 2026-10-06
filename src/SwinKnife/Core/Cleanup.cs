using System.Runtime.InteropServices;

namespace SwinKnife.Core;

/// <summary>Una categoria di file superflui che si possono eliminare.</summary>
public sealed class CleanTarget
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool DefaultOn { get; init; } = true;
    public bool NeedsAdmin { get; init; }
    /// <summary>Cartelle da svuotare (vengono eliminati i contenuti, non le cartelle stesse).</summary>
    public Func<IEnumerable<string>> Roots { get; init; } = () => [];
    /// <summary>Filtro facoltativo sui file (es. solo quelli vecchi).</summary>
    public Func<FileInfo, bool>? Filter { get; init; }
    public bool IsRecycleBin { get; init; }

    // risultati dell'analisi
    public long Size { get; set; }
    public long Count { get; set; }
    public List<string> Files { get; } = new();
}

public static class Cleanup
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static bool OlderThan(FileInfo f, TimeSpan age) => DateTime.Now - f.LastWriteTime > age;

    /// <summary>Sottocartelle di cache nei profili dei browser basati su Chromium.</summary>
    private static IEnumerable<string> ChromiumCaches(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        IEnumerable<string> profiles;
        try { profiles = Directory.EnumerateDirectories(userData).ToList(); }
        catch { yield break; }
        foreach (var profile in profiles)
            foreach (var sub in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) yield return p;
            }
        foreach (var shared in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
        {
            var p = Path.Combine(userData, shared);
            if (Directory.Exists(p)) yield return p;
        }
    }

    private static IEnumerable<string> FirefoxCaches()
    {
        var profiles = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) yield break;
        foreach (var p in Directory.EnumerateDirectories(profiles))
        {
            var c = Path.Combine(p, "cache2");
            if (Directory.Exists(c)) yield return c;
        }
    }

    private static IEnumerable<string> Existing(params string[] paths) => paths.Where(Directory.Exists);

    public static List<CleanTarget> Targets() =>
    [
        new()
        {
            Key = "temp", Title = L.T("File temporanei"), Description = L.T("Lasciati dai programmi nella cartella Temp. Vengono tenuti quelli delle ultime 24 ore."),
            Roots = () => Existing(Path.GetTempPath()), Filter = f => OlderThan(f, TimeSpan.FromDays(1)),
        },
        new()
        {
            Key = "recycle", Title = L.T("Cestino"), Description = L.T("I file che hai eliminato: svuotandolo non si potranno più ripristinare dal Cestino."),
            IsRecycleBin = true,
        },
        new()
        {
            Key = "browser", Title = L.T("Cache dei browser"),
            Description = L.T("Copie di pagine e immagini salvate da Chrome, Edge, Firefox, Brave e Opera. Password, cronologia e accessi non vengono toccati. Chiudi i browser per liberare più spazio."),
            Roots = () => ChromiumCaches(Path.Combine(Local, @"Google\Chrome\User Data"))
                .Concat(ChromiumCaches(Path.Combine(Local, @"Microsoft\Edge\User Data")))
                .Concat(ChromiumCaches(Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data")))
                .Concat(ChromiumCaches(Path.Combine(Local, "Opera Software")))
                .Concat(FirefoxCaches()),
        },
        new()
        {
            Key = "apps", Title = L.T("Cache di altre app"), Description = L.T("Discord, Spotify, Microsoft Teams, Steam (pagine web) e simili: le app le ricreano quando servono."),
            Roots = () => Existing(
                    Path.Combine(Roaming, @"discord\Cache"), Path.Combine(Roaming, @"discord\Code Cache"), Path.Combine(Roaming, @"discord\GPUCache"),
                    Path.Combine(Local, @"Spotify\Data"), Path.Combine(Local, @"Spotify\Browser\Cache"),
                    Path.Combine(Roaming, @"Microsoft\Teams\Cache"), Path.Combine(Roaming, @"Microsoft\Teams\Service Worker\CacheStorage"),
                    Path.Combine(Local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache"),
                    Path.Combine(Local, @"Steam\htmlcache"), Path.Combine(Local, @"D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache"),
                    Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"))
                .Concat(new[] { "Cache", "CachedData", "Code Cache", "GPUCache" }.Select(d => Path.Combine(Roaming, "Code", d)).Where(Directory.Exists)),
        },
        new()
        {
            Key = "thumbs", Title = L.T("Miniature di Esplora risorse"), Description = L.T("Anteprime delle immagini nelle cartelle: verranno ricreate quando apri le cartelle."),
            DefaultOn = false,
            Roots = () => Existing(Path.Combine(Local, @"Microsoft\Windows\Explorer")),
            Filter = f => f.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase),
        },
        new()
        {
            Key = "crash", Title = L.T("Rapporti errori e dump"), Description = L.T("Rapporti inviati o in coda a Microsoft dopo un blocco dei programmi e file di dump."),
            Roots = () => Existing(Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
        },
        new()
        {
            Key = "wintemp", Title = L.T("File temporanei di Windows"), Description = "La cartella Temp di sistema (C:\\Windows\\Temp). Vengono tenuti quelli delle ultime 24 ore.",
            NeedsAdmin = true,
            Roots = () => Existing(Path.Combine(WinDir, "Temp")), Filter = f => OlderThan(f, TimeSpan.FromDays(1)),
        },
        new()
        {
            Key = "wu", Title = L.T("Aggiornamenti di Windows già installati"),
            Description = L.T("Pacchetti scaricati da Windows Update e già installati. Se un aggiornamento è in corso, Windows li riscaricherà."),
            NeedsAdmin = true,
            Roots = () => Existing(Path.Combine(WinDir, @"SoftwareDistribution\Download")),
        },
        new()
        {
            Key = "do", Title = L.T("Ottimizzazione recapito"), Description = L.T("Copie degli aggiornamenti condivise con altri PC in rete."),
            NeedsAdmin = true,
            Roots = () => Existing(Path.Combine(WinDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
                Path.Combine(WinDir, @"SoftwareDistribution\DeliveryOptimization")),
        },
        new()
        {
            Key = "minidump", Title = L.T("Dump di memoria degli arresti di sistema"), Description = L.T("Creati quando Windows si blocca con la schermata blu."),
            NeedsAdmin = true,
            Roots = () => Existing(Path.Combine(WinDir, "Minidump"), Path.Combine(WinDir, "LiveKernelReports")),
        },
        new()
        {
            Key = "installers", Title = L.T("Vecchi installer nei Download"),
            Description = L.T("File .exe e .msi scaricati più di 30 giorni fa nella cartella Download: di solito servono solo una volta. Controlla l'elenco prima di eliminarli."),
            DefaultOn = false,
            Roots = () => Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            Filter = f => f.Extension.ToLowerInvariant() is ".exe" or ".msi" && OlderThan(f, TimeSpan.FromDays(30)) && f.DirectoryName is { } d &&
                          d.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), StringComparison.OrdinalIgnoreCase),
        },
        new()
        {
            Key = "swin", Title = L.T("File temporanei di SwinKnife"), Description = L.T("Anteprime e conversioni intermedie create da questa app."),
            Roots = () => Existing(AppInfo.TempDir),
        },
    ];

    // ------------------------------------------------------------------ analisi
    public static void Measure(CleanTarget t, CancellationToken ct)
    {
        t.Size = 0;
        t.Count = 0;
        t.Files.Clear();
        if (t.IsRecycleBin)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                t.Size = info.i64Size;
                t.Count = info.i64NumItems;
            }
            return;
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var root in t.Roots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles("*", options); }
            catch { continue; }
            try
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (t.Filter != null && !t.Filter(f)) continue;
                        t.Size += f.Length;
                        t.Count++;
                        t.Files.Add(f.FullName);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
    }

    // ------------------------------------------------------------------ pulizia
    /// <summary>Elimina i file trovati; restituisce byte liberati e file saltati (in uso o protetti).</summary>
    public static (long freed, int skipped) Clean(CleanTarget t, Action<string>? progress, CancellationToken ct)
    {
        if (t.IsRecycleBin)
        {
            var size = t.Size;
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 || t.Count == 0 ? (size, 0) : (0, (int)t.Count);
        }
        long freed = 0;
        var skipped = 0;
        var n = 0;
        foreach (var path in t.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 200 == 0) progress?.Invoke(L.T($"{t.Title}: {n} di {t.Files.Count} file…"));
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                var len = fi.Length;
                if (fi.IsReadOnly) fi.IsReadOnly = false;
                fi.Delete();
                freed += len;
            }
            catch
            {
                skipped++;
            }
        }
        // rimuovo le sottocartelle rimaste vuote (le cartelle principali restano)
        if (t.Filter == null)
            foreach (var root in t.Roots())
                RemoveEmptyDirs(root, true);
        return (freed, skipped);
    }

    private static void RemoveEmptyDirs(string dir, bool isRoot)
    {
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var sub in Directory.EnumerateDirectories(dir).ToList()) RemoveEmptyDirs(sub, false);
            if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    // ------------------------------------------------------------------ Cestino
    [StructLayout(LayoutKind.Sequential)] // allineamento naturale a 64 bit
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
}
