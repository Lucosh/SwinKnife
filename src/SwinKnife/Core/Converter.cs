using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ImageMagick;
using SwinKnife.Pdf;

namespace SwinKnife.Core;

public sealed class ConvertOptions
{
    public uint Quality { get; set; } = 90;
    /// <summary>("pct", 50) oppure ("max", 1920); null = dimensione originale.</summary>
    public (string kind, int value)? Resize { get; set; }
    public bool KeepExif { get; set; } = true;
    public bool MergePdf { get; set; }
    public string MergeName { get; set; } = "immagini_unite.pdf";
    public int Dpi { get; set; } = 150;
    public bool PdfToWordWithWord { get; set; } = true;
    public char CsvDelimiter { get; set; } = ';';
    public string AudioBitrate { get; set; } = "192k";
    public int? VideoHeight { get; set; }
    public int Crf { get; set; } = 23;
    public int GifWidth { get; set; } = 480;
}

public sealed record ConvertResult(List<string> Outputs, List<(string file, string error)> Errors, bool Cancelled);

/// <summary>Motore di conversione tra formati.</summary>
public static class Converter
{
    public static readonly Dictionary<string, string> Labels = new()
    {
        ["jpg"] = L.T("JPG – immagine"), ["png"] = L.T("PNG – immagine con trasparenza"), ["webp"] = L.T("WEBP – immagine web"),
        ["avif"] = L.T("AVIF – immagine moderna"), ["jxl"] = L.T("JPEG XL"), ["bmp"] = L.T("BMP – bitmap"), ["gif"] = "GIF",
        ["tiff"] = L.T("TIFF – immagine"), ["ico"] = L.T("ICO – icona di Windows"), ["heic"] = L.T("HEIC – formato iPhone"),
        ["pdf"] = L.T("PDF – documento"), ["docx"] = L.T("DOCX – Word"), ["odt"] = L.T("ODT – OpenDocument testo"),
        ["rtf"] = L.T("RTF – testo formattato"), ["txt"] = L.T("TXT – solo testo"), ["html"] = L.T("HTML – pagina web"),
        ["xlsx"] = L.T("XLSX – Excel"), ["csv"] = L.T("CSV – tabella"), ["json"] = L.T("JSON – dati"), ["ods"] = L.T("ODS – OpenDocument foglio"),
        ["pptx"] = L.T("PPTX – PowerPoint"), ["mp3"] = L.T("MP3 – audio"), ["wav"] = L.T("WAV – audio non compresso"),
        ["flac"] = L.T("FLAC – audio senza perdita"), ["ogg"] = L.T("OGG – audio"), ["m4a"] = L.T("M4A – audio AAC"), ["opus"] = L.T("OPUS – audio"),
        ["mp4"] = L.T("MP4 – video H.264"), ["mkv"] = L.T("MKV – video"), ["avi"] = L.T("AVI – video"), ["mov"] = L.T("MOV – video"),
        ["webm"] = L.T("WEBM – video web"), ["ocr-txt"] = L.T("TXT – testo riconosciuto (OCR)"), ["ocr-pdf"] = L.T("PDF ricercabile (OCR)"),
    };

    private static List<string>? _imageTargets;

    public static List<string> ImageTargets => _imageTargets ??=
        new[] { "jpg", "png", "webp", "avif", "heic", "jxl", "bmp", "gif", "tiff", "ico", "pdf" }
            .Where(t => ImageIO.CanWrite(ImageIO.WriteFormats[t])).ToList();

    public static List<string> TargetsFor(string path)
    {
        var kind = Formats.KindOf(path);
        var own = Formats.Ext(path).TrimStart('.') switch { "jpeg" => "jpg", "tif" => "tiff", "htm" => "html", var e => e };
        List<string> t = kind switch
        {
            FileKind.Image or FileKind.Svg or FileKind.Raw => [.. ImageTargets],
            FileKind.Pdf => ["docx", "png", "jpg", "txt", "ocr-pdf"],
            FileKind.Word => ["pdf", "docx", "odt", "rtf", "txt", "html"],
            FileKind.Excel => ["pdf", "xlsx", "csv", "json", "ods"],
            FileKind.PowerPoint => ["pdf", "pptx", "png", "jpg"],
            FileKind.Table => ["xlsx", "json", "pdf"],
            FileKind.Json => ["csv", "xlsx"],
            FileKind.Markdown => ["pdf", "html"],
            FileKind.Html => ["pdf", "txt"],
            FileKind.Text => ["pdf"],
            FileKind.Audio => ["mp3", "wav", "flac", "ogg", "m4a", "opus"],
            FileKind.Video => ["mp4", "mkv", "avi", "mov", "webm", "gif", "mp3", "wav"],
            _ => [],
        };
        if (!Formats.IsImageLike(kind)) t.Remove(own);
        if (Formats.IsImageLike(kind) && OcrAvailable) t.Add("ocr-txt");
        if (!OcrAvailable) t.Remove("ocr-pdf");
        return t;
    }

    private static bool? _ocr;
    private static bool OcrAvailable => _ocr ??= SafeOcr();

    private static bool SafeOcr()
    {
        try { return Ocr.Available; }
        catch { return false; }
    }

    public static bool NeedsFfmpeg(IEnumerable<string> files) =>
        files.Any(f => Formats.KindOf(f) is FileKind.Audio or FileKind.Video);

    /// <summary>Converte un elenco di file. Va eseguito su un thread STA in background (per Office).</summary>
    public static ConvertResult RunBatch(IReadOnlyList<string> files, string target, string? outDir, ConvertOptions opt,
        Action<double, string> progress, CancellationToken ct)
    {
        var outputs = new List<string>();
        var errors = new List<(string, string)>();
        using var office = new OfficeSession();
        if (target == "pdf" && opt.MergePdf && files.Count > 1 && files.All(f => Formats.IsImageLike(Formats.KindOf(f))))
        {
            var dir = outDir ?? Path.GetDirectoryName(files[0])!;
            var dst = Util.UniquePath(Path.Combine(dir, opt.MergeName));
            PdfTools.ImagesToPdf(files, dst, (f, m) =>
            {
                ct.ThrowIfCancellationRequested();
                progress(f, m);
            });
            outputs.Add(dst);
            return new ConvertResult(outputs, errors, false);
        }
        for (var i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) return new ConvertResult(outputs, errors, true);
            var f = files[i];
            var lo = i / (double)files.Count;
            var hi = (i + 1) / (double)files.Count;
            var label = $"[{i + 1}/{files.Count}] {Path.GetFileName(f)}";
            void Report(double frac, string? msg = null) =>
                progress(frac < 0 ? -1 : lo + (hi - lo) * Math.Clamp(frac, 0, 1), msg ?? label);
            Report(0);
            try
            {
                var dir = outDir ?? Path.GetDirectoryName(f)!;
                Directory.CreateDirectory(dir);
                outputs.AddRange(ConvertFile(f, target, dir, opt, office, Report, ct));
            }
            catch (OperationCanceledException)
            {
                return new ConvertResult(outputs, errors, true);
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, $"Conversione {f} → {target}");
                errors.Add((f, ex.Message));
            }
        }
        return new ConvertResult(outputs, errors, false);
    }

    private static List<string> ConvertFile(string src, string target, string dir, ConvertOptions opt, OfficeSession office,
        Action<double, string?> report, CancellationToken ct)
    {
        var kind = Formats.KindOf(src);
        var stem = Path.GetFileNameWithoutExtension(src);
        string Out(string ext) => Util.UniquePath(Path.Combine(dir, $"{stem}.{ext}"));

        switch (kind)
        {
            case FileKind.Image or FileKind.Svg or FileKind.Raw when target == "ocr-txt":
            {
                report(-1, L.T($"Riconoscimento del testo in {Path.GetFileName(src)}"));
                var dst = Out("txt");
                File.WriteAllText(dst, Ocr.ImageToText(src).GetAwaiter().GetResult());
                return [dst];
            }
            case FileKind.Image or FileKind.Svg or FileKind.Raw:
                report(-1, L.T($"Conversione di {Path.GetFileName(src)}"));
                return [ConvertImage(src, target, Out(target), opt)];

            case FileKind.Pdf:
                return target switch
                {
                    "png" or "jpg" => PdfToImages(src, target, dir, opt.Dpi, report, ct),
                    "txt" => [PdfToText(src, Out("txt"))],
                    "ocr-pdf" => [OcrPdf(src, Util.UniquePath(Path.Combine(dir, stem + L.T("_ricercabile") + ".pdf")), report, ct)],
                    "docx" => [PdfToDocx(src, Out("docx"), opt, office, report)],
                    _ => throw new NotSupportedException(),
                };

            case FileKind.Word:
            {
                report(-1, L.T($"Conversione di {Path.GetFileName(src)} con Word…"));
                var dst = Out(target);
                office.Convert(src, dst, target);
                return [dst];
            }

            case FileKind.Excel:
            {
                report(-1, L.T($"Conversione di {Path.GetFileName(src)}…"));
                if (target is "pdf" or "xlsx" or "ods")
                {
                    var dst = Out(target);
                    office.Convert(src, dst, target);
                    return [dst];
                }
                var path = src;
                string? tmp = null;
                if (Formats.Ext(src) is not (".xlsx" or ".xlsm"))
                {
                    tmp = Path.Combine(AppInfo.TempDir, $"{Guid.NewGuid():N}.xlsx");
                    Directory.CreateDirectory(AppInfo.TempDir);
                    office.Convert(src, tmp, "xlsx");
                    path = tmp;
                }
                try { return Tables.Write(Tables.Read(path), Out(target), target, opt.CsvDelimiter); }
                finally { if (tmp != null) File.Delete(tmp); }
            }

            case FileKind.PowerPoint:
            {
                report(-1, L.T($"Conversione di {Path.GetFileName(src)} con PowerPoint…"));
                if (target is "pdf" or "pptx")
                {
                    var dst = Out(target);
                    office.Convert(src, dst, target);
                    return [dst];
                }
                Directory.CreateDirectory(AppInfo.TempDir);
                var pdf = Path.Combine(AppInfo.TempDir, $"{Guid.NewGuid():N}.pdf");
                office.Convert(src, pdf, "pdf");
                try
                {
                    var renamed = Path.Combine(AppInfo.TempDir, stem + ".pdf");
                    File.Move(pdf, renamed, true);
                    pdf = renamed;
                    return PdfToImages(pdf, target, dir, opt.Dpi, report, ct);
                }
                finally { File.Delete(pdf); }
            }

            case FileKind.Table or FileKind.Json:
                if (target == "pdf")
                {
                    var html = TableToHtml(Tables.Read(src), stem);
                    var dst = Out("pdf");
                    HtmlPdf.ToPdf(null, html, dst);
                    return [dst];
                }
                return Tables.Write(Tables.Read(src), Out(target), target, opt.CsvDelimiter);

            case FileKind.Markdown:
            {
                var html = HtmlPdf.MarkdownToHtml(Formats.ReadText(src).text, stem);
                if (target == "html")
                {
                    var dst = Out("html");
                    File.WriteAllText(dst, html);
                    return [dst];
                }
                var pdf = Out("pdf");
                HtmlPdf.ToPdf(null, html, pdf, Path.GetDirectoryName(src));
                return [pdf];
            }

            case FileKind.Html:
                if (target == "txt")
                {
                    var dst = Out("txt");
                    File.WriteAllText(dst, HtmlPdf.HtmlToText(Formats.ReadText(src).text));
                    return [dst];
                }
                else
                {
                    var dst = Out("pdf");
                    HtmlPdf.ToPdf(src, null, dst);
                    return [dst];
                }

            case FileKind.Text:
            {
                var dst = Out("pdf");
                HtmlPdf.ToPdf(null, HtmlPdf.TextToHtml(Formats.ReadText(src).text), dst);
                return [dst];
            }

            case FileKind.Audio or FileKind.Video:
                return [ConvertMedia(src, target, Out(target), opt, report, ct)];
        }
        throw new NotSupportedException(L.T($"Conversione {Formats.Ext(src)} → {target} non supportata."));
    }

    // ------------------------------------------------------------------ immagini
    public static string ConvertImage(string src, string target, string dst, ConvertOptions opt)
    {
        var fmt = ImageIO.WriteFormats[target];
        if (target is "gif" or "webp" && Formats.Ext(src) is ".gif" or ".webp")
        {
            using var coll = new MagickImageCollection(src);
            if (coll.Count > 1)
            {
                coll.Coalesce();
                foreach (var frame in coll)
                {
                    ApplyResize(frame, opt);
                    if (target == "webp") frame.Quality = opt.Quality;
                }
                if (target == "gif") coll.Optimize();
                coll.Write(dst, fmt);
                return dst;
            }
        }
        using var img = ImageIO.Load(src);
        ApplyResize(img, opt);
        ImageIO.Save(img, dst, target, opt.Quality, opt.KeepExif);
        return dst;
    }

    private static void ApplyResize(IMagickImage<byte> img, ConvertOptions opt)
    {
        if (opt.Resize is not { } r) return;
        if (r.kind == "pct") img.Resize(new Percentage(r.value));
        else if (img.Width > r.value || img.Height > r.value) img.Resize(new MagickGeometry((uint)r.value, (uint)r.value));
    }

    // ------------------------------------------------------------------ PDF
    private static readonly object PdfLock = new();

    public static List<string> PdfToImages(string src, string fmt, string dir, int dpi, Action<double, string?> report, CancellationToken ct)
    {
        lock (PdfLock)
        {
            using var doc = PdfDoc.Open(src);
            var n = doc.PageCount;
            var stem = Path.GetFileNameWithoutExtension(src);
            if (n > 1)
            {
                dir = Util.UniquePath(Path.Combine(dir, stem + L.T("_pagine")));
                Directory.CreateDirectory(dir);
            }
            var digits = Math.Max(3, n.ToString().Length);
            var outs = new List<string>();
            for (var i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                report(i / (double)n, L.T($"Pagina {i + 1} di {n}"));
                var (px, w, h) = doc.RenderRaw(i, dpi / 72.0);
                using var img = ImageIO.FromBgra(px, w, h);
                img.Alpha(AlphaOption.Off);
                img.Density = new Density(dpi);
                var name = n > 1 ? $"{stem}_pag{(i + 1).ToString().PadLeft(digits, '0')}.{fmt}" : $"{stem}.{fmt}";
                var dst = Util.UniquePath(Path.Combine(dir, name));
                ImageIO.Save(img, dst, fmt, 90);
                outs.Add(dst);
            }
            return outs;
        }
    }

    private static string OcrPdf(string src, string dst, Action<double, string?> report, CancellationToken ct)
    {
        using var doc = PdfDoc.Open(src);
        Ocr.MakeSearchable(doc, (f, m) => report(f, m), ct).GetAwaiter().GetResult();
        File.WriteAllBytes(dst, doc.Save());
        return dst;
    }

    public static string PdfToText(string src, string dst)
    {
        lock (PdfLock)
        {
            using var doc = PdfDoc.Open(src);
            using var w = new StreamWriter(dst, false, new System.Text.UTF8Encoding(false));
            for (var i = 0; i < doc.PageCount; i++)
            {
                w.WriteLine(L.T($"===== Pagina {i + 1} ====="));
                w.WriteLine(doc.PageText(i));
            }
            return dst;
        }
    }

    private static string PdfToDocx(string src, string dst, ConvertOptions opt, OfficeSession office, Action<double, string?> report)
    {
        if (opt.PdfToWordWithWord && OfficeSession.WordAvailable)
        {
            report(-1, L.T("Conversione con Microsoft Word (mantiene impaginazione e immagini)…"));
            office.Word(Path.GetFullPath(src), Path.GetFullPath(dst), "docx");
            return dst;
        }
        report(-1, L.T("Estrazione del testo…"));
        lock (PdfLock)
        {
            using var pdf = PdfDoc.Open(src);
            using var word = WordprocessingDocument.Create(dst, WordprocessingDocumentType.Document);
            var main = word.AddMainDocumentPart();
            var body = new Body();
            for (var i = 0; i < pdf.PageCount; i++)
            {
                foreach (var line in pdf.PageText(i).Split('\n'))
                    body.Append(new Paragraph(new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve })));
                if (i < pdf.PageCount - 1)
                    body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        return dst;
    }

    private static string TableToHtml(List<Tables.Sheet> sheets, string title)
    {
        var sb = new System.Text.StringBuilder($"<!doctype html><html><head><meta charset='utf-8'><style>{HtmlPdf.Css} body{{max-width:none}} td,th{{font-size:9pt}}</style></head><body>");
        foreach (var s in sheets)
        {
            sb.Append($"<h2>{System.Net.WebUtility.HtmlEncode(sheets.Count > 1 ? s.Name : title)}</h2><table>");
            for (var r = 0; r < s.Rows.Count; r++)
            {
                var tag = r == 0 ? "th" : "td";
                sb.Append("<tr>");
                foreach (var v in s.Rows[r]) sb.Append($"<{tag}>{System.Net.WebUtility.HtmlEncode(v?.ToString() ?? "")}</{tag}>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }
        return sb.Append("</body></html>").ToString();
    }

    // ------------------------------------------------------------------ audio e video
    private static readonly Dictionary<string, string[]> AudioCodecs = new()
    {
        ["mp3"] = ["-c:a", "libmp3lame"], ["wav"] = ["-c:a", "pcm_s16le"], ["flac"] = ["-c:a", "flac"],
        ["ogg"] = ["-c:a", "libvorbis"], ["m4a"] = ["-c:a", "aac"], ["opus"] = ["-c:a", "libopus"],
    };

    public static string ConvertMedia(string src, string target, string dst, ConvertOptions opt, Action<double, string?> report, CancellationToken ct)
    {
        var exe = Ffmpeg.Find() ?? throw new InvalidOperationException(L.T("FFmpeg non è installato."));
        var duration = Ffmpeg.Duration(exe, src);
        var args = new List<string> { "-i", src };
        if (AudioCodecs.TryGetValue(target, out var codec))
        {
            args.AddRange(["-vn", "-map_metadata", "0"]);
            args.AddRange(codec);
            if (target is "mp3" or "m4a" or "ogg" or "opus") args.AddRange(["-b:a", opt.AudioBitrate]);
        }
        else if (target == "gif")
        {
            args.AddRange(["-vf", $"fps=12,scale={opt.GifWidth}:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0"]);
        }
        else
        {
            if (opt.VideoHeight is { } h) args.AddRange(["-vf", $"scale=-2:'min({h},ih)'"]);
            switch (target)
            {
                case "mp4" or "mkv" or "mov":
                    args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", opt.Crf.ToString(), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", opt.AudioBitrate]);
                    if (target != "mkv") args.AddRange(["-movflags", "+faststart"]);
                    break;
                case "avi":
                    args.AddRange(["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "4", "-c:a", "libmp3lame", "-b:a", opt.AudioBitrate]);
                    break;
                case "webm":
                    args.AddRange(["-c:v", "libvpx-vp9", "-crf", Math.Min(63, opt.Crf + 8).ToString(), "-b:v", "0", "-deadline", "good",
                        "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"]);
                    break;
            }
        }
        args.Add(dst);
        Ffmpeg.Run(exe, args, duration, f => report(f, null), ct);
        return dst;
    }
}
