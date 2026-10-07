using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SwinKnife.Pages;

/// <summary>Collezione osservabile con inserimenti/rimozioni in blocco (una sola notifica).</summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void InsertRange(int index, IEnumerable<T> items)
    {
        foreach (var it in items) Items.Insert(index++, it);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void RemoveRange(int index, int count)
    {
        for (var i = 0; i < count; i++) Items.RemoveAt(index);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void ResetWith(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var it in items) Items.Add(it);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public partial class DiskAnalyzerPage : UserControl, IToolPage
{
    public sealed class TreeRow(DiskNode node, int depth) : INotifyPropertyChanged
    {
        private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C));
        public DiskNode Node { get; } = node;
        public int Depth { get; } = depth;
        public bool Expanded { get; set; }
        public Thickness Indent => new(Depth * 18, 0, 0, 0);
        public string Expander => Node.IsDir && Node.Children!.Count > 0 ? (Expanded ? "▾" : "▸") : "";
        public SymbolRegular Icon => Node.Parent == null ? SymbolRegular.Storage24 : Node.IsDir ? SymbolRegular.Folder24 : SymbolRegular.Document24;
        public bool IsDir => Node.IsDir;
        public Brush IconBrush => Node.IsDir ? FolderBrush : Controls.Ui.Res("TextFillColorSecondaryBrush");
        public string Name => Node.Name;
        public string SizeText => Util.HumanSize(Node.Size);
        public double Fraction => Node.Parent is { Size: > 0 } p ? Node.Size / (double)p.Size : 1;
        public double BarWidth => 110 * Fraction;
        public string PercentText => (Fraction * 100).ToString("0.0", Util.It) + " %";
        public string FilesText => Node.IsDir ? Util.Number(Node.Files) : "";
        public string ModifiedText => Util.Date(Node.Modified);
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public sealed record ExtRow(string Ext, string Key, Brush Swatch, string SizeText, string PercentText, string CountText, int? ColorIndex);
    private sealed record DriveItem(string Label, string Path);

    private readonly MainWindow _main;
    private readonly BulkCollection<TreeRow> _rows = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _mapCts;
    private ScanResult? _result;
    private DiskNode? _mapRoot;
    private Dictionary<string, int> _colors = new();

    public DiskAnalyzerPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TreeList.ItemsSource = _rows;
        TreeList.SelectionChanged += (_, _) =>
        {
            Map.Selected = (TreeList.SelectedItem as TreeRow)?.Node;
            Map.InvalidateVisual();
        };
        Map.NeedsRender += RenderMap;
        Map.Hovered += n => MainWindow.Notify(n == null ? "" : $"{n.FullPath}  —  {Util.HumanSize(n.Size)}", 0);
        Map.NodeClicked += SelectNode;
        Map.NodeDoubleClicked += n => ZoomTo(n.IsDir ? n : n.Parent);
        FillDrives();
        AdminBanner.Visibility = !RawSource.IsAdmin() && DriveInfo.GetDrives().Any(d => MftScan.IsNtfsVolume(d.Name)) ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _declinedAdmin;

    private void Elevate_Click(object sender, RoutedEventArgs e) => Elevate((DriveCombo.SelectedItem as DriveItem)?.Path);

    /// <summary>Riavvia SwinKnife come amministratore riaprendo l'analisi della stessa cartella.</summary>
    private bool Elevate(string? path)
    {
        if (!_main.CanCloseAllPages()) return false;
        // "C:\" tra virgolette diventerebbe C:" sulla riga di comando: aggiungo un punto
        var arg = path == null ? "" : $" \"{(path.EndsWith('\\') ? path + "." : path)}\"";
        if (RawSource.RelaunchAsAdmin("--page disk --elevated" + arg))
        {
            App.Quit();
            return true;
        }
        Dlg.Error(L.T("Non è stato possibile ottenere i privilegi di amministratore."));
        return false;
    }

    private void FillDrives()
    {
        var items = new List<DriveItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var used = d.TotalSize - d.TotalFreeSpace;
                var name = string.IsNullOrEmpty(d.VolumeLabel) ? d.DriveType == DriveType.Removable ? L.T("Unità rimovibile") : L.T("Disco locale") : d.VolumeLabel;
                items.Add(new DriveItem(L.T($"{d.Name}  {name}  —  {Util.HumanSize(used)} usati di {Util.HumanSize(d.TotalSize)}"), d.Name));
            }
            catch { }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        items.Add(new DriveItem(L.T($"Cartella utente  ({home})"), home));
        DriveCombo.ItemsSource = items;
        DriveCombo.SelectedIndex = 0;
    }

    private void SetSource(string path)
    {
        var items = ((List<DriveItem>)DriveCombo.ItemsSource).ToList();
        items.Insert(0, new DriveItem(path, path));
        DriveCombo.ItemsSource = items;
        DriveCombo.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ IToolPage
    public bool Accepts(string path) => Directory.Exists(path);

    public void OpenFile(string path)
    {
        if (!Directory.Exists(path)) return;
        SetSource(path);
        Go_Click(this, new RoutedEventArgs());
    }

    public void Shutdown()
    {
        _scanCts?.Cancel();
        _mapCts?.Cancel();
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var d = Dlg.PickFolder(L.T("Scegli la cartella da analizzare"), "disk");
        if (d != null) SetSource(d);
    }

    // ------------------------------------------------------------------ scansione
    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveItem item || _scanCts != null) return;
        var ntfs = MftScan.IsNtfsVolume(item.Path);
        if (ntfs && !RawSource.IsAdmin() && !_declinedAdmin)
        {
            if (Dlg.Confirm(L.T("Da amministratore SwinKnife legge direttamente la tabella dei file di NTFS, come WinDirStat e WizTree: un disco intero si analizza in pochi secondi.\n\nSenza privilegi l'analisi funziona lo stesso, ma su un disco pieno può richiedere qualche minuto."),
                    L.T("Analisi veloce"), L.T("Riavvia come amministratore"), L.T("Continua senza")))
            {
                if (Elevate(item.Path)) return;
            }
            _declinedAdmin = true;
        }
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var skip = SkipCloud.IsChecked == true;
        GoBtn.IsEnabled = false;
        StopBtn.Visibility = Visibility.Visible;
        Busy.Visibility = Visibility.Visible;
        StatusText.Text = L.T("Scansione in corso…");
        _rows.ResetWith([]);
        ExtList.ItemsSource = null;
        Map.SetMap(null, 1);
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Report(string m) => ((IProgress<string>)progress).Report(m);
            var fast = ntfs && RawSource.IsAdmin();
            _result = await Task.Run(() =>
            {
                if (fast)
                {
                    try
                    {
                        return MftScan.Scan(item.Path, skip, Report, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppInfo.Log(ex, "Analisi veloce (MFT), passo a quella normale");
                        fast = false;
                    }
                }
                return DiskScan.Scan(item.Path, skip, Report, token);
            });
            var root = _result.Root;
            var extra = new List<string>();
            if (_result.Errors > 0) extra.Add(L.T($"{_result.Errors} cartelle non accessibili"));
            if (_result.CloudOnly > 0) extra.Add(L.T($"{Util.Number(_result.CloudOnly)} file solo-cloud esclusi"));
            extra.Add(fast ? L.T($"analisi veloce in {clock.Elapsed.TotalSeconds:0.0} s") : L.T($"analisi in {clock.Elapsed.TotalSeconds:0.0} s"));
            StatusText.Text = (_result.Cancelled ? L.T("Scansione interrotta: ") : "") + L.T($"{Util.Number(root.Files)} file · {Util.HumanSize(root.Size)}") +
                              $"  ({string.Join("; ", extra)})";
            _colors = DiskScan.ColorIndex(_result.Extensions);
            var top = new TreeRow(root, 0) { Expanded = true };
            _rows.ResetWith(new[] { top }.Concat(root.Children!.Select(c => new TreeRow(c, 1))));
            FillExtensions();
            _mapRoot = root;
            RenderMap();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("Scansione interrotta.");
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Errore: ") + ex.Message;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            GoBtn.IsEnabled = true;
            StopBtn.Visibility = Visibility.Collapsed;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void FillExtensions()
    {
        if (_result == null) return;
        var total = Math.Max(1, _result.Root.Size);
        ExtList.ItemsSource = _result.Extensions.OrderByDescending(kv => kv.Value.size).Take(300).Select(kv =>
        {
            var idx = _colors.TryGetValue(kv.Key, out var i) ? i : (int?)null;
            var c = idx is { } k ? DiskScan.Palette[k] : DiskScan.OtherColor;
            return new ExtRow(kv.Key.Length > 0 ? kv.Key : L.T("(nessuna)"), kv.Key, new SolidColorBrush(Color.FromRgb(c.r, c.g, c.b)),
                Util.HumanSize(kv.Value.size), (kv.Value.size * 100.0 / total).ToString("0.0", Util.It), Util.Number(kv.Value.count), idx);
        }).ToList();
    }

    private void Ext_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExtList.SelectedItem is ExtRow r)
        {
            Map.HighlightExtension(r.ColorIndex ?? -2);
            if (r.ColorIndex == null) MainWindow.Notify(L.T("Solo le 12 estensioni più ingombranti hanno un colore proprio nella mappa"));
        }
        else Map.HighlightExtension(null);
    }

    // ------------------------------------------------------------------ mappa
    private async void RenderMap()
    {
        if (_mapRoot == null) return;
        _mapCts?.Cancel();
        _mapCts = new CancellationTokenSource();
        var token = _mapCts.Token;
        var (w, h, dpi) = Map.PixelSize();
        var root = _mapRoot;
        var colors = _colors;
        MapLabel.Text = L.T($"Mappa di: {root.FullPath}  ·  {Util.HumanSize(root.Size)}");
        UpBtn.IsEnabled = root.Parent != null;
        var map = await Task.Run(() => DiskScan.Render(root, w, h, colors, token));
        if (map != null && !token.IsCancellationRequested)
        {
            Map.SetMap(map, dpi);
            if (ExtList.SelectedItem is ExtRow r) Map.HighlightExtension(r.ColorIndex ?? -2);
        }
    }

    private void ZoomTo(DiskNode? node)
    {
        if (node == null || node == _mapRoot) return;
        _mapRoot = node;
        RenderMap();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => ZoomTo(_mapRoot?.Parent);

    // ------------------------------------------------------------------ albero
    private void Expand(TreeRow row)
    {
        if (!row.Node.IsDir || row.Node.Children!.Count == 0) return;
        var index = _rows.IndexOf(row);
        if (row.Expanded)
        {
            var end = index + 1;
            while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
            row.Expanded = false;
            _rows.RemoveRange(index + 1, end - index - 1);
        }
        else
        {
            row.Expanded = true;
            _rows.InsertRange(index + 1, row.Node.Children.Select(c => new TreeRow(c, row.Depth + 1)));
        }
        row.Refresh();
    }

    private void Expander_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TreeRow row)
        {
            Expand(row);
            e.Handled = true;
        }
    }

    private void Tree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeList.SelectedItem is not TreeRow row) return;
        if (row.Node.IsDir) Expand(row);
        else Util.OpenExternal(row.Node.FullPath);
    }

    private void SelectNode(DiskNode node)
    {
        var chain = new List<DiskNode>();
        for (var n = node.Parent; n != null; n = n.Parent) chain.Insert(0, n);
        foreach (var anc in chain)
        {
            var row = _rows.FirstOrDefault(r => r.Node == anc);
            if (row is { Expanded: false }) Expand(row);
        }
        var target = _rows.FirstOrDefault(r => r.Node == node);
        if (target == null) return;
        TreeList.SelectedItem = target;
        TreeList.ScrollIntoView(target);
    }

    // ------------------------------------------------------------------ menu contestuale
    private void TreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        TreeMenu.Items.Clear();
        if (TreeList.SelectedItem is not TreeRow row)
        {
            TreeMenu.IsOpen = false;
            return;
        }
        var node = row.Node;
        var path = node.FullPath;
        void Add(string text, SymbolRegular icon, Action action)
        {
            var mi = new MenuItem { Header = text, Icon = new SymbolIcon { Symbol = icon } };
            mi.Click += (_, _) => action();
            TreeMenu.Items.Add(mi);
        }
        if (!node.IsDir)
        {
            Add(L.T("Apri"), SymbolRegular.Open24, () => Util.OpenExternal(path));
            Add(L.T("Apri in SwinKnife"), SymbolRegular.Eye24, () => _main.OpenFile(path, "viewer"));
        }
        Add(L.T("Mostra in Esplora risorse"), SymbolRegular.FolderOpen24, () => Util.Reveal(path));
        Add(L.T("Copia percorso"), SymbolRegular.Copy24, () => Clipboard.SetText(path));
        if (node.IsDir) Add(L.T("Mostra nella mappa"), SymbolRegular.DataPie24, () => ZoomTo(node));
        if (node.Parent != null)
        {
            TreeMenu.Items.Add(new Separator());
            Add(L.T("Sposta nel Cestino…"), SymbolRegular.Delete24, () => Delete(row));
        }
    }

    private void Delete(TreeRow row)
    {
        var node = row.Node;
        var path = node.FullPath;
        var what = node.IsDir ? L.T("la cartella e tutto il suo contenuto") : L.T("il file");
        if (!Dlg.Confirm(L.T($"Spostare nel Cestino {what}?\n\n{path}\n\n({Util.HumanSize(node.Size)})"))) return;
        try
        {
            DiskScan.SendToRecycleBin(path);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Impossibile eliminare:\n") + ex.Message);
            return;
        }
        for (var m = _mapRoot; m != null; m = m.Parent)
        {
            if (m == node)
            {
                _mapRoot = node.Parent;
                break;
            }
        }
        var index = _rows.IndexOf(row);
        var end = index + 1;
        while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
        DiskScan.Remove(node);
        _rows.RemoveRange(index, end - index);
        foreach (var r in _rows) r.Refresh();
        RenderMap();
        MainWindow.Notify(L.T("Spostato nel Cestino: ") + path);
    }
}
