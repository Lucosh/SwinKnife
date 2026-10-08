using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Monitor in tempo reale di CPU, memoria e rete, con grafico e processi più pesanti.</summary>
public sealed class ResourceMonitorPage : UserControl, IToolPage
{
    private readonly LiveStats _stats = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _cpu = Big(), _ram = Big(), _net = Big();
    private readonly TextBlock _ramSub = Ui.Hint(""), _netSub = Ui.Hint("");
    private readonly Canvas _cpuGraph = new() { Height = 90, ClipToBounds = true };
    private readonly StackPanel _top = new();
    private readonly List<double> _cpuHistory = new();
    private int _tick;

    public ResourceMonitorPage(MainWindow main)
    {
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 0) };
        tiles.Children.Add(Tile(L.T("CPU"), _cpu, Ui.Hint("")));
        tiles.Children.Add(Tile(L.T("Memoria"), _ram, _ramSub));
        tiles.Children.Add(Tile(L.T("Rete"), _net, _netSub));

        var graphCard = Ui.Card(L.T("CPU negli ultimi secondi"), null,
            new Border { Child = _cpuGraph, Background = Ui.Res("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6) });
        var topCard = Ui.Card(L.T("Processi che usano più memoria"), null, _top);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Monitor risorse"), L.T("Vedi in diretta quanto lavorano CPU, memoria e rete.")),
            Ui.Card(L.T("Adesso"), null, tiles), graphCard, topCard);

        _timer.Tick += (_, _) => Update();
        _stats.Sample(); // primo campione per inizializzare i delta
    }

    public void Activated() => _timer.Start();
    public void Shutdown() => _timer.Stop();

    private static TextBlock Big() => new() { FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Ui.Res("TextFillColorPrimaryBrush") };

    private static FrameworkElement Tile(string label, TextBlock value, TextBlock sub)
    {
        var p = new StackPanel { Margin = new Thickness(6) };
        p.Children.Add(new TextBlock { Text = label, Foreground = Ui.Res("TextFillColorTertiaryBrush"), FontSize = 12, FontWeight = FontWeights.SemiBold });
        p.Children.Add(value);
        p.Children.Add(sub);
        return p;
    }

    private void Update()
    {
        var s = _stats.Sample();
        _cpu.Text = $"{s.CpuPercent:0}%";
        _ram.Text = $"{s.RamPercent:0}%";
        _ramSub.Text = $"{Util.HumanSize(s.RamUsed)} / {Util.HumanSize(s.RamTotal)}";
        _net.Text = Speed(s.NetDownBps + s.NetUpBps);
        _netSub.Text = L.T($"↓ {Speed(s.NetDownBps)}   ↑ {Speed(s.NetUpBps)}");

        _cpuHistory.Add(s.CpuPercent);
        if (_cpuHistory.Count > 120) _cpuHistory.RemoveAt(0);
        DrawCpu();

        if (_tick++ % 2 == 0) RefreshTop();
    }

    private static string Speed(double bps)
    {
        if (bps < 1024) return $"{bps:0} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:0.0} KB/s";
        return $"{bps / 1024 / 1024:0.0} MB/s";
    }

    private void DrawCpu()
    {
        _cpuGraph.Children.Clear();
        var w = _cpuGraph.ActualWidth;
        var h = _cpuGraph.ActualHeight;
        if (w <= 0 || h <= 0 || _cpuHistory.Count < 2) return;
        var step = w / 119.0;
        var points = new PointCollection();
        for (var i = 0; i < _cpuHistory.Count; i++)
            points.Add(new Point(i * step, h - _cpuHistory[i] / 100.0 * h));
        var line = new Polyline { Points = points, Stroke = Ui.Res("AccentTextFillColorPrimaryBrush"), StrokeThickness = 1.6 };
        var fillPts = new PointCollection(points) { new Point((_cpuHistory.Count - 1) * step, h), new Point(0, h) };
        _cpuGraph.Children.Add(new Polygon { Points = fillPts, Fill = new SolidColorBrush(Color.FromArgb(40, 0x4C, 0x8B, 0xF5)) });
        _cpuGraph.Children.Add(line);
    }

    private void RefreshTop()
    {
        _top.Children.Clear();
        foreach (var (name, ram) in LiveStats.TopByMemory(6))
        {
            var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var size = new TextBlock { Text = Util.HumanSize(ram), Foreground = Ui.Res("TextFillColorSecondaryBrush") };
            DockPanel.SetDock(size, Dock.Right);
            dock.Children.Add(size);
            dock.Children.Add(new TextBlock { Text = name, Foreground = Ui.Res("TextFillColorPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            _top.Children.Add(dock);
        }
    }
}
