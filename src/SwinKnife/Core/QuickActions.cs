using SwinKnife.Dialogs;
using SwinKnife.Pages;
using SwinKnife.Pdf;

namespace SwinKnife.Core;

/// <summary>Azioni lanciate dal menu del tasto destro di Esplora risorse (--action nome file).</summary>
public static class QuickActions
{
    public static async void Run(MainWindow window, string action, IReadOnlyList<string> files)
    {
        try
        {
            switch (action)
            {
                case "compress-pdf":
                    foreach (var f in files.Where(f => Formats.KindOf(f) == FileKind.Pdf)) await CompressPdf(f);
                    break;
                case "ocr-pdf":
                    foreach (var f in files.Where(f => Formats.KindOf(f) == FileKind.Pdf)) await OcrPdf(f);
                    break;
                case "extract-here":
                    foreach (var f in files.Where(Archives.IsArchive)) await ExtractHere(f);
                    break;
                case "gemini":
                    if (files.Count > 0) await ((GeminiPage)window.ShowPage("gemini")).AskAboutFile(files[0], GeminiPage.Prompts[0]);
                    break;
                default:
                    Dlg.Error(L.T($"Azione sconosciuta: {action}"));
                    break;
            }
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, $"Azione {action}");
            Dlg.Error(ex.Message);
        }
    }

    /// <summary>Estrae accanto all'archivio, in una cartella con il suo nome (o direttamente, se contiene già un'unica cartella).</summary>
    public static async Task ExtractHere(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        string? password = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                MainWindow.Notify(L.T($"Estrazione di {Path.GetFileName(path)}…"), 0);
                var pw = password;
                var (count, folder) = await Task.Run(() =>
                {
                    ArchiveSummary? info = null;
                    try { info = Archives.Inspect(path, pw); }
                    catch (ArchivePasswordException) { throw; }
                    catch { }
                    var single = info?.SingleRoot;
                    var target = single != null && !Directory.Exists(Path.Combine(parent, single))
                        ? parent : Util.UniquePath(Path.Combine(parent, Archives.Stem(path)));
                    var n = Archives.Extract(path, target, pw, (_, _) => { }, CancellationToken.None);
                    return (n, target == parent ? Path.Combine(parent, single!) : target);
                });
                MainWindow.Notify(L.T($"Estratti {count} file in {Path.GetFileName(folder)}"), 10);
                Util.OpenFolder(folder);
                return;
            }
            catch (ArchivePasswordException ex)
            {
                password = Dlg.Prompt(Path.GetFileName(path), ex.Message + L.T(" Scrivi la password:"), "", password: true);
                if (string.IsNullOrEmpty(password)) return;
            }
        }
    }

    public static async Task CompressPdf(string path)
    {
        MainWindow.Notify(L.T($"Compressione di {Path.GetFileName(path)}…"), 0);
        var before = File.ReadAllBytes(path);
        var after = await Task.Run(() => PdfTools.Compress(before, PdfTools.CompressionLevel.Medium));
        if (after.Length >= before.Length)
        {
            MainWindow.Notify(L.T($"{Path.GetFileName(path)} è già compatto: nessun risparmio possibile"));
            return;
        }
        var dst = Util.UniquePath(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + L.T("_compresso") + ".pdf"));
        await File.WriteAllBytesAsync(dst, after);
        MainWindow.Notify($"{Path.GetFileName(dst)}: {Util.HumanSize(before.Length)} → {Util.HumanSize(after.Length)}", 12);
        Util.Reveal(dst);
    }

    public static async Task OcrPdf(string path)
    {
        if (!Ocr.Available)
        {
            Dlg.Error(L.T("Il riconoscimento del testo (OCR) non è disponibile: aggiungi una lingua in Impostazioni di Windows › Lingua."));
            return;
        }
        var dst = Util.UniquePath(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + L.T("_ricercabile") + ".pdf"));
        using var doc = PdfDoc.Open(path);
        var pages = await Ocr.MakeSearchable(doc, (_, m) => MainWindow.Notify($"{Path.GetFileName(path)}: {m}", 0), CancellationToken.None);
        await File.WriteAllBytesAsync(dst, doc.Save());
        MainWindow.Notify(pages > 0 ? L.T($"Testo riconosciuto in {pages} pagine: {Path.GetFileName(dst)}") : L.T("Il PDF aveva già del testo selezionabile"), 12);
        Util.Reveal(dst);
    }
}
