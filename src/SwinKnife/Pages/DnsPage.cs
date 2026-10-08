using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Cambia i server DNS della scheda di rete scegliendo tra provider noti (più privacy/velocità).</summary>
public sealed class DnsPage : UserControl, IToolPage
{
    private readonly ComboBox _adapter = new() { MinWidth = 300, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _preset = new() { MinWidth = 300, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _current = Ui.Hint("");
    private List<DnsSettings.Adapter> _adapters = new();

    public DnsPage(MainWindow main)
    {
        foreach (var p in DnsSettings.Presets) _preset.Items.Add(p.Name);
        _preset.SelectedIndex = 1; // Cloudflare

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Cos'è il DNS"),
            Message = L.T("Il DNS traduce i nomi dei siti in indirizzi. Cambiarlo con uno più veloce o che blocca malware e pubblicità può migliorare velocità e privacy, senza toccare nient'altro. «Automatico» ripristina quello del router. La modifica richiede l'amministratore (comparirà una richiesta di Windows)."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _adapter.SelectionChanged += (_, _) => ShowCurrent();

        var card = Ui.Card(L.T("Cambia DNS"), null,
            Ui.Row(Ui.Label(L.T("Scheda di rete:"), bold: true), new Border { Width = 10 }, _adapter,
                new Border { Width = 8 }, Ui.IconBtn(SymbolRegular.ArrowClockwise24, L.T("Aggiorna"), (_, _) => Refresh())),
            _current,
            Ui.Row(Ui.Label(L.T("Server DNS:"), bold: true), new Border { Width = 10 }, _preset),
            Ui.Row(Ui.Btn(L.T("Applica"), SymbolRegular.Checkmark24, (_, _) => Apply(), primary: true)));
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[0].Margin = new Thickness(0, 10, 0, 0);
        _current.Margin = new Thickness(0, 8, 0, 0);
        rows[1].Margin = new Thickness(0, 14, 0, 0);
        rows[2].Margin = new Thickness(0, 14, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Cambio DNS"), L.T("Passa a un DNS più veloce o che blocca pubblicità e malware.")),
            info, card);
        Refresh();
    }

    private void Refresh()
    {
        var selected = _adapter.SelectedItem as string;
        _adapters = DnsSettings.Adapters();
        _adapter.Items.Clear();
        foreach (var a in _adapters) _adapter.Items.Add(a.Name);
        if (_adapter.Items.Count == 0) { _current.Text = L.T("Nessuna scheda di rete attiva."); return; }
        var idx = selected != null ? _adapters.FindIndex(a => a.Name == selected) : 0;
        _adapter.SelectedIndex = idx >= 0 ? idx : 0;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        var a = Current();
        _current.Text = a == null ? "" : L.T($"{a.Description} — DNS attuale: {a.CurrentDns}");
    }

    private DnsSettings.Adapter? Current()
    {
        var i = _adapter.SelectedIndex;
        return i >= 0 && i < _adapters.Count ? _adapters[i] : null;
    }

    private void Apply()
    {
        var a = Current();
        if (a == null) { Dlg.Info(L.T("Scegli una scheda di rete.")); return; }
        var preset = DnsSettings.Presets[_preset.SelectedIndex];
        if (!Dlg.Confirm(L.T($"Impostare «{preset.Name}» sulla scheda «{a.Name}»?"))) return;
        try
        {
            if (DnsSettings.Apply(a.Name, preset))
            {
                MainWindow.Notify(L.T("DNS aggiornato."), 8);
                Refresh();
            }
            else
            {
                Dlg.Error(L.T("Non è stato possibile cambiare il DNS. Serve l'autorizzazione dell'amministratore: riprova e accetta la richiesta di Windows."));
            }
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    public void Activated() => Refresh();
}
