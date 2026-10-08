using ImageMagick;

namespace SwinKnife.Core;

/// <summary>Trasforma le foto di un documento in pagine pulite (ritaglio prospettico no, ma raddrizza e migliora).</summary>
public static class DocScan
{
    public enum Mode { Color, Gray, BlackWhite }

    /// <summary>Migliora una singola foto di documento e la salva come JPEG.</summary>
    public static void Enhance(string src, string dst, Mode mode, bool deskew)
    {
        using var img = new MagickImage(src);
        img.AutoOrient();
        img.Alpha(AlphaOption.Remove);

        if (deskew)
        {
            try { img.Deskew(new Percentage(40)); } catch { /* immagini già dritte o troppo rumorose */ }
        }

        switch (mode)
        {
            case Mode.Color:
                img.BrightnessContrast(new Percentage(4), new Percentage(22));
                img.AutoLevel();
                img.Sharpen();
                break;
            case Mode.Gray:
                img.Grayscale(PixelIntensityMethod.Rec709Luminance);
                img.AutoLevel();
                img.BrightnessContrast(new Percentage(5), new Percentage(28));
                img.Sharpen();
                break;
            case Mode.BlackWhite:
                img.Grayscale(PixelIntensityMethod.Rec709Luminance);
                img.AutoLevel();
                try { img.AutoThreshold(AutoThresholdMethod.OTSU); }
                catch { img.Threshold(new Percentage(55)); }
                break;
        }

        img.Quality = 90;
        img.Format = MagickFormat.Jpeg;
        img.Write(dst);
    }

    /// <summary>Migliora tutte le foto e le unisce in un unico PDF (una pagina per foto).</summary>
    public static void BuildPdf(IReadOnlyList<string> sources, string dstPdf, Mode mode, bool deskew,
        Action<double, string>? progress = null)
    {
        Directory.CreateDirectory(AppInfo.TempDir);
        var temps = new List<string>();
        try
        {
            for (var i = 0; i < sources.Count; i++)
            {
                progress?.Invoke(i / (double)(sources.Count + 1), L.T($"Elaboro la pagina {i + 1}…"));
                var tmp = Path.Combine(AppInfo.TempDir, $"docscan_{Guid.NewGuid():N}.jpg");
                Enhance(sources[i], tmp, mode, deskew);
                temps.Add(tmp);
            }
            progress?.Invoke((double)sources.Count / (sources.Count + 1), L.T("Creo il PDF…"));
            Pdf.PdfTools.ImagesToPdf(temps, dstPdf);
            progress?.Invoke(1, L.T("Fatto."));
        }
        finally
        {
            foreach (var t in temps) { try { File.Delete(t); } catch { } }
        }
    }
}
