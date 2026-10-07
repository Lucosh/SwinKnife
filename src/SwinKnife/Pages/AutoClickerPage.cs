using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Clicca in automatico a intervalli regolari, con una scorciatoia globale per avviare e fermare (F6).</summary>
public sealed class AutoClickerPage : UserControl, IToolPage
{
    private readonly NumberBox _interval = new() { Value = 100, Minimum = 1, Maximum = 600000, Width = 120 };
    private readonly ComboBox _button = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _clickType = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly RadioButton _forever = new() { Content = L.T("finché non premo di nuovo F6"), IsChecked = true };
    private readonly RadioButton _times = new() { Content = L.T("un numero preciso di clic:"), VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _count = new() { Value = 50, Minimum = 1, Maximum = 1_000_000, Width = 120, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _atCursor = new() { Content = L.T("dove si trova il mouse"), IsChecked = true };
    private readonly RadioButton _atFixed = new() { Content = L.T("in un punto fisso:"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _point = Ui.Hint(L.T("(nessun punto)"));
    private readonly Wpf.Ui.Controls.Button _startBtn, _captureBtn;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0), FontSize = 15 };

    private GlobalHotkey? _hotkey;
    private Thread? _worker;
    private volatile bool _running;
    private (int x, int y)? _fixed;
    private long _clicks;

    public AutoClickerPage(MainWindow main)
    {
        foreach (var s in new[] { L.T("Tasto sinistro"), L.T("Tasto destro"), L.T("Tasto centrale") }) _button.Items.Add(s);
        foreach (var s in new[] { L.T("Clic singolo"), L.T("Doppio clic") }) _clickType.Items.Add(s);
        _button.SelectedIndex = 0;
        _clickType.SelectedIndex = 0;

        _startBtn = Ui.Btn(L.T("Avvia (F6)"), SymbolRegular.Play24, (_, _) => Toggle(), primary: true);
        _captureBtn = Ui.Btn(L.T("Cattura posizione (3 s)"), SymbolRegular.Target24, (_, _) => Capture());

        var speed = Ui.Card(L.T("Clic"), null,
            Ui.Row(Ui.Label(L.T("Intervallo:")), new Border { Width = 8 }, _interval, new Border { Width = 8 }, Ui.Label(L.T("millisecondi")), new Border { Width = 16 }, _button, _clickType));

        var repeat = Ui.Card(L.T("Quante volte"), null,
            _forever, Ui.Row(_times, _count));

        var place = Ui.Card(L.T("Dove cliccare"), null,
            _atCursor, Ui.Row(_atFixed, new Border { Width = 10 }, _captureBtn, new Border { Width = 10 }, _point));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Autoclicker"), L.T("Clicca da solo a intervalli regolari. Premi F6 per avviare e fermare, anche dentro altri programmi.")),
            speed, repeat, place,
            Ui.Row(_startBtn), _status);
    }

    public void Activated()
    {
        _hotkey ??= new GlobalHotkey(0, 0x75, () => Dispatcher.InvokeAsync(Toggle)); // F6
        if (_hotkey is { Ok: false }) _status.Text = L.T("Nota: la scorciatoia F6 è già usata da un altro programma; usa il pulsante Avvia.");
    }

    private void Capture()
    {
        _atFixed.IsChecked = true;
        _captureBtn.IsEnabled = false;
        _point.Text = L.T("Sposta il mouse sul punto…  3");
        var left = 3;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            left--;
            if (left > 0) { _point.Text = L.T("Sposta il mouse sul punto…  ") + left; return; }
            timer.Stop();
            _fixed = InputSim.CursorPos();
            _point.Text = $"X={_fixed.Value.x}, Y={_fixed.Value.y}";
            _captureBtn.IsEnabled = true;
        };
        timer.Start();
    }

    private void Toggle()
    {
        if (_running) Stop();
        else Start();
    }

    private void Start()
    {
        if (_atFixed.IsChecked == true && _fixed == null) { Dlg.Info(L.T("Cattura prima la posizione fissa, oppure scegli «dove si trova il mouse».")); return; }
        var interval = Math.Max(1, (int)Math.Round(_interval.Value ?? 100));
        var button = (ClickButton)_button.SelectedIndex;
        var dbl = _clickType.SelectedIndex == 1;
        var limit = _forever.IsChecked == true ? 0 : Math.Max(1, (int)Math.Round(_count.Value ?? 1));
        var pos = _atFixed.IsChecked == true ? _fixed : null;

        _clicks = 0;
        _running = true;
        _startBtn.Content = L.T("Ferma (F6)");
        _startBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24 };

        _worker = new Thread(() =>
        {
            while (_running)
            {
                if (pos is { } p) InputSim.MoveTo(p.x, p.y);
                InputSim.Click(button, dbl);
                var done = Interlocked.Increment(ref _clicks);
                Dispatcher.InvokeAsync(() => _status.Text = L.T($"Clic eseguiti: {done}"));
                if (limit > 0 && done >= limit) { Dispatcher.InvokeAsync(Stop); break; }
                Thread.Sleep(interval);
            }
        }) { IsBackground = true, Name = "SwinKnifeAutoClicker" };
        _worker.Start();
    }

    private void Stop()
    {
        _running = false;
        _startBtn.Content = L.T("Avvia (F6)");
        _startBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 };
        _status.Text = L.T($"Fermato. Clic totali: {Interlocked.Read(ref _clicks)}");
    }

    public bool CanClose() { Stop(); return true; }
    public void Shutdown() { _running = false; _hotkey?.Dispose(); _hotkey = null; }
}
