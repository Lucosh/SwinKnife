using System.Collections.ObjectModel;
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
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Rinomina tanti file insieme con anteprima: modello con data di scatto e numerazione, cerca e sostituisci.</summary>
public sealed class RenamePage : UserControl, IToolPage
{
    private readonly ObservableCollection<RenameItem> _items = new();
    private readonly RenameRules _rules = new();
    private readonly DataGrid _grid;
    private readonly TextBox _template = new() { Text = BulkRename.Tok("name") };
    private readonly TextBox _find = new() { PlaceholderText = L.T("Testo da cercare") };
    private readonly TextBox _replace = new() { PlaceholderText = L.T("Sostituisci con (vuoto = elimina)") };
    private readonly CheckBox _regex = new() { Content = L.T("Espressione regolare") };
    private readonly CheckBox _matchCase = new() { Content = L.T("Maiuscole/minuscole esatte") };
    private readonly ComboBox _case = new(), _dateFormat = new(), _ext = new(), _order = new();
    private readonly TextBox _start = new() { Text = "1", Width = 74 }, _step = new() { Text = "1", Width = 64 }, _digits = new() { Text = "3", Width = 56 };
    private readonly TextBox _newExt = new() { PlaceholderText = "es. jpg", Width = 110, Visibility = Visibility.Collapsed };
    private readonly CheckBox _spaces = new() { Content = L.T("Sostituisci gli spazi con _") };
    private readonly CheckBox _accents = new() { Content = L.T("Togli gli accenti (è → e)") };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Wpf.Ui.Controls.Button _apply, _undo;
    private readonly FrameworkElement _empty;
    private List<(string from, string to)> _lastRename = new();
    private CancellationTokenSource? _datesCts;
    private bool _building = true;

    public RenamePage(MainWindow main)
    {
        // ---- regole
        foreach (var s in new[] { L.T("Invariate"), L.T("tutto minuscolo"), L.T("TUTTO MAIUSCOLO"), L.T("Iniziali Maiuscole") }) _case.Items.Add(s);
        foreach (var (label, fmt) in new[] { ("2026-10-06", "yyyy-MM-dd"), ("20261006", "yyyyMMdd"), ("06-10-2026", "dd-MM-yyyy"), ("2026-10", "yyyy-MM"), ("2026", "yyyy"), ("2026-10-06 21.30.15", "yyyy-MM-dd HH.mm.ss"), ("20261006_213015", "yyyyMMdd_HHmmss") })
            _dateFormat.Items.Add(new ComboBoxItem { Content = label, Tag = fmt });
        foreach (var s in new[] { L.T("Invariata"), "minuscola", "MAIUSCOLA", L.T("Cambia in…") }) _ext.Items.Add(s);
        foreach (var s in new[] { L.T("Ordine dell'elenco"), L.T("Nome attuale"), L.T("Data (scatto o modifica)") }) _order.Items.Add(s);
        _case.SelectedIndex = _ext.SelectedIndex = 0;
        _dateFormat.SelectedIndex = 0;
        _order.SelectedIndex = 2;

        var tokens = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (token, tip) in new[]
                 {
                     (BulkRename.Tok("name"), L.T("Nome attuale (senza estensione)")), (BulkRename.Tok("num"), L.T("Numero progressivo")), (BulkRename.Tok("date"), L.T("Data di scatto della foto o di modifica del file")),
                     (BulkRename.Tok("time"), L.T("Ora di scatto o di modifica")), (BulkRename.Tok("folder"), L.T("Nome della cartella che contiene il file")),
                 })
        {
            var b = Ui.Btn(token, null, (_, _) => InsertToken(token), tip);
            b.Padding = new Thickness(8, 3, 8, 3);
            b.Margin = new Thickness(0, 0, 4, 4);
            tokens.Children.Add(b);
        }
        var presets = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (label, tpl) in new[]
                 {
                     (L.T("Foto per data"), $"{BulkRename.Tok("date")} {BulkRename.Tok("time")}"),
                     (L.T("Vacanza 001"), $"{L.T("Vacanza")} {BulkRename.Tok("num")}"),
                     (L.T("Data + nome"), $"{BulkRename.Tok("date")} {BulkRename.Tok("name")}"),
                     (L.T("Cartella 001"), $"{BulkRename.Tok("folder")} {BulkRename.Tok("num")}"),
                 })
        {
            var b = Ui.Btn(label, null, (_, _) => _template.Text = tpl, L.T($"Usa il modello «{tpl}»"));
            b.Padding = new Thickness(8, 3, 8, 3);
            b.Margin = new Thickness(0, 0, 4, 4);
            presets.Children.Add(b);
        }

        var opts = new StackPanel();
        void Section(string title, params UIElement[] content)
        {
            opts.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("SectionTitle"), Margin = new Thickness(0, opts.Children.Count == 0 ? 0 : 16, 0, 6) });
            foreach (var c in content) opts.Children.Add(c);
        }
        Section(L.T("Nuovo nome"), _template, tokens, Ui.Hint(L.T("Esempi pronti:")), presets);
        Section(L.T("Numerazione"), Ui.Row(Ui.Label(L.T("Parti da")), Gap(), _start, Gap(14), Ui.Label(L.T("passo")), Gap(), _step, Gap(14), Ui.Label(L.T("cifre")), Gap(), _digits),
            Spaced(Ui.Row(Ui.Label(L.T("Ordina per")), Gap(), _order)));
        Section(L.T("Data"), Ui.Row(Ui.Label(L.T("Formato")), Gap(), _dateFormat),
            Spaced(Ui.Hint(L.T("Per le foto si usa la data di scatto, per gli altri file la data di modifica."))));
        Section(L.T("Cerca e sostituisci"), _find, Spaced(_replace), Spaced(_regex), Spaced(_matchCase));
        Section(L.T("Altre opzioni"), Ui.Row(Ui.Label(L.T("Lettere")), Gap(), _case), Spaced(Ui.Row(Ui.Label(L.T("Estensione")), Gap(), _ext, Gap(), _newExt)),
            Spaced(_spaces), Spaced(_accents));
        var optsBorder = new Border
        {
            Style = (Style)Application.Current.FindResource("Panel"), Margin = new Thickness(0, 0, 12, 0),
            Child = new ScrollViewer { Content = opts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) },
        };

        foreach (var t in new[] { _template, _find, _replace, _start, _step, _digits, _newExt }) t.TextChanged += (_, _) => Update();
        foreach (var c in new[] { _regex, _matchCase, _spaces, _accents }) c.Click += (_, _) => Update();
        foreach (var c in new[] { _case, _dateFormat, _ext, _order }) c.SelectionChanged += (_, _) => Update();

        // ---- elenco
        _grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None,
            ItemsSource = _items, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Nome attuale"), Binding = new Binding(nameof(RenameItem.OldName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var newCol = new DataGridTextColumn { Header = L.T("Nuovo nome"), Binding = new Binding(nameof(RenameItem.NewName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) };
        var bold = new Style(typeof(TextBlock));
        bold.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        newCol.ElementStyle = bold;
        _grid.Columns.Add(newCol);
        var statusStyle = new Style(typeof(TextBlock));
        statusStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Ui.Res("TextFillColorSecondaryBrush")));
        var problem = new DataTrigger { Binding = new Binding(nameof(RenameItem.Problem)), Value = true };
        problem.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x3C))));
        statusStyle.Triggers.Add(problem);
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Stato"), Binding = new Binding(nameof(RenameItem.Status)), Width = 200, ElementStyle = statusStyle });

        var listBar = Ui.Row(
            Ui.Btn(L.T("Aggiungi file…"), SymbolRegular.DocumentAdd24, (_, _) => AddFiles(Dlg.OpenFiles(L.T("File da rinominare"), Dlg.AllFiles, "rename"))),
            Ui.Btn(L.T("Aggiungi cartella…"), SymbolRegular.FolderAdd24, (_, _) =>
            {
                var d = Dlg.PickFolder(L.T("Cartella con i file da rinominare"), "rename");
                if (d != null) AddFiles([d]);
            }),
            Ui.Btn(L.T("Rimuovi dall'elenco"), SymbolRegular.Dismiss24, (_, _) =>
            {
                foreach (var it in _grid.SelectedItems.Cast<RenameItem>().ToList()) _items.Remove(it);
                Update();
            }),
            Ui.Btn(L.T("Svuota"), SymbolRegular.Delete24, (_, _) =>
            {
                _items.Clear();
                Update();
            }));
        listBar.Margin = new Thickness(0, 0, 0, 8);

        _apply = Ui.Btn(L.T("Rinomina"), SymbolRegular.Rename24, (_, _) => Apply(), primary: true);
        _undo = Ui.Btn(L.T("Annulla l'ultima rinomina"), SymbolRegular.ArrowUndo24, (_, _) => Undo());
        _undo.IsEnabled = false;
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = Ui.Row(_undo, _apply);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);

        _empty = Ui.Placeholder(SymbolRegular.Rename24, L.T("Trascina qui i file o una cartella, oppure usa «Aggiungi file».\nVedrai l'anteprima dei nuovi nomi prima di rinominare."), out _);
        var listArea = new Grid();
        listArea.Children.Add(_grid);
        listArea.Children.Add(_empty);

        var right = new DockPanel();
        DockPanel.SetDock(listBar, Dock.Top);
        right.Children.Add(listBar);
        DockPanel.SetDock(bottom, Dock.Bottom);
        right.Children.Add(bottom);
        right.Children.Add(listArea);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(450) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(optsBorder);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 18) };
        var head = Ui.Header(L.T("Rinomina in blocco"), L.T("Dai un nome ordinato a foto e documenti: data di scatto, numerazione, cerca e sostituisci. Controlla l'anteprima e poi rinomina."));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(body);
        Content = root;

        _building = false;
        Update();
    }

    private static FrameworkElement Gap(double w = 8) => new Border { Width = w };

    private static UIElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 8, 0, 0);
        return e;
    }

    private void InsertToken(string token)
    {
        var pos = _template.CaretIndex;
        _template.Text = _template.Text.Insert(Math.Min(pos, _template.Text.Length), token);
        _template.CaretIndex = pos + token.Length;
        _template.Focus();
    }

    // ------------------------------------------------------------------ file
    public bool Accepts(string path) => true;

    public void OpenFile(string path) => AddFiles([path]);

    public void AddFiles(IReadOnlyList<string> paths)
    {
        var known = _items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<RenameItem>();
        foreach (var p in paths)
        {
            IEnumerable<string> files = Directory.Exists(p)
                ? Directory.EnumerateFiles(p).Where(f => (File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) == 0).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                : File.Exists(p) ? [p] : [];
            foreach (var f in files)
            {
                if (!known.Add(f)) continue;
                var item = new RenameItem { Path = Path.GetFullPath(f), Modified = File.GetLastWriteTime(f) };
                _items.Add(item);
                added.Add(item);
            }
        }
        Update();
        if (added.Count > 0) _ = LoadDates(added);
    }

    /// <summary>Legge in background le date di scatto delle foto e aggiorna l'anteprima.</summary>
    private async Task LoadDates(List<RenameItem> items)
    {
        _datesCts?.Cancel();
        _datesCts = new CancellationTokenSource();
        var ct = _datesCts.Token;
        var pending = _items.Where(i => i.Taken == null).ToList();
        _summary.Text = L.T("Lettura delle date di scatto…");
        await Task.Run(() =>
        {
            Parallel.ForEach(pending, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, i => i.Taken = BulkRename.DateTaken(i.Path));
        }, ct).ContinueWith(_ => { });
        if (!ct.IsCancellationRequested) Update();
    }

    // ------------------------------------------------------------------ anteprima
    private static int Int(TextBox t, int fallback, int min, int max) =>
        int.TryParse(t.Text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void Update()
    {
        if (_building) return;
        _newExt.Visibility = _ext.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        _rules.Template = _template.Text;
        _rules.Find = _find.Text;
        _rules.Replace = _replace.Text;
        _rules.Regex = _regex.IsChecked == true;
        _rules.MatchCase = _matchCase.IsChecked == true;
        _rules.Case = (CaseMode)_case.SelectedIndex;
        _rules.Ext = (ExtMode)_ext.SelectedIndex;
        _rules.NewExt = _newExt.Text;
        _rules.Start = Int(_start, 1, -1_000_000, 1_000_000_000);
        _rules.Step = Int(_step, 1, -1000, 1000);
        _rules.Digits = Int(_digits, 3, 1, 9);
        _rules.DateFormat = (string)((ComboBoxItem)_dateFormat.SelectedItem).Tag;
        _rules.Order = (RenameOrder)_order.SelectedIndex;
        _rules.SpacesToUnderscore = _spaces.IsChecked == true;
        _rules.RemoveAccents = _accents.IsChecked == true;
        var error = BulkRename.Preview(_items, _rules);
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var changed = _items.Count(i => i.Changed && !i.Problem);
        var problems = _items.Count(i => i.Problem);
        _summary.Text = error ?? (_items.Count == 0 ? "" :
            L.T($"{_items.Count} file · {changed} da rinominare") + (problems > 0 ? L.T($" · {problems} con problemi (verranno saltati)") : ""));
        _summary.Foreground = error != null || problems > 0 ? new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x3C)) : Ui.Res("TextFillColorSecondaryBrush");
        _apply.IsEnabled = error == null && changed > 0;
    }

    // ------------------------------------------------------------------ rinomina
    private void Apply()
    {
        var todo = _items.Where(i => i.Changed && !i.Problem).ToList();
        if (todo.Count == 0) return;
        if (!Dlg.Confirm(L.T($"Rinominare {todo.Count} file?"), L.T("Rinomina in blocco"), L.T("Rinomina"), L.T("Annulla"))) return;
        var done = BulkRename.Apply(todo.Select(i => (i.Path, Path.Combine(i.Folder, i.NewName))), out var errors);
        var map = done.ToDictionary(d => d.from, d => d.to, StringComparer.OrdinalIgnoreCase);
        foreach (var it in _items)
            if (map.TryGetValue(it.Path, out var to))
            {
                it.Path = to;
                it.Refresh();
            }
        _lastRename = done;
        _undo.IsEnabled = done.Count > 0;
        Update();
        if (errors.Count > 0) Dlg.Error(L.T($"{errors.Count} file non sono stati rinominati:\n\n") + string.Join("\n", errors.Take(15)));
        MainWindow.Notify(L.T($"{done.Count} file rinominati"));
    }

    private void Undo()
    {
        if (_lastRename.Count == 0) return;
        var done = BulkRename.Apply(_lastRename.Select(d => (d.to, d.from)), out var errors);
        var map = done.ToDictionary(d => d.from, d => d.to, StringComparer.OrdinalIgnoreCase);
        foreach (var it in _items)
            if (map.TryGetValue(it.Path, out var to))
            {
                it.Path = to;
                it.Refresh();
            }
        _lastRename.Clear();
        _undo.IsEnabled = false;
        Update();
        if (errors.Count > 0) Dlg.Error(L.T("Alcuni file non sono stati riportati al nome originale:\n\n") + string.Join("\n", errors.Take(15)));
        MainWindow.Notify(L.T($"Ripristinati i nomi di {done.Count} file"));
    }

    public void Shutdown() => _datesCts?.Cancel();
}
