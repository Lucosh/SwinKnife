using ImageMagick;

namespace SwinKnife.Core;

/// <summary>Impostazioni per elaborare tante foto insieme (per e-mail, web, chat).</summary>
public sealed class PhotoBatchOptions
{
    public int MaxSide { get; init; }                 // 0 = dimensioni originali
    public string Format { get; init; } = "";         // "" = stesso formato (JPG per i formati non scrivibili)
    public uint Quality { get; init; } = 82;
    public MetadataMode Metadata { get; init; } = MetadataMode.RemoveGps;
    public string OutputDir { get; init; } = "";
}

public enum MetadataMode { Keep, RemoveGps, RemoveAll }

public static class PhotoBatch
{
    private static readonly HashSet<string> Writable = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "webp", "avif", "gif", "bmp", "tiff", "tif", "jxl" };

    /// <summary>Elabora una foto e restituisce il percorso creato.</summary>
    public static string Process(string src, PhotoBatchOptions o)
    {
        using var img = ImageIO.Load(src); // già raddrizzata secondo l'EXIF
        if (o.MaxSide > 0 && Math.Max(img.Width, img.Height) > o.MaxSide)
            img.Resize(new MagickGeometry((uint)o.MaxSide, (uint)o.MaxSide) { Greater = true });
        var ext = Path.GetExtension(src).TrimStart('.').ToLowerInvariant();
        var fmt = o.Format.Length > 0 ? o.Format : Writable.Contains(ext) ? ext switch { "jpeg" => "jpg", "tif" => "tiff", _ => ext } : "jpg";
        switch (o.Metadata)
        {
            case MetadataMode.RemoveAll:
                foreach (var p in new[] { "exif", "xmp", "iptc", "8bim" }) img.RemoveProfile(p);
                break;
            case MetadataMode.RemoveGps:
                if (img.GetExifProfile() is { } exif)
                {
                    foreach (var v in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal)).ToList())
                        exif.RemoveValue(v.Tag);
                    img.SetProfile(exif);
                }
                img.RemoveProfile("xmp"); // l'XMP può contenere a sua volta la posizione
                break;
        }
        Directory.CreateDirectory(o.OutputDir);
        var dst = Util.UniquePath(Path.Combine(o.OutputDir, Path.GetFileNameWithoutExtension(src) + "." + fmt));
        ImageIO.Save(img, dst, fmt, o.Quality, keepExif: o.Metadata != MetadataMode.RemoveAll);
        return dst;
    }

    /// <summary>True se la foto contiene la posizione GPS.</summary>
    public static bool HasGps(string path)
    {
        try
        {
            using var img = new MagickImage();
            img.Ping(path);
            return img.GetExifProfile()?.Values.Any(v => v.Tag.ToString().StartsWith("GPSLat", StringComparison.Ordinal)) == true;
        }
        catch
        {
            return false;
        }
    }
}
