using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Crea attività pianificate di Windows per far partire programmi o script a orari stabiliti.</summary>
public sealed class SchedulePage : UserControl, IToolPage
{
    private static readonly (string label, string code)[] Days =
    [
        (L.T("Lunedì"), "MON"), (L.T("Martedì"), "TUE"), (L.T("Mercoledì"), "WED"), (L.T("Giovedì"), "THU"),
        (L.T("Venerdì"), "FRI"), (L.T("Sabato"), "SAT"), (L.T("Domenica"), "SUN"),
    ];

    private readonly TextBox _name = new() { PlaceholderText = L.T("Nome (es. Backup foto)"), Width = 300 };
    private readonly System.Windows.Controls.TextBox _program = new() { IsReadOnly = true, Width = 360, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _args = new() { PlaceholderText = L.T("Parametri (facoltativi)"), Width = 360 };
    private readonly ComboBox _when = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _day = new() { Width = 140, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _time = new() { Text = "09:00", Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new();

    public SchedulePage(MainWindow main)
    {
        foreach (var w in new[] { L.T("Ogni giorno"), L.T("Ogni settimana"), L.T("A ogni accesso"), L.T("Una volta sola") }) _when.Items.Add(w);
        _when.SelectedIndex = 0;
        foreach (var d in Days) _day.Items.Add(d.label);
        _day.SelectedIndex = 0;
        _when.SelectionChanged += (_, _) => UpdateFields();

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("A cosa serve"),
            Message = L.T("Fa partire da solo un programma o uno script agli orari che scegli, usando l'Utilità di pianificazione di Windows. Utile ad esempio per lanciare un backup ogni giorno. Le attività restano anche se chiudi SwinKnife."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var form = Ui.Card(L.T("Nuova attività"), null,
            _name,
            Ui.Row(_program, new Border { Width = 8 },
                Ui.Btn(L.T("Scegli programma…"), SymbolRegular.FolderOpen24, (_, _) => Pick())),
            _args,
            Ui.Row(Ui.Label(L.T("Quando:"), bold: true), new Border { Width = 8 }, _when,
                new Border { Width = 14 }, _day,
                new Border { Width = 14 }, Ui.Label(L.T("alle")), new Border { Width = 6 }, _time),
            Ui.Row(Ui.Btn(L.T("Crea attività"), SymbolRegular.CalendarAdd24, (_, _) => Create(), primary: true)));
        var rows = ((StackPanel)form.Child).Children.OfType<StackPanel>().ToList();
        _name.Margin = new Thickness(0, 10, 0, 0);
        rows[0].Margin = new Thickness(0, 10, 0, 0);
        _args.Margin = new Thickness(0, 10, 0, 0);
        rows[1].Margin = new Thickness(0, 12, 0, 0);
        rows[2].Margin = new Thickness(0, 14, 0, 0);

        var saved = Ui.Card(L.T("Attività create con SwinKnife"), null, _list);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Pianificazione attività"), L.T("Fai partire programmi e script agli orari che decidi tu.")),
            info, form, saved);
        UpdateFields();
        Render();
    }

    private void UpdateFields()
    {
        var w = (Scheduler.When)_when.SelectedIndex;
        _day.Visibility = w == Scheduler.When.Weekly ? Visibility.Visible : Visibility.Collapsed;
        _time.IsEnabled = w != Scheduler.When.AtLogon;
    }

    private void Pick()
    {
        var f = Dlg.OpenFile(L.T("Scegli il programma o lo script"),
            L.T("Programmi e script|*.exe;*.bat;*.cmd;*.ps1;*.vbs|Tutti i file|*.*"), "schedprog");
        if (f != null) { _program.Text = f; if (_name.Text.Trim().Length == 0) _name.Text = Path.GetFileNameWithoutExtension(f); }
    }

    private void Create()
    {
        var w = (Scheduler.When)_when.SelectedIndex;
        if (w != Scheduler.When.AtLogon && !TimeOnly.TryParse(_time.Text.Trim(), out _))
        { Dlg.Info(L.T("Scrivi l'orario come HH:MM, per esempio 09:30.")); return; }

        var job = new Scheduler.Job(
            _name.Text.Trim(), _program.Text.Trim(), _args.Text.Trim(), w,
            _time.Text.Trim(), Days[_day.SelectedIndex].code);
        try
        {
            if (Scheduler.Exists(job.Name) && !Dlg.Confirm(L.T("Esiste già un'attività con questo nome. Sostituirla?"))) return;
            Scheduler.Create(job);
            MainWindow.Notify(L.T("Attività creata."));
            _name.Text = _args.Text = "";
            _program.Text = "";
            Render();
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private string Describe(Scheduler.Job j) => j.When switch
    {
        Scheduler.When.Daily => L.T($"ogni giorno alle {j.Time}"),
        Scheduler.When.Weekly => L.T($"ogni {Days.First(d => d.code == j.Day).label} alle {j.Time}"),
        Scheduler.When.AtLogon => L.T("a ogni accesso"),
        _ => L.T($"una volta alle {j.Time}"),
    };

    private void Render()
    {
        _list.Children.Clear();
        var jobs = Scheduler.Saved();
        if (jobs.Count == 0) { _list.Children.Add(Ui.Hint(L.T("Nessuna attività creata."))); return; }
        foreach (var j in jobs)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = j.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            text.Children.Add(Ui.Hint($"{Path.GetFileName(j.Program)} — {Describe(j)}"));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Ui.Btn(L.T("Esegui ora"), SymbolRegular.Play24, (_, _) => RunNow(j.Name)));
            actions.Children.Add(Ui.Btn(L.T("Elimina"), SymbolRegular.Delete24, (_, _) => Remove(j.Name)));

            var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
            dock.Children.Add(text);
            _list.Children.Add(new Border { Child = dock, Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), Background = Ui.Res("CardBackgroundFillColorDefaultBrush") });
        }
    }

    private void RunNow(string name)
    {
        try { Scheduler.RunNow(name); MainWindow.Notify(L.T("Attività avviata.")); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void Remove(string name)
    {
        if (!Dlg.Confirm(L.T($"Eliminare l'attività «{name}»?"))) return;
        Scheduler.Delete(name);
        Render();
    }
}
