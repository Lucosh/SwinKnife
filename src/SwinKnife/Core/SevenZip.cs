using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SwinKnife.Core;

/// <summary>Creazione di archivi .7z con 7-Zip installato oppure con 7zr.exe (scaricato al primo uso, ~600 KB).</summary>
public static partial class SevenZip
{
    public static readonly string LocalDir = Path.Combine(AppInfo.DataDir, "7zip");
    private const string DownloadUrl = "https://www.7-zip.org/a/7zr.exe";

    public static string? Find()
    {
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var b = Environment.GetEnvironmentVariable(env);
            if (b == null) continue;
            var p = Path.Combine(b, "7-Zip", "7z.exe");
            if (File.Exists(p)) return p;
        }
        var local = Path.Combine(LocalDir, "7zr.exe");
        return File.Exists(local) ? local : null;
    }

    public static async Task<string> DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(LocalDir);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwinKnife/" + AppInfo.Version);
        var bytes = await http.GetByteArrayAsync(DownloadUrl, ct);
        var dst = Path.Combine(LocalDir, "7zr.exe");
        await File.WriteAllBytesAsync(dst, bytes, ct);
        return dst;
    }

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex Percent();

    /// <summary>Crea un archivio 7z. level 1..9, password facoltativa (AES-256 con nomi cifrati).</summary>
    public static void Create(string exe, string archive, IEnumerable<string> items, int level, string? password,
        Action<double> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "a", "-t7z", $"-mx={level}", "-bsp1", "-bso0", "-y", "-sccUTF-8" }) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(password))
        {
            psi.ArgumentList.Add("-p" + password);
            psi.ArgumentList.Add("-mhe=on");
        }
        psi.ArgumentList.Add(archive);
        foreach (var i in items) psi.ArgumentList.Add(i);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() =>
        {
            try { p.Kill(true); } catch { }
        });
        var err = p.StandardError.ReadToEndAsync(ct);
        var buf = new char[256];
        int n;
        while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
        {
            var m = Percent().Matches(new string(buf, 0, n));
            if (m.Count > 0) progress(int.Parse(m[^1].Groups[1].Value) / 100.0);
        }
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode > 1) throw new IOException("7-Zip: " + (err.IsCompletedSuccessfully ? err.Result.Trim() : $"codice {p.ExitCode}"));
    }
}
