using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Test di velocità della connessione, reti Wi-Fi vicine e dispositivi collegati alla rete di casa.</summary>
public sealed class NetworkPage : UserControl, IToolPage
{
    public sealed record WifiRow(string Ssid, string Signal, int SignalPercent, string Channel, string Band, string Security, string Bssid, bool Mine);

    private readonly TextBlock _down = Big(), _up = Big(), _ping = Big(), _jitter = Big();
    private readonly TextBlock _speedInfo = Ui.Hint();
    private readonly ProgressBar _speedBar = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _speedBtn, _wifiBtn, _lanBtn;
    private readonly TextBlock _wifiNow = Ui.Label("");
    private readonly TextBlock _wifiHint = Ui.Hint();
    private readonly DataGrid _wifi = Grid();
    private readonly DataGrid _lan = Grid();
    private readonly TextBlock _lanInfo = Ui.Hint();
    private readonly ProgressBar _lanBar = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

    public NetworkPage(MainWindow main)
    {
        // ---- velocità
        _speedBtn = Ui.Btn(L.T("Avvia il test"), SymbolRegular.Play24, async (_, _) => await SpeedTest(), primary: true);
        var gauges = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        gauges.Children.Add(Gauge(L.T("Download"), _down, "Mbit/s"));
        gauges.Children.Add(Gauge(L.T("Upload"), _up, "Mbit/s"));
        gauges.Children.Add(Gauge(L.T("Ping"), _ping, "ms"));
        gauges.Children.Add(Gauge(L.T("Variazione (jitter)"), _jitter, "ms"));
        var last = Settings.Get("net.last");
        _speedInfo.Text = last != null ? L.T("Ultimo test: ") + last : L.T("Misura la velocità reale della tua connessione a Internet (circa 20 secondi, usa i server di Cloudflare).");
        _speedInfo.TextWrapping = TextWrapping.Wrap;
        var speedCard = Ui.Card(L.T("Velocità di Internet"), null, gauges, _speedBar, Ui.Row(_speedBtn, _speedInfo));
        ((StackPanel)((StackPanel)speedCard.Child).Children[^1]).Margin = new Thickness(0, 12, 0, 0);

        // ---- Wi-Fi
        _wifi.Columns.Add(Col(L.T("Rete"), nameof(WifiRow.Ssid), 1, star: true));
        _wifi.Columns.Add(SignalColumn());
        _wifi.Columns.Add(Col(L.T("Canale"), nameof(WifiRow.Channel), 70));
        _wifi.Columns.Add(Col(L.T("Banda"), nameof(WifiRow.Band), 80));
        _wifi.Columns.Add(Col(L.T("Sicurezza"), nameof(WifiRow.Security), 100));
        _wifi.Columns.Add(Col("BSSID", nameof(WifiRow.Bssid), 150));
        _wifi.MaxHeight = 320;
        _wifiBtn = Ui.Btn(L.T("Cerca le reti"), SymbolRegular.Wifi124, async (_, _) => await Wifi(true));
        _wifiHint.TextWrapping = TextWrapping.Wrap;
        var wifiCard = Ui.Card(L.T("Wi-Fi"), L.T("La tua connessione e le reti vicine: il segnale e i canali affollati spiegano spesso una connessione lenta."),
            _wifiNow, _wifiHint, Ui.Row(_wifiBtn), Box(_wifi));
        ((StackPanel)((StackPanel)wifiCard.Child).Children[^2]).Margin = new Thickness(0, 10, 0, 0);

        // ---- dispositivi
        _lan.Columns.Add(Col(L.T("Indirizzo IP"), nameof(LanDevice.Ip), 130));
        _lan.Columns.Add(Col(L.T("Nome"), nameof(LanDevice.Name), 1, star: true));
        _lan.Columns.Add(Col(L.T("Indirizzo MAC"), nameof(LanDevice.Mac), 170));
        _lan.Columns.Add(Col("", nameof(LanDevice.Kind), 110));
        _lan.MaxHeight = 360;
        _lanBtn = Ui.Btn(L.T("Cerca i dispositivi"), SymbolRegular.Search24, async (_, _) => await Lan());
        var lanCard = Ui.Card(L.T("Dispositivi nella rete"), L.T("Telefoni, PC, TV, stampanti e altri dispositivi collegati alla tua stessa rete in questo momento."),
            Ui.Row(_lanBtn, _lanInfo), _lanBar, Box(_lan));

        Content = Ui.ScrollPage(Ui.Header(L.T("Rete e Wi-Fi"), L.T("Velocità della connessione, qualità del Wi-Fi e dispositivi collegati alla rete di casa.")),
            speedCard, wifiCard, lanCard);
        Loaded += async (_, _) =>
        {
            if (_wifi.ItemsSource == null) await Wifi(false);
        };
    }

    private static TextBlock Big() => new() { Text = "—", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") };

    private static UIElement Gauge(string title, TextBlock value, string unit)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        p.Children.Add(new TextBlock { Text = title, Foreground = Ui.Res("TextFillColorSecondaryBrush") });
        p.Children.Add(Ui.Row(value, new TextBlock { Text = " " + unit, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6), Foreground = Ui.Res("TextFillColorTertiaryBrush") }));
        return p;
    }

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None,
    };

    private static DataGridTextColumn Col(string header, string path, double width, bool star = false) =>
        new() { Header = header, Binding = new Binding(path), Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel) };

    private static DataGridTemplateColumn SignalColumn()
    {
        var col = new DataGridTemplateColumn { Header = L.T("Segnale"), Width = new DataGridLength(230), SortMemberPath = nameof(WifiRow.SignalPercent) };
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(WifiRow.SignalPercent)));
        bar.SetValue(WidthProperty, 70.0);
        bar.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(WifiRow.Signal)));
        text.SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        panel.AppendChild(bar);
        panel.AppendChild(text);
        col.CellTemplate = new DataTemplate { VisualTree = panel };
        return col;
    }

    private static Border Box(UIElement c) => new() { Margin = new Thickness(0, 10, 0, 0), Child = c };

    private async Task SpeedTest()
    {
        _speedBtn.IsEnabled = false;
        _speedBar.Visibility = Visibility.Visible;
        _down.Text = _up.Text = _ping.Text = _jitter.Text = "…";
        try
        {
            var r = await Task.Run(() => NetTools.SpeedTestAsync((msg, p) => Dispatcher.InvokeAsync(() =>
            {
                _speedInfo.Text = msg;
                _speedBar.Value = p;
            }), CancellationToken.None));
            _down.Text = r.DownloadMbps.ToString("0.0", L.Culture);
            _up.Text = r.UploadMbps.ToString("0.0", L.Culture);
            _ping.Text = r.PingMs.ToString("0", L.Culture);
            _jitter.Text = r.JitterMs.ToString("0", L.Culture);
            var verdict = r.DownloadMbps switch
            {
                < 10 => L.T("lenta: va bene per navigare, a fatica per i video in HD"),
                < 30 => L.T("discreta: video in HD e videochiamate"),
                < 100 => L.T("buona: video in 4K e più dispositivi insieme"),
                _ => L.T("ottima: anche file grandi e tante persone insieme"),
            };
            _speedInfo.Text = L.T($"Connessione {verdict}.") + (r.Provider.Length > 0 ? $"  {r.Provider} · {r.Server}" : $"  {r.Server}");
            Settings.Set("net.last", $"{DateTime.Now.ToString("g", L.Culture)} · ↓ {_down.Text} · ↑ {_up.Text} Mbit/s · {_ping.Text} ms");
        }
        catch (Exception ex)
        {
            _down.Text = _up.Text = _ping.Text = _jitter.Text = "—";
            _speedInfo.Text = L.T("Test non riuscito: ") + ex.Message;
        }
        finally
        {
            _speedBtn.IsEnabled = true;
            _speedBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task Wifi(bool rescan)
    {
        _wifiBtn.IsEnabled = false;
        if (rescan) _wifiHint.Text = L.T("Scansione delle reti…");
        try
        {
            var (nets, now) = await Task.Run(() => NetTools.WifiAsync(rescan));
            if (now != null)
            {
                var quality = now.SignalPercent switch { >= 75 => L.T("ottimo"), >= 50 => L.T("buono"), >= 30 => L.T("debole"), _ => L.T("molto debole") };
                _wifiNow.Text = L.T($"Connesso a «{now.Ssid}» · segnale {now.SignalPercent} % ({quality}) · canale {now.Channel} ({now.Band}) · {now.RxMbps:0} Mbit/s") + (now.Phy.Length > 0 ? " · " + now.Phy : "");
            }
            else _wifiNow.Text = nets.Count == 0 ? L.T("Nessuna scheda Wi-Fi attiva (o il PC è collegato via cavo).") : L.T("Non connesso a una rete Wi-Fi.");
            var hints = new List<string>();
            if (now is { SignalPercent: < 40 }) hints.Add(L.T("Il segnale è debole: avvicinati al router o valuta un ripetitore."));
            if (now != null && now.Band.StartsWith("2") && nets.Any(n => n.Ssid == now.Ssid && n.Band.StartsWith("5")))
                hints.Add(L.T("La tua rete trasmette anche a 5 GHz: collegandoti a quella banda andresti più veloce (vicino al router)."));
            if (now != null && now.Band.StartsWith("2") && NetTools.BestChannel24(nets) is { } best && best != now.Channel)
                hints.Add(L.T($"Sul canale {now.Channel} ci sono molte reti: impostando il router sul canale {best} potresti avere meno disturbi."));
            _wifiHint.Text = string.Join("\n", hints);
            var mine = now?.Bssid;
            _wifi.ItemsSource = nets.Where(n => n.Ssid.Length > 0).Select(n => new WifiRow(n.Bssid == mine ? "★ " + n.Ssid : n.Ssid, $"{n.SignalPercent} % · {n.Rssi} dBm", n.SignalPercent,
                n.Channel > 0 ? n.Channel.ToString() : "", n.Band, n.Secured ? L.T("protetta") : L.T("aperta"), n.Bssid, n.Bssid == mine)).ToList();
        }
        catch (NetTools.WifiAccessDenied ex)
        {
            _wifiHint.Text = ex.Message;
            if (Dlg.Confirm(ex.Message, AppInfo.Name, L.T("Apri le impostazioni"), L.T("Chiudi"))) Util.OpenUrl("ms-settings:privacy-location");
        }
        catch (Exception ex)
        {
            _wifiHint.Text = L.T("Errore: ") + ex.Message;
        }
        finally
        {
            _wifiBtn.IsEnabled = true;
        }
    }

    private async Task Lan()
    {
        _lanBtn.IsEnabled = false;
        _lanBar.Visibility = Visibility.Visible;
        _lanInfo.Text = "  " + L.T("Cerco i dispositivi (circa 10 secondi)…");
        try
        {
            var list = await Task.Run(() => NetTools.ScanLanAsync(p => Dispatcher.InvokeAsync(() => _lanBar.Value = p), CancellationToken.None));
            _lan.ItemsSource = list;
            _lanInfo.Text = "  " + L.T($"{list.Count} dispositivi trovati. Alcuni (es. telefoni in standby) possono non rispondere.");
        }
        catch (Exception ex)
        {
            _lanInfo.Text = "  " + L.T("Errore: ") + ex.Message;
        }
        finally
        {
            _lanBtn.IsEnabled = true;
            _lanBar.Visibility = Visibility.Collapsed;
        }
    }
}
