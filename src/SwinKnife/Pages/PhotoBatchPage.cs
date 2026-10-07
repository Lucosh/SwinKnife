using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ImageMagick;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Ridimensiona, comprimi e togli i dati nascosti (posizione GPS…) da tante foto insieme.</summary>
public sealed class PhotoBatchPage : UserControl, IToolPage
{
    public sealed class Item : INotifyPropertyChanged
    {
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public long Size { get; init; }
        public string SizeText => Util.HumanSize(Size);
        public string Dimensions { get; set; } = "";
        public string Gps { get; set; } = "";
        public string Result { get; set; } = "";
        public string? Output { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static readonly (string label, int side)[] Sizes =
    [
        (L.T("Originali"), 0), (L.T("Grande · 2560 px"), 2560), (L.T("E-mail e chat · 1920 px"), 1920), (L.T("Web · 1280 px"), 1280), (L.T("Piccola · 800 px"), 800),
    ];
    private static readonly (string label, string fmt)[] FormatsList =
    [
        (L.T("Come l'originale"), ""), ("JPG", "jpg"), ("WEBP", "webp"), ("AVIF", "avif"), ("PNG", "png"),
    ];

    private readonly ObservableCollection<Item> _items = new();
    private readonly DataGrid _grid;
    private readonly ComboBox _size = new() { Width = 220 }, _format = new() { Width = 170 }, _meta = new() { Width = 300 };
    private readonly Slider _quality = new() { Minimum = 40, Maximum = 100, Value = 82, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _qualityText = Ui.Hint();
    private readonly TextBlock _outText = Ui.Hint();
    private readonly TextBlock _summary = Ui.Label("");
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _go;
    private string? _outDir;

    public PhotoBatchPage(MainWindow main)
    {
        foreach (var (l, _) in Sizes) _size.Items.Add(l);
        foreach (var (l, _) in FormatsList) _format.Items.Add(l);
        foreach (var l in new[] { L.T("Mantieni tutti i dati (EXIF)"), L.T("Togli la posizione GPS (consigliato)"), L.T("Togli tutti i dati nascosti") }) _meta.Items.Add(l);
        _size.SelectedIndex = int.TryParse(Settings.Get("batch.size"), out var s) ? s : 2;
        _format.SelectedIndex = int.TryParse(Settings.Get("batch.format"), out var f) ? f : 0;
        _meta.SelectedIndex = int.TryParse(Settings.Get("batch.meta"), out var m) ? m : 1;
        _quality.Value = int.TryParse(Settings.Get("batch.quality"), out var q) ? q : 82;
        _quality.ValueChanged += (_, _) => _qualityText.Text = $"{_quality.Value:0}";
        _qualityText.Text = $"{_quality.Value:0}";
        _outDir = Settings.Get("batch.out");
        _outText.Text = _outDir ?? L.T("una sottocartella \"Ridotte\" accanto alle foto");

        _grid = new DataGrid
        {
            ItemsSource = _items, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended,
        };
        void Col(string h, string p, double w) => _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(p), Width = new DataGridLength(w, w < 0 ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel) });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Foto"), Binding = new Binding(nameof(Item.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Col(L.T("Dimensioni"), nameof(Item.Dimensions), 120);
        Col(L.T("Peso"), nameof(Item.SizeText), 90);
        Col(L.T("Posizione"), nameof(Item.Gps), 100);
        Col(L.T("Risultato"), nameof(Item.Result), 220);
        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is Item { Output: { } o } && File.Exists(o)) Util.OpenExternal(o);
            else if (_grid.SelectedItem is Item i) Util.OpenExternal(i.Path);
        };

        _go = Ui.Btn(L.T("Elabora le foto"), SymbolRegular.Play24, async (_, _) => await Run(), primary: true);
        var toolbar = Ui.Row(
            Ui.Btn(L.T("Aggiungi foto…"), SymbolRegular.ImageAdd24, (_, _) =>
            {
                var files = Dlg.OpenFiles(L.T("Scegli le foto"), L.T("Immagini|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.webp;*.avif;*.tif;*.tiff;*.bmp;*.gif;*.jxl;*.dng;*.cr2;*.nef;*.arw|") + Dlg.AllFiles, "batch");
                AddFiles(files);
            }),
            Ui.Btn(L.T("Aggiungi cartella…"), SymbolRegular.FolderAdd24, (_, _) =>
            {
                if (Dlg.PickFolder(L.T("Cartella con le foto"), "batch.dir") is { } d) AddFiles([d]);
            }),
            Ui.Btn(L.T("Svuota"), SymbolRegular.Delete24, (_, _) => { _items.Clear(); UpdateSummary(); }));

        var opts = new WrapPanel();
        UIElement Opt(string label, UIElement c) => Ui.Row(Ui.Label(label), new Border { Width = 8 }, c, new Border { Width = 24 });
        opts.Children.Add(Opt(L.T("Dimensione"), _size));
        opts.Children.Add(Opt(L.T("Formato"), _format));
        opts.Children.Add(Opt(L.T("Qualità"), Ui.Row(_quality, _qualityText)));
        var row2 = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        row2.Children.Add(Opt(L.T("Dati nascosti"), _meta));
        row2.Children.Add(Ui.Row(Ui.Label(L.T("Salva in:")), new Border { Width = 8 }, _outText,
            Ui.Btn(L.T("Cambia…"), SymbolRegular.Folder24, (_, _) =>
            {
                if (Dlg.PickFolder(L.T("Dove salvare le foto elaborate"), "batch.out") is { } d)
                {
                    _outDir = d;
                    Settings.Set("batch.out", d);
                    _outText.Text = d;
                }
            }),
            Ui.Btn(L.T("Accanto alle foto"), null, (_, _) =>
            {
                _outDir = null;
                Settings.Set("batch.out", null);
                _outText.Text = L.T("una sottocartella \"Ridotte\" accanto alle foto");
            })));
        var optCard = Ui.Card(L.T("Cosa fare"), L.T("Gli originali non vengono modificati: le copie elaborate vanno nella cartella indicata."), opts, row2);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_go, Dock.Right);
        bottom.Children.Add(_go);
        bottom.Children.Add(_summary);
        var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        var header = Ui.Header(L.T("Foto in blocco"), L.T("Ridimensiona e comprimi tante foto insieme per e-mail, chat o web, e togli i dati nascosti come la posizione GPS."));
        toolbar.Margin = new Thickness(0, 0, 0, 10);
        foreach (var e in new UIElement[] { header, optCard, toolbar }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        DockPanel.SetDock(_progress, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(_progress);
        dock.Children.Add(bottom);
        dock.Children.Add(_grid);
        Content = dock;
        UpdateSummary();
    }

    public bool Accepts(string path) => Directory.Exists(path) || Formats.IsImageLike(Formats.KindOf(path));
    public void OpenFile(string path) => AddFiles([path]);
    public void AddFiles(IReadOnlyList<string> paths) => AddFiles(paths.ToArray());

    private void AddFiles(string[] paths)
    {
        var files = paths.SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p).Where(f => Formats.IsImageLike(Formats.KindOf(f)))
                : [p])
            .Where(f => _items.All(i => !i.Path.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
        var added = files.Select(f => new Item { Path = f, Size = new FileInfo(f).Length }).ToList();
        foreach (var i in added) _items.Add(i);
        UpdateSummary();
        // dimensioni e GPS in background
        _ = Task.Run(() =>
        {
            foreach (var i in added)
            {
                try
                {
                    using var img = new MagickImage();
                    img.Ping(i.Path);
                    i.Dimensions = $"{img.Width} × {img.Height}";
                }
                catch { i.Dimensions = "?"; }
                i.Gps = PhotoBatch.HasGps(i.Path) ? L.T("📍 sì") : L.T("no");
                Dispatcher.InvokeAsync(i.Refresh);
            }
        });
    }

    private void UpdateSummary()
    {
        _go.IsEnabled = _items.Count > 0;
        _summary.Text = _items.Count == 0 ? L.T("Trascina qui foto o cartelle.") : L.T($"{_items.Count} foto · {Util.HumanSize(_items.Sum(i => i.Size))}");
    }

    private async Task Run()
    {
        Settings.Set("batch.size", _size.SelectedIndex.ToString());
        Settings.Set("batch.format", _format.SelectedIndex.ToString());
        Settings.Set("batch.meta", _meta.SelectedIndex.ToString());
        Settings.Set("batch.quality", ((int)_quality.Value).ToString());
        var side = Sizes[_size.SelectedIndex].side;
        var fmt = FormatsList[_format.SelectedIndex].fmt;
        var meta = (MetadataMode)_meta.SelectedIndex;
        var quality = (uint)_quality.Value;
        _go.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Maximum = _items.Count;
        _progress.Value = 0;
        long before = 0, after = 0;
        var ok = 0;
        var items = _items.ToList();
        await Task.Run(() => Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
        {
            try
            {
                var outDir = _outDir ?? Path.Combine(i.Folder, L.T("Ridotte"));
                var dst = PhotoBatch.Process(i.Path, new PhotoBatchOptions { MaxSide = side, Format = fmt, Quality = quality, Metadata = meta, OutputDir = outDir });
                var size = new FileInfo(dst).Length;
                i.Output = dst;
                i.Result = L.T($"{Util.HumanSize(size)} ({(size - i.Size) * 100.0 / Math.Max(1, i.Size):+0;-0} %)");
                Interlocked.Add(ref before, i.Size);
                Interlocked.Add(ref after, size);
                Interlocked.Increment(ref ok);
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Foto in blocco " + i.Path);
                i.Result = L.T("Errore: ") + ex.Message;
            }
            Dispatcher.InvokeAsync(() =>
            {
                i.Refresh();
                _progress.Value++;
            });
        }));
        _progress.Visibility = Visibility.Collapsed;
        _go.IsEnabled = true;
        _summary.Text = L.T($"{ok} di {items.Count} foto elaborate · da {Util.HumanSize(before)} a {Util.HumanSize(after)}");
        var first = items.FirstOrDefault(i => i.Output != null)?.Output;
        if (first != null && Dlg.Confirm(_summary.Text + "\n\n" + L.T("Aprire la cartella con le foto elaborate?"), AppInfo.Name, L.T("Apri cartella"), L.T("Chiudi")))
            Util.Reveal(first);
    }
}
