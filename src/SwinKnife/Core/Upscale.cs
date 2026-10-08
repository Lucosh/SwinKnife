using System.Diagnostics;
using System.Text;

namespace SwinKnife.Core;

/// <summary>Ingrandimento foto con AI (Real-ESRGAN ncnn-vulkan, scaricato al primo uso). Usa la GPU.</summary>
public static class Upscale
{
    private const string BinUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
    public static readonly string Dir = Path.Combine(AppInfo.DataDir, "realesrgan");

    public static string? FindExe() =>
        Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "realesrgan-ncnn-vulkan.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    public static bool Ready => FindExe() != null;

    public static async Task EnsureAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (FindExe() == null)
        {
            await Downloader.ZipAsync(BinUrl, Dir, progress, ct);
            if (FindExe() == null) throw new InvalidOperationException(L.T("Lo strumento di ingrandimento non è stato trovato dopo il download."));
        }
    }

    /// <summary>scale 2, 3 o 4. model: realesrgan-x4plus (foto) o realesrgan-x4plus-anime.</summary>
    public static async Task RunAsync(string input, string output, int scale, string model, Action<string> onLine, CancellationToken ct)
    {
        var exe = FindExe() ?? throw new InvalidOperationException(L.T("Strumento di ingrandimento non disponibile."));
        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in new[] { "-i", input, "-o", output, "-s", scale.ToString(), "-n", model })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new InvalidOperationException(L.T("Ingrandimento non riuscito. Potrebbe servire una scheda grafica compatibile con Vulkan."));
    }
}
