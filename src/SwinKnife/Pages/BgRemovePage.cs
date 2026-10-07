using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Rimuove lo sfondo dalle foto con un modello di intelligenza artificiale che gira sul PC.</summary>
public sealed class BgRemovePage : UserControl, IToolPage
{
    private sealed class Entry
    {
        public required string Path { get; init; }
        public MagickImage? Mask { get; set; }
    }

    private readonly ListBox _files = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly Image _before = new() { Stretch = Stretch.Uniform };
    private readonly Image _after = new() { Stretch = Stretch.Uniform };
    private readonly ComboBox _mode = new() { Width = 200 };
    private readonly Border _colorSwatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = Ui.Hint();
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _save, _saveAll;
    private readonly FrameworkElement _empty;
    private readonly Grid _preview;
    private MagickColor _color = MagickColors.White;
    private Entry? _current;
    private int _job;

    public BgRemovePage(MainWindow main)
    {
        foreach (var m in new[] { L.T("Trasparente (PNG)"), L.T("Colore pieno"), L.T("Sfondo sfocato") }) _mode.Items.Add(m);
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) =>
        {
            _colorSwatch.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _ = Render();
        };
        _colorSwatch.Background = Brushes.White;
        _colorSwatch.BorderBrush = Ui.Res("CardStrokeColorDefaultBrush");
        _colorSwatch.Visibility = Visibility.Collapsed;
        _colorSwatch.Cursor = System.Windows.Input.Cursors.Hand;
        _colorSwatch.ToolTip = L.T("Scegli il colore dello sfondo");
        _colorSwatch.MouseLeftButtonUp += (_, _) => PickColor();
        _files.SelectionChanged += async (_, _) =>
        {
            if (_files.SelectedItem is ListBoxItem { Tag: Entry e }) await Show(e);
        };

        _save = Ui.Btn(L.T("Salva…"), SymbolRegular.Save24, async (_, _) => await SaveCurrent(), primary: true);
        _saveAll = Ui.Btn(L.T("Salva tutte"), SymbolRegular.SaveMultiple24, async (_, _) => await SaveAll());
        var toolbar = Ui.Row(
            Ui.Btn(L.T("Aggiungi foto…"), SymbolRegular.ImageAdd24, (_, _) =>
                AddFiles(Dlg.OpenFiles(L.T("Scegli le foto"), L.T("Immagini|*.jpg;*.jpeg;*.png;*.heic;*.webp;*.avif;*.bmp;*.tif;*.tiff|") + Dlg.AllFiles, "bg"))),
            Ui.Separator(), Ui.Label(L.T("Sfondo")), new Border { Width = 8 }, _mode, _colorSwatch, Ui.Separator(), _save, _saveAll);
        toolbar.Margin = new Thickness(0, 0, 0, 10);

        // anteprima prima/dopo, il "dopo" su scacchiera per vedere la trasparenza
        _preview = new Grid();
        _preview.ColumnDefinitions.Add(new ColumnDefinition());
        _preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _preview.ColumnDefinitions.Add(new ColumnDefinition());
        _preview.Children.Add(Frame(L.T("Prima"), _before, false));
        var afterFrame = Frame(L.T("Dopo"), _after, true);
        Grid.SetColumn(afterFrame, 2);
        _preview.Children.Add(afterFrame);

        _empty = Ui.Placeholder(SymbolRegular.Wand24, L.T("Trascina qui una o più foto: il soggetto viene ritagliato in automatico. La prima volta si scarica il modello di intelligenza artificiale (44 MB), poi funziona anche senza Internet."), out _);
        var left = new Border { Style = (Style)Application.Current.FindResource("Panel"), Padding = new Thickness(6), Child = _files, Width = 240, Margin = new Thickness(0, 0, 12, 0) };
        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        var host = new Grid();
        host.Children.Add(_preview);
        host.Children.Add(_empty);
        body.Children.Add(host);

        var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        var header = Ui.Header(L.T("Rimuovi sfondo"), L.T("Ritaglia il soggetto delle foto (persone, animali, oggetti) e metti uno sfondo trasparente, colorato o sfocato. Tutto sul PC: le foto non vengono inviate a nessuno."));
        foreach (var e in new UIElement[] { header, toolbar }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(_status);
        bottom.Children.Add(_busy);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(body);
        Content = dock;
        UpdateState();
    }

    private static Border Frame(string title, Image img, bool checker)
    {
        var g = new Grid();
        if (checker)
        {
            var tile = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
                new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }))
            { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
            g.Children.Add(new Border { Background = Brushes.White });
            g.Children.Add(new Border { Background = tile });
        }
        g.Children.Add(img);
        var label = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 6), Foreground = Ui.Res("TextFillColorSecondaryBrush") };
        var dp = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        dp.Children.Add(label);
        dp.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = g, Background = Ui.Res("ControlFillColorDefaultBrush") });
        return new Border { Child = dp };
    }

    public bool Accepts(string path) => Formats.IsImageLike(Formats.KindOf(path));
    public void OpenFile(string path) => AddFiles([path]);
    public void AddFiles(IReadOnlyList<string> paths) => AddFiles(paths.ToArray());

    private void AddFiles(string[] paths)
    {
        ListBoxItem? first = null;
        foreach (var p in paths.Where(p => Formats.IsImageLike(Formats.KindOf(p))))
        {
            var item = new ListBoxItem { Tag = new Entry { Path = p }, Content = Ui.Row(new Image { Source = ShellIcons.For(p), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }, Ui.Label(Path.GetFileName(p))) };
            _files.Items.Add(item);
            first ??= item;
        }
        UpdateState();
        if (first != null) _files.SelectedItem = first;
    }

    private void UpdateState()
    {
        var any = _files.Items.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _preview.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _save.IsEnabled = _saveAll.IsEnabled = any;
    }

    private async Task<bool> EnsureModel()
    {
        if (BgRemoval.ModelReady) return true;
        if (!Dlg.Confirm(L.T("Per rimuovere lo sfondo serve il modello di intelligenza artificiale (44 MB, da scaricare una volta sola). Scaricarlo adesso?"), AppInfo.Name, L.T("Scarica"), L.T("Annulla")))
            return false;
        _busy.Visibility = Visibility.Visible;
        try
        {
            await BgRemoval.DownloadModelAsync(new Progress<double>(p => _status.Text = L.T($"Download del modello: {p * 100:0} %")), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Download non riuscito:\n") + ex.Message);
            return false;
        }
        finally
        {
            _busy.Visibility = Visibility.Collapsed;
        }
    }

    private async Task Show(Entry e)
    {
        _current = e;
        var job = ++_job;
        try
        {
            var (bmp, _) = await Task.Run(() => ImageIO.LoadForDisplay(e.Path, 1600));
            if (job != _job) return;
            _before.Source = bmp;
            _after.Source = null;
            if (!await EnsureModel()) return;
            if (e.Mask == null)
            {
                _busy.Visibility = Visibility.Visible;
                _status.Text = L.T("Riconosco il soggetto…");
                e.Mask = await Task.Run(() =>
                {
                    using var img = ImageIO.Load(e.Path);
                    return BgRemoval.Mask(img);
                });
            }
            await Render();
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Rimozione sfondo " + e.Path);
            _status.Text = L.T("Errore: ") + ex.Message;
        }
        finally
        {
            if (job == _job) _busy.Visibility = Visibility.Collapsed;
        }
    }

    private BgMode Mode => (BgMode)Math.Max(0, _mode.SelectedIndex);

    private async Task Render()
    {
        if (_current is not { Mask: { } mask } e) return;
        var job = ++_job;
        var mode = Mode;
        var color = _color;
        _busy.Visibility = Visibility.Visible;
        var bmp = await Task.Run(() =>
        {
            using var img = ImageIO.Load(e.Path);
            // l'anteprima si calcola su una copia ridotta
            var s = Math.Min(1.0, 1400.0 / Math.Max(img.Width, img.Height));
            using var m = (MagickImage)mask.Clone();
            if (s < 1)
            {
                img.Resize((uint)(img.Width * s), (uint)(img.Height * s));
                m.Resize(new MagickGeometry(img.Width, img.Height) { IgnoreAspectRatio = true });
            }
            using var result = BgRemoval.Apply(img, m, mode, color);
            return ImageIO.ToBitmapSource(result);
        });
        if (job != _job) return;
        _after.Source = bmp;
        _busy.Visibility = Visibility.Collapsed;
        _status.Text = L.T("Soggetto ritagliato. Salva per ottenere l'immagine a piena risoluzione.");
    }

    private void PickColor()
    {
        var menu = new ContextMenu();
        void Add(string name, Color c)
        {
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = name,
                Icon = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
            };
            mi.Click += (_, _) => SetColor(c);
            menu.Items.Add(mi);
        }
        Add(L.T("Bianco"), Colors.White);
        Add(L.T("Nero"), Colors.Black);
        Add(L.T("Grigio chiaro"), Color.FromRgb(0xEE, 0xEE, 0xEE));
        Add(L.T("Azzurro (foto tessera)"), Color.FromRgb(0xD6, 0xE9, 0xF8));
        Add(L.T("Blu"), Color.FromRgb(0x1F, 0x4E, 0x9C));
        Add(L.T("Verde"), Color.FromRgb(0x2E, 0x9E, 0x5B));
        Add(L.T("Rosso"), Color.FromRgb(0xE5, 0x48, 0x4D));
        var custom = new System.Windows.Controls.MenuItem { Header = L.T("Personalizzato…") };
        custom.Click += (_, _) =>
        {
            if (Dlg.Prompt(L.T("Colore dello sfondo"), L.T("Codice colore (es. #FFCC00):"), $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}") is not { } hex) return;
            try { SetColor((Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex)); }
            catch { Dlg.Error(L.T("Codice colore non valido.")); }
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(custom);
        menu.PlacementTarget = _colorSwatch;
        menu.IsOpen = true;
    }

    private void SetColor(Color c)
    {
        _color = MagickColor.FromRgb(c.R, c.G, c.B);
        _colorSwatch.Background = new SolidColorBrush(c);
        _ = Render();
    }

    private string Ext => Mode == BgMode.Transparent ? ".png" : ".jpg";

    private async Task<string> SaveTo(Entry e, string dst)
    {
        var mode = Mode;
        var color = _color;
        await Task.Run(() =>
        {
            using var img = ImageIO.Load(e.Path);
            e.Mask ??= BgRemoval.Mask(img);
            using var result = BgRemoval.Apply(img, e.Mask, mode, color);
            ImageIO.Save(result, dst, mode == BgMode.Transparent ? "png" : "jpg", 92);
        });
        return dst;
    }

    private async Task SaveCurrent()
    {
        if (_current == null || !await EnsureModel()) return;
        var name = Path.GetFileNameWithoutExtension(_current.Path) + L.T("_senza_sfondo") + Ext;
        var dst = Dlg.SaveFile(L.T("Salva immagine"), name, Ext == ".png" ? "PNG|*.png" : "JPG|*.jpg", "bg.save");
        if (dst == null) return;
        _busy.Visibility = Visibility.Visible;
        try
        {
            await SaveTo(_current, dst);
            MainWindow.Notify(L.T($"Salvata: {Path.GetFileName(dst)}"));
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
        finally { _busy.Visibility = Visibility.Collapsed; }
    }

    private async Task SaveAll()
    {
        if (!await EnsureModel()) return;
        var dir = Dlg.PickFolder(L.T("Dove salvare le immagini senza sfondo"), "bg.dir");
        if (dir == null) return;
        _busy.Visibility = Visibility.Visible;
        var entries = _files.Items.OfType<ListBoxItem>().Select(i => (Entry)i.Tag).ToList();
        var done = 0;
        foreach (var e in entries)
        {
            _status.Text = L.T($"Salvataggio {done + 1} di {entries.Count}…");
            try
            {
                await SaveTo(e, Util.UniquePath(Path.Combine(dir, Path.GetFileNameWithoutExtension(e.Path) + L.T("_senza_sfondo") + Ext)));
                done++;
            }
            catch (Exception ex) { AppInfo.Log(ex, "Salva senza sfondo " + e.Path); }
        }
        _busy.Visibility = Visibility.Collapsed;
        _status.Text = L.T($"{done} immagini salvate in {dir}");
        Util.OpenFolder(dir);
    }
}
