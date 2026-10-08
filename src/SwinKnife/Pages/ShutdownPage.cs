using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Programma lo spegnimento, il riavvio, la sospensione o il blocco del PC dopo un certo tempo.</summary>
public sealed class ShutdownPage : UserControl, IToolPage
{
    private static readonly (string label, PowerActions.Kind kind)[] Actions =
    [
        (L.T("Spegni"), PowerActions.Kind.Shutdown),
        (L.T("Riavvia"), PowerActions.Kind.Restart),
        (L.T("Sospendi"), PowerActions.Kind.Sleep),
        (L.T("Iberna"), PowerActions.Kind.Hibernate),
        (L.T("Blocca"), PowerActions.Kind.Lock),
    ];

    private readonly ComboBox _action = new() { Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _minutes = new() { Value = 30, Minimum = 0, Maximum = 1440, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 15, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Wpf.Ui.Controls.Button _startBtn, _cancelBtn;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _target;
    private PowerActions.Kind _pending;
    private bool _running;

    public ShutdownPage(MainWindow main)
    {
        foreach (var a in Actions) _action.Items.Add(a.label);
        _action.SelectedIndex = 0;

        _startBtn = Ui.Btn(L.T("Avvia il conto alla rovescia"), SymbolRegular.Play24, (_, _) => Start(), primary: true);
        _cancelBtn = Ui.Btn(L.T("Annulla"), SymbolRegular.Dismiss24, (_, _) => Cancel());
        _cancelBtn.IsEnabled = false;

        var card = Ui.Card(L.T("Azione programmata"), null,
            Ui.Row(Ui.Label(L.T("Azione:"), bold: true), new Border { Width = 10 }, _action),
            Ui.Row(Ui.Label(L.T("Fra quanti minuti:"), bold: true), new Border { Width = 10 }, _minutes,
                new Border { Width = 10 }, Ui.Hint(L.T("0 = subito"))),
            Ui.Row(_startBtn, _cancelBtn),
            _status);
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[0].Margin = new Thickness(0, 10, 0, 0);
        rows[1].Margin = new Thickness(0, 12, 0, 0);
        rows[2].Margin = new Thickness(0, 16, 0, 0);

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Salva prima il tuo lavoro"),
            Message = L.T("Spegnimento e riavvio vengono gestiti da Windows e avvengono anche se chiudi SwinKnife. Sospensione, ibernazione e blocco richiedono invece che SwinKnife resti aperto fino allo scadere del tempo."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Spegnimento programmato"), L.T("Spegni, riavvia o sospendi il PC dopo un po' di tempo.")),
            info, card);

        _timer.Tick += (_, _) => Tick();
        UpdateStatus();
    }

    private void Start()
    {
        var kind = Actions[_action.SelectedIndex].kind;
        var minutes = (int)Math.Round(_minutes.Value ?? 0);
        var seconds = Math.Max(0, minutes * 60);

        if (minutes == 0)
        {
            if (!Dlg.Confirm(L.T($"Eseguire «{Actions[_action.SelectedIndex].label}» adesso?"))) return;
            PowerActions.Now(kind);
            return;
        }

        _pending = kind;
        _target = DateTime.Now.AddSeconds(seconds);
        _running = true;
        if (PowerActions.IsSystemScheduled(kind)) PowerActions.Schedule(kind, seconds); // shutdown.exe
        else _timer.Start(); // gestito dall'app

        if (!_timer.IsEnabled) _timer.Start(); // anche per aggiornare il conto alla rovescia
        _startBtn.IsEnabled = false;
        _action.IsEnabled = _minutes.IsEnabled = false;
        _cancelBtn.IsEnabled = true;
        UpdateStatus();
        MainWindow.Notify(L.T("Conto alla rovescia avviato."));
    }

    private void Tick()
    {
        if (!_running) { _timer.Stop(); return; }
        if (DateTime.Now >= _target)
        {
            _timer.Stop();
            if (!PowerActions.IsSystemScheduled(_pending)) PowerActions.Now(_pending);
            _running = false;
            return;
        }
        UpdateStatus();
    }

    private void Cancel()
    {
        _timer.Stop();
        if (_running && PowerActions.IsSystemScheduled(_pending)) PowerActions.Abort();
        _running = false;
        _startBtn.IsEnabled = true;
        _action.IsEnabled = _minutes.IsEnabled = true;
        _cancelBtn.IsEnabled = false;
        UpdateStatus();
        MainWindow.Notify(L.T("Programmazione annullata."));
    }

    private void UpdateStatus()
    {
        if (_running)
        {
            var left = _target - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            _status.Text = L.T($"⏳ {Actions.First(a => a.kind == _pending).label} fra {Util.HumanTime(left.TotalSeconds)} (alle {_target:HH:mm:ss}).");
            _status.Foreground = Ui.Res("TextFillColorPrimaryBrush");
        }
        else
        {
            _status.Text = L.T("Nessuna azione programmata.");
            _status.Foreground = Ui.Res("TextFillColorSecondaryBrush");
        }
    }

    public bool CanClose()
    {
        if (_running && !PowerActions.IsSystemScheduled(_pending))
            return Dlg.Confirm(L.T("C'è un conto alla rovescia attivo che verrà annullato chiudendo. Continuare?"));
        return true;
    }

    public void Shutdown() => _timer.Stop();
}
