using System.Diagnostics;
using System.Text;

namespace SwinKnife.Core;

/// <summary>
/// Trascrizione audio/video offline con whisper.cpp (binario + modello ggml scaricati al primo uso).
/// Converte prima l'audio in WAV 16 kHz con ffmpeg, poi genera testo e sottotitoli.
/// </summary>
public static class Whisper
{
    private const string BinUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
    private const long ModelSize = 147_951_465; // ggml-base.bin

    public static readonly string Dir = Path.Combine(AppInfo.DataDir, "whisper");
    public static string ModelPath => Path.Combine(Dir, "ggml-base.bin");

    public static string? FindExe()
    {
        if (!Directory.Exists(Dir)) return null;
        foreach (var name in new[] { "whisper-cli.exe", "main.exe", "whisper.exe" })
        {
            var hit = Directory.EnumerateFiles(Dir, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    public static bool Ready => FindExe() != null && File.Exists(ModelPath);

    public static async Task EnsureAsync(IProgress<(double, string)> progress, CancellationToken ct)
    {
        if (FindExe() == null)
        {
            await Downloader.ZipAsync(BinUrl, Dir, new Progress<double>(p => progress.Report((p * 0.5, L.T("Scarico il motore di trascrizione…")))), ct);
            if (FindExe() == null) throw new InvalidOperationException(L.T("Il motore di trascrizione non è stato trovato dopo il download."));
        }
        if (!File.Exists(ModelPath))
            await Downloader.FileAsync(ModelUrl, ModelPath, new Progress<double>(p => progress.Report((0.5 + p * 0.5, L.T("Scarico il modello linguistico (~145 MB)…")))), ct);
    }

    /// <summary>Trascrive un file multimediale. Restituisce (percorso .txt, percorso .srt).</summary>
    public static async Task<(string txt, string srt)> TranscribeAsync(string media, string language, string ffmpeg,
        Action<string> onLine, CancellationToken ct)
    {
        var exe = FindExe() ?? throw new InvalidOperationException(L.T("Motore di trascrizione non disponibile."));
        Directory.CreateDirectory(AppInfo.TempDir);
        var wav = Path.Combine(AppInfo.TempDir, "whisper_" + Guid.NewGuid().ToString("N") + ".wav");
        var outBase = Path.ChangeExtension(media, null) + "_trascrizione";

        try
        {
            // 1) audio in WAV mono 16 kHz
            Ffmpeg.Run(ffmpeg, ["-i", media, "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav], 0, _ => { }, ct);
            // 2) whisper
            var args = new List<string> { "-m", ModelPath, "-f", wav, "-otxt", "-osrt", "-of", outBase };
            if (!string.IsNullOrEmpty(language) && language != "auto") { args.Add("-l"); args.Add(language); }
            await RunAsync(exe, args, onLine, ct);
        }
        finally { try { File.Delete(wav); } catch { } }

        return (outBase + ".txt", outBase + ".srt");
    }

    private static async Task RunAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new InvalidOperationException(L.T($"La trascrizione è fallita (codice {p.ExitCode})."));
    }
}
