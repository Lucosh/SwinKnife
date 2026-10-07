using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Clipboard = System.Windows.Clipboard;
using ListView = Wpf.Ui.Controls.ListView;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using GridViewColumn = Wpf.Ui.Controls.GridViewColumn;
using GridView = Wpf.Ui.Controls.GridView;

namespace SwinKnife.Pages;

/// <summary>Ricerca istantanea di file e cartelle su tutti i dischi, mentre scrivi (come "Everything").</summary>
public sealed class SearchPage : UserControl, IToolPage
{
    public sealed class Row(SearchHit hit)
    {
        private string? _path;
        public SearchHit Hit { get; } = hit;
        public string Name => Hit.Volume.Name[Hit.Index] ?? "";
        public string FullPath => _path ??= Hit.Volume.PathOf(Hit.Index);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
        public bool IsDir => Hit.Volume.IsDir(Hit.Index);
        public long Size => Hit.Volume.SizeOf(Hit.Index);
        public string SizeText => IsDir ? "" : Util.HumanSize(Size);
        public DateTime Modified => Hit.Volume.ModifiedOf(Hit.Index);
        public string ModifiedText => Util.Date(Modified);
        public ImageSource? Icon => ShellIcons.For(FullPath);
    }

    private const int MaxShown = 50_000;
    private readonly TextBox _query = new() { PlaceholderText = L.T("Scrivi parte del nome… (es. fattura 2025, *.pdf, ext:jpg;png, -bozza)"), FontSize = 15, Icon = new SymbolIcon { Symbol = SymbolRegular.Search24 } };
    private readonly ComboBox _kind = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListView _list = new();
    private readonly TextBlock _status = Ui.Hint();
    private readonly Border _banner;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private List<Row> _rows = new();
    private string _sort = "name";
    private bool _desc;
    private bool _dirty;

    public SearchPage(MainWindow main)
    {
        foreach (var k in new[] { L.T("Tutto"), L.T("Cartelle"), L.T("Documenti"), L.T("Immagini"), L.T("Audio"), L.T("Video"), L.T("Archivi"), L.T("Programmi") }) _kind.Items.Add(k);
        _kind.SelectedIndex = 0;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Run();
        }, Dispatcher);
        _query.TextChanged += (_, _) => Restart();
        _kind.SelectionChanged += (_, _) => Restart();

        // ---- elenco
        var view = new GridView();
        view.Columns.Add(Column(L.T("Nome"), "name", 330, NameTemplate()));
        view.Columns.Add(Column(L.T("Cartella"), "folder", 420, Text(nameof(Row.Folder))));
        view.Columns.Add(Column(L.T("Dimensione"), "size", 100, Text(nameof(Row.SizeText), TextAlignment.Right)));
        view.Columns.Add(Column(L.T("Ultima modifica"), "date", 150, Text(nameof(Row.ModifiedText))));
        _list.View = view;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.MouseDoubleClick += (_, _) => Open(false);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Open(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) CopyPaths();
        };
        _list.ContextMenu = Menu();
        _query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _list.Items.Count > 0)
            {
                _list.SelectedIndex = 0;
                (_list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
                e.Handled = true;
            }
        };
        // drag & drop verso altri programmi
        _list.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _list.SelectedItems.Count == 0 || e.OriginalSource is not FrameworkElement { DataContext: Row }) return;
            var files = new StringCollection();
            files.AddRange(_list.SelectedItems.OfType<Row>().Select(r => r.FullPath).ToArray());
            var data = new DataObject();
            data.SetFileDropList(files);
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Link);
        };

        _banner = new Border
        {
            Style = (Style)Application.Current.FindResource("WarnBanner"), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed,
        };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_kind, Dock.Right);
        top.Children.Add(_kind);
        top.Children.Add(_query);
        var header = Ui.Header(L.T("Cerca file"), L.T("Trova all'istante file e cartelle su tutti i dischi mentre scrivi. Doppio clic per aprire, Ctrl+Invio per aprire la cartella, trascina i risultati dove ti servono."));
        var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        foreach (var e in new UIElement[] { header, _banner, top }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(0, 8, 0, 0);
        dock.Children.Add(_status);
        dock.Children.Add(_list);
        Content = dock;

        FileIndexes.Changed += OnIndexChanged;
        Unloaded += (_, _) => FileIndexes.Changed -= OnIndexChanged;
        Loaded += async (_, _) =>
        {
            FileIndexes.Changed -= OnIndexChanged;
            FileIndexes.Changed += OnIndexChanged;
            _query.Focus();
            await EnsureIndex();
        };
    }

    public void Activated() => _query.Focus();

    // ------------------------------------------------------------------ indice
    private async Task EnsureIndex()
    {
        if (!FileIndexes.Ready)
        {
            _status.Text = L.T("Preparo l'indice dei file…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await FileIndexes.EnsureAsync(m => Dispatcher.InvokeAsync(() => _status.Text = m));
            MainWindow.Notify(L.T($"Indice pronto: {Util.Number(FileIndexes.TotalCount)} voci in {sw.Elapsed.TotalSeconds:0.0} s"));
        }
        UpdateBanner();
        Run();
    }

    private void UpdateBanner()
    {
        if (FileIndexes.Fast || !DriveInfo.GetDrives().Any(d => MftScan.IsNtfsVolume(d.Name)))
        {
            _banner.Visibility = Visibility.Collapsed;
            return;
        }
        var btn = Ui.Btn(L.T("Riavvia come amministratore"), SymbolRegular.Shield24, (_, _) =>
        {
            if (!MainWindow.Instance!.CanCloseAllPages()) return;
            if (RawSource.RelaunchAsAdmin("--page search --elevated")) App.Quit();
        }, primary: true);
        var text = new TextBlock
        {
            Text = L.T("Da amministratore l'indice si crea in pochi secondi leggendo la tabella dei file di NTFS e resta aggiornato in tempo reale."),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = Ui.Res("TextFillColorPrimaryBrush"),
        };
        var dp = new DockPanel();
        DockPanel.SetDock(btn, Dock.Right);
        dp.Children.Add(btn);
        dp.Children.Add(text);
        _banner.Child = dp;
        _banner.Visibility = Visibility.Visible;
    }

    private void OnIndexChanged()
    {
        // i file cambiano di continuo: aggiorno i risultati al massimo ogni 2 secondi
        if (_dirty) return;
        _dirty = true;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(2000);
            _dirty = false;
            if (IsVisible && _query.Text.Trim().Length > 0) Run();
        });
    }

    // ------------------------------------------------------------------ ricerca
    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async void Run()
    {
        if (!FileIndexes.Ready) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var q = new SearchQuery(_query.Text, (SearchQuery.Kind)Math.Max(0, _kind.SelectedIndex));
        if (q.IsEmpty)
        {
            _rows = new();
            _list.ItemsSource = null;
            _status.Text = L.T($"{Util.Number(FileIndexes.TotalCount)} file e cartelle indicizzati. Inizia a scrivere per cercare.");
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<Row> rows;
        int total;
        try
        {
            (rows, total) = await Task.Run(() =>
            {
                var (hits, n) = FileIndexes.Search(q, MaxShown, cts.Token);
                var r = hits.Select(h => new Row(h)).ToList();
                Sort(r);
                return (r, n);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;
        rows = rows.Where(r => !r.Hit.Volume.IsDeleted(r.FullPath)).ToList();
        _rows = rows;
        _list.ItemsSource = rows;
        _status.Text = (total > rows.Count ? L.T($"{Util.Number(total)} risultati (mostrati i primi {Util.Number(rows.Count)})") : L.T($"{Util.Number(rows.Count)} risultati"))
                       + L.T($" in {sw.Elapsed.TotalMilliseconds:0} ms");
    }

    private void Sort(List<Row> rows)
    {
        Comparison<Row> cmp = _sort switch
        {
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "date" => (a, b) => a.Modified.CompareTo(b.Modified),
            "folder" => (a, b) => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase),
            // a parità di nome prima le cartelle e i percorsi più corti
            _ => (a, b) =>
            {
                var c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : b.IsDir.CompareTo(a.IsDir);
            },
        };
        rows.Sort(_desc ? (a, b) => cmp(b, a) : cmp);
    }

    private GridViewColumn Column(string header, string key, double width, DataTemplate template)
    {
        var h = new GridViewColumnHeader { Content = header, HorizontalContentAlignment = HorizontalAlignment.Left };
        h.Click += (_, _) =>
        {
            _desc = _sort == key ? !_desc : key is "size" or "date";
            _sort = key;
            Sort(_rows);
            _list.ItemsSource = null;
            _list.ItemsSource = _rows;
        };
        return new GridViewColumn { Header = h, Width = width, CellTemplate = template };
    }

    private static DataTemplate Text(string path, TextAlignment align = TextAlignment.Left)
    {
        var f = new FrameworkElementFactory(typeof(TextBlock));
        f.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneTime });
        f.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        f.SetValue(TextBlock.TextAlignmentProperty, align);
        return new DataTemplate { VisualTree = f };
    }

    private static DataTemplate NameTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var img = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        img.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding(nameof(Row.Icon)) { Mode = BindingMode.OneTime, IsAsync = true });
        img.SetValue(WidthProperty, 16.0);
        img.SetValue(HeightProperty, 16.0);
        img.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Name)) { Mode = BindingMode.OneTime });
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(img);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    // ------------------------------------------------------------------ azioni
    private ContextMenu Menu()
    {
        var m = new ContextMenu();
        void Add(string text, SymbolRegular icon, Action run)
        {
            var mi = new MenuItem { Header = text, Icon = new SymbolIcon { Symbol = icon } };
            mi.Click += (_, _) => run();
            m.Items.Add(mi);
        }
        Add(L.T("Apri"), SymbolRegular.Open24, () => Open(false));
        Add(L.T("Apri con SwinKnife"), SymbolRegular.Eye24, () =>
        {
            if (_list.SelectedItem is Row r && !r.IsDir) MainWindow.Instance?.OpenFile(r.FullPath);
        });
        Add(L.T("Mostra nella cartella"), SymbolRegular.FolderOpen24, () => Open(true));
        Add(L.T("Copia percorso"), SymbolRegular.Copy24, CopyPaths);
        Add(L.T("Sposta nel Cestino"), SymbolRegular.Delete24, () =>
        {
            var sel = _list.SelectedItems.OfType<Row>().ToList();
            if (sel.Count == 0 || !Dlg.Confirm(L.T($"Spostare {sel.Count} elementi nel Cestino?"))) return;
            try
            {
                DiskScan.SendToRecycleBin(sel.Select(r => r.FullPath));
                _rows = _rows.Except(sel).ToList();
                _list.ItemsSource = _rows;
            }
            catch (Exception ex) { Dlg.Error(ex.Message); }
        });
        m.Items.Add(new Separator());
        Add(L.T("Aggiorna l'indice"), SymbolRegular.ArrowSync24, async () =>
        {
            _list.ItemsSource = null;
            await FileIndexes.RebuildAsync(msg => Dispatcher.InvokeAsync(() => _status.Text = msg));
            UpdateBanner();
            Run();
        });
        return m;
    }

    private void Open(bool folder)
    {
        if (_list.SelectedItem is not Row r) return;
        if (folder || !File.Exists(r.FullPath) && !Directory.Exists(r.FullPath))
        {
            if (File.Exists(r.FullPath) || Directory.Exists(r.FullPath)) Util.Reveal(r.FullPath);
            else MainWindow.Notify(L.T("Questo elemento non esiste più."));
        }
        else Util.OpenExternal(r.FullPath);
    }

    private void CopyPaths()
    {
        var paths = _list.SelectedItems.OfType<Row>().Select(r => r.FullPath).ToList();
        if (paths.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, paths));
        MainWindow.Notify(paths.Count == 1 ? L.T("Percorso copiato") : L.T($"{paths.Count} percorsi copiati"));
    }
}
