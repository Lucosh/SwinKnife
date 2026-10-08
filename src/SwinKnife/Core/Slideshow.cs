using System.Globalization;
using System.Text;
using ImageMagick;

namespace SwinKnife.Core;

/// <summary>Monta una serie di foto in un video (con musica facoltativa) usando ffmpeg.</summary>
public static class Slideshow
{
    public static (int w, int h) Resolution(string name) => name switch
    {
        "720p" => (1280, 720),
        "4k" => (3840, 2160),
        _ => (1920, 1080),
    };

    public static void Build(string ffmpeg, IReadOnlyList<string> images, double secondsEach, string? music,
        int width, int height, string dst, Action<double> progress, CancellationToken ct)
    {
        if (images.Count == 0) throw new InvalidOperationException(L.T("Aggiungi almeno una foto."));
        Directory.CreateDirectory(AppInfo.TempDir);
        var temps = new List<string>();
        var listFile = Path.Combine(AppInfo.TempDir, $"slideshow_{Guid.NewGuid():N}.txt");
        try
        {
            // 1) porta ogni foto esattamente alla risoluzione scelta (così ffmpeg non si lamenta di dimensioni diverse)
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            foreach (var img in images)
            {
                ct.ThrowIfCancellationRequested();
                var tmp = Path.Combine(AppInfo.TempDir, $"slide_{Guid.NewGuid():N}.jpg");
                using (var m = new MagickImage(img))
                {
                    m.AutoOrient();
                    m.Alpha(AlphaOption.Remove);
                    m.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = false });
                    m.BackgroundColor = MagickColors.Black;
                    m.Extent((uint)width, (uint)height, Gravity.Center);
                    m.Quality = 92;
                    m.Format = MagickFormat.Jpeg;
                    m.Write(tmp);
                }
                temps.Add(tmp);
                sb.Append("file '").Append(tmp.Replace("'", "'\\''")).Append("'\n");
                sb.Append("duration ").Append(secondsEach.ToString(inv)).Append('\n');
            }
            // il concat demuxer ignora la durata dell'ultima: ripeto l'ultimo file
            sb.Append("file '").Append(temps[^1].Replace("'", "'\\''")).Append("'\n");
            File.WriteAllText(listFile, sb.ToString(), new UTF8Encoding(false));

            // 2) ffmpeg
            var args = new List<string> { "-f", "concat", "-safe", "0", "-i", listFile };
            var hasMusic = !string.IsNullOrEmpty(music) && File.Exists(music);
            if (hasMusic) { args.Add("-i"); args.Add(music!); }
            args.AddRange(["-vf", "fps=30,format=yuv420p", "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"]);
            if (hasMusic) args.AddRange(["-c:a", "aac", "-b:a", "192k", "-shortest"]);
            else args.Add("-an");
            args.Add(dst);

            var duration = images.Count * secondsEach;
            Ffmpeg.Run(ffmpeg, args, duration, progress, ct);
        }
        finally
        {
            foreach (var t in temps) { try { File.Delete(t); } catch { } }
            try { File.Delete(listFile); } catch { }
        }
    }
}
