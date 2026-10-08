using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SwinKnife.Core;

/// <summary>Scarica video o audio dal web tramite yt-dlp (scaricato al primo uso). Usa ffmpeg per unire/convertire.</summary>
public static partial class YtDlp
{
    private const string ExeUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public static readonly string Dir = Path.Combine(AppInfo.DataDir, "yt-dlp");
    public static string ExePath => Path.Combine(Dir, "yt-dlp.exe");
    public static bool Ready => File.Exists(ExePath);

    public static async Task EnsureAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (!Ready) await Downloader.FileAsync(ExeUrl, ExePath, progress, ct);
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex Percent();

    /// <summary>Scarica nel percorso indicato. audioOnly = estrai solo l'audio in MP3.</summary>
    public static async Task DownloadAsync(string url, string outDir, bool audioOnly, string ffmpegDir,
        Action<double> progress, Action<string> onLine, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var args = new List<string>
        {
            "--no-playlist", "--newline", "--ffmpeg-location", ffmpegDir,
            "-o", Path.Combine(outDir, "%(title)s.%(ext)s"),
        };
        if (audioOnly) { args.Add("-x"); args.Add("--audio-format"); args.Add("mp3"); args.Add("--audio-quality"); args.Add("0"); }
        else { args.Add("-f"); args.Add("bv*+ba/b"); args.Add("--merge-output-format"); args.Add("mp4"); }
        args.Add(url);

        var psi = new ProcessStartInfo(ExePath)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            onLine(e.Data);
            var m = Percent().Match(e.Data);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                progress(pct / 100.0);
        };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { onLine(e.Data); err.AppendLine(e.Data); } };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new InvalidOperationException(L.T("Download non riuscito:\n") + err.ToString().Trim());
    }
}
