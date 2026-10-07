using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SwinKnife.Controls;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Controlla la stabilità della connessione nel tempo: ping continuo con grafico, latenza e pacchetti persi.</summary>
public sealed class PingMonitorPage : UserControl, IToolPage
{
    private readonly TextBox _host = new() { Text = "8.8.8.8", Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _interval = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _startBtn;
    private readonly Canvas _canvas = new() { Height = 170, ClipToBounds = true };
    private readonly TextBlock _stats = new() { Margin = new Thickness(0, 10, 0, 0), FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly Ping _ping = new();
    private readonly List<double> _samples = new();   // ms, oppure -1 se timeout
    private const int MaxSamples = 180;
    private long _sent, _lost;
    private double _min = double.MaxValue, _max, _sum;
    private bool _running;
    private CancellationTokenSource? _cts;

    public PingMonitorPage(MainWindow main)
    {
        foreach (var (label, _) in Intervals) _interval.Items.Add(label);
        _interval.SelectedIndex = 1;
        _startBtn = Ui.Btn(L.T("Avvia"), SymbolRegular.Play24, (_, _) => Toggle(), primary: true);

        _canvas.SizeChanged += (_, _) => Redraw();
        var graphCard = Ui.Card(L.T("Latenza nel tempo"), L.T("Ogni barra è un ping; più è bassa, meglio è. I buchi rossi sono pacchetti persi."),
            new Border { Child = _canvas, Background = Ui.Res("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6) },
            _stats);

        var controls = Ui.Card(L.T("Destinazione"), null,
            Ui.Row(Ui.Label(L.T("Indirizzo o sito:")), new Border { Width = 8 }, _host, _interval, new Border { Width = 8 }, _startBtn));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Monitor connessione"), L.T("Scopri quando e quanto cade Internet, con un ping continuo e un grafico.")),
            controls, graphCard);
    }

    private static readonly (string, int)[] Intervals =
    [
        (L.T("ogni 0,5 s"), 500), (L.T("ogni 1 s"), 1000), (L.T("ogni 2 s"), 2000), (L.T("ogni 5 s"), 5000),
    ];

    private void Toggle()
    {
        if (_running) Stop();
        else Start();
    }

    private void Start()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) return;
        _samples.Clear();
        _sent = _lost = 0;
        _min = double.MaxValue; _max = _sum = 0;
        _running = true;
        _startBtn.Content = L.T("Interrompi");
        _startBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24 };
        _host.IsEnabled = _interval.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _ = Loop(host, Intervals[_interval.SelectedIndex].Item2, _cts.Token);
    }

    private void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _startBtn.Content = L.T("Avvia");
        _startBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 };
        _host.IsEnabled = _interval.IsEnabled = true;
    }

    private async System.Threading.Tasks.Task Loop(string host, int interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            double ms = -1;
            try
            {
                var reply = await _ping.SendPingAsync(host, Math.Min(interval, 2000));
                if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
            }
            catch { ms = -1; }
            if (ct.IsCancellationRequested) break;

            _sent++;
            if (ms < 0) _lost++;
            else { _min = Math.Min(_min, ms); _max = Math.Max(_max, ms); _sum += ms; }
            _samples.Add(ms);
            if (_samples.Count > MaxSamples) _samples.RemoveAt(0);
            Redraw();
            UpdateStats();

            try { await System.Threading.Tasks.Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void UpdateStats()
    {
        var ok = _sent - _lost;
        var avg = ok > 0 ? _sum / ok : 0;
        var loss = _sent > 0 ? _lost * 100.0 / _sent : 0;
        var last = _samples.Count > 0 ? _samples[^1] : -1;
        _stats.Text = L.T($"Ultimo: {(last < 0 ? "—" : last + " ms")}   ·   min {(ok > 0 ? _min + " ms" : "—")}   ·   medio {(ok > 0 ? avg.ToString("0") + " ms" : "—")}   ·   max {(ok > 0 ? _max + " ms" : "—")}")
            + "\n" + L.T($"Inviati {_sent}   ·   persi {_lost}   ·   perdita {loss:0.#}%");
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0 || _samples.Count == 0) return;

        var scaleMax = Math.Max(50, _samples.Where(s => s >= 0).DefaultIfEmpty(0).Max() * 1.2);
        var barW = w / MaxSamples;
        var good = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var mid = new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x3E));
        var bad = new SolidColorBrush(Color.FromRgb(0xE5, 0x4B, 0x4B));

        for (var i = 0; i < _samples.Count; i++)
        {
            var x = i * barW;
            var v = _samples[i];
            if (v < 0)
            {
                _canvas.Children.Add(new Rectangle { Width = Math.Max(1, barW - 1), Height = h, Fill = bad, Opacity = 0.35 });
                Canvas.SetLeft(_canvas.Children[^1], x);
                Canvas.SetTop(_canvas.Children[^1], 0);
                continue;
            }
            var bh = Math.Max(2, v / scaleMax * h);
            var rect = new Rectangle { Width = Math.Max(1, barW - 1), Height = bh, Fill = v < 60 ? good : v < 150 ? mid : bad };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, h - bh);
            _canvas.Children.Add(rect);
        }
    }

    public bool CanClose() { if (_running) Stop(); return true; }
    public void Shutdown() { _cts?.Cancel(); _ping.Dispose(); }
}
