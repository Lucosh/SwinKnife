using System.Runtime.InteropServices.WindowsRuntime;
using SwinKnife.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WRect = System.Windows.Rect;

namespace SwinKnife.Core;

/// <summary>Riconoscimento del testo con il motore OCR integrato in Windows (nessun download).</summary>
public static class Ocr
{
    public static bool Available => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    private static OcrEngine Engine() =>
        OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(L.Culture.Name))
        ?? OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException(L.T("Nessuna lingua OCR installata in Windows (Impostazioni › Lingua)."));

    public sealed record Word(WRect Box, string Text);

    /// <summary>Parole riconosciute con il loro riquadro in pixel.</summary>
    public static async Task<(List<Word> words, string text)> Recognize(byte[] bgra, int width, int height)
    {
        var max = (int)OcrEngine.MaxImageDimension;
        if (width > max || height > max)
        {
            // il motore ha un limite di dimensione: riduco l'immagine e riscalo i riquadri
            var s = Math.Min(max / (double)width, max / (double)height);
            using var img = ImageIO.FromBgra(bgra, width, height);
            img.Resize((uint)(width * s), (uint)(height * s));
            var (w2, t2) = await Recognize(ImageIO.ToBgra(img), (int)img.Width, (int)img.Height).ConfigureAwait(false);
            return (w2.Select(w => new Word(new WRect(w.Box.X / s, w.Box.Y / s, w.Box.Width / s, w.Box.Height / s), w.Text)).ToList(), t2);
        }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await Engine().RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);
        var words = new List<Word>();
        var lines = new List<string>();
        foreach (var line in result.Lines)
        {
            lines.Add(line.Text);
            foreach (var w in line.Words)
                words.Add(new Word(new WRect(w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height), w.Text));
        }
        return (words, string.Join("\n", lines));
    }

    public static async Task<string> ImageToText(string path)
    {
        byte[] px;
        int w, h;
        using (var img = ImageIO.Load(path))
        {
            px = ImageIO.ToBgra(img);
            w = (int)img.Width;
            h = (int)img.Height;
        }
        return (await Recognize(px, w, h).ConfigureAwait(false)).text;
    }

    /// <summary>
    /// Aggiunge uno strato di testo invisibile alle pagine scansionate: il PDF resta identico
    /// ma diventa ricercabile e il testo si può selezionare e copiare.
    /// </summary>
    public static async Task<int> MakeSearchable(PdfDoc doc, Action<double, string>? progress, CancellationToken ct)
    {
        const double dpi = 300;
        var pagesDone = 0;
        var n = doc.PageCount;
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i / (double)n, L.T($"OCR pagina {i + 1} di {n}…"));
            if (doc.PageText(i).Trim().Length > 30) continue; // la pagina ha già del testo
            var scale = dpi / 72;
            var (px, w, h) = doc.RenderRaw(i, scale);
            var (words, _) = await Recognize(px, w, h);
            if (words.Count == 0) continue;
            doc.AddOcrLayer(i, words.Select(wd => (new WRect(wd.Box.X / scale, wd.Box.Y / scale, wd.Box.Width / scale, wd.Box.Height / scale), wd.Text + " ")));
            pagesDone++;
        }
        progress?.Invoke(1, L.T("OCR completato"));
        return pagesDone;
    }
}
