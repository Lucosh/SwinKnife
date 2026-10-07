using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SwinKnife.Core;

public sealed class InstalledProgram
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Installed { get; init; }
    public long SizeBytes { get; set; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string Icon { get; init; } = "";
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string KeyPath { get; init; }        // ...\Uninstall\{chiave}
    public bool IsMsi { get; init; }
    public string KeyName => KeyPath[(KeyPath.LastIndexOf('\\') + 1)..];
    public bool PerUser => Hive == RegistryHive.CurrentUser;
}

public sealed class Leftover
{
    public required string Path { get; init; }          // cartella o "HKCU\Software\…"
    public bool IsRegistry { get; init; }
    public long Size { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool Selected { get; set; } = true;
}

/// <summary>Programmi installati (quelli in "App installate" di Windows) e ricerca dei residui dopo la disinstallazione.</summary>
public static class Programs
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> List()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(name);
                        if (k?.GetValue("DisplayName") is not string display || display.Trim().Length == 0) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") != null || (k.GetValue("ReleaseType") as string) is "Update" or "Hotfix" or "Security Update") continue;
                        var uninstall = k.GetValue("UninstallString") as string ?? "";
                        if (uninstall.Length == 0) continue;
                        DateTime? date = null;
                        if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) date = dt;
                        list.Add(new InstalledProgram
                        {
                            Name = display.Trim(),
                            Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                            Version = k.GetValue("DisplayVersion") as string ?? "",
                            Installed = date,
                            SizeBytes = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
                            InstallLocation = (k.GetValue("InstallLocation") as string ?? "").Trim('"', ' '),
                            UninstallString = uninstall,
                            QuietUninstallString = k.GetValue("QuietUninstallString") as string ?? "",
                            Icon = (k.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim('"'),
                            Hive = hive, View = view, KeyPath = UninstallKey + "\\" + name,
                            IsMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
        // lo stesso programma può comparire sia a 32 che a 64 bit
        return list.GroupBy(p => (p.Name, p.Version)).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool StillInstalled(InstalledProgram p)
    {
        using var root = RegistryKey.OpenBaseKey(p.Hive, p.View);
        using var k = root.OpenSubKey(p.KeyPath);
        return k != null;
    }

    /// <summary>Avvia il disinstallatore del programma e aspetta che finisca.</summary>
    public static async Task UninstallAsync(InstalledProgram p)
    {
        string exe, args;
        if (p.IsMsi && Regex.Match(p.UninstallString, @"\{[0-9A-Fa-f\-]{36}\}") is { Success: true } guid)
        {
            exe = "msiexec.exe";
            args = $"/x {guid.Value}";
        }
        else (exe, args) = Split(p.UninstallString);
        var started = DateTime.Now.AddSeconds(-2);
        using var proc = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }) ?? throw new InvalidOperationException(L.T("Impossibile avviare il disinstallatore."));
        await proc.WaitForExitAsync();
        // molti disinstallatori si copiano altrove e terminano subito: aspetto finché ne vedo uno partito dopo (al massimo 10 minuti)
        for (var i = 0; i < 600 && StillInstalled(p) && UninstallerRunning(started); i++) await Task.Delay(1000);
    }

    private static bool UninstallerRunning(DateTime since) =>
        Process.GetProcesses().Any(x =>
        {
            try
            {
                var n = x.ProcessName;
                var typical = n.StartsWith("Au_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Un_", StringComparison.OrdinalIgnoreCase)
                              || n.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || n.StartsWith("uninst", StringComparison.OrdinalIgnoreCase)
                              || n.Equals("msiexec", StringComparison.OrdinalIgnoreCase) && x.MainWindowHandle != IntPtr.Zero;
                return typical && x.StartTime >= since;
            }
            catch
            {
                return false; // processo di un altro utente o elevato: non leggibile
            }
        });

    /// <summary>Separa eseguibile e argomenti di una riga di comando anche senza virgolette ("C:\Program Files\X\uninst.exe /S").</summary>
    private static (string exe, string args) Split(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 0 ? (cmd[1..end], cmd[(end + 1)..].Trim()) : (cmd.Trim('"'), "");
        }
        var exeEnd = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? (cmd[..(exeEnd + 4)], cmd[(exeEnd + 4)..].Trim()) : (cmd, "");
    }

    // ------------------------------------------------------------------ residui
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "common files", "programs", "temp", "packages", "microsoft corporation", "google", "intel", "nvidia", "amd", "app", "apps", "bin", "data", "update", "updates",
    };

    /// <summary>Nome "pulito": senza versione, architettura e punteggiatura (es. "VLC media player 3.0.20 (64-bit)" → "vlcmediaplayer").</summary>
    private static string Core(string s)
    {
        s = Regex.Replace(s, @"\(.*?\)|\b(x64|x86|64-bit|32-bit|v?\d+(\.\d+)+)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    }

    public static List<Leftover> FindLeftovers(InstalledProgram p)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Name(string s)
        {
            var c = Core(s);
            if (c.Length >= 4 && !Generic.Contains(c)) names.Add(c);
        }
        Name(p.Name);
        if (p.InstallLocation.Length > 0) Name(Path.GetFileName(p.InstallLocation.TrimEnd('\\')));
        var publisher = Core(p.Publisher);
        var found = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var admin = RawSource.IsAdmin();

        void Dir(string path, bool needsAdmin)
        {
            if (!Directory.Exists(path) || !seen.Add(path)) return;
            long size = 0;
            try { size = new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
            catch { }
            found.Add(new Leftover { Path = path, Size = size, NeedsAdmin = needsAdmin && !admin });
        }

        if (p.InstallLocation.Length > 3 && Directory.Exists(p.InstallLocation) && !Generic.Contains(Path.GetFileName(p.InstallLocation.TrimEnd('\\'))))
            Dir(p.InstallLocation, !p.PerUser);
        var roots = new (string path, bool admin)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), false),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"), false),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true),
        };
        foreach (var (root, needsAdmin) in roots.Where(r => Directory.Exists(r.path)))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }
            foreach (var d in dirs)
            {
                var n = Core(Path.GetFileName(d));
                if (names.Contains(n)) Dir(d, needsAdmin);
                // cartella del produttore che contiene quella del programma (es. "VideoLAN\VLC")
                else if (publisher.Length >= 4 && n == publisher)
                {
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(d).Where(s => names.Contains(Core(Path.GetFileName(s)))))
                            Dir(sub, needsAdmin);
                    }
                    catch { }
                }
            }
        }

        // collegamenti sul Desktop che puntano a cartelle trovate
        foreach (var desk in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(desk, "*.lnk").Where(l => names.Contains(Core(Path.GetFileNameWithoutExtension(l)))))
                    if (seen.Add(lnk)) found.Add(new Leftover { Path = lnk, Size = new FileInfo(lnk).Length });
            }
            catch { }
        }

        // registro: chiavi col nome del programma o sotto quella del produttore
        foreach (var (hive, label) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            foreach (var sw in new[] { "Software", @"Software\WOW6432Node" })
            {
                using var soft = hive.OpenSubKey(sw);
                if (soft == null) continue;
                foreach (var sub in soft.GetSubKeyNames())
                {
                    var n = Core(sub);
                    if (names.Contains(n)) found.Add(new Leftover { Path = $@"{label}\{sw}\{sub}", IsRegistry = true, NeedsAdmin = label == "HKLM" && !admin });
                    else if (publisher.Length >= 4 && n == publisher)
                    {
                        using var pub = soft.OpenSubKey(sub);
                        foreach (var app in pub?.GetSubKeyNames() ?? [])
                            if (names.Contains(Core(app)))
                                found.Add(new Leftover { Path = $@"{label}\{sw}\{sub}\{app}", IsRegistry = true, NeedsAdmin = label == "HKLM" && !admin });
                    }
                }
            }
        }
        // la voce di disinstallazione rimasta orfana
        if (StillInstalled(p))
        {
            var hiveLabel = p.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            var path = p.View == RegistryView.Registry32 && p.Hive == RegistryHive.LocalMachine ? p.KeyPath.Replace(@"Software\", @"Software\WOW6432Node\") : p.KeyPath;
            found.Add(new Leftover { Path = $@"{hiveLabel}\{path}", IsRegistry = true, NeedsAdmin = hiveLabel == "HKLM" && !admin, Selected = false });
        }
        return found;
    }

    /// <summary>Elimina i residui scelti: file nel Cestino, chiavi di registro dopo averne salvato una copia .reg.</summary>
    public static (int ok, int failed, string? backup) Remove(IEnumerable<Leftover> items)
    {
        int ok = 0, failed = 0;
        string? backupDir = null;
        var files = new List<string>();
        foreach (var it in items)
        {
            if (!it.IsRegistry)
            {
                files.Add(it.Path);
                continue;
            }
            try
            {
                backupDir ??= Path.Combine(AppInfo.DataDir, "backup", DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                Directory.CreateDirectory(backupDir);
                var file = Path.Combine(backupDir, Util.SafeFileName(it.Path.Replace('\\', '_')) + ".reg");
                using (var exp = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{it.Path}\" \"{file}\" /y") { CreateNoWindow = true, UseShellExecute = false }))
                    exp?.WaitForExit(15000);
                var hive = it.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine;
                hive.DeleteSubKeyTree(it.Path[5..], false);
                ok++;
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Rimozione residuo " + it.Path);
                failed++;
            }
        }
        if (files.Count > 0)
        {
            try
            {
                DiskScan.SendToRecycleBin(files);
                ok += files.Count;
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Residui nel Cestino");
                failed += files.Count;
            }
        }
        return (ok, failed, backupDir);
    }
}
