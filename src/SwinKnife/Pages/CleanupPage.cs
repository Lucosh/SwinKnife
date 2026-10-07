using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Pulizia del PC: file temporanei, cache, Cestino, avanzi di Windows Update.</summary>
public sealed class CleanupPage : UserControl, IToolPage
{
    private sealed class Row
    {
        public required CleanTarget Target { get; init; }
        public required CheckBox Check { get; init; }
        public required TextBlock SizeText { get; init; }
        public required Wpf.Ui.Controls.Button ShowBtn { get; init; }
        public bool Available { get; init; }
        public bool Measured { get; set; }
    }

    private readonly MainWindow _main;
    private readonly List<Row> _rows = new();
    private readonly TextBlock _total = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Wpf.Ui.Controls.Button _analyze, _clean, _stop;
    private readonly ProgressBar _progress = new() { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private CancellationTokenSource? _cts;
    private bool _analyzed;

    public CleanupPage(MainWindow main)
    {
        _main = main;
        _status.Style = (Style)Application.Current.FindResource("Hint");
        var admin = RawSource.IsAdmin();

        _analyze = Ui.Btn(L.T("Analizza di nuovo"), SymbolRegular.ArrowClockwise24, (_, _) => _ = Analyze());
        _clean = Ui.Btn(L.T("Pulisci"), SymbolRegular.Broom24, (_, _) => _ = Clean(), primary: true);
        _stop = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stop.Visibility = Visibility.Collapsed;

        var summary = new StackPanel();
        summary.Children.Add(_total);
        summary.Children.Add(_status);
        var buttons = Ui.Row(_clean, _analyze, _stop);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        summary.Children.Add(buttons);
        summary.Children.Add(_progress);

        var list = new StackPanel();
        foreach (var t in Cleanup.Targets())
        {
            var available = !t.NeedsAdmin || admin;
            var check = new CheckBox
            {
                Content = new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") },
                IsChecked = available && t.DefaultOn, IsEnabled = available, VerticalAlignment = VerticalAlignment.Center,
            };
            check.Click += (_, _) => UpdateTotal();
            var size = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), MinWidth = 90, TextAlignment = TextAlignment.Right };
            var row = new Row
            {
                Target = t, Check = check, SizeText = size, Available = available,
                ShowBtn = Ui.IconBtn(SymbolRegular.List24, L.T("Mostra i file")),
            };
            row.ShowBtn.Click += (_, _) => ShowFiles(row);
            row.ShowBtn.Visibility = Visibility.Hidden;
            _rows.Add(row);

            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(check);
            text.Children.Add(new TextBlock
            {
                Text = t.Description + (available ? "" : L.T(" (richiede di avviare SwinKnife come amministratore)")),
                Style = (Style)Application.Current.FindResource("Hint"), Margin = new Thickness(28, 0, 0, 0),
            });
            g.Children.Add(text);
            Grid.SetColumn(size, 1);
            g.Children.Add(size);
            Grid.SetColumn(row.ShowBtn, 2);
            g.Children.Add(row.ShowBtn);
            list.Children.Add(g);
        }

        var tools = new WrapPanel();
        foreach (var (label, icon, action) in new (string, SymbolRegular, Action)[]
                 {
                     (L.T("Trova i file più grandi"), SymbolRegular.DataPie24, () => _main.ShowPage("disk")),
                     (L.T("Trova i duplicati"), SymbolRegular.DocumentCopy24, () => _main.ShowPage("dupes")),
                     (L.T("Disinstalla programmi"), SymbolRegular.Apps24, () => Launch("ms-settings:appsfeatures")),
                     (L.T("Sensore memoria di Windows"), SymbolRegular.Storage24, () => Launch("ms-settings:storagesense")),
                     (L.T("Pulizia disco di Windows"), SymbolRegular.Broom24, () => Launch("cleanmgr.exe")),
                 })
        {
            var b = Ui.Btn(label, icon, (_, _) => action());
            b.Margin = new Thickness(0, 0, 6, 6);
            tools.Children.Add(b);
        }

        var children = new List<UIElement> { Ui.Header(L.T("Pulizia PC"), L.T("Libera spazio eliminando file temporanei e cache che Windows e i programmi possono ricreare.")) };
        if (!admin)
        {
            var banner = new Border { Style = (Style)Application.Current.FindResource("InfoBanner"), Margin = new Thickness(0, 0, 0, 14) };
            var bp = new DockPanel();
            var elevate = Ui.Btn(L.T("Riavvia come amministratore"), SymbolRegular.ShieldKeyhole24, (_, _) =>
            {
                if (!_main.CanCloseAllPages()) return;
                if (RawSource.RelaunchAsAdmin("--page clean --elevated")) App.Quit();
            });
            DockPanel.SetDock(elevate, Dock.Right);
            bp.Children.Add(elevate);
            bp.Children.Add(Ui.Label(L.T("Alcune voci (file di Windows Update, temporanei di sistema) si possono pulire solo con i privilegi di amministratore.")));
            banner.Child = bp;
            children.Add(banner);
        }
        children.Add(Ui.Card(L.T("Spazio recuperabile"), null, summary));
        children.Add(Ui.Card(L.T("Cosa pulire"), null, list));
        children.Add(Ui.Card(L.T("Altri modi per liberare spazio"), null, tools));
        Content = Ui.ScrollPage(children.ToArray());

        Loaded += (_, _) =>
        {
            if (!_analyzed) _ = Analyze();
        };
    }

    private static void Launch(string cmd)
    {
        try { Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true }); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void SetBusy(bool busy)
    {
        _analyze.IsEnabled = _clean.IsEnabled = !busy;
        _stop.Visibility = _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in _rows) r.Check.IsEnabled = !busy && r.Available;
    }

    private void UpdateTotal()
    {
        var sel = _rows.Where(r => r.Check.IsChecked == true && r.Measured).ToList();
        var total = sel.Sum(r => r.Target.Size);
        _total.Text = _analyzed ? L.T($"{Util.HumanSize(total)} da liberare") : L.T("Analisi in corso…");
        _clean.IsEnabled = _cts == null && total > 0;
    }

    private async Task Analyze()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _analyzed = false;
        SetBusy(true);
        _status.Text = L.T("Calcolo dello spazio occupato…");
        foreach (var r in _rows)
        {
            r.Measured = false;
            r.SizeText.Text = r.Available ? "…" : "—";
            r.ShowBtn.Visibility = Visibility.Hidden;
        }
        UpdateTotal();
        try
        {
            foreach (var r in _rows.Where(r => r.Available))
            {
                _status.Text = L.T($"Analisi: {r.Target.Title}…");
                await Task.Run(() => Cleanup.Measure(r.Target, ct), ct);
                r.Measured = true;
                r.SizeText.Text = r.Target.Count == 0 ? L.T("vuoto") : Util.HumanSize(r.Target.Size);
                r.SizeText.Foreground = Ui.Res(r.Target.Size > 500 << 20 ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
                r.ShowBtn.Visibility = r.Target.Count > 0 && !r.Target.IsRecycleBin ? Visibility.Visible : Visibility.Hidden;
            }
            _analyzed = true;
            var all = _rows.Where(r => r.Measured).Sum(r => r.Target.Size);
            _status.Text = L.T($"In totale ci sono {Util.HumanSize(all)} di file superflui. Scegli cosa eliminare e premi Pulisci.");
        }
        catch (OperationCanceledException)
        {
            _analyzed = true;
            _status.Text = L.T("Analisi interrotta.");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateTotal();
        }
    }

    private async Task Clean()
    {
        var sel = _rows.Where(r => r.Check.IsChecked == true && r.Measured && r.Target.Size > 0).ToList();
        if (sel.Count == 0) return;
        var total = sel.Sum(r => r.Target.Size);
        if (!Dlg.Confirm(L.T($"Eliminare definitivamente {Util.HumanSize(total)} di file?\n\n") + string.Join("\n", sel.Select(r => $"• {r.Target.Title} ({Util.HumanSize(r.Target.Size)})")) +
                         L.T("\n\nI file in uso verranno saltati."), L.T("Pulizia PC"), L.T("Elimina"), L.T("Annulla")))
            return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true);
        long freed = 0;
        var skipped = 0;
        try
        {
            foreach (var r in sel)
            {
                _status.Text = L.T($"Pulizia: {r.Target.Title}…");
                var (f, s) = await Task.Run(() => Cleanup.Clean(r.Target, m => Dispatcher.InvokeAsync(() => _status.Text = m), ct), ct);
                freed += f;
                skipped += s;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Pulizia non riuscita:\n") + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
        var msg = L.T($"Liberati {Util.HumanSize(freed)}.") + (skipped > 0 ? L.T($" {skipped} file erano in uso e sono stati lasciati: chiudi i programmi aperti e riprova per liberare altro spazio.") : "");
        MainWindow.Notify(L.T($"Pulizia completata: liberati {Util.HumanSize(freed)}"), 10);
        await Analyze();
        _status.Text = msg;
    }

    private void ShowFiles(Row row)
    {
        var t = row.Target;
        var lb = new ListBox { Height = 380, FontSize = 12 };
        foreach (var f in t.Files.Take(2000))
        {
            long len = 0;
            try { len = new FileInfo(f).Length; }
            catch { }
            lb.Items.Add($"{Util.HumanSize(len),10}   {f}");
        }
        var panel = new DockPanel();
        var hint = Ui.Hint(t.Files.Count > 2000 ? L.T($"Primi 2000 file su {Util.Number(t.Files.Count)}.") : L.T($"{Util.Number(t.Files.Count)} file, {Util.HumanSize(t.Size)}."));
        DockPanel.SetDock(hint, Dock.Bottom);
        hint.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(hint);
        panel.Children.Add(lb);
        var dlg = new FormDialog(t.Title, panel, 820);
        var folder = t.Roots().FirstOrDefault();
        if (folder != null) dlg.AddButton(L.T("Apri cartella"), 1);
        dlg.AddButton(L.T("Chiudi"), 0, true, true);
        if (dlg.Run() == 1 && folder != null) Util.OpenFolder(folder);
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("La pulizia è in corso: interromperla?"));

    public void Shutdown() => _cts?.Cancel();
}
