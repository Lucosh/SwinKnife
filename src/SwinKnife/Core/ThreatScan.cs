using System.Diagnostics;

namespace SwinKnife.Core;

public enum ItemKind { Process, File, Startup }
public enum Risk { Clean, Unknown, Suspicious, Malicious }

/// <summary>Un elemento analizzato (processo, file o voce di avvio) con il suo esito.</summary>
public sealed class ScanItem
{
    public ItemKind Kind { get; init; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Pid { get; set; }
    public long Size { get; set; }
    public SignStatus Sign { get; set; } = SignStatus.Unsigned;
    public string Signer { get; set; } = "";
    public VtResult? Vt { get; set; }
    public List<string> Flags { get; } = new();
    public StartupItem? Startup { get; set; }

    public Risk Risk
    {
        get
        {
            if (Vt is { Status: VtStatus.Malicious }) return Risk.Malicious;
            if (Vt is { Status: VtStatus.Suspicious }) return Risk.Suspicious;
            if (Flags.Count > 0) return Risk.Suspicious;
            if (Vt is { Status: VtStatus.Clean }) return Risk.Clean;
            return Risk.Unknown;
        }
    }
}

/// <summary>Raccoglie processi, file e voci di avvio e vi applica i controlli euristici locali.</summary>
public static class ThreatScan
{
    private static readonly string[] Executables =
        { ".exe", ".dll", ".scr", ".com", ".sys", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jar", ".msi", ".lnk" };

    public static bool IsExecutable(string path) => Executables.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Cartelle in cui un eseguibile "normalmente" non dovrebbe trovarsi.</summary>
    private static readonly string[] SuspiciousDirs = BuildSuspicious();

    private static string[] BuildSuspicious()
    {
        var list = new List<string>();
        void Add(Environment.SpecialFolder f) { try { var p = Environment.GetFolderPath(f); if (p.Length > 0) list.Add(p); } catch { } }
        Add(Environment.SpecialFolder.LocalApplicationData);
        list.Add(System.IO.Path.GetTempPath());
        var up = Environment.GetEnvironmentVariable("USERPROFILE");
        if (up != null) list.Add(System.IO.Path.Combine(up, "Downloads"));
        return list.Select(p => p.TrimEnd('\\') + "\\").ToArray();
    }

    /// <summary>Firma + euristiche per un singolo file (niente rete).</summary>
    public static void Inspect(ScanItem item)
    {
        item.Flags.Clear();
        if (!File.Exists(item.Path)) { item.Flags.Add(L.T("il file non esiste più")); return; }
        try { item.Size = new FileInfo(item.Path).Length; } catch { }

        var (status, signer) = FileSignature.Check(item.Path);
        item.Sign = status;
        item.Signer = signer;

        var inTemp = item.Path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase);
        var suspDir = SuspiciousDirs.Any(d => item.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase));

        if (status == SignStatus.Invalid) item.Flags.Add(L.T("firma digitale non valida o alterata"));
        if (inTemp) item.Flags.Add(L.T("eseguito dalla cartella temporanea"));
        if (status != SignStatus.Valid && suspDir && IsExecutable(item.Path))
            item.Flags.Add(L.T("non firmato ed eseguito da una cartella a rischio"));
        if (DoubleExtension(item.Name)) item.Flags.Add(L.T("doppia estensione sospetta (es. .pdf.exe)"));
    }

    private static bool DoubleExtension(string name)
    {
        var lower = name.ToLowerInvariant();
        string[] lures = { ".pdf", ".doc", ".docx", ".xls", ".jpg", ".png", ".txt", ".mp4" };
        return lures.Any(l => lower.Contains(l + ".")) && IsExecutable(name);
    }

    /// <summary>Elenco dei processi con percorso e firma (senza rete).</summary>
    public static List<ScanItem> Processes()
    {
        var list = new List<ScanItem>();
        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            var item = new ScanItem { Kind = ItemKind.Process, Pid = p.Id, Name = SafeName(p), Path = path ?? "" };
            if (!string.IsNullOrEmpty(path)) Inspect(item);
            list.Add(item);
            p.Dispose();
        }
        return list
            .GroupBy(i => (i.Name, i.Path))
            .Select(g => g.First())
            .OrderByDescending(i => (int)i.Risk)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string SafeName(Process p) { try { return p.ProcessName; } catch { return "?"; } }

    public static List<ScanItem> Files(string folder, bool onlyExecutables, CancellationToken ct)
    {
        var list = new List<ScanItem>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (onlyExecutables && !IsExecutable(f)) continue;
            var item = new ScanItem { Kind = ItemKind.File, Name = System.IO.Path.GetFileName(f), Path = f };
            Inspect(item);
            list.Add(item);
        }
        return list.OrderByDescending(i => (int)i.Risk).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<ScanItem> Startup()
    {
        var list = new List<ScanItem>();
        foreach (var s in SysInfo.StartupItems())
        {
            var item = new ScanItem { Kind = ItemKind.Startup, Name = s.Name, Path = s.Executable ?? "", Startup = s };
            if (!string.IsNullOrEmpty(item.Path)) Inspect(item);
            list.Add(item);
        }
        return list.OrderByDescending(i => (int)i.Risk).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
