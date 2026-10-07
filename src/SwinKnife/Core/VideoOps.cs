using System.Globalization;

namespace SwinKnife.Core;

/// <summary>Operazioni veloci sui video con FFmpeg: taglia, comprimi, estrai l'audio, GIF, togli l'audio, ruota, unisci.</summary>
public static class VideoOps
{
    private static string T(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Argomenti per limitare l'elaborazione al tratto [from, to] (in secondi; to &lt;= 0 = fino alla fine).</summary>
    private static List<string> Range(string src, double from, double to)
    {
        var a = new List<string>();
        if (from > 0) a.AddRange(["-ss", T(from)]);
        a.AddRange(["-i", src]);
        if (to > 0) a.AddRange(["-t", T(to - Math.Max(0, from))]);
        return a;
    }

    private static readonly string[] H264 = ["-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p", "-movflags", "+faststart"];

    public static List<string> Trim(string src, string dst, double from, double to, bool fast)
    {
        var a = Range(src, from, to);
        if (fast) a.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero"]);
        else a.AddRange([.. H264, "-crf", "20", "-c:a", "aac", "-b:a", "160k"]);
        a.Add(dst);
        return a;
    }

    /// <summary>Ricodifica per stare sotto una dimensione (in MB), riducendo la risoluzione se il bitrate è basso.</summary>
    public static List<string> Compress(string src, string dst, double from, double to, double duration, double targetMb, int width, int height, bool audio)
    {
        var len = (to > 0 ? to : duration) - Math.Max(0, from);
        var a = Range(src, from, to);
        var audioKbps = audio ? 96 : 0;
        var videoKbps = Math.Max(150, (int)(targetMb * 8 * 1024 * 0.94 / Math.Max(1, len)) - audioKbps);
        // con pochi kbps una risoluzione minore viene molto meglio
        var maxSide = videoKbps < 600 ? 640 : videoKbps < 1400 ? 854 : videoKbps < 3000 ? 1280 : 1920;
        var scale = Math.Max(width, height) > maxSide
            ? (width >= height ? $"scale={maxSide}:-2" : $"scale=-2:{maxSide}")
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        a.AddRange(["-vf", scale, .. H264, "-b:v", $"{videoKbps}k", "-maxrate", $"{videoKbps * 3 / 2}k", "-bufsize", $"{videoKbps * 2}k"]);
        if (audio) a.AddRange(["-c:a", "aac", "-b:a", $"{audioKbps}k", "-ac", "2"]);
        else a.Add("-an");
        a.Add(dst);
        return a;
    }

    public static List<string> ExtractAudio(string src, string dst, double from, double to)
    {
        var a = Range(src, from, to);
        a.Add("-vn");
        a.AddRange(Path.GetExtension(dst).ToLowerInvariant() == ".mp3" ? ["-c:a", "libmp3lame", "-q:a", "2"] : ["-c:a", "aac", "-b:a", "192k"]);
        a.Add(dst);
        return a;
    }

    public static List<string> Gif(string src, string dst, double from, double to, int width, int fps)
    {
        var a = Range(src, from, to);
        a.AddRange(["-vf", $"fps={fps},scale={width}:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0", dst]);
        return a;
    }

    public static List<string> Mute(string src, string dst, double from, double to)
    {
        var a = Range(src, from, to);
        a.AddRange(["-an", "-c:v", "copy", dst]);
        return a;
    }

    /// <summary>Rotazione di 90° (orario), 180° o 270°.</summary>
    public static List<string> Rotate(string src, string dst, int degrees)
    {
        var vf = degrees switch { 90 => "transpose=1", 180 => "transpose=1,transpose=1", _ => "transpose=2" };
        return ["-i", src, "-vf", vf, .. H264, "-crf", "20", "-c:a", "copy", dst];
    }

    /// <summary>Unisce più video uno dopo l'altro portandoli alla stessa risoluzione del primo.</summary>
    public static List<string> Concat(IReadOnlyList<string> files, string dst, int width, int height, bool allAudio)
    {
        var a = new List<string>();
        foreach (var f in files) a.AddRange(["-i", f]);
        var w = width / 2 * 2;
        var h = height / 2 * 2;
        var filter = new System.Text.StringBuilder();
        for (var i = 0; i < files.Count; i++)
            filter.Append($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v{i}];");
        for (var i = 0; i < files.Count; i++) filter.Append(allAudio ? $"[v{i}][{i}:a]" : $"[v{i}]");
        filter.Append($"concat=n={files.Count}:v=1:a={(allAudio ? 1 : 0)}[v]" + (allAudio ? "[a]" : ""));
        a.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]"]);
        if (allAudio) a.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "160k"]);
        a.AddRange([.. H264, "-crf", "21", dst]);
        return a;
    }
}
