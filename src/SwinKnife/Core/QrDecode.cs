using ImageMagick;
using ZXing;
using ZXing.Common;

namespace SwinKnife.Core;

/// <summary>Legge il contenuto di un QR code (o di un codice a barre) da un'immagine.</summary>
public static class QrDecode
{
    private static BarcodeReaderGeneric Reader() => new()
    {
        AutoRotate = true,
        Options = new DecodingOptions { TryHarder = true, TryInverted = true },
    };

    /// <summary>Decodifica da byte RGB grezzi (3 byte per pixel).</summary>
    public static string? FromRgb(byte[] rgb, int width, int height)
    {
        if (rgb.Length < width * height * 3) return null;
        var src = new RGBLuminanceSource(rgb, width, height, RGBLuminanceSource.BitmapFormat.RGB24);
        return Reader().Decode(src)?.Text;
    }

    public static string? FromFile(string path)
    {
        using var img = new MagickImage(path);
        return FromMagick(img);
    }

    public static string? FromMagick(MagickImage img)
    {
        var text = DecodeGray(img);
        if (text != null) return text;
        // secondo tentativo ingrandendo le immagini piccole (QR fotografati da lontano)
        if (Math.Min(img.Width, img.Height) < 600)
        {
            using var big = (MagickImage)img.Clone();
            var scale = 900.0 / Math.Min(img.Width, img.Height);
            big.Resize((uint)(img.Width * scale), (uint)(img.Height * scale));
            return DecodeGray(big);
        }
        return null;
    }

    private static string? DecodeGray(MagickImage img)
    {
        using var g = (MagickImage)img.Clone();
        g.Alpha(AlphaOption.Remove);        // appiattisce la trasparenza su bianco
        var w = (int)g.Width;
        var h = (int)g.Height;
        using var px = g.GetPixels();
        var rgb = px.ToByteArray(PixelMapping.RGB);  // pixel grezzi: 3 byte per pixel
        return rgb == null ? null : FromRgb(rgb, w, h);
    }
}
