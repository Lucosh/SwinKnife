using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SwinKnife.Core;

/// <summary>Percorsi dell'applicazione, log e impostazioni persistenti.</summary>
public static class AppInfo
{
    public const string Name = "SwinKnife";

    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static readonly string TempDir = Path.Combine(DataDir, "tmp");

    public static string LogFile => Path.Combine(DataDir, "swinknife.log");

    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // il log non deve mai far fallire l'app
        }
    }

    public static void Log(Exception ex, string context) => Log($"{context}: {ex}");

    public static void CleanTemp()
    {
        try
        {
            Directory.CreateDirectory(TempDir);
            foreach (var f in Directory.EnumerateFiles(TempDir))
            {
                try { File.Delete(f); } catch { /* in uso: riprovo al prossimo avvio */ }
            }
        }
        catch { }
    }
}

/// <summary>Piccolo archivio chiave/valore salvato in settings.json.</summary>
public static class Settings
{
    private static readonly string FilePath = Path.Combine(AppInfo.DataDir, "settings.json");
    private static Dictionary<string, string>? _values;

    private static Dictionary<string, string> Values
    {
        get
        {
            if (_values != null) return _values;
            try
            {
                _values = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch
            {
                _values = new();
            }
            return _values;
        }
    }

    public static string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public static string Get(string key, string fallback) => Get(key) ?? fallback;

    public static void Set(string key, string? value)
    {
        if (value == null) Values.Remove(key);
        else Values[key] = value;
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Salvataggio impostazioni");
        }
    }
}

public static class Util
{
    /// <summary>Cultura della lingua dell'interfaccia (numeri e date).</summary>
    public static CultureInfo It => L.Culture;

    public static string HumanSize(double bytes)
    {
        string[] units = { "byte", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (Math.Abs(bytes) >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes:0} byte" : bytes.ToString("0.0", It) + " " + units[i];
    }

    public static string HumanTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Number(long n) => n.ToString("N0", It);

    public static string Date(DateTime dt) => dt == default ? "—" : dt.ToString("g", It);

    public static void OpenExternal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Dlg.Error(L.T($"Impossibile aprire il file:\n{ex.Message}"));
        }
    }

    public static void Reveal(string path) =>
        Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"");

    public static void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Restituisce un percorso libero aggiungendo " (1)", " (2)"... se necessario.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var cand = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
    }

    public static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
