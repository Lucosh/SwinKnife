using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace SwinKnife.Core;

public sealed record UpdateInfo(Version Version, string PageUrl, string? SetupUrl, string? SetupName, string? SumsUrl);

/// <summary>
/// Aggiornamenti automatici dalle release di GitHub: controlla l'ultima versione pubblicata, scarica l'installer,
/// ne verifica l'impronta SHA-256 (SHA256SUMS.txt della release) e lo avvia in modalità silenziosa;
/// l'installer chiude SwinKnife, lo aggiorna e lo riapre.
/// </summary>
public static class Updater
{
    public const string Repo = "Lucosh/SwinKnife";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases/latest";
    private const string AppId = "{6F2B8E1C-4A1D-4C7B-9E5A-5D3C2B1A9F70}_is1"; // AppId dell'installer (installer/SwinKnife.iss)

    public static Version Current => Version.TryParse(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    public static bool AutoCheck
    {
        get => Settings.Get("update.auto") != "0";
        set => Settings.Set("update.auto", value ? null : "0");
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SwinKnife", AppInfo.Version));
        return http;
    }

    /// <summary>L'ultima release se è più recente di questa versione, altrimenti null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = Client();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // nessuna release pubblicata
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        Settings.Set("update.last", DateTime.UtcNow.ToString("o"));
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var latest) || latest <= Current) return null;
        string? setupUrl = null, setupName = null, sumsUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("SwinKnife-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                setupUrl = url;
                setupName = name;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
        }
        return new UpdateInfo(latest, root.GetProperty("html_url").GetString() ?? ReleasesPage, setupUrl, setupName, sumsUrl);
    }

    /// <summary>True se questa copia è stata installata con l'installer (e quindi si può aggiornare da sola).</summary>
    public static bool InstalledWithSetup()
    {
        var dir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}");
            if (k?.GetValue("InstallLocation") is string loc && Path.GetFullPath(loc).TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Scarica l'installer e controlla che l'impronta SHA-256 corrisponda a quella pubblicata.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct = default)
    {
        if (u.SetupUrl == null || u.SetupName == null) throw new InvalidOperationException(L.T("La nuova versione non contiene l'installer."));
        using var http = Client();
        http.Timeout = TimeSpan.FromMinutes(30);
        var dir = Path.Combine(AppInfo.TempDir, "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, u.SetupName);
        using (var resp = await http.GetAsync(u.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(done / (double)total);
            }
        }
        if (u.SumsUrl != null)
        {
            var sums = await http.GetStringAsync(u.SumsUrl, ct);
            var expected = sums.Split('\n').Select(l => l.Trim().Split("  ", 2)).FirstOrDefault(p => p.Length == 2 && p[1].Trim() == u.SetupName)?[0];
            await using var fs = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (expected == null || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(path);
                throw new InvalidDataException(L.T("Il file scaricato non corrisponde a quello pubblicato: aggiornamento annullato."));
            }
        }
        return path;
    }

    /// <summary>Avvia l'installer in modalità silenziosa: chiude SwinKnife, aggiorna e lo riapre (/RELAUNCH=1).</summary>
    public static void RunInstaller(string setup) =>
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1") { UseShellExecute = true });

    /// <summary>Al massimo una volta al giorno, se l'utente non l'ha disattivato.</summary>
    public static bool DueForCheck() =>
        AutoCheck && !(DateTime.TryParse(Settings.Get("update.last"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                       && DateTime.UtcNow - last < TimeSpan.FromHours(20));
}
