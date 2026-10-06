using DocumentFormat.OpenXml.Packaging;
using SwinKnife.Pdf;

namespace SwinKnife.Core;

/// <summary>Estrae il testo leggibile da documenti di ogni tipo (per Gemini e simili).</summary>
public static class FileText
{
    public static string Extract(string path, int maxChars = 60_000)
    {
        var text = Formats.KindOf(path) switch
        {
            FileKind.Pdf => FromPdf(path),
            FileKind.Word when Formats.Ext(path) is ".docx" or ".docm" => FromDocx(path),
            FileKind.PowerPoint when Formats.Ext(path) is ".pptx" or ".pptm" => FromPptx(path),
            FileKind.Word or FileKind.PowerPoint or FileKind.Excel when Formats.Ext(path) is not (".xlsx" or ".xlsm") => ViaOffice(path),
            FileKind.Excel or FileKind.Table => FromTable(path),
            FileKind.Html => HtmlPdf.HtmlToText(Formats.ReadText(path, 8 << 20).text),
            FileKind.Text or FileKind.Markdown or FileKind.Json => Formats.ReadText(path, 8 << 20).text,
            FileKind.Image or FileKind.Raw => Ocr.ImageToText(path).GetAwaiter().GetResult(),
            _ => Formats.LooksLikeText(File.ReadAllBytes(path).AsSpan(0, (int)Math.Min(8192, new FileInfo(path).Length)))
                ? Formats.ReadText(path, 8 << 20).text : "",
        };
        text = text.Trim();
        return text.Length > maxChars ? text[..maxChars] + L.T("\n[…testo troncato…]") : text;
    }

    private static string FromPdf(string path)
    {
        using var doc = PdfDoc.Open(path);
        var text = string.Join("\n\n", Enumerable.Range(0, doc.PageCount).Select(doc.PageText));
        if (text.Trim().Length > 0 || !Ocr.Available) return text;
        // PDF scansionato: riconosco il testo (al massimo 30 pagine)
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Math.Min(doc.PageCount, 30); i++)
        {
            var (px, w, h) = doc.RenderRaw(i, 200 / 72.0);
            sb.AppendLine(Ocr.Recognize(px, w, h).GetAwaiter().GetResult().text).AppendLine();
        }
        return sb.ToString();
    }

    private static string FromDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body == null ? "" : string.Join("\n", body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
    }

    private static string FromPptx(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var slides = doc.PresentationPart?.SlideParts ?? [];
        return string.Join("\n\n", slides.Select((s, i) =>
            L.T($"[Diapositiva {i + 1}]\n") + string.Join("\n", s.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>().Select(p => p.InnerText))));
    }

    private static string FromTable(string path) =>
        string.Join("\n\n", Tables.Read(path, 2000).Select(s =>
            (s.Name + "\n") + string.Join("\n", s.Rows.Select(r => string.Join("\t", r.Select(v => v?.ToString() ?? ""))))));

    private static string ViaOffice(string path)
    {
        Directory.CreateDirectory(AppInfo.TempDir);
        var pdf = Path.Combine(AppInfo.TempDir, $"{Guid.NewGuid():N}.pdf");
        Sta.Run(() =>
        {
            using var office = new OfficeSession();
            office.Convert(path, pdf, "pdf");
        }).GetAwaiter().GetResult();
        try { return FromPdf(pdf); }
        finally { File.Delete(pdf); }
    }
}
