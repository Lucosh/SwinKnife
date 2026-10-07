using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SwinKnife.Core;

/// <summary>Individua (o scarica) ffmpeg ed esegue conversioni audio/video con avanzamento.</summary>
public static partial class Ffmpeg
{
    public static readonly string LocalDir = Path.Combine(AppInfo.DataDir, "ffmpeg");
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public static string? Find()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(LocalDir, "ffmpeg.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Scarica ffmpeg (build ufficiale BtbN) nella cartella dati dell'app.</summary>
    public static async Task<string> DownloadAsync(IProgress<(double, string)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(LocalDir);
        var zipPath = Path.Combine(AppInfo.TempDir, "ffmpeg.zip");
        Directory.CreateDirectory(AppInfo.TempDir);
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SwinKnife/" + AppInfo.Version);
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress?.Report((total > 0 ? done / (double)total : -1, L.T($"Download di FFmpeg: {Util.HumanSize(done)}")));
            }
        }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new IOException(L.T("L'archivio scaricato non contiene ffmpeg.exe"));
            entry.ExtractToFile(Path.Combine(LocalDir, "ffmpeg.exe"), true);
        }
        File.Delete(zipPath);
        return Path.Combine(LocalDir, "ffmpeg.exe");
    }

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    public static double Duration(string exe, string src)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(src);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var m = DurationRegex().Match(err);
        if (!m.Success) return 0;
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
               double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"Video: .*?, (\d{2,5})x(\d{2,5})")]
    private static partial Regex SizeRegex();

    /// <summary>Durata, risoluzione e presenza dell'audio di un file multimediale.</summary>
    public static (double duration, int width, int height, bool audio) Probe(string exe, string src)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false, StandardErrorEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(src);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        double d = 0;
        var m = DurationRegex().Match(err);
        if (m.Success)
            d = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var sz = SizeRegex().Match(err);
        return (d, sz.Success ? int.Parse(sz.Groups[1].Value) : 0, sz.Success ? int.Parse(sz.Groups[2].Value) : 0, err.Contains("Audio:"));
    }

    /// <summary>Trova FFmpeg o lo scarica (chiedendo conferma all'utente tramite la funzione passata).</summary>
    public static async Task<string?> EnsureAsync(Func<bool> confirm, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        if (Find() is { } exe) return exe;
        if (!confirm()) return null;
        return await DownloadAsync(progress, ct);
    }

    public static void Run(string exe, IEnumerable<string> args, double duration, Action<double> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-hide_banner", "-y", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) err.AppendLine(e.Data);
        };
        p.BeginErrorReadLine();
        using var reg = ct.Register(() =>
        {
            try { p.Kill(true); } catch { }
        });
        string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            if (duration > 0 && (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms=")) &&
                long.TryParse(line[(line.IndexOf('=') + 1)..], out var us))
                progress(Math.Clamp(us / 1e6 / duration, 0, 1));
        }
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            var msg = err.ToString().Trim();
            throw new IOException(string.IsNullOrEmpty(msg) ? L.T($"ffmpeg ha restituito il codice {p.ExitCode}") : msg);
        }
    }
}
