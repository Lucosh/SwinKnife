using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Accende altri PC della rete locale inviando un "magic packet" (Wake-on-LAN).</summary>
public sealed class WolPage : UserControl, IToolPage
{
    private sealed record Device(string Name, string Mac);

    private readonly TextBox _name = new() { PlaceholderText = L.T("Nome (es. PC camera)"), Width = 220 };
    private readonly TextBox _mac = new() { PlaceholderText = "00:11:22:33:44:55", Width = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly StackPanel _list = new();

    public WolPage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Serve prepararlo una volta"),
            Message = L.T("Sul PC da accendere deve essere attivo il Wake-on-LAN (nel BIOS e nelle proprietà della scheda di rete). Funziona via cavo di rete; in Wi-Fi spesso non è supportato. I due PC devono essere sulla stessa rete."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var add = Ui.Card(L.T("Accendi un PC"), null,
            Ui.Row(_name, _mac),
            Ui.Row(Ui.Btn(L.T("Accendi ora"), SymbolRegular.Power24, (_, _) => Send(_mac.Text), primary: true),
                   Ui.Btn(L.T("Salva dispositivo"), SymbolRegular.Save24, (_, _) => Save())));
        ((StackPanel)add.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 10, 0, 0);

        var saved = Ui.Card(L.T("Dispositivi salvati"), null, _list);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Wake-on-LAN"), L.T("Accendi un altro computer della rete con un clic.")),
            info, add, saved);
        Render();
    }

    private List<Device> Devices()
    {
        try { return JsonSerializer.Deserialize<List<Device>>(Settings.Get("wol.devices") ?? "[]") ?? new(); }
        catch { return new(); }
    }

    private void Store(List<Device> d) => Settings.Set("wol.devices", JsonSerializer.Serialize(d));

    private void Save()
    {
        var mac = _mac.Text.Trim();
        var name = _name.Text.Trim();
        if (name.Length == 0 || mac.Length == 0) { Dlg.Info(L.T("Scrivi nome e indirizzo MAC.")); return; }
        try { WakeOnLan.ParseMac(mac); } catch (Exception ex) { Dlg.Error(ex.Message); return; }
        var d = Devices();
        d.RemoveAll(x => x.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        d.Add(new Device(name, mac));
        Store(d);
        _name.Text = _mac.Text = "";
        Render();
    }

    private void Send(string mac)
    {
        try { WakeOnLan.Send(mac.Trim()); MainWindow.Notify(L.T("Magic packet inviato.")); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void Render()
    {
        _list.Children.Clear();
        var d = Devices();
        if (d.Count == 0) { _list.Children.Add(Ui.Hint(L.T("Nessun dispositivo salvato."))); return; }
        foreach (var dev in d)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = dev.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            text.Children.Add(Ui.Hint(dev.Mac));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Ui.Btn(L.T("Accendi"), SymbolRegular.Power24, (_, _) => Send(dev.Mac), primary: true));
            actions.Children.Add(Ui.Btn(L.T("Rimuovi"), SymbolRegular.Delete24, (_, _) => { var l = Devices(); l.RemoveAll(x => x.Mac == dev.Mac); Store(l); Render(); }));
            var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
            dock.Children.Add(text);
            _list.Children.Add(new Border { Child = dock, Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), Background = Ui.Res("CardBackgroundFillColorDefaultBrush") });
        }
    }
}
