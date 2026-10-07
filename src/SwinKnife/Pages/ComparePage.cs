using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using ListBox = System.Windows.Controls.ListBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Confronta due file (testi, codice, Word, PDF…) riga per riga, oppure due cartelle per sincronizzarle.</summary>
public sealed class ComparePage : UserControl, IToolPage
{
    public sealed record DiffRow(string LeftNo, string Left, Brush LeftBg, string RightNo, string Right, Brush RightBg, bool Changed);

    public sealed class FolderRow(CompareItem item)
    {
        public CompareItem Item { get; } = item;
        public string Path => Item.RelPath;
        public string Status => Item.Status switch
        {
            CompareStatus.Same => L.T("uguale"),
            CompareStatus.OnlyLeft => L.T("solo a sinistra"),
            CompareStatus.OnlyRight => L.T("solo a destra"),
            _ => Item.Newer switch { < 0 => L.T("diverso · più recente a sinistra"), > 0 => L.T("diverso · più recente a destra"), _ => L.T("diverso") },
        };
        public Brush StatusBrush => Item.Status switch
        {
            CompareStatus.Same => Ui.Res("TextFillColorTertiaryBrush"),
            CompareStatus.OnlyLeft => new SolidColorBrush(Color.FromRgb(0xE8, 0x80, 0x80)),
            CompareStatus.OnlyRight => new SolidColorBrush(Color.FromRgb(0x7C, 0xC8, 0x7C)),
            _ => new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x50)),
        };
        public string LeftInfo => Item.Left is { } f ? $"{Util.HumanSize(f.Length)} · {Util.Date(f.LastWriteTime)}" : "—";
        public string RightInfo => Item.Right is { } f ? $"{Util.HumanSize(f.Length)} · {Util.Date(f.LastWriteTime)}" : "—";
    }

    private static readonly Brush Removed = Frozen(Color.FromArgb(70, 0xE5, 0x48, 0x4D));
    private static readonly Brush Added = Frozen(Color.FromArgb(70, 0x4C, 0xAF, 0x50));
    private static readonly Brush Empty = Frozen(Color.FromArgb(25, 128, 128, 128));
    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private readonly RadioButton _filesMode, _foldersMode;
    private readonly TextBox _left = new() { PlaceholderText = L.T("Primo file o cartella (sinistra)") };
    private readonly TextBox _right = new() { PlaceholderText = L.T("Secondo file o cartella (destra)") };
    private readonly CheckBox _ignoreSpace = new() { Content = L.T("Ignora gli spazi"), Margin = new Thickness(12, 0, 0, 0) };
    private readonly CheckBox _content = new() { Content = L.T("Confronta anche il contenuto (più lento)"), Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ListBox _diff = new() { BorderThickness = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
    private readonly DataGrid _folders;
    private readonly ObservableCollection<FolderRow> _folderRows = new();
    private readonly CheckBox _hideSame = new() { Content = L.T("Nascondi i file uguali"), IsChecked = true };
    private readonly StackPanel _folderActions;
    private readonly StackPanel _fileActions;
    private readonly TextBlock _summary = Ui.Label("");
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private List<CompareItem> _items = new();
    private CancellationTokenSource? _cts;

    public ComparePage(MainWindow main)
    {
        _filesMode = Segment(L.T("File"), true);
        _foldersMode = Segment(L.T("Cartelle"), false);
        _filesMode.Checked += (_, _) => SwitchMode();
        _foldersMode.Checked += (_, _) => SwitchMode();

        // ---- diff dei file
        _diff.ItemTemplate = DiffTemplate();
        VirtualizingPanel.SetIsVirtualizing(_diff, true);
        VirtualizingPanel.SetVirtualizationMode(_diff, VirtualizationMode.Recycling);
        _diff.ItemContainerStyle = new Style(typeof(ListBoxItem))
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        };

        // ---- cartelle
        _folders = new DataGrid
        {
            ItemsSource = _folderRows, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None, SelectionMode = DataGridSelectionMode.Extended, Visibility = Visibility.Collapsed,
        };
        var status = new DataGridTemplateColumn { Header = L.T("Stato"), Width = new DataGridLength(220) };
        var st = new FrameworkElementFactory(typeof(TextBlock));
        st.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.Status)));
        st.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(FolderRow.StatusBrush)));
        status.CellTemplate = new DataTemplate { VisualTree = st };
        _folders.Columns.Add(status);
        _folders.Columns.Add(new DataGridTextColumn { Header = L.T("File"), Binding = new Binding(nameof(FolderRow.Path)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _folders.Columns.Add(new DataGridTextColumn { Header = L.T("Sinistra"), Binding = new Binding(nameof(FolderRow.LeftInfo)), Width = new DataGridLength(210) });
        _folders.Columns.Add(new DataGridTextColumn { Header = L.T("Destra"), Binding = new Binding(nameof(FolderRow.RightInfo)), Width = new DataGridLength(210) });
        _folders.MouseDoubleClick += (_, _) =>
        {
            // doppio clic su un file diverso: lo confronto riga per riga
            if (_folders.SelectedItem is FolderRow { Item: { Left: { } a, Right: { } b } })
            {
                _left.Text = a.FullName;
                _right.Text = b.FullName;
                _filesMode.IsChecked = true;
                _ = Run();
            }
        };
        _hideSame.Click += (_, _) => FillFolders();

        _fileActions = Ui.Row(
            Ui.Btn(L.T("Differenza precedente"), SymbolRegular.ArrowUp24, (_, _) => Jump(-1)),
            Ui.Btn(L.T("Differenza successiva"), SymbolRegular.ArrowDown24, (_, _) => Jump(1)));
        _folderActions = Ui.Row(_hideSame,
            Ui.Btn(L.T("Copia a destra →"), SymbolRegular.ArrowRight24, (_, _) => CopySelected(toRight: true)),
            Ui.Btn(L.T("← Copia a sinistra"), SymbolRegular.ArrowLeft24, (_, _) => CopySelected(toRight: false)),
            Ui.Btn(L.T("Rendi la destra uguale alla sinistra"), SymbolRegular.ArrowSync24, (_, _) => Mirror()));
        _folderActions.Visibility = Visibility.Collapsed;
        _hideSame.Margin = new Thickness(0, 0, 12, 0);

        Grid PathRow(TextBox box, bool left)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var b = Ui.Btn(L.T("Sfoglia…"), SymbolRegular.FolderOpen24, (_, _) =>
            {
                var p = _foldersMode.IsChecked == true ? Dlg.PickFolder(L.T("Scegli la cartella"), "compare.dir") : Dlg.OpenFile(L.T("Scegli il file"), key: "compare");
                if (p != null) box.Text = p;
            });
            b.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(b, 1);
            g.Children.Add(box);
            g.Children.Add(b);
            return g;
        }
        var go = Ui.Btn(L.T("Confronta"), SymbolRegular.BranchCompare24, async (_, _) => await Run(), primary: true);
        var swap = Ui.Btn(L.T("Scambia"), SymbolRegular.ArrowSwap24, (_, _) => (_left.Text, _right.Text) = (_right.Text, _left.Text));
        var top = new StackPanel();
        top.Children.Add(Ui.Row(_filesMode, _foldersMode));
        ((StackPanel)top.Children[0]).Margin = new Thickness(0, 0, 0, 10);
        top.Children.Add(PathRow(_left, true));
        top.Children.Add(PathRow(_right, false));
        top.Children.Add(Ui.Row(go, swap, _ignoreSpace, _content));
        var card = new Border { Style = (Style)Application.Current.FindResource("Panel"), Child = top, Margin = new Thickness(0, 0, 0, 10) };

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new Grid();
        actions.Children.Add(_fileActions);
        actions.Children.Add(_folderActions);
        DockPanel.SetDock(actions, Dock.Right);
        bottom.Children.Add(actions);
        bottom.Children.Add(_summary);
        var results = new Grid();
        results.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Panel"), Padding = new Thickness(4), Child = _diff });
        results.Children.Add(_folders);

        var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        var header = Ui.Header(L.T("Confronta"), L.T("Trova le differenze tra due file (testi, codice, Word, PDF…) o tra due cartelle, e sincronizzale."));
        foreach (var e in new UIElement[] { header, card }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_busy, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(_busy);
        dock.Children.Add(results);
        Content = dock;
    }

    private static RadioButton Segment(string text, bool on) => new()
    {
        Content = text, IsChecked = on, GroupName = "cmpmode", Style = (Style)Application.Current.FindResource("SegmentButton"),
    };

    private void SwitchMode()
    {
        var folders = _foldersMode.IsChecked == true;
        _left.PlaceholderText = folders ? L.T("Prima cartella (sinistra)") : L.T("Primo file (sinistra)");
        _right.PlaceholderText = folders ? L.T("Seconda cartella (destra)") : L.T("Secondo file (destra)");
        _content.Visibility = folders ? Visibility.Visible : Visibility.Collapsed;
        _ignoreSpace.Visibility = folders ? Visibility.Collapsed : Visibility.Visible;
        _folders.Visibility = _folderActions.Visibility = folders ? Visibility.Visible : Visibility.Collapsed;
        _fileActions.Visibility = folders ? Visibility.Collapsed : Visibility.Visible;
        ((FrameworkElement)((Border)_diff.Parent).Parent).Visibility = Visibility.Visible;
        ((Border)_diff.Parent).Visibility = folders ? Visibility.Collapsed : Visibility.Visible;
        _summary.Text = "";
    }

    public bool Accepts(string path) => true;
    public void OpenFile(string path) => AddFiles([path]);

    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count >= 2)
        {
            _left.Text = paths[0];
            _right.Text = paths[1];
        }
        else if (paths.Count == 1)
        {
            if (_left.Text.Length == 0 || _right.Text.Length > 0) { _left.Text = paths[0]; _right.Text = ""; }
            else _right.Text = paths[0];
        }
        var dirs = paths.Count > 0 && Directory.Exists(paths[0]);
        (dirs ? _foldersMode : _filesMode).IsChecked = true;
        if (_left.Text.Length > 0 && _right.Text.Length > 0) _ = Run();
    }

    private async Task Run()
    {
        var a = _left.Text.Trim().Trim('"');
        var b = _right.Text.Trim().Trim('"');
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _busy.Visibility = Visibility.Visible;
        try
        {
            if (_foldersMode.IsChecked == true)
            {
                if (!Directory.Exists(a) || !Directory.Exists(b)) { Dlg.Error(L.T("Scegli due cartelle esistenti.")); return; }
                var content = _content.IsChecked == true;
                _items = await Task.Run(() => FolderCompare.Compare(a, b, content, m => Dispatcher.InvokeAsync(() => _summary.Text = m), cts.Token), cts.Token);
                FillFolders();
            }
            else
            {
                if (!File.Exists(a) || !File.Exists(b)) { Dlg.Error(L.T("Scegli due file esistenti.")); return; }
                await CompareFiles(a, b, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Confronto non riuscito:\n") + ex.Message);
        }
        finally
        {
            if (cts == _cts) _busy.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ file
    private async Task CompareFiles(string a, string b, CancellationToken ct)
    {
        var ignore = _ignoreSpace.IsChecked == true;
        var (rows, text) = await Task.Run(() =>
        {
            var ta = FileText.Extract(a, 5_000_000);
            var tb = FileText.Extract(b, 5_000_000);
            if (ta.Length == 0 && tb.Length == 0)
            {
                // file binari (immagini, programmi…): dico solo se sono identici
                var same = FolderCompare.SameContent(a, b, ct);
                return (new List<DiffRow>(), same ? L.T("I due file sono identici byte per byte.") : L.T("I file sono diversi (non contengono testo da confrontare riga per riga)."));
            }
            var model = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(ta, tb, ignore);
            var list = new List<DiffRow>(model.OldText.Lines.Count);
            int added = 0, removed = 0, changed = 0;
            for (var i = 0; i < model.OldText.Lines.Count; i++)
            {
                var l = model.OldText.Lines[i];
                var r = model.NewText.Lines[i];
                var lBg = l.Type switch { ChangeType.Deleted or ChangeType.Modified => Removed, ChangeType.Imaginary => Empty, _ => Brushes.Transparent };
                var rBg = r.Type switch { ChangeType.Inserted or ChangeType.Modified => Added, ChangeType.Imaginary => Empty, _ => Brushes.Transparent };
                if (l.Type == ChangeType.Modified) changed++;
                else if (l.Type == ChangeType.Deleted) removed++;
                else if (r.Type == ChangeType.Inserted) added++;
                list.Add(new DiffRow(l.Position?.ToString() ?? "", l.Text ?? "", lBg, r.Position?.ToString() ?? "", r.Text ?? "", rBg,
                    l.Type != ChangeType.Unchanged || r.Type != ChangeType.Unchanged));
            }
            var summary = added + removed + changed == 0
                ? L.T("Nessuna differenza nel testo.")
                : L.T($"{changed} righe modificate · {removed} rimosse · {added} aggiunte");
            return (list, summary);
        }, ct);
        _diff.ItemsSource = rows;
        _summary.Text = text;
        if (rows.Count > 0) Jump(1, fromStart: true);
    }

    private DataTemplate DiffTemplate()
    {
        FrameworkElementFactory Cell(string textPath, string bgPath, int col, bool number)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding(bgPath));
            border.SetValue(Grid.ColumnProperty, col);
            var t = new FrameworkElementFactory(typeof(TextBlock));
            t.SetBinding(TextBlock.TextProperty, new Binding(textPath));
            t.SetValue(TextBlock.PaddingProperty, new Thickness(number ? 4 : 8, 1, 6, 1));
            if (number)
            {
                t.SetValue(TextBlock.ForegroundProperty, Ui.Res("TextFillColorTertiaryBrush"));
                t.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            }
            else t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            border.AppendChild(t);
            return border;
        }
        var grid = new FrameworkElementFactory(typeof(Grid));
        foreach (var w in new[] { new GridLength(48), new GridLength(1, GridUnitType.Star), new GridLength(48), new GridLength(1, GridUnitType.Star) })
        {
            var cd = new FrameworkElementFactory(typeof(ColumnDefinition));
            cd.SetValue(ColumnDefinition.WidthProperty, w);
            grid.AppendChild(cd);
        }
        grid.AppendChild(Cell(nameof(DiffRow.LeftNo), nameof(DiffRow.LeftBg), 0, true));
        grid.AppendChild(Cell(nameof(DiffRow.Left), nameof(DiffRow.LeftBg), 1, false));
        grid.AppendChild(Cell(nameof(DiffRow.RightNo), nameof(DiffRow.RightBg), 2, true));
        grid.AppendChild(Cell(nameof(DiffRow.Right), nameof(DiffRow.RightBg), 3, false));
        return new DataTemplate { VisualTree = grid };
    }

    private void Jump(int dir, bool fromStart = false)
    {
        if (_diff.ItemsSource is not List<DiffRow> rows || rows.Count == 0) return;
        var i = fromStart ? -1 : Math.Max(-1, _diff.SelectedIndex);
        // salto il blocco di differenze in cui mi trovo
        while (!fromStart && i >= 0 && i < rows.Count && rows[i].Changed) i += dir;
        for (i += fromStart ? 1 : 0; i >= 0 && i < rows.Count; i += dir)
        {
            if (!rows[i].Changed) continue;
            _diff.SelectedIndex = i;
            _diff.ScrollIntoView(rows[Math.Min(rows.Count - 1, i + 8)]);
            _diff.ScrollIntoView(rows[Math.Max(0, i - 3)]);
            return;
        }
    }

    // ------------------------------------------------------------------ cartelle
    private void FillFolders()
    {
        _folderRows.Clear();
        foreach (var it in _items.Where(i => _hideSame.IsChecked != true || i.Status != CompareStatus.Same)) _folderRows.Add(new FolderRow(it));
        int same = _items.Count(i => i.Status == CompareStatus.Same), diff = _items.Count(i => i.Status == CompareStatus.Different),
            onlyL = _items.Count(i => i.Status == CompareStatus.OnlyLeft), onlyR = _items.Count(i => i.Status == CompareStatus.OnlyRight);
        _summary.Text = L.T($"{_items.Count} file · {same} uguali · {diff} diversi · {onlyL} solo a sinistra · {onlyR} solo a destra");
    }

    private void CopySelected(bool toRight)
    {
        var sel = _folders.SelectedItems.OfType<FolderRow>().Select(r => r.Item).ToList();
        if (sel.Count == 0) sel = _items.Where(i => i.Status != CompareStatus.Same).ToList();
        var copy = sel.Where(i => toRight ? i.Left != null : i.Right != null).ToList();
        if (copy.Count == 0) return;
        var overwrite = copy.Count(i => toRight ? i.Right != null : i.Left != null);
        var msg = L.T($"Copiare {copy.Count} file verso {(toRight ? L.T("destra") : L.T("sinistra"))}?");
        if (overwrite > 0) msg += "\n\n" + L.T($"{overwrite} file esistenti verranno sostituiti.");
        if (!Dlg.Confirm(msg)) return;
        string from = toRight ? _left.Text : _right.Text, to = toRight ? _right.Text : _left.Text;
        var errors = 0;
        foreach (var i in copy)
        {
            try { FolderCompare.Copy(i, from, to); }
            catch (Exception ex) { errors++; AppInfo.Log(ex, "Copia confronto " + i.RelPath); }
        }
        MainWindow.Notify(errors == 0 ? L.T($"{copy.Count} file copiati") : L.T($"{copy.Count - errors} file copiati, {errors} errori (vedi registro)"));
        _ = Run();
    }

    private void Mirror()
    {
        var toCopy = _items.Where(i => i.Status is CompareStatus.OnlyLeft or CompareStatus.Different).ToList();
        var extra = _items.Where(i => i.Status == CompareStatus.OnlyRight).ToList();
        if (toCopy.Count + extra.Count == 0)
        {
            Dlg.Info(L.T("Le due cartelle sono già uguali."));
            return;
        }
        if (!Dlg.Confirm(L.T($"La cartella di destra diventerà uguale a quella di sinistra:\n\n• {toCopy.Count} file copiati o sostituiti\n• {extra.Count} file in più a destra spostati nel Cestino\n\nContinuare?"))) return;
        foreach (var i in toCopy)
            try { FolderCompare.Copy(i, _left.Text, _right.Text); }
            catch (Exception ex) { AppInfo.Log(ex, "Sincronizza " + i.RelPath); }
        if (extra.Count > 0)
            try { DiskScan.SendToRecycleBin(extra.Select(i => Path.Combine(_right.Text, i.RelPath))); }
            catch (Exception ex) { Dlg.Error(ex.Message); }
        MainWindow.Notify(L.T("Cartelle sincronizzate"));
        _ = Run();
    }
}
