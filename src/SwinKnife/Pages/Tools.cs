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
        new("search", L.T("Cerca file"), SymbolRegular.Search24, L.T("File e documenti"),
            L.T("Trova all'istante qualsiasi file o cartella su tutti i dischi, mentre scrivi."),
            w => new SearchPage(w)),
        new("convert", L.T("Converti"), SymbolRegular.ArrowSwap24, L.T("File e documenti"),
            L.T("JPG↔PNG↔WEBP↔AVIF, HEIC e RAW, Word↔PDF, PDF→immagini, Excel/CSV/JSON, audio, video e testo da immagini (OCR)."),
            w => new ConverterPage(w)),
        new("pdf", L.T("Editor PDF"), SymbolRegular.DocumentPdf24, L.T("File e documenti"),
            L.T("Modifica il testo, compila moduli, firma, OCR, unisci, dividi, ruota, comprimi, proteggi."),
            w => new PdfEditorPage(w)),
        new("translate", L.T("Traduci documenti"), SymbolRegular.Translate24, L.T("File e documenti"),
            L.T("Traduci documenti Word, PowerPoint, Excel, PDF e sottotitoli in un'altra lingua mantenendo la formattazione."),
            w => new TranslatePage(w)),
        new("compare", L.T("Confronta"), SymbolRegular.BranchCompare24, L.T("File e documenti"),
            L.T("Differenze tra due file (testi, Word, PDF, codice) o tra due cartelle, con sincronizzazione."),
            w => new ComparePage(w)),
        new("archive", L.T("Archivi"), SymbolRegular.FolderZip24, L.T("File e documenti"),
            L.T("Crea archivi ZIP, 7z e TAR.GZ (anche con password AES-256) ed estrai qualsiasi archivio."),
            w => new ArchivePage(w)),

        new("photo", L.T("Editor foto"), SymbolRegular.ImageEdit24, L.T("Foto e schermo"),
            L.T("Regolazioni, filtri e ritaglio in stile iPhone."),
            w => new PhotoEditorPage(w)),
        new("batch", L.T("Foto in blocco"), SymbolRegular.ImageMultiple24, L.T("Foto e schermo"),
            L.T("Ridimensiona e comprimi tante foto insieme e togli la posizione GPS e gli altri dati nascosti."),
            w => new PhotoBatchPage(w)),
        new("bgremove", L.T("Rimuovi sfondo"), SymbolRegular.Wand24, L.T("Foto e schermo"),
            L.T("Ritaglia in automatico persone, animali e oggetti dalle foto, con l'intelligenza artificiale sul PC."),
            w => new BgRemovePage(w)),
        new("video", L.T("Editor video"), SymbolRegular.VideoClip24, L.T("Foto e schermo"),
            L.T("Taglia, comprimi per WhatsApp o e-mail, estrai l'audio, crea GIF, ruota e unisci video."),
            w => new VideoPage(w)),
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
        new("uninstall", L.T("Disinstalla programmi"), SymbolRegular.AppsListDetail24, L.T("Disco e sistema"),
            L.T("Rimuovi i programmi che non usi e poi le cartelle e le voci di registro che si lasciano dietro."),
            w => new UninstallPage(w)),
        new("shred", L.T("Eliminazione sicura"), SymbolRegular.ShieldDismiss24, L.T("Disco e sistema"),
            L.T("Cancella file e cartelle in modo che nessun programma di recupero li possa ritrovare."),
            w => new ShredPage(w)),
        new("recovery", L.T("Recupero file"), SymbolRegular.ArrowCounterclockwise24, L.T("Disco e sistema"),
            L.T("Ritrova file eliminati da chiavette, schede SD e dischi, con i nomi originali quando possibile."),
            w => new RecoveryPage(w)),
        new("sysinfo", L.T("Info sistema"), SymbolRegular.Info24, L.T("Disco e sistema"),
            L.T("Hardware, salute dei dischi, batteria, rete e programmi all'avvio."),
            w => new SystemInfoPage(w)),

        new("phone", L.T("Invia al telefono"), SymbolRegular.Phone24, L.T("Rete"),
            L.T("Scambia foto e file con il telefono sulla stessa rete Wi-Fi: inquadri un QR code e basta."),
            w => new PhonePage(w)),
        new("network", L.T("Rete e Wi-Fi"), SymbolRegular.Wifi124, L.T("Rete"),
            L.T("Test di velocità, reti Wi-Fi vicine con segnale e canali, dispositivi collegati alla rete di casa."),
            w => new NetworkPage(w)),
        new("quick", L.T("Strumenti rapidi"), SymbolRegular.Keyboard24, L.T("Utilità"),
            L.T("Copia il testo da qualsiasi punto dello schermo, contagocce, righello, finestra sempre in primo piano: con una scorciatoia."),
            w => new QuickToolsPage(w)),
        new("clipboard", L.T("Appunti"), SymbolRegular.ClipboardTextLtr24, L.T("Utilità"),
            L.T("Cronologia di tutto quello che copi: testi, immagini e file, da cercare e incollare di nuovo."),
            w => new ClipboardPage(w)),
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
