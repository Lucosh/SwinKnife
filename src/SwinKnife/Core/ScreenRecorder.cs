using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace SwinKnife.Core;

/// <summary>Registrazione dello schermo in MP4 con FFmpeg (gdigrab), con audio facoltativo dal microfono.</summary>
public sealed partial class ScreenRecorder : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _clock = new();
    private bool _stopping;

    public string Output { get; }
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>FFmpeg si è fermato da solo (errore).</summary>
    public event Action<string>? Failed;

    [GeneratedRegex("\"(.+?)\" \\(audio\\)")]
    private static partial Regex AudioLine();

    /// <summary>Dispositivi audio DirectShow (microfoni, "Stereo Mix"...).</summary>
    public static List<string> AudioDevices(string ffmpeg)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return AudioLine().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <param name="area">Rettangolo in pixel fisici assoluti (coordinate del desktop virtuale).</param>
    public ScreenRecorder(string ffmpeg, Int32Rect area, int fps, bool cursor, string? audioDevice, string output)
    {
        Output = output;
        // H.264 in 4:2:0 vuole dimensioni pari
        var w = area.Width & ~1;
        var h = area.Height & ~1;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var args = new List<string>
        {
            "-hide_banner", "-y", "-thread_queue_size", "512", "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", cursor ? "1" : "0",
            "-offset_x", area.X.ToString(), "-offset_y", area.Y.ToString(), "-video_size", $"{w}x{h}", "-i", "desktop",
        };
        if (audioDevice != null) args.AddRange(["-thread_queue_size", "512", "-f", "dshow", "-i", "audio=" + audioDevice]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-r", fps.ToString()]);
        if (audioDevice != null) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_log)
            {
                _log.AppendLine(e.Data);
                if (_log.Length > 20_000) _log.Remove(0, 10_000);
            }
        };
        _process.Exited += (_, _) =>
        {
            if (!_stopping) Failed?.Invoke(LastLines());
        };
    }

    public void Start()
    {
        _process.Start();
        _process.BeginErrorReadLine();
        _clock.Start();
    }

    private string LastLines()
    {
        lock (_log)
            return string.Join("\n", _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
    }

    /// <summary>Ferma la registrazione chiudendo correttamente il file.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        _clock.Stop();
        if (_process.HasExited) return;
        try
        {
            await _process.StandardInput.WriteAsync('q');
            await _process.StandardInput.FlushAsync();
        }
        catch { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await _process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { _process.Kill(true); } catch { }
        }
        if (!File.Exists(Output) || new FileInfo(Output).Length == 0)
            throw new IOException(L.T("La registrazione non è stata salvata.\n") + LastLines());
    }

    /// <summary>Converte un video in GIF animata con tavolozza ottimizzata.</summary>
    public static void ToGif(string ffmpeg, string video, string gif, int fps, int maxWidth, Action<double> progress, CancellationToken ct)
    {
        var duration = Ffmpeg.Duration(ffmpeg, video);
        Ffmpeg.Run(ffmpeg,
        [
            "-i", video, "-vf",
            $"fps={fps},scale='min({maxWidth},iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
            "-loop", "0", gif,
        ], duration, progress, ct);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _stopping = true;
            try { _process.Kill(true); } catch { }
        }
        _process.Dispose();
    }
}
