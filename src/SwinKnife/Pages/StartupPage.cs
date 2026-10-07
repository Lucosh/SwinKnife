using System.Management;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace SwinKnife.Pages;

/// <summary>Gestisce i programmi che partono con Windows e i servizi: attiva/disattiva, avvia/ferma.</summary>
public sealed class StartupPage : UserControl, IToolPage
{
    private readonly StackPanel _startupList = new();
    private readonly StackPanel _serviceList = new();
    private readonly TextBox _search = new() { PlaceholderText = L.T("Cerca un servizio…"), Width = 320 };
    private readonly CheckBox _onlyRunning = new() { Content = L.T("Solo quelli in esecuzione"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _svcCount = Ui.Hint("");
    private List<ServiceRow> _services = new();

    private sealed record ServiceRow(string Name, string Display, string State, string StartMode);

    public StartupPage(MainWindow main)
    {
        if (!RawSource.IsAdmin())
            _startupList.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
                Title = L.T("Alcune modifiche richiedono l'amministratore"),
                Message = L.T("Puoi gestire le tue voci subito. Per quelle valide per tutti gli utenti e per avviare o fermare i servizi, riavvia SwinKnife come amministratore."),
                Margin = new Thickness(0, 0, 0, 10),
            });

        var startupCard = Ui.Card(L.T("Programmi all'avvio"),
            L.T("Disattiva quelli che non ti servono per accendere il PC più in fretta."),
            Ui.Row(Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => LoadStartup())), _startupList);

        _search.TextChanged += (_, _) => RenderServices();
        _onlyRunning.Checked += (_, _) => RenderServices();
        _onlyRunning.Unchecked += (_, _) => RenderServices();
        var serviceCard = Ui.Card(L.T("Servizi di Windows"),
            L.T("Avvia o ferma i servizi. Non disattivare quelli che non conosci."),
            Ui.Row(_search, _onlyRunning, Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => LoadServices())),
            _svcCount, _serviceList);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Avvio e servizi"), L.T("Scegli cosa parte con Windows e gestisci i servizi di sistema.")),
            startupCard, serviceCard);

        Loaded += (_, _) => { if (_startupList.Children.Count <= 1) { LoadStartup(); LoadServices(); } };
    }

    // ------------------------------------------------------------------ avvio
    private void LoadStartup()
    {
        for (var i = _startupList.Children.Count - 1; i >= 0; i--)
            if (_startupList.Children[i] is not InfoBar) _startupList.Children.RemoveAt(i);
        List<StartupItem> items;
        try { items = SysInfo.StartupItems(); }
        catch (Exception ex) { _startupList.Children.Add(Ui.Hint(ex.Message)); return; }
        if (items.Count == 0) { _startupList.Children.Add(Ui.Hint(L.T("Nessun programma all'avvio."))); return; }
        foreach (var it in items) _startupList.Children.Add(StartupRow(it));
    }

    private FrameworkElement StartupRow(StartupItem it)
    {
        var sw = new ToggleSwitch { IsChecked = it.Enabled, VerticalAlignment = VerticalAlignment.Center };
        sw.Checked += (_, _) => ToggleStartup(it, true, sw);
        sw.Unchecked += (_, _) => ToggleStartup(it, false, sw);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = it.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
        var sub = it.Publisher.Length > 0 ? it.Publisher + "  ·  " + it.Location : it.Location;
        text.Children.Add(Ui.Hint(sub));
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(sw, Dock.Right);
        dock.Children.Add(sw);
        dock.Children.Add(text);
        return dock;
    }

    private void ToggleStartup(StartupItem it, bool enabled, ToggleSwitch sw)
    {
        if (it.Enabled == enabled) return;
        try { SysInfo.SetEnabled(it, enabled); }
        catch (Exception ex)
        {
            sw.IsChecked = it.Enabled; // ripristino lo stato
            Dlg.Error(RawSource.IsAdmin() ? ex.Message
                : L.T("Per cambiare questa voce serve avviare SwinKnife come amministratore."));
        }
    }

    // ------------------------------------------------------------------ servizi
    private async void LoadServices()
    {
        _svcCount.Text = L.T("Lettura…");
        _serviceList.Children.Clear();
        try
        {
            _services = await System.Threading.Tasks.Task.Run(ReadServices);
            RenderServices();
        }
        catch (Exception ex) { _svcCount.Text = ""; _serviceList.Children.Add(Ui.Hint(ex.Message)); }
    }

    private static List<ServiceRow> ReadServices()
    {
        var list = new List<ServiceRow>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode FROM Win32_Service");
        foreach (var o in searcher.Get())
            list.Add(new ServiceRow(
                (o["Name"] as string) ?? "", (o["DisplayName"] as string) ?? "",
                (o["State"] as string) ?? "", (o["StartMode"] as string) ?? ""));
        return list.OrderBy(s => s.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void RenderServices()
    {
        _serviceList.Children.Clear();
        var q = _search.Text.Trim();
        var filtered = _services.Where(s =>
            (q.Length == 0 || s.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            && (_onlyRunning.IsChecked != true || s.State.Equals("Running", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _svcCount.Text = L.T($"{filtered.Count} servizi");
        foreach (var s in filtered.Take(300)) _serviceList.Children.Add(ServiceRowView(s));
        if (filtered.Count > 300) _serviceList.Children.Add(Ui.Hint(L.T("Mostro i primi 300: affina la ricerca per vedere gli altri.")));
    }

    private FrameworkElement ServiceRowView(ServiceRow s)
    {
        var running = s.State.Equals("Running", StringComparison.OrdinalIgnoreCase);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = s.Display.Length > 0 ? s.Display : s.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Ui.Hint($"{s.Name}  ·  {LocalState(s.State)}  ·  {LocalMode(s.StartMode)}"));
        var btn = Ui.Btn(running ? L.T("Ferma") : L.T("Avvia"), running ? SymbolRegular.Stop24 : SymbolRegular.Play24,
            (_, _) => ServiceAction(s, !running));
        btn.VerticalAlignment = VerticalAlignment.Center;
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(text);
        return dock;
    }

    private async void ServiceAction(ServiceRow s, bool start)
    {
        try
        {
            var code = await System.Threading.Tasks.Task.Run(() =>
            {
                using var mo = new ManagementObject($"Win32_Service.Name='{s.Name.Replace("'", "\\'")}'");
                var r = mo.InvokeMethod(start ? "StartService" : "StopService", null);
                return Convert.ToUInt32(r);
            });
            if (code == 0) { MainWindow.Notify(start ? L.T($"Servizio «{s.Display}» avviato.") : L.T($"Servizio «{s.Display}» fermato.")); LoadServices(); }
            else if (code == 2) Dlg.Error(L.T("Accesso negato: avvia SwinKnife come amministratore per gestire i servizi."));
            else Dlg.Error(L.T($"Operazione non riuscita (codice {code})."));
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private static string LocalState(string s) => s.ToLowerInvariant() switch
    {
        "running" => L.T("in esecuzione"), "stopped" => L.T("fermo"), "paused" => L.T("in pausa"), _ => s,
    };

    private static string LocalMode(string m) => m.ToLowerInvariant() switch
    {
        "auto" => L.T("automatico"), "manual" => L.T("manuale"), "disabled" => L.T("disabilitato"), _ => m,
    };
}
