using System.Windows;
using Wpf.Ui.Controls;

namespace SwinKnife.Pages;

/// <summary>Contratto (tutto facoltativo) che le pagine degli strumenti possono implementare.</summary>
public interface IToolPage
{
    /// <summary>Se la pagina corrente sa gestire il file trascinato nella finestra.</summary>
    bool Accepts(string path) => false;

    void OpenFile(string path) { }

    void AddFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0) OpenFile(paths[0]);
    }

    /// <summary>Chiamato alla chiusura: false per annullarla (es. modifiche non salvate).</summary>
    bool CanClose() => true;

    void Shutdown() { }

    void Activated() { }
}

public sealed record ToolInfo(string Key, string Title, SymbolRegular Icon, string Group, string Description,
    Func<MainWindow, FrameworkElement> Create);

public static class Tools
{
    public static readonly ToolInfo[] All =
    [
        new("home", L.T("Home"), SymbolRegular.Home24, "", "", w => new HomePage(w)),

        new("viewer", L.T("Apri file"), SymbolRegular.Open24, L.T("File e documenti"),
            L.T("Visualizza immagini, PDF, documenti Office, video, audio, archivi, testo e qualsiasi altro file."),
            w => new ViewerPage(w)),
        new("convert", L.T("Converti"), SymbolRegular.ArrowSwap24, L.T("File e documenti"),
            L.T("JPG↔PNG↔WEBP↔AVIF, HEIC e RAW, Word↔PDF, PDF→immagini, Excel/CSV/JSON, audio, video e testo da immagini (OCR)."),
            w => new ConverterPage(w)),
        new("pdf", L.T("Editor PDF"), SymbolRegular.DocumentPdf24, L.T("File e documenti"),
            L.T("Modifica il testo, compila moduli, firma, OCR, unisci, dividi, ruota, comprimi, proteggi."),
            w => new PdfEditorPage(w)),
        new("archive", L.T("Archivi"), SymbolRegular.FolderZip24, L.T("File e documenti"),
            L.T("Crea archivi ZIP, 7z e TAR.GZ (anche con password AES-256) ed estrai qualsiasi archivio."),
            w => new ArchivePage(w)),

        new("photo", L.T("Editor foto"), SymbolRegular.ImageEdit24, L.T("Foto e schermo"),
            L.T("Regolazioni, filtri e ritaglio in stile iPhone."),
            w => new PhotoEditorPage(w)),
        new("capture", L.T("Cattura schermo"), SymbolRegular.Screenshot24, L.T("Foto e schermo"),
            L.T("Screenshot di area, finestra o schermo con frecce, testo e sfocatura; registrazione video e GIF."),
            w => new CapturePage(w)),
        new("rename", L.T("Rinomina in blocco"), SymbolRegular.Rename24, L.T("Foto e schermo"),
            L.T("Rinomina tanti file insieme: data di scatto, numerazione, cerca e sostituisci, con anteprima."),
            w => new RenamePage(w)),

        new("disk", L.T("Analisi disco"), SymbolRegular.DataPie24, L.T("Disco e sistema"),
            L.T("Scopri cosa occupa spazio con una mappa interattiva, come WinDirStat."),
            w => new DiskAnalyzerPage(w)),
        new("dupes", L.T("Trova duplicati"), SymbolRegular.DocumentCopy24, L.T("Disco e sistema"),
            L.T("File identici e foto quasi uguali: scegli cosa tenere e libera spazio."),
            w => new DuplicatesPage(w)),
        new("clean", L.T("Pulizia PC"), SymbolRegular.Broom24, L.T("Disco e sistema"),
            L.T("File temporanei, cache dei browser, Cestino, aggiornamenti di Windows: vedi quanto recuperi."),
            w => new CleanupPage(w)),
        new("recovery", L.T("Recupero file"), SymbolRegular.ArrowCounterclockwise24, L.T("Disco e sistema"),
            L.T("Ritrova file eliminati da chiavette, schede SD e dischi, con i nomi originali quando possibile."),
            w => new RecoveryPage(w)),
        new("sysinfo", L.T("Info sistema"), SymbolRegular.Info24, L.T("Disco e sistema"),
            L.T("Hardware, salute dei dischi, batteria, rete e programmi all'avvio."),
            w => new SystemInfoPage(w)),

        new("qr", L.T("QR e password"), SymbolRegular.QrCode24, L.T("Utilità"),
            L.T("Genera QR code (link, Wi-Fi, contatti) e password sicure."),
            w => new QrPasswordPage(w)),
        new("gemini", "Gemini", SymbolRegular.Sparkle24, L.T("Utilità"),
            L.T("L'assistente AI di Google con il tuo account; chiedigli di riassumere o tradurre i tuoi file."),
            w => new GeminiPage(w)),
        new("settings", L.T("Impostazioni"), SymbolRegular.Settings24, L.T("Utilità"),
            L.T("Menu del tasto destro di Esplora risorse, componenti aggiuntivi, cartella dati."),
            w => new SettingsPage(w)),
    ];
}
