using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;
using DataGrid = System.Windows.Controls.DataGrid;

namespace SwinKnife.Pages;

/// <summary>Trova file identici e foto simili e aiuta a scegliere quali eliminare.</summary>
public sealed class DuplicatesPage : UserControl, IToolPage
{
    private readonly MainWindow _main;
    private readonly ListBox _folders = new() { MinHeight = 44, MaxHeight = 110 };
    private readonly RadioButton _modeSame = new() { Content = L.T("File identici (stesso contenuto)"), IsChecked = true, GroupName = "dupmode", Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton _modePhoto = new() { Content = L.T("Foto simili (anche ridimensionate o ritoccate)"), GroupName = "dupmode" };
    private readonly ComboBox _minSize = new() { Width = 150 };
    private readonly ComboBox _similarity = new() { Width = 230 };
    private readonly TextBlock _optLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Width = 150 };
    private readonly Wpf.Ui.Controls.Button _go, _stop, _trash;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DataGrid _grid;
    private readonly DataGridTextColumn _resColumn;
    private readonly ComboBox _keepRule = new() { Width = 220 };
    private readonly TextBlock _selection = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 320 };
    private readonly Image _previewIcon = new() { Width = 48, Height = 48 };
    private readonly TextBlock _previewInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel _strip = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _previewButtons;
    private readonly Dictionary<string, BitmapSource?> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly FrameworkElement _empty;
    private List<List<DupFile>> _groups = new();
    private bool _photoResults;
    private CancellationTokenSource? _cts;

    public DuplicatesPage(MainWindow main)
    {
        _main = main;
        foreach (var (label, value) in new[] { (L.T("qualsiasi"), 1L), (L.T("almeno 100 KB"), 100L << 10), (L.T("almeno 1 MB"), 1L << 20), (L.T("almeno 10 MB"), 10L << 20), (L.T("almeno 100 MB"), 100L << 20) })
            _minSize.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _minSize.SelectedIndex = 1;
        foreach (var (label, value) in new[] { (L.T("Quasi identiche"), 4), (L.T("Molto simili"), 8), (L.T("Simili (anche foto a raffica)"), 11) })
            _similarity.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _similarity.SelectedIndex = 1;
        _modeSame.Checked += (_, _) => UpdateMode();
        _modePhoto.Checked += (_, _) => UpdateMode();
        foreach (var (label, rule) in new[]
                 {
                     (L.T("la migliore qualità"), Duplicates.KeepRule.Best), (L.T("il più vecchio"), Duplicates.KeepRule.Oldest),
                     (L.T("il più recente"), Duplicates.KeepRule.Newest), (L.T("il percorso più corto"), Duplicates.KeepRule.ShortestPath),
                 })
            _keepRule.Items.Add(new ComboBoxItem { Content = label, Tag = rule });
        _keepRule.SelectedIndex = 0;

        // ---- cartelle
        var addBtn = Ui.Btn(L.T("Aggiungi cartella…"), SymbolRegular.FolderAdd24, (_, _) =>
        {
            var d = Dlg.PickFolder(L.T("Cartella in cui cercare i duplicati"), "dupes");
            if (d != null) AddFolder(d);
        });
        var quick = Ui.Row(addBtn);
        foreach (var (label, folder) in new[]
                 {
                     (L.T("Immagini"), Environment.SpecialFolder.MyPictures), (L.T("Documenti"), Environment.SpecialFolder.MyDocuments),
                     (L.T("Download"), Environment.SpecialFolder.UserProfile), (L.T("Desktop"), Environment.SpecialFolder.Desktop),
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (folder == Environment.SpecialFolder.UserProfile) path = Path.Combine(path, "Downloads");
            if (Directory.Exists(path)) quick.Children.Add(Ui.Btn("+ " + label, null, (_, _) => AddFolder(path)));
        }
        var remove = Ui.Btn(L.T("Rimuovi"), SymbolRegular.Dismiss24, (_, _) =>
        {
            if (_folders.SelectedItem != null) _folders.Items.Remove(_folders.SelectedItem);
        });
        quick.Children.Add(remove);
        quick.Margin = new Thickness(0, 8, 0, 0);

        var mode = Ui.Row(_modeSame, _modePhoto);
        mode.Margin = new Thickness(0, 12, 0, 0);
        _go = Ui.Btn(L.T("Cerca duplicati"), SymbolRegular.Search24, (_, _) => _ = Search(), primary: true);
        _stop = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stop.Visibility = Visibility.Collapsed;
        var opts = Ui.Row(_optLabel, _minSize, _similarity, new Border { Width = 18 }, _go, _stop);
        opts.Margin = new Thickness(0, 10, 0, 0);

        var setup = new StackPanel();
        setup.Children.Add(_folders);
        setup.Children.Add(quick);
        setup.Children.Add(mode);
        setup.Children.Add(opts);
        setup.Children.Add(_progress);
        var setupCard = Ui.Card(L.T("Dove cercare"), null, setup);

        // ---- risultati
        _grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(_grid, true);
        VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(DupFile.Delete)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(MinWidthProperty, 0.0);
        _grid.Columns.Add(new DataGridTemplateColumn { Header = L.T("Elimina"), CellTemplate = new DataTemplate { VisualTree = check }, Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Nome"), Binding = new Binding(nameof(DupFile.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Cartella"), Binding = new Binding(nameof(DupFile.Folder)), Width = new DataGridLength(3, DataGridLengthUnitType.Star), IsReadOnly = true });
        _resColumn = new DataGridTextColumn { Header = L.T("Risoluzione"), Binding = new Binding(nameof(DupFile.Resolution)), Width = 110, IsReadOnly = true };
        _grid.Columns.Add(_resColumn);
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Dimensione"), Binding = new Binding(nameof(DupFile.SizeText)), Width = 95, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Modificato"), Binding = new Binding(nameof(DupFile.ModifiedText)), Width = 130, IsReadOnly = true });
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        header.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        header.SetValue(MarginProperty, new Thickness(6, 12, 0, 4));
        header.SetValue(TextBlock.ForegroundProperty, Ui.Res("TextFillColorPrimaryBrush"));
        _grid.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });
        _grid.SelectionChanged += (_, _) => ShowPreview();
        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is DupFile f) Util.OpenExternal(f.Path);
        };

        // ---- anteprima
        _previewButtons = Ui.Row(
            Ui.Btn(L.T("Apri"), SymbolRegular.Open24, (_, _) => { if (_grid.SelectedItem is DupFile f) Util.OpenExternal(f.Path); }),
            Ui.Btn(L.T("Mostra"), SymbolRegular.FolderOpen24, (_, _) => { if (_grid.SelectedItem is DupFile f) Util.Reveal(f.Path); }, L.T("Mostra in Esplora risorse")));
        _previewButtons.Margin = new Thickness(0, 10, 0, 0);
        _previewButtons.Visibility = Visibility.Collapsed;
        var previewPanel = new StackPanel();
        previewPanel.Children.Add(_preview);
        previewPanel.Children.Add(_previewIcon);
        previewPanel.Children.Add(_previewInfo);
        previewPanel.Children.Add(_previewButtons);
        previewPanel.Children.Add(_strip);
        var previewBorder = new Border
        {
            Style = (Style)Application.Current.FindResource("Panel"), Margin = new Thickness(12, 0, 0, 0),
            Child = new ScrollViewer { Content = previewPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        _trash = Ui.Btn(L.T("Sposta nel Cestino"), SymbolRegular.Delete24, (_, _) => _ = DeleteSelected(), primary: true);
        var bottom = Ui.Row(Ui.Label(L.T("Tieni")), new Border { Width = 8 }, _keepRule,
            Ui.Btn(L.T("Seleziona i doppioni"), SymbolRegular.CheckmarkCircle24, (_, _) => AutoSelect(), L.T("Segna da eliminare tutti i file di ogni gruppo tranne quello da tenere")),
            Ui.Btn(L.T("Deseleziona tutto"), null, (_, _) => { foreach (var f in _groups.SelectMany(g => g)) f.Delete = false; }),
            _selection, _trash);
        bottom.Margin = new Thickness(0, 10, 0, 0);

        _empty = Ui.Placeholder(SymbolRegular.DocumentCopy24, L.T("Scegli una o più cartelle e premi «Cerca duplicati».\nI file solo su OneDrive (non scaricati) vengono saltati."), out _);

        var results = new Grid();
        results.ColumnDefinitions.Add(new ColumnDefinition());
        results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        results.Children.Add(_grid);
        Grid.SetColumn(previewBorder, 1);
        results.Children.Add(previewBorder);
        results.Children.Add(_empty);
        Grid.SetColumnSpan(_empty, 2);

        var root = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = Ui.Header(L.T("Trova duplicati"), L.T("Scopri i file copiati più volte e le foto quasi uguali, scegli cosa tenere e libera spazio. I file eliminati finiscono nel Cestino."));
        root.Children.Add(head);
        Grid.SetRow(setupCard, 1);
        root.Children.Add(setupCard);
        Grid.SetRow(_status, 2);
        _status.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_status);
        Grid.SetRow(results, 3);
        root.Children.Add(results);
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);
        Content = root;

        UpdateMode();
        ShowResults();
    }

    // ------------------------------------------------------------------ cartelle e opzioni
    private void AddFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (!_folders.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) _folders.Items.Add(path);
    }

    public bool Accepts(string path) => Directory.Exists(path);

    public void OpenFile(string path)
    {
        if (Directory.Exists(path)) AddFolder(path);
    }

    public void AddFiles(IReadOnlyList<string> paths)
    {
        foreach (var p in paths) OpenFile(p);
    }

    private void UpdateMode()
    {
        if (_minSize == null) return;
        var photo = _modePhoto.IsChecked == true;
        _optLabel.Text = photo ? L.T("Somiglianza") : L.T("Dimensione dei file");
        _minSize.Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
        _similarity.Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _go.IsEnabled = !busy;
        _stop.Visibility = _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    // ------------------------------------------------------------------ ricerca
    private async Task Search()
    {
        var roots = _folders.Items.Cast<string>().Where(Directory.Exists).ToList();
        if (roots.Count == 0)
        {
            Dlg.Info(L.T("Aggiungi almeno una cartella in cui cercare."));
            return;
        }
        var photo = _modePhoto.IsChecked == true;
        var minSize = (long)((ComboBoxItem)_minSize.SelectedItem).Tag;
        var threshold = (int)((ComboBoxItem)_similarity.SelectedItem).Tag;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _groups = new();
        ShowResults();
        SetBusy(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var last = System.Diagnostics.Stopwatch.StartNew();
        void Progress(string m)
        {
            if (last.ElapsedMilliseconds < 150) return;
            last.Restart();
            Dispatcher.InvokeAsync(() => _status.Text = m);
        }
        try
        {
            var (groups, cloud) = await Task.Run(() =>
            {
                int skipped;
                var g = photo
                    ? Duplicates.FindSimilarPhotos(roots, threshold, Progress, ct, out skipped)
                    : Duplicates.FindIdentical(roots, minSize, Progress, ct, out skipped);
                return (g, skipped);
            }, ct);
            _groups = groups;
            _photoResults = photo;
            foreach (var g in _groups) Duplicates.AutoSelect(g, SelectedRule);
            ShowResults();
            var wasted = _groups.Sum(g => g.Where(f => f.Delete).Sum(f => f.Size));
            _status.Text = (_groups.Count == 0 ? L.T("Nessun duplicato trovato.") : L.T($"Trovati {_groups.Count} gruppi ({_groups.Sum(g => g.Count)} file): puoi liberare fino a {Util.HumanSize(wasted)}. Ho già selezionato i doppioni da eliminare: controlla e premi «Sposta nel Cestino».")) +
                           (cloud > 0 ? L.T($" {Util.Number(cloud)} file solo nel cloud sono stati saltati.") : "") +
                           $" ({Util.HumanTime(sw.Elapsed.TotalSeconds)})";
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("Ricerca interrotta.");
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Ricerca non riuscita:\n") + ex.Message);
            _status.Text = "";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private Duplicates.KeepRule SelectedRule => (Duplicates.KeepRule)((ComboBoxItem)_keepRule.SelectedItem).Tag;

    private void ShowResults()
    {
        var items = new List<DupFile>();
        var id = 0;
        foreach (var g in _groups)
        {
            id++;
            var size = _photoResults ? $"{g.Count} foto simili" : L.T($"{g.Count} copie da {Util.HumanSize(g[0].Size)}");
            var label = L.T($"Gruppo {id} · {size}");
            foreach (var f in g)
            {
                f.Group = id;
                f.GroupLabel = label;
                f.PropertyChanged -= File_PropertyChanged;
                f.PropertyChanged += File_PropertyChanged;
                items.Add(f);
            }
        }
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DupFile.GroupLabel)));
        _grid.ItemsSource = view;
        _resColumn.Visibility = _photoResults ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _grid.Visibility = _groups.Count == 0 ? Visibility.Hidden : Visibility.Visible;
        ShowPreview();
        UpdateSelection();
    }

    private void File_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateSelection();
        if (_grid.SelectedItem is DupFile sel && sender is DupFile f && f.Group == sel.Group) UpdateStripMarks();
    }

    private void UpdateSelection()
    {
        var sel = _groups.SelectMany(g => g).Where(f => f.Delete).ToList();
        _selection.Text = sel.Count == 0 ? "" : L.T($"{sel.Count} file selezionati · {Util.HumanSize(sel.Sum(f => f.Size))}");
        _trash.IsEnabled = _cts == null && sel.Count > 0;
    }

    private void AutoSelect()
    {
        foreach (var g in _groups) Duplicates.AutoSelect(g, SelectedRule);
    }

    // ------------------------------------------------------------------ anteprima
    private static BitmapSource? LoadThumb(string path, uint size)
    {
        try
        {
            var s = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
            s.SetDefine(MagickFormat.Jpeg, "size", $"{size * 2}x{size * 2}");
            using var img = new MagickImage(path, s);
            img.AutoOrient();
            img.Thumbnail(new MagickGeometry(size, size));
            return ImageIO.ToBitmapSource(img);
        }
        catch
        {
            return null;
        }
    }

    private async Task<BitmapSource?> Thumb(string path, uint size)
    {
        var key = path + "|" + size;
        if (_thumbs.TryGetValue(key, out var b)) return b;
        b = await Task.Run(() => LoadThumb(path, size));
        if (_thumbs.Count > 400) _thumbs.Clear();
        _thumbs[key] = b;
        return b;
    }

    private async void ShowPreview()
    {
        _preview.Source = null;
        _strip.Children.Clear();
        if (_grid.SelectedItem is not DupFile f)
        {
            _previewIcon.Source = null;
            _previewInfo.Text = _groups.Count > 0 ? L.T("Seleziona un file per vederne l'anteprima.") : "";
            _previewButtons.Visibility = Visibility.Collapsed;
            return;
        }
        _previewButtons.Visibility = Visibility.Visible;
        var isImage = Duplicates.ImageExts.Contains(Path.GetExtension(f.Path).ToLowerInvariant());
        _previewIcon.Source = isImage ? null : ShellIcons.For(f.Path, true);
        _previewIcon.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        _previewInfo.Text = L.T($"{f.Name}\n{f.Folder}\n{f.SizeText}{(f.Resolution.Length > 0 ? " · " + f.Resolution : "")} · modificato il {f.ModifiedText}");
        var group = _groups.FirstOrDefault(g => g.Contains(f));
        if (group != null && isImage)
        {
            foreach (var other in group)
            {
                var img = new Image { Width = 96, Height = 72, Stretch = Stretch.Uniform };
                var border = new Border
                {
                    Child = img, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(2), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4),
                    Tag = other, Cursor = Cursors.Hand, ToolTip = other.Path,
                };
                border.MouseLeftButtonUp += (_, _) => _grid.SelectedItem = other;
                _strip.Children.Add(border);
                _ = Thumb(other.Path, 160).ContinueWith(t => img.Source = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
            }
            UpdateStripMarks();
        }
        if (!isImage) return;
        var big = await Thumb(f.Path, 640);
        if (_grid.SelectedItem == f) _preview.Source = big;
    }

    /// <summary>Bordo rosso sulle miniature dei file segnati da eliminare, accento su quello mostrato.</summary>
    private void UpdateStripMarks()
    {
        foreach (Border b in _strip.Children)
        {
            var f = (DupFile)b.Tag;
            b.BorderBrush = f == _grid.SelectedItem ? Ui.Res("SwinAccentBrush")
                : f.Delete ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : Brushes.Transparent;
            b.Opacity = f.Delete ? 0.55 : 1;
        }
    }

    // ------------------------------------------------------------------ eliminazione
    private async Task DeleteSelected()
    {
        var sel = _groups.SelectMany(g => g).Where(f => f.Delete).ToList();
        if (sel.Count == 0) return;
        var whole = _groups.Where(g => g.All(f => f.Delete)).ToList();
        if (whole.Count > 0)
        {
            Dlg.Info(L.T($"In {whole.Count} gruppi hai selezionato tutti i file: tienine almeno uno per gruppo (per esempio il gruppo {whole[0][0].Group})."));
            _grid.SelectedItem = whole[0][0];
            _grid.ScrollIntoView(whole[0][0]);
            return;
        }
        if (!Dlg.Confirm(L.T($"Spostare {sel.Count} file ({Util.HumanSize(sel.Sum(f => f.Size))}) nel Cestino?\nPotrai ripristinarli dal Cestino se cambi idea."),
                L.T("Trova duplicati"), L.T("Sposta nel Cestino"), L.T("Annulla")))
            return;
        _trash.IsEnabled = false;
        _status.Text = L.T("Spostamento nel Cestino…");
        _preview.Source = null;
        var failed = 0;
        await Task.Run(() =>
        {
            foreach (var chunk in sel.Chunk(100))
            {
                try { DiskScan.SendToRecycleBin(chunk.Select(f => f.Path)); }
                catch { }
                failed += chunk.Count(f => File.Exists(f.Path));
            }
        });
        var removed = sel.Where(f => !File.Exists(f.Path)).ToHashSet();
        _groups = _groups.Select(g => g.Where(f => !removed.Contains(f)).ToList()).Where(g => g.Count > 1).ToList();
        ShowResults();
        _status.Text = L.T($"{removed.Count} file spostati nel Cestino ({Util.HumanSize(removed.Sum(f => f.Size))}).") +
                       (failed > 0 ? L.T($" {failed} file non sono stati spostati (in uso o protetti).") : "");
        MainWindow.Notify(L.T($"{removed.Count} duplicati spostati nel Cestino"));
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("La ricerca dei duplicati è in corso: interromperla?"));

    public void Shutdown() => _cts?.Cancel();
}
