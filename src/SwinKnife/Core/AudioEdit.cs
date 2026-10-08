using System.Globalization;

namespace SwinKnife.Core;

/// <summary>Modifiche semplici all'audio con ffmpeg: taglio, dissolvenze, volume, normalizzazione, riduzione rumore.</summary>
public static class AudioEdit
{
    public sealed record Options(
        double? TrimStart, double? TrimEnd, double FadeIn, double FadeOut,
        bool Normalize, bool Denoise, double GainDb, string Format);

    public static string Extension(string format) => format switch
    {
        "mp3" => ".mp3", "wav" => ".wav", "m4a" => ".m4a", "flac" => ".flac", _ => ".mp3",
    };

    public static void Process(string ffmpeg, string src, string dst, Options o, double duration,
        Action<double> progress, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>();

        // taglio sull'ingresso (preciso per la ricodifica)
        if (o.TrimStart is > 0) { args.Add("-ss"); args.Add(o.TrimStart.Value.ToString(inv)); }
        args.Add("-i"); args.Add(src);
        double? clipLen = null;
        if (o.TrimEnd is { } end && end > (o.TrimStart ?? 0))
        {
            clipLen = end - (o.TrimStart ?? 0);
            args.Add("-t"); args.Add(clipLen.Value.ToString(inv));
        }

        var filters = new List<string>();
        if (o.Denoise) filters.Add("afftdn=nf=-25");
        if (Math.Abs(o.GainDb) > 0.01) filters.Add($"volume={o.GainDb.ToString(inv)}dB");
        if (o.FadeIn > 0) filters.Add($"afade=t=in:st=0:d={o.FadeIn.ToString(inv)}");
        if (o.FadeOut > 0)
        {
            var total = clipLen ?? (duration - (o.TrimStart ?? 0));
            var st = Math.Max(0, total - o.FadeOut);
            filters.Add($"afade=t=out:st={st.ToString(inv)}:d={o.FadeOut.ToString(inv)}");
        }
        if (o.Normalize) filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        if (filters.Count > 0) { args.Add("-af"); args.Add(string.Join(",", filters)); }

        switch (o.Format)
        {
            case "wav": args.AddRange(["-c:a", "pcm_s16le"]); break;
            case "flac": args.AddRange(["-c:a", "flac"]); break;
            case "m4a": args.AddRange(["-c:a", "aac", "-b:a", "256k"]); break;
            default: args.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]); break;
        }
        args.Add(dst);

        var prog = clipLen ?? Math.Max(0, duration - (o.TrimStart ?? 0));
        Ffmpeg.Run(ffmpeg, args, prog > 0 ? prog : duration, progress, ct);
    }
}
