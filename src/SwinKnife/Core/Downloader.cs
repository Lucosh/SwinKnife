using System.IO.Compression;
using System.Net.Http;

namespace SwinKnife.Core;

/// <summary>Scarica (ed eventualmente estrae) i componenti opzionali al primo uso.</summary>
public static class Downloader
{
    public static async Task FileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwinKnife/" + AppInfo.Version);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }
        File.Move(tmp, dest, true);
    }

    public static async Task ZipAsync(string url, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var tmp = Path.Combine(destDir, "download.zip.part");
        await FileAsync(url, tmp, progress, ct);
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(tmp);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var outPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!outPath.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                entry.ExtractToFile(outPath, true);
            }
        }, ct);
        try { File.Delete(tmp); } catch { }
    }
}
