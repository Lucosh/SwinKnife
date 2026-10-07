using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Raccoglie i comandi di manutenzione di Windows (SFC, DISM, controllo disco, rete) in pulsanti sicuri.</summary>
public sealed class MaintenancePage : UserControl, IToolPage
{
    private sealed record Task(string Key, string Title, string Desc, string Command, bool NeedsAdmin, bool DefaultOn);

    private static readonly Task[] Tasks =
    [
        new("sfc", L.T("Ripara i file di sistema (SFC)"), L.T("Controlla e ripara i file di Windows danneggiati."), "sfc /scannow", true, true),
        new("dism", L.T("Ripara l'immagine di Windows (DISM)"), L.T("Sistema i problemi che SFC da solo non riesce a correggere."), "DISM /Online /Cleanup-Image /RestoreHealth", true, true),
        new("chkdsk", L.T("Controlla il disco C:"), L.T("Cerca errori sul disco di sistema (la scansione completa parte al riavvio)."), "chkdsk C:", true, false),
        new("flushdns", L.T("Svuota la cache DNS"), L.T("Risolve molti problemi di siti che non si aprono."), "ipconfig /flushdns", false, true),
        new("winsock", L.T("Ripristina Winsock"), L.T("Riporta la rete allo stato di fabbrica (serve riavviare)."), "netsh winsock reset", true, false),
        new("tcpip", L.T("Ripristina lo stack TCP/IP"), L.T("Reimposta i protocolli di rete (serve riavviare)."), "netsh int ip reset", true, false),
        new("renewip", L.T("Rinnova l'indirizzo IP"), L.T("Rilascia e richiede di nuovo l'IP dal router."), "ipconfig /release & ipconfig /renew", false, false),
        new("wucache", L.T("Svuota la cache di Windows Update"), L.T("Utile quando gli aggiornamenti si bloccano."),
            "net stop wuauserv & net stop bits & rd /s /q %windir%\\SoftwareDistribution & net start wuauserv & net start bits", true, false),
    ];

    private readonly Dictionary<string, CheckBox> _checks = new();

    public MaintenancePage(MainWindow main)
    {
        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Scegli le operazioni e premi Esegui: si apre una finestra del Prompt dei comandi, come amministratore, dove vedi l'avanzamento. Alcune operazioni chiedono un riavvio per completarsi."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var list = new StackPanel();
        foreach (var t in Tasks)
        {
            var cb = new CheckBox { IsChecked = t.DefaultOn, Margin = new Thickness(0, 6, 0, 0) };
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = t.Title + (t.NeedsAdmin ? "" : L.T("  (senza amministratore)")), FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            head.Children.Add(Ui.Hint(t.Desc));
            cb.Content = head;
            _checks[t.Key] = cb;
            list.Children.Add(cb);
        }

        var runSel = Ui.Btn(L.T("Esegui le operazioni scelte"), SymbolRegular.PlayCircle24, (_, _) => RunSelected(), primary: true);
        var card = Ui.Card(L.T("Operazioni di manutenzione"), null, list, Ui.Row(runSel));
        ((StackPanel)card.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 14, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Manutenzione Windows"), L.T("Ripara file di sistema, disco e rete con i comandi ufficiali di Windows, in un clic.")),
            warn, card);
    }

    private void RunSelected()
    {
        var chosen = Tasks.Where(t => _checks[t.Key].IsChecked == true).ToList();
        if (chosen.Count == 0) { Dlg.Info(L.T("Scegli almeno un'operazione.")); return; }
        var needsAdmin = chosen.Any(t => t.NeedsAdmin);

        var script = "";
        foreach (var t in chosen)
            script += $"echo ======== {t.Title} ======== & {t.Command} & echo. & ";
        script += "echo. & echo " + L.T("Operazioni completate. Chiudi pure questa finestra.") + " & pause";

        var psi = new ProcessStartInfo("cmd.exe", "/k \"" + script + "\"") { UseShellExecute = true };
        if (needsAdmin) psi.Verb = "runas";
        try { Process.Start(psi); MainWindow.Notify(L.T("Manutenzione avviata nella finestra del Prompt dei comandi.")); }
        catch (Exception ex) { Dlg.Error(L.T("Impossibile avviare le operazioni:\n") + ex.Message); }
    }
}
