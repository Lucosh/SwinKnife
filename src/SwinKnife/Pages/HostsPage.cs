using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Blocca pubblicità e tracker scrivendo i domini nel file hosts di Windows (reversibile).</summary>
public sealed class HostsPage : UserControl, IToolPage
{
    private const string Begin = "# >>> SwinKnife adblock >>>";
    private const string End = "# <<< SwinKnife adblock <<<";
    private const string ListUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";

    private static string HostsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private readonly TextBlock _status = new() { FontSize = 15, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _manual = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 90, MaxHeight = 200, Padding = new Thickness(8),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
    };
    private readonly Wpf.Ui.Controls.Button _enableBtn, _disableBtn, _applyBtn;

    public HostsPage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Aggiunge i domini di pubblicità e tracker al file hosts, così il sistema non li raggiunge più (vale per tutti i browser e le app). È reversibile in ogni momento. Modificare il file hosts richiede i permessi di amministratore."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _enableBtn = Ui.Btn(L.T("Attiva il blocco pubblicità"), SymbolRegular.ShieldTask24, async (_, _) => await Enable(), primary: true);
        _disableBtn = Ui.Btn(L.T("Disattiva"), SymbolRegular.ShieldDismiss24, (_, _) => Disable());
        var card = Ui.Card(L.T("Blocco pubblicità e tracker"), null, _status, Ui.Row(_enableBtn, _disableBtn));
        ((StackPanel)card.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 6, 0, 0);

        _manual.Text = "";
        _applyBtn = Ui.Btn(L.T("Applica l'elenco personale"), SymbolRegular.Checkmark24, (_, _) => ApplyManual());
        var manualCard = Ui.Card(L.T("Blocca anche questi domini (uno per riga)"),
            L.T("Per bloccare altri siti: scrivi solo il dominio, es. esempio.com"),
            _manual, Ui.Row(_applyBtn));
        ((StackPanel)manualCard.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 8, 0, 0);
        _manual.Margin = new Thickness(0, 4, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Blocco pubblicità"), L.T("Meno pubblicità e tracker su tutto il PC, modificando il file hosts di Windows.")),
            info, card, manualCard);
        Refresh();
    }

    private (bool active, int count) State()
    {
        try
        {
            var lines = File.ReadAllLines(HostsFile);
            var inBlock = false;
            var count = 0;
            var active = false;
            foreach (var l in lines)
            {
                if (l.Trim() == Begin) { inBlock = active = true; continue; }
                if (l.Trim() == End) { inBlock = false; continue; }
                if (inBlock && l.TrimStart().StartsWith("0.0.0.0", StringComparison.Ordinal)) count++;
            }
            return (active, count);
        }
        catch { return (false, 0); }
    }

    private void Refresh()
    {
        var (active, count) = State();
        if (active)
        {
            _status.Text = L.T($"✓ Blocco attivo: {Util.Number(count)} domini bloccati.");
            _status.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
            _enableBtn.Content = L.T("Aggiorna l'elenco");
        }
        else
        {
            _status.Text = L.T("Blocco non attivo.");
            _status.Foreground = Ui.Res("TextFillColorSecondaryBrush");
            _enableBtn.Content = L.T("Attiva il blocco pubblicità");
        }
        _disableBtn.IsEnabled = active;
    }

    private async Task Enable()
    {
        _enableBtn.IsEnabled = false;
        try
        {
            MainWindow.Notify(L.T("Scarico l'elenco dei domini…"), 0);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SwinKnife/" + AppInfo.Version);
            var text = await http.GetStringAsync(ListUrl);
            var domains = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("0.0.0.0 ", StringComparison.Ordinal))
                .Select(l => l[8..].Split(' ', '#')[0].Trim())
                .Where(d => d.Length > 0 && d != "0.0.0.0")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            MainWindow.Notify("");
            WriteBlock(domains);
            MainWindow.Notify(L.T($"Blocco attivato: {Util.Number(domains.Count)} domini."), 8);
        }
        catch (UnauthorizedAccessException) { NeedAdmin(); }
        catch (Exception ex) { MainWindow.Notify(""); Dlg.Error(ex.Message); }
        finally { _enableBtn.IsEnabled = true; Refresh(); }
    }

    private void ApplyManual()
    {
        var extra = _manual.Text.Split('\n').Select(d => d.Trim().TrimStart("0.0.0.0".ToCharArray()).Trim()).Where(d => d.Length > 0 && d.Contains('.')).ToList();
        // unisco i domini già presenti con quelli personali
        try
        {
            var current = ReadBlock();
            current.AddRange(extra);
            WriteBlock(current.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            MainWindow.Notify(L.T("Elenco aggiornato."));
            Refresh();
        }
        catch (UnauthorizedAccessException) { NeedAdmin(); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void Disable()
    {
        if (!Dlg.Confirm(L.T("Disattivare il blocco pubblicità?"))) return;
        try { WriteBlock(new List<string>()); MainWindow.Notify(L.T("Blocco disattivato.")); }
        catch (UnauthorizedAccessException) { NeedAdmin(); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
        Refresh();
    }

    private List<string> ReadBlock()
    {
        var list = new List<string>();
        if (!File.Exists(HostsFile)) return list;
        var inBlock = false;
        foreach (var l in File.ReadAllLines(HostsFile))
        {
            if (l.Trim() == Begin) { inBlock = true; continue; }
            if (l.Trim() == End) { inBlock = false; continue; }
            if (inBlock && l.TrimStart().StartsWith("0.0.0.0 ", StringComparison.Ordinal)) list.Add(l.Trim()[8..].Trim());
        }
        return list;
    }

    /// <summary>Riscrive il file hosts tenendo tutto ciò che non è nostro e sostituendo la nostra sezione.</summary>
    private void WriteBlock(List<string> domains)
    {
        var original = File.Exists(HostsFile) ? File.ReadAllLines(HostsFile).ToList() : new List<string>();
        var kept = new List<string>();
        var inBlock = false;
        foreach (var l in original)
        {
            if (l.Trim() == Begin) { inBlock = true; continue; }
            if (l.Trim() == End) { inBlock = false; continue; }
            if (!inBlock) kept.Add(l);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);

        var sb = new StringBuilder();
        foreach (var l in kept) sb.AppendLine(l);
        if (domains.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Begin);
            sb.AppendLine("# " + L.T("Generato da SwinKnife — non modificare a mano questa sezione"));
            foreach (var d in domains) sb.AppendLine("0.0.0.0 " + d);
            sb.AppendLine(End);
        }
        File.WriteAllText(HostsFile, sb.ToString(), new UTF8Encoding(false));
        try { Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }

    private void NeedAdmin()
    {
        MainWindow.Notify("");
        if (Dlg.Confirm(L.T("Per modificare il file hosts serve l'amministratore. Riavviare SwinKnife come amministratore adesso?")))
            RawSource.RelaunchAsAdmin("--page hosts --restart");
    }
}
