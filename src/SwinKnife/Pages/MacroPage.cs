using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Registra una sequenza di tastiera e mouse e la riproduce, come le macro di Logitech/Razer. F9 registra, F10 riproduce.</summary>
public sealed class MacroPage : UserControl, IToolPage
{
    private static readonly string Dir = Path.Combine(AppInfo.DataDir, "macros");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly CheckBox _captureMove = new() { Content = L.T("Registra anche i movimenti del mouse"), IsChecked = true };
    private readonly RadioButton _forever = new() { Content = L.T("ripeti finché non premo di nuovo F10"), IsChecked = true };
    private readonly RadioButton _times = new() { Content = L.T("ripeti un numero di volte:"), VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _count = new() { Value = 1, Minimum = 1, Maximum = 100000, Width = 110, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _speed = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly Wpf.Ui.Controls.Button _recBtn, _playBtn;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0), FontSize = 15 };
    private readonly StackPanel _saved = new();

    private InputRecorder? _recorder;
    private List<MacroEvent> _events = new();
    private bool _recording;
    private CancellationTokenSource? _playCts;
    private GlobalHotkey? _recHotkey, _playHotkey;

    private static readonly (string, double)[] Speeds =
    [
        (L.T("velocità originale"), 1), (L.T("2× più veloce"), 2), (L.T("4× più veloce"), 4), (L.T("metà velocità"), 0.5),
    ];

    public MacroPage(MainWindow main)
    {
        foreach (var (label, _) in Speeds) _speed.Items.Add(label);
        _speed.SelectedIndex = 0;

        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come si usa"),
            Message = L.T("Premi F9 (o il pulsante) per iniziare a registrare, fai le tue azioni, poi premi di nuovo F9 per fermarti. Premi F10 per riprodurre la macro; premilo di nuovo per fermarla. Puoi salvarla e riusarla."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _recBtn = Ui.Btn(L.T("Registra (F9)"), SymbolRegular.Record24, (_, _) => ToggleRecord(), primary: true);
        _playBtn = Ui.Btn(L.T("Riproduci (F10)"), SymbolRegular.Play24, (_, _) => TogglePlay());
        var saveBtn = Ui.Btn(L.T("Salva…"), SymbolRegular.Save24, (_, _) => Save());

        var recCard = Ui.Card(L.T("Registrazione"), null,
            _captureMove, Ui.Row(_recBtn, _playBtn, saveBtn));

        var playCard = Ui.Card(L.T("Riproduzione"), null,
            _forever, Ui.Row(_times, _count),
            Ui.Row(Ui.Label(L.T("Velocità:")), new Border { Width = 8 }, _speed));

        var savedCard = Ui.Card(L.T("Macro salvate"), null,
            Ui.Row(Ui.Btn(L.T("Aggiorna elenco"), SymbolRegular.ArrowClockwise24, (_, _) => LoadList())), _saved);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Macro"), L.T("Registra una sequenza di tasti e clic e falla ripetere automaticamente.")),
            warn, recCard, playCard, savedCard, _status);
        LoadList();
    }

    public void Activated()
    {
        _recHotkey ??= new GlobalHotkey(0, 0x78, () => Dispatcher.InvokeAsync(ToggleRecord)); // F9
        _playHotkey ??= new GlobalHotkey(0, 0x79, () => Dispatcher.InvokeAsync(TogglePlay));   // F10
    }

    // ------------------------------------------------------------------ registrazione
    private void ToggleRecord()
    {
        if (_playCts != null) return; // non registrare durante la riproduzione
        if (_recording)
        {
            _events = _recorder?.Stop() ?? _events;
            _recording = false;
            _recBtn.Content = L.T("Registra (F9)");
            _recBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Record24 };
            _status.Text = L.T($"Registrati {_events.Count} eventi. Premi F10 per riprodurli.");
        }
        else
        {
            _recorder = new InputRecorder([0x78, 0x79]) { CaptureMouseMove = _captureMove.IsChecked == true };
            _recorder.Start();
            _recording = true;
            _recBtn.Content = L.T("Ferma registrazione (F9)");
            _recBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24 };
            _status.Text = L.T("Registrazione in corso… fai le tue azioni, poi premi F9.");
        }
    }

    // ------------------------------------------------------------------ riproduzione
    private void TogglePlay()
    {
        if (_recording) return;
        if (_playCts != null) { _playCts.Cancel(); return; }
        if (_events.Count == 0) { Dlg.Info(L.T("Non c'è nessuna macro da riprodurre: registrane una o caricane una salvata.")); return; }

        var repeat = _forever.IsChecked == true ? 0 : Math.Max(1, (int)Math.Round(_count.Value ?? 1));
        var speed = Speeds[_speed.SelectedIndex].Item2;
        _playCts = new CancellationTokenSource();
        var ct = _playCts.Token;
        _playBtn.Content = L.T("Ferma (F10)");
        _playBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24 };
        _status.Text = L.T("Riproduzione in corso… premi F10 per fermare.");
        var events = _events;
        Task.Run(() =>
        {
            try { InputRecorder.Play(events, repeat, speed, ct); }
            catch (Exception ex) { AppInfo.Log(ex, "Macro play"); }
            finally { Dispatcher.InvokeAsync(EndPlay); }
        });
    }

    private void EndPlay()
    {
        _playCts?.Dispose();
        _playCts = null;
        _playBtn.Content = L.T("Riproduci (F10)");
        _playBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Play24 };
        _status.Text = L.T($"Pronto. {_events.Count} eventi in memoria.");
    }

    // ------------------------------------------------------------------ salvataggio
    private void Save()
    {
        if (_events.Count == 0) { Dlg.Info(L.T("Registra prima una macro.")); return; }
        var name = Dlg.Prompt(L.T("Salva la macro"), L.T("Nome della macro:"), L.T("La mia macro"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            File.WriteAllText(Path.Combine(Dir, safe + ".json"), JsonSerializer.Serialize(_events, Json));
            MainWindow.Notify(L.T($"Macro «{name}» salvata."));
            LoadList();
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void LoadList()
    {
        _saved.Children.Clear();
        if (!Directory.Exists(Dir) || Directory.GetFiles(Dir, "*.json").Length == 0)
        {
            _saved.Children.Add(Ui.Hint(L.T("Nessuna macro salvata.")));
            return;
        }
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var load = Ui.Btn(L.T("Carica"), SymbolRegular.FolderOpen24, (_, _) => Load(file));
            var del = Ui.IconBtn(SymbolRegular.Delete24, L.T("Elimina"), (_, _) =>
            {
                if (Dlg.Confirm(L.T($"Eliminare la macro «{name}»?"))) { try { File.Delete(file); } catch { } LoadList(); }
            });
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(del, Dock.Right);
            DockPanel.SetDock(load, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(load);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            _saved.Children.Add(row);
        }
    }

    private void Load(string file)
    {
        try
        {
            _events = JsonSerializer.Deserialize<List<MacroEvent>>(File.ReadAllText(file)) ?? new();
            _status.Text = L.T($"Caricata «{Path.GetFileNameWithoutExtension(file)}»: {_events.Count} eventi. Premi F10 per riprodurla.");
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    public bool CanClose()
    {
        if (_recording || _playCts != null)
            return Dlg.Confirm(L.T("C'è una macro in registrazione o riproduzione: interromperla?"));
        return true;
    }

    public void Shutdown()
    {
        _playCts?.Cancel();
        if (_recording) try { _recorder?.Stop(); } catch { }
        _recHotkey?.Dispose();
        _playHotkey?.Dispose();
        _recHotkey = _playHotkey = null;
    }
}
