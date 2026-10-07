using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SwinKnife.Controls;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>
/// Strumenti di ricognizione di rete con interfaccia semplice (scansione porte, DNS, WHOIS, traceroute,
/// porte aperte del PC) per imparare e fare controlli di sicurezza sulle proprie reti.
/// </summary>
public sealed class SecurityPage : UserControl, IToolPage
{
    private CancellationTokenSource? _cts;

    // scansione porte
    private readonly TextBox _host = Input("scanme.nmap.org");
    private readonly ComboBox _preset = new() { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 };
    private readonly TextBox _portSpec = Input("1-1024") ;
    private readonly CheckBox _useNmap = new() { Content = L.T("Usa nmap (più preciso)"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _scanBtn;
    private readonly ProgressBar _scanBar = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _scanInfo = Ui.Hint();
    private readonly TextBlock _scanCmd = Cmd();
    private readonly DataGrid _ports = Grid();
    private readonly TextBox _nmapOut = Mono();

    // dns / whois
    private readonly TextBox _dnsHost = Input("google.com");
    private readonly TextBox _dnsOut = Mono();
    private readonly TextBox _whoisDomain = Input("example.com");
    private readonly TextBox _whoisOut = Mono();

    // traceroute
    private readonly TextBox _traceHost = Input("google.com");
    private readonly DataGrid _hops = Grid();
    private readonly Button _traceBtn;

    // porte locali
    private readonly DataGrid _local = Grid();

    public SecurityPage(MainWindow main)
    {
        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
            Title = L.T("Usa questi strumenti solo sulle tue reti o dove sei autorizzato"),
            Message = L.T("Sono strumenti di diagnostica e studio (come ping o nslookup). Analizzare sistemi altrui senza permesso è illegale."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        // ---- scansione porte
        _preset.Items.Add(L.T("Porte comuni"));
        _preset.Items.Add(L.T("1-1024 (di sistema)"));
        _preset.Items.Add(L.T("1-65535 (tutte, lento)"));
        _preset.Items.Add(L.T("Personalizzate"));
        _preset.SelectedIndex = 0;
        _preset.SelectionChanged += (_, _) => { _portSpec.IsEnabled = _preset.SelectedIndex == 3; UpdateScanCmd(); };
        _portSpec.IsEnabled = false;
        _portSpec.Width = 160;
        _host.TextChanged += (_, _) => UpdateScanCmd();
        _portSpec.TextChanged += (_, _) => UpdateScanCmd();
        _useNmap.Checked += (_, _) => UpdateScanCmd();
        _useNmap.Unchecked += (_, _) => UpdateScanCmd();
        _useNmap.IsEnabled = SecTools.NmapPath() != null;
        if (!_useNmap.IsEnabled) _useNmap.ToolTip = L.T("Installa nmap per abilitarlo (winget install nmap).");

        _scanBtn = Ui.Btn(L.T("Scansiona"), SymbolRegular.Play24, async (_, _) => await Scan(), primary: true);
        _ports.Columns.Add(Col(L.T("Porta"), nameof(PortResult.Port), 90));
        _ports.Columns.Add(Col(L.T("Servizio"), nameof(PortResult.Service), 1, star: true));
        _ports.MaxHeight = 260;
        _nmapOut.Visibility = Visibility.Collapsed;

        var scanRow = Ui.Row(Lbl(L.T("Host:")), _host, Lbl("  "), _preset, _portSpec, _useNmap);
        var scanCard = Ui.Card(L.T("Scansione porte"),
            L.T("Scopre quali porte (servizi) sono aperte su un computer: è il primo passo per capire cosa espone un dispositivo."),
            scanRow, Ui.Row(_scanBtn, _scanInfo), _scanBar, _scanCmd, Box(_ports), _nmapOut);
        UpdateScanCmd();

        // ---- DNS
        var dnsBtn = Ui.Btn(L.T("Risolvi"), SymbolRegular.Search24, async (_, _) => await DnsLookup());
        _dnsOut.MinHeight = 60;
        var dnsCard = Ui.Card(L.T("DNS: da nome a indirizzo"),
            L.T("Trova l'indirizzo IP di un sito e viceversa. Equivale a «nslookup»."),
            Ui.Row(Lbl(L.T("Nome o IP:")), _dnsHost, dnsBtn), Box(_dnsOut));

        // ---- WHOIS
        var whoisBtn = Ui.Btn(L.T("Cerca"), SymbolRegular.Search24, async (_, _) => await Whois());
        _whoisOut.MinHeight = 160;
        var whoisCard = Ui.Card(L.T("WHOIS: a chi appartiene un dominio"),
            L.T("Mostra chi ha registrato un dominio, quando scade e i server DNS."),
            Ui.Row(Lbl(L.T("Dominio:")), _whoisDomain, whoisBtn), Box(_whoisOut));

        // ---- traceroute
        _traceBtn = Ui.Btn(L.T("Traccia il percorso"), SymbolRegular.Play24, async (_, _) => await Trace());
        _hops.Columns.Add(Col("#", nameof(Hop.Number), 40));
        _hops.Columns.Add(Col(L.T("Indirizzo"), nameof(Hop.Address), 150));
        _hops.Columns.Add(Col(L.T("Nome"), nameof(Hop.Host), 1, star: true));
        _hops.Columns.Add(Col(L.T("Tempo"), nameof(Hop.Ms), 90));
        _hops.MaxHeight = 300;
        var traceCard = Ui.Card(L.T("Percorso verso un sito (traceroute)"),
            L.T("Mostra i passaggi (router) che i dati attraversano per arrivare a destinazione. Equivale a «tracert»."),
            Ui.Row(Lbl(L.T("Host:")), _traceHost, _traceBtn), Box(_hops));

        // ---- porte locali
        var localBtn = Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => LoadLocal());
        _local.Columns.Add(Col(L.T("Protocollo"), nameof(LocalPort.Protocol), 90));
        _local.Columns.Add(Col(L.T("Porta"), nameof(LocalPort.Port), 80));
        _local.Columns.Add(Col(L.T("Indirizzo"), nameof(LocalPort.Address), 150));
        _local.Columns.Add(Col(L.T("Programma"), nameof(LocalPort.Process), 1, star: true));
        _local.Columns.Add(Col("PID", nameof(LocalPort.Pid), 70));
        _local.MaxHeight = 300;
        var localCard = Ui.Card(L.T("Porte aperte su questo PC"),
            L.T("I servizi in ascolto sul tuo computer e quale programma li usa: utile per controllare cosa è esposto."),
            Ui.Row(localBtn), Box(_local));

        Content = Ui.ScrollPage(Ui.Header(L.T("Sicurezza di rete"),
                L.T("Strumenti di ricognizione per imparare e per controllare le tue reti.")),
            warn, scanCard, dnsCard, whoisCard, traceCard, localCard);

        Loaded += (_, _) => { if (_local.ItemsSource == null) LoadLocal(); };
    }

    public void Shutdown() => _cts?.Cancel();

    // ------------------------------------------------------------------ azioni
    private IEnumerable<int> Ports() => _preset.SelectedIndex switch
    {
        0 => SecTools.CommonPorts.Select(p => p.port),
        1 => Enumerable.Range(1, 1024),
        2 => Enumerable.Range(1, 65535),
        _ => SecTools.ParsePortSpec(_portSpec.Text),
    };

    private string PortSpecText() => _preset.SelectedIndex switch
    {
        0 => string.Join(",", SecTools.CommonPorts.Select(p => p.port)),
        1 => "1-1024",
        2 => "1-65535",
        _ => _portSpec.Text,
    };

    private void UpdateScanCmd()
    {
        var host = _host.Text.Trim();
        _scanCmd.Text = _useNmap.IsChecked == true
            ? $"nmap -p {(_preset.SelectedIndex == 0 ? string.Join(",", SecTools.CommonPorts.Select(p => p.port)) : PortSpecText())} -sV {host}"
            : L.T($"equivale a: scansione TCP di {host} sulle porte {(_preset.SelectedIndex == 2 ? "1-65535" : PortSpecText())}");
    }

    private async Task Scan()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) return;
        _scanBtn.IsEnabled = false;
        _scanBar.Visibility = Visibility.Visible;
        _scanBar.Value = 0;
        _ports.ItemsSource = null;
        _nmapOut.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        try
        {
            if (_useNmap.IsChecked == true)
            {
                _nmapOut.Visibility = Visibility.Visible;
                _nmapOut.Clear();
                _scanInfo.Text = "  " + L.T("nmap in corso…");
                var args = $"-p {(_preset.SelectedIndex == 0 ? string.Join(",", SecTools.CommonPorts.Select(p => p.port)) : PortSpecText())} -sV {host}";
                await SecTools.RunNmapAsync(args, line => Dispatcher.BeginInvoke(() => { _nmapOut.AppendText(line + "\n"); _nmapOut.ScrollToEnd(); }), _cts.Token);
                _scanInfo.Text = "  " + L.T("Fatto.");
            }
            else
            {
                _scanInfo.Text = "  " + L.T("Scansione…");
                var results = await SecTools.ScanPortsAsync(host, Ports(), 500,
                    (p, found) => Dispatcher.InvokeAsync(() => { _scanBar.Value = p; _scanInfo.Text = "  " + L.T($"{(int)(p * 100)}% · {found} aperte"); }), _cts.Token);
                _ports.ItemsSource = results;
                _scanInfo.Text = "  " + (results.Count == 0 ? L.T("Nessuna porta aperta trovata.") : L.T($"{results.Count} porte aperte."));
            }
        }
        catch (OperationCanceledException) { _scanInfo.Text = "  " + L.T("Annullato."); }
        catch (Exception ex) { _scanInfo.Text = "  " + L.T("Errore: ") + ex.Message; }
        finally
        {
            _scanBtn.IsEnabled = true;
            _scanBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task DnsLookup()
    {
        _dnsOut.Text = L.T("Risoluzione…");
        try
        {
            var r = await SecTools.LookupAsync(_dnsHost.Text.Trim(), CancellationToken.None);
            if (r.Error.Length > 0) { _dnsOut.Text = L.T("Errore: ") + r.Error; return; }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(L.T("Indirizzi:"));
            foreach (var a in r.Addresses) sb.AppendLine("  " + a);
            if (r.ReverseName != null) sb.AppendLine(L.T("Nome (reverse): ") + r.ReverseName);
            _dnsOut.Text = sb.ToString().TrimEnd();
        }
        catch (Exception ex) { _dnsOut.Text = L.T("Errore: ") + ex.Message; }
    }

    private async Task Whois()
    {
        _whoisOut.Text = L.T("Interrogazione…");
        try { _whoisOut.Text = await SecTools.WhoisAsync(_whoisDomain.Text.Trim(), CancellationToken.None); }
        catch (Exception ex) { _whoisOut.Text = L.T("Errore: ") + ex.Message; }
    }

    private async Task Trace()
    {
        _traceBtn.IsEnabled = false;
        var hops = new System.Collections.ObjectModel.ObservableCollection<Hop>();
        _hops.ItemsSource = hops;
        _cts = new CancellationTokenSource();
        try
        {
            await SecTools.TracerouteAsync(_traceHost.Text.Trim(), 30, h => Dispatcher.InvokeAsync(() => hops.Add(h)), _cts.Token);
        }
        catch (Exception ex) { Dialogs.Dlg.Error(L.T("Errore: ") + ex.Message); }
        finally { _traceBtn.IsEnabled = true; }
    }

    private void LoadLocal()
    {
        try { _local.ItemsSource = SecTools.LocalListeningPorts(); } catch { }
    }

    // ------------------------------------------------------------------ helper UI
    private static TextBox Input(string text) => new()
    {
        Text = text, Width = 220, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 5, 8, 5),
    };

    private static TextBox Mono() => new()
    {
        IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 10, 0, 0),
        TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = 260, AcceptsReturn = true,
    };

    private static TextBlock Cmd() => new()
    {
        FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = Ui.Res("TextFillColorTertiaryBrush"),
        Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Lbl(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Ui.Res("TextFillColorSecondaryBrush") };

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None,
    };

    private static DataGridTextColumn Col(string header, string path, double width, bool star = false) =>
        new() { Header = header, Binding = new Binding(path), Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel) };

    private static Border Box(UIElement c) => new() { Margin = new Thickness(0, 10, 0, 0), Child = c };
}
