using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SwinKnife.Core;

namespace SwinKnife.Pdf;

/// <summary>Operazioni sui PDF fatte con PDFsharp: immagini→PDF, compressione e password.</summary>
public static class PdfTools
{
    /// <summary>Una pagina A4 per immagine, orientamento automatico, margini di mezzo centimetro.</summary>
    public static void ImagesToPdf(IReadOnlyList<string> files, string dst, Action<double, string>? progress = null)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < files.Count; i++)
        {
            progress?.Invoke(i / (double)files.Count, L.T($"Pagina {i + 1}: {Path.GetFileName(files[i])}"));
            using var img = ImageIO.Load(files[i]);
            byte[] data;
            if (img.HasAlpha)
            {
                img.Format = MagickFormat.Png;
                data = img.ToByteArray();
            }
            else if (Formats.Ext(files[i]) is ".jpg" or ".jpeg" && img.Orientation is OrientationType.TopLeft or OrientationType.Undefined)
            {
                data = File.ReadAllBytes(files[i]); // JPEG originale senza ricompressione
            }
            else
            {
                img.Quality = 92;
                img.Format = MagickFormat.Jpeg;
                data = img.ToByteArray();
            }
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(img.Width > img.Height ? 842 : 595);
            page.Height = XUnit.FromPoint(img.Width > img.Height ? 595 : 842);
            using var ms = new MemoryStream(data);
            using var ximg = XImage.FromStream(ms);
            using var gfx = XGraphics.FromPdfPage(page);
            const double m = 14;
            var aw = page.Width.Point - 2 * m;
            var ah = page.Height.Point - 2 * m;
            var s = Math.Min(aw / img.Width, ah / img.Height);
            var w = img.Width * s;
            var h = img.Height * s;
            gfx.DrawImage(ximg, m + (aw - w) / 2, m + (ah - h) / 2, w, h);
        }
        doc.Save(dst);
    }

    public static byte[] Encrypt(byte[] pdf, string userPassword, string? ownerPassword = null)
    {
        using var input = new MemoryStream(pdf);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = ownerPassword ?? userPassword;
        doc.SecurityHandler.SetEncryptionToV5();
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    public enum CompressionLevel { Light, Medium, Strong }

    /// <summary>Ricomprime le immagini (JPEG) riducendone risoluzione e qualità e comprime i flussi.</summary>
    public static byte[] Compress(byte[] pdf, CompressionLevel level, Action<string>? progress = null)
    {
        using var input = new MemoryStream(pdf);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        if (level != CompressionLevel.Light)
        {
            var (maxSide, quality) = level == CompressionLevel.Medium ? (1800u, 72u) : (1200u, 55u);
            var images = doc.Internals.GetAllObjects().OfType<PdfDictionary>()
                .Where(d => d.Elements.GetName("/Subtype") == "/Image" && d.Stream != null).ToList();
            var n = 0;
            foreach (var img in images)
            {
                n++;
                progress?.Invoke(L.T($"Immagine {n} di {images.Count}"));
                try { RecompressImage(img, maxSide, quality); }
                catch (Exception ex) { AppInfo.Log(ex, "Compressione immagine PDF"); }
            }
        }
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    private static void RecompressImage(PdfDictionary img, uint maxSide, uint quality)
    {
        if (img.Elements.ContainsKey("/SMask") || img.Elements.ContainsKey("/Mask") || img.Elements.GetBoolean("/ImageMask")) return;
        var filter = img.Elements["/Filter"];
        var filterName = filter is PdfName pn ? pn.Value : filter is PdfArray { Elements.Count: 1 } arr ? arr.Elements.GetName(0) : null;
        var width = img.Elements.GetInteger("/Width");
        var height = img.Elements.GetInteger("/Height");
        if (width * height < 250_000) return;
        var old = img.Stream.Value;
        MagickImage? m = null;
        if (filterName == "/DCTDecode")
        {
            m = new MagickImage(old);
        }
        else if (filterName == "/FlateDecode" && img.Elements.GetInteger("/BitsPerComponent") == 8)
        {
            var cs = img.Elements["/ColorSpace"] is PdfName c ? c.Value : null;
            var mapping = cs switch { "/DeviceRGB" => PixelMapping.RGB, "/DeviceGray" => (PixelMapping?)null, _ => (PixelMapping?)null };
            if (cs is not ("/DeviceRGB" or "/DeviceGray")) return;
            if (!img.Stream.TryUncompress()) return;
            var raw = img.Stream.Value;
            m = new MagickImage();
            if (mapping is { } map)
                m.ReadPixels(raw, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, map));
            else
                m.ReadPixels(raw, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "R"));
            if (cs == "/DeviceGray") m.ColorType = ColorType.Grayscale;
        }
        if (m == null) return;
        using (m)
        {
            if (m.Width > maxSide || m.Height > maxSide) m.Resize(new MagickGeometry(maxSide, maxSide));
            var cmyk = m.ColorSpace == ColorSpace.CMYK;
            if (cmyk) return; // i JPEG CMYK richiedono /Decode invertito: li lascio stare
            m.Quality = quality;
            m.Format = MagickFormat.Jpeg;
            m.Strip();
            var data = m.ToByteArray();
            if (data.Length >= old.Length && filterName == "/DCTDecode") return;
            img.Stream.Value = data;
            img.Elements.SetName("/Filter", "/DCTDecode");
            img.Elements.Remove("/DecodeParms");
            img.Elements.SetInteger("/Width", (int)m.Width);
            img.Elements.SetInteger("/Height", (int)m.Height);
            img.Elements.SetInteger("/BitsPerComponent", 8);
            img.Elements.SetName("/ColorSpace", m.ColorType == ColorType.Grayscale ? "/DeviceGray" : "/DeviceRGB");
        }
    }
}
