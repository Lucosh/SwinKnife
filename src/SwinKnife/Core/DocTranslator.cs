using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SwinKnife.Core;

public delegate Task<List<string>> Translator(IReadOnlyList<string> texts, CancellationToken ct);

/// <summary>
/// Traduce documenti mantenendo la formattazione: si traducono solo i testi, paragrafo per paragrafo,
/// e i tratti con formattazione diversa (grassetto, colori…) sono marcati con &lt;n&gt;…&lt;/n&gt; per rimetterli al loro posto.
/// </summary>
public static class DocTranslator
{
    public static readonly string[] Supported = [".docx", ".pptx", ".xlsx", ".txt", ".md", ".srt", ".pdf"];

    public static bool IsSupported(string path) => Supported.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string OutputPath(string src, string code, string? ext = null) =>
        Util.UniquePath(Path.Combine(Path.GetDirectoryName(src)!, $"{Path.GetFileNameWithoutExtension(src)}.{code}{ext ?? Path.GetExtension(src)}"));

    /// <summary>Traduce il file e restituisce i percorsi creati.</summary>
    public static async Task<List<string>> TranslateFile(string src, string code, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext == ".pdf") return await Pdf(src, code, t, progress, ct);
        var dst = OutputPath(src, code);
        switch (ext)
        {
            case ".docx":
                File.Copy(src, dst);
                await Docx(dst, t, progress, ct);
                break;
            case ".pptx":
                File.Copy(src, dst);
                await Pptx(dst, t, progress, ct);
                break;
            case ".xlsx":
                File.Copy(src, dst);
                await Xlsx(dst, t, progress, ct);
                break;
            case ".srt":
                await Srt(src, dst, t, progress, ct);
                break;
            default:
                await Text(src, dst, t, progress, ct, ext == ".md");
                break;
        }
        return [dst];
    }

    // ------------------------------------------------------------------ traduzione a gruppi
    /// <summary>Divide i testi in gruppi (per non superare i limiti del servizio) e li traduce, due gruppi alla volta.</summary>
    private static async Task<List<string>> All(List<string> texts, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        var result = texts.ToList();
        var todo = Enumerable.Range(0, texts.Count).Where(i => texts[i].Trim().Length > 0 && Regex.IsMatch(texts[i], @"\p{L}")).ToList();
        var batches = new List<List<int>>();
        var cur = new List<int>();
        var chars = 0;
        foreach (var i in todo)
        {
            if (cur.Count > 0 && (cur.Count >= 40 || chars + texts[i].Length > 6000))
            {
                batches.Add(cur);
                cur = new List<int>();
                chars = 0;
            }
            cur.Add(i);
            chars += texts[i].Length;
        }
        if (cur.Count > 0) batches.Add(cur);
        var done = 0;
        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (b, token) =>
        {
            var tr = await t(b.Select(i => texts[i]).ToList(), token);
            for (var k = 0; k < b.Count; k++) result[b[k]] = tr[k];
            var d = Interlocked.Increment(ref done);
            progress(d / (double)batches.Count, L.T($"Traduzione: {d} di {batches.Count} blocchi"));
        });
        return result;
    }

    private static readonly Regex Tag = new(@"<(\d+)>(.*?)</\1>", RegexOptions.Singleline);

    /// <summary>Testo con i segnaposto per i gruppi di formattazione (un solo gruppo = testo semplice).</summary>
    private static string Tagged(IReadOnlyList<string> groups) =>
        groups.Count == 1 ? groups[0] : string.Concat(groups.Select((g, i) => $"<{i + 1}>{g}</{i + 1}>"));

    /// <summary>Rimette il testo tradotto nei gruppi; se i segnaposto si sono persi, tutto va nel primo.</summary>
    private static string[] Untag(string translated, int count)
    {
        var parts = new string[count];
        Array.Fill(parts, "");
        if (count == 1)
        {
            parts[0] = translated;
            return parts;
        }
        var found = false;
        foreach (Match m in Tag.Matches(translated))
        {
            var i = int.Parse(m.Groups[1].Value) - 1;
            if (i >= 0 && i < count)
            {
                parts[i] += m.Groups[2].Value;
                found = true;
            }
        }
        if (!found) parts[0] = Regex.Replace(translated, @"</?\d+>", "");
        else
        {
            // testo rimasto fuori dai segnaposto (raro): lo aggiungo al primo gruppo
            var rest = Tag.Replace(translated, "").Trim();
            if (rest.Length > 0) parts[0] = (parts[0] + " " + rest).Trim();
        }
        return parts;
    }

    // ------------------------------------------------------------------ Word
    private static async Task Docx(string path, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        var main = doc.MainDocumentPart!;
        var roots = new List<OpenXmlPartRootElement?> { main.Document };
        roots.AddRange(main.HeaderParts.Select(p => (OpenXmlPartRootElement?)p.Header));
        roots.AddRange(main.FooterParts.Select(p => (OpenXmlPartRootElement?)p.Footer));
        roots.Add(main.FootnotesPart?.Footnotes);
        roots.Add(main.EndnotesPart?.Endnotes);
        roots.Add(main.WordprocessingCommentsPart?.Comments);
        var paragraphs = roots.Where(r => r != null).SelectMany(r => r!.Descendants<W.Paragraph>()).ToList();
        var items = new List<List<List<W.Text>>>();          // paragrafo → gruppi → testi
        var texts = new List<string>();
        foreach (var p in paragraphs)
        {
            // le run del paragrafo in ordine, anche dentro collegamenti e revisioni, ma non quelle delle caselle di testo
            // (che hanno paragrafi propri e vengono tradotte a parte)
            var runs = p.Descendants<W.Run>().Where(r => r.Elements<W.Text>().Any() && r.Ancestors<W.Paragraph>().First() == p).ToList();
            var groups = Group(runs, r => r.RunProperties?.OuterXml ?? "", r => r.Elements<W.Text>().ToList());
            if (groups.Count == 0) continue;
            items.Add(groups);
            texts.Add(Tagged(groups.Select(g => string.Concat(g.Select(x => x.Text))).ToList()));
        }
        var tr = await All(texts, t, progress, ct);
        for (var i = 0; i < items.Count; i++) Apply(items[i], Untag(tr[i], items[i].Count), (x, s) => { x.Text = s; x.Space = SpaceProcessingModeValues.Preserve; }, x => x.Text = "");
        main.Document.Save();
    }

    /// <summary>Raggruppa le "run" consecutive con la stessa formattazione.</summary>
    private static List<List<TText>> Group<TRun, TText>(List<TRun> runs, Func<TRun, string> format, Func<TRun, List<TText>> texts)
    {
        var groups = new List<List<TText>>();
        string? last = null;
        foreach (var r in runs)
        {
            var f = format(r);
            if (groups.Count == 0 || f != last) groups.Add(new List<TText>());
            groups[^1].AddRange(texts(r));
            last = f;
        }
        return groups;
    }

    private static void Apply<TText>(List<List<TText>> groups, string[] parts, Action<TText, string> set, Action<TText> clear)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            if (groups[g].Count == 0) continue;
            set(groups[g][0], parts[g]);
            foreach (var x in groups[g].Skip(1)) clear(x);
        }
    }

    // ------------------------------------------------------------------ PowerPoint
    private static async Task Pptx(string path, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        using var doc = PresentationDocument.Open(path, true);
        var parts = doc.PresentationPart!.SlideParts.Select(s => (OpenXmlPartRootElement)s.Slide)
            .Concat(doc.PresentationPart.SlideParts.Where(s => s.NotesSlidePart != null).Select(s => (OpenXmlPartRootElement)s.NotesSlidePart!.NotesSlide)).ToList();
        var items = new List<List<List<A.Text>>>();
        var texts = new List<string>();
        foreach (var p in parts.SelectMany(r => r.Descendants<A.Paragraph>()))
        {
            var runs = p.Elements<A.Run>().Where(r => r.Text != null).ToList();
            var groups = Group(runs, r => r.RunProperties?.OuterXml ?? "", r => new List<A.Text> { r.Text! });
            if (groups.Count == 0) continue;
            items.Add(groups);
            texts.Add(Tagged(groups.Select(g => string.Concat(g.Select(x => x.Text))).ToList()));
        }
        var tr = await All(texts, t, progress, ct);
        for (var i = 0; i < items.Count; i++) Apply(items[i], Untag(tr[i], items[i].Count), (x, s) => x.Text = s, x => x.Text = "");
        foreach (var r in parts) r.Save();
    }

    // ------------------------------------------------------------------ Excel
    private static async Task Xlsx(string path, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        using var wb = new XLWorkbook(path);
        var cells = wb.Worksheets.SelectMany(ws => ws.CellsUsed(c => c.DataType == XLDataType.Text && !c.HasFormula)).ToList();
        var tr = await All(cells.Select(c => c.GetString()).ToList(), t, progress, ct);
        for (var i = 0; i < cells.Count; i++)
            if (tr[i] != cells[i].GetString()) cells[i].Value = tr[i];
        wb.Save();
    }

    // ------------------------------------------------------------------ testo, Markdown, sottotitoli
    private static async Task Text(string src, string dst, Translator t, Action<double, string> progress, CancellationToken ct, bool markdown)
    {
        var (text, enc) = Formats.ReadText(src, 64 << 20);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var keep = new bool[lines.Length];
        var code = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (markdown && lines[i].TrimStart().StartsWith("```")) { code = !code; keep[i] = true; }
            else keep[i] = code;
        }
        var idx = Enumerable.Range(0, lines.Length).Where(i => !keep[i]).ToList();
        var tr = await All(idx.Select(i => lines[i]).ToList(), t, progress, ct);
        for (var k = 0; k < idx.Count; k++) lines[idx[k]] = tr[k];
        await File.WriteAllTextAsync(dst, string.Join(Environment.NewLine, lines), enc, ct);
    }

    private static async Task Srt(string src, string dst, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        var (text, enc) = Formats.ReadText(src, 32 << 20);
        var blocks = Regex.Split(text.Replace("\r\n", "\n").Trim(), @"\n\s*\n");
        var subs = new List<(int block, string body)>();
        var parsed = blocks.Select(b => b.Split('\n')).ToList();
        for (var i = 0; i < parsed.Count; i++)
            if (parsed[i].Length >= 3 && parsed[i][1].Contains("-->")) subs.Add((i, string.Join("\n", parsed[i].Skip(2))));
        var tr = await All(subs.Select(s => s.body).ToList(), t, progress, ct);
        for (var k = 0; k < subs.Count; k++)
            parsed[subs[k].block] = parsed[subs[k].block].Take(2).Concat(tr[k].Split('\n')).ToArray();
        var sb = new StringBuilder();
        foreach (var b in parsed) sb.Append(string.Join("\r\n", b)).Append("\r\n\r\n");
        await File.WriteAllTextAsync(dst, sb.ToString(), enc, ct);
    }

    // ------------------------------------------------------------------ PDF (tramite Word)
    private static async Task<List<string>> Pdf(string src, string code, Translator t, Action<double, string> progress, CancellationToken ct)
    {
        if (!OfficeSession.WordAvailable) throw new NotSupportedException(L.T("Per tradurre un PDF serve Microsoft Word: in alternativa convertilo prima in Word (.docx)."));
        progress(0, L.T("Conversione del PDF in Word…"));
        var docx = OutputPath(src, code, ".docx");
        await Sta.Run(() =>
        {
            using var office = new OfficeSession();
            office.Convert(src, docx, "docx");
        });
        await Docx(docx, t, progress, ct);
        progress(1, L.T("Creazione del PDF tradotto…"));
        var pdf = OutputPath(src, code, ".pdf");
        await Sta.Run(() =>
        {
            using var office = new OfficeSession();
            office.Convert(docx, pdf, "pdf");
        });
        return [pdf, docx];
    }
}
