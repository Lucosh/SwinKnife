using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace SwinKnife.Core;

/// <summary>Lettura/scrittura immagini con Magick.NET (HEIC, AVIF, RAW, PSD, SVG...) e conversione verso WPF.</summary>
public static class ImageIO
{
    public static bool CanWrite(MagickFormat f) =>
        MagickNET.SupportedFormats.Any(x => x.Format == f && x.SupportsWriting);

    public static MagickImage Load(string path)
    {
        var settings = new MagickReadSettings();
        if (Formats.KindOf(path) == FileKind.Svg)
        {
            settings.Density = new Density(144);
            settings.BackgroundColor = MagickColors.Transparent;
        }
        var img = new MagickImage(path, settings);
        img.AutoOrient();
        return img;
    }

    public static BitmapSource ToBitmapSource(IMagickImage<byte> image)
    {
        var w = (int)image.Width;
        var h = (int)image.Height;
        var bytes = image.ToByteArray(MagickFormat.Bgra);
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        src.Freeze();
        return src;
    }

    /// <summary>Pixel BGRA (non premoltiplicati) di un'immagine Magick.</summary>
    public static byte[] ToBgra(IMagickImage<byte> image) => image.ToByteArray(MagickFormat.Bgra);

    public static MagickImage FromBgra(byte[] bgra, int width, int height)
    {
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA);
        var img = new MagickImage();
        img.ReadPixels(bgra, settings);
        return img;
    }

    public static (BitmapSource image, Dictionary<string, string> info) LoadForDisplay(string path, uint maxSide = 12000)
    {
        using var img = Load(path);
        var info = new Dictionary<string, string>
        {
            [L.T("Formato")] = img.Format.ToString().ToUpperInvariant(),
            [L.T("Dimensioni")] = L.T($"{img.Width} × {img.Height} px"),
            [L.T("Profondità colore")] = $"{img.Depth} bit",
        };
        foreach (var kv in ExifSummary(img)) info[kv.Key] = kv.Value;
        if (img.Width > maxSide || img.Height > maxSide) img.Resize(new MagickGeometry(maxSide, maxSide));
        return (ToBitmapSource(img), info);
    }

    /// <summary>Fotogrammi di una GIF/WEBP animata (null se l'immagine è statica).</summary>
    public static List<(BitmapSource frame, int delayMs)>? LoadAnimation(string path)
    {
        using var coll = new MagickImageCollection(path);
        if (coll.Count <= 1) return null;
        coll.Coalesce();
        var frames = new List<(BitmapSource, int)>();
        foreach (var f in coll.Take(500))
            frames.Add((ToBitmapSource(f), Math.Max(20, (int)f.AnimationDelay * 10)));
        return frames;
    }

    public static Dictionary<string, string> ExifSummary(IMagickImage img)
    {
        var d = new Dictionary<string, string>();
        var exif = img.GetExifProfile();
        if (exif == null) return d;
        void Add(string label, object? v)
        {
            var s = v?.ToString()?.Trim('\0', ' ');
            if (!string.IsNullOrEmpty(s)) d[label] = s;
        }
        Add(L.T("Fotocamera"), $"{exif.GetValue(ExifTag.Make)?.Value} {exif.GetValue(ExifTag.Model)?.Value}".Trim());
        Add(L.T("Scattata il"), exif.GetValue(ExifTag.DateTimeOriginal)?.Value);
        var exp = exif.GetValue(ExifTag.ExposureTime)?.Value;
        if (exp is { } e && e.Denominator != 0)
        {
            var sec = e.ToDouble();
            Add(L.T("Esposizione"), sec < 1 ? $"1/{Math.Round(1 / sec)} s" : $"{sec:0.#} s");
        }
        var fn = exif.GetValue(ExifTag.FNumber)?.Value;
        if (fn is { } f && f.Denominator != 0) Add(L.T("Diaframma"), $"f/{f.ToDouble():0.0}");
        var iso = exif.GetValue(ExifTag.ISOSpeedRatings)?.Value;
        if (iso is { Length: > 0 }) Add("ISO", iso[0]);
        var fl = exif.GetValue(ExifTag.FocalLength)?.Value;
        if (fl is { } l && l.Denominator != 0) Add(L.T("Focale"), $"{l.ToDouble():0} mm");
        Add(L.T("Obiettivo"), exif.GetValue(ExifTag.LensModel)?.Value);
        Add(L.T("Software"), exif.GetValue(ExifTag.Software)?.Value);
        if (exif.GetValue(ExifTag.GPSLatitude) != null) d["GPS"] = "presente";
        return d;
    }

    public static readonly Dictionary<string, MagickFormat> WriteFormats = new()
    {
        ["jpg"] = MagickFormat.Jpeg, ["png"] = MagickFormat.Png, ["webp"] = MagickFormat.WebP,
        ["avif"] = MagickFormat.Avif, ["jxl"] = MagickFormat.Jxl, ["bmp"] = MagickFormat.Bmp,
        ["gif"] = MagickFormat.Gif, ["tiff"] = MagickFormat.Tiff, ["ico"] = MagickFormat.Icon,
        ["pdf"] = MagickFormat.Pdf, ["heic"] = MagickFormat.Heic,
    };

    public static bool IsLossy(string fmt) => fmt is "jpg" or "webp" or "avif" or "jxl" or "heic";

    /// <summary>Prepara e salva un'immagine nel formato richiesto.</summary>
    public static void Save(MagickImage img, string dst, string fmt, uint quality = 90, bool keepExif = true)
    {
        if (!keepExif)
        {
            foreach (var p in new[] { "exif", "xmp", "iptc", "8bim" }) img.RemoveProfile(p);
        }
        switch (fmt)
        {
            case "jpg":
            case "bmp":
            case "pdf":
                if (img.HasAlpha)
                {
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaOption.Remove);
                }
                if (fmt == "jpg")
                {
                    img.Quality = quality;
                    img.Settings.Interlace = Interlace.Jpeg;
                }
                break;
            case "webp":
            case "avif":
            case "jxl":
            case "heic":
                img.Quality = quality;
                break;
            case "tiff":
                img.Settings.Compression = CompressionMethod.LZW;
                break;
            case "ico":
            {
                var side = Math.Max(img.Width, img.Height);
                img.BackgroundColor = MagickColors.Transparent;
                img.Extent(side, side, Gravity.Center);
                if (side != 256) img.Resize(256, 256);
                img.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,24,16");
                break;
            }
        }
        img.Format = WriteFormats[fmt];
        img.Write(dst);
    }
}
