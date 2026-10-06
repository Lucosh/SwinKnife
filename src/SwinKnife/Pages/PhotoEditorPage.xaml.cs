using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageMagick;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

public partial class PhotoEditorPage : UserControl, IToolPage
{
    public sealed class FilterItem(string key, string label) : INotifyPropertyChanged
    {
        private BitmapSource? _image;
        public string Key { get; } = key;
        public string Label { get; } = label;
        public BitmapSource? Image
        {
            get => _image;
            set
            {
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private const int PreviewMax = 1500;
    private const int FastMax = 760;

    private static readonly Dictionary<string, SymbolRegular> AdjIcons = new()
    {
        ["exposure"] = SymbolRegular.BrightnessHigh24, ["brilliance"] = SymbolRegular.Sparkle24, ["highlights"] = SymbolRegular.WeatherSunny24,
        ["shadows"] = SymbolRegular.DarkTheme24, ["contrast"] = SymbolRegular.CircleHalfFill24, ["brightness"] = SymbolRegular.BrightnessHigh24,
        ["blackpoint"] = SymbolRegular.CircleHalfFill24, ["saturation"] = SymbolRegular.Color24, ["vibrance"] = SymbolRegular.Drop24,
        ["warmth"] = SymbolRegular.WeatherSunny24, ["tint"] = SymbolRegular.Color24, ["sharpness"] = SymbolRegular.Pen24,
        ["definition"] = SymbolRegular.Diamond24, ["noise"] = SymbolRegular.Eraser24, ["vignette"] = SymbolRegular.Eye24,
    };

    private static readonly (string label, double? ratio)[] Aspects =
    [
        (L.T("Libero"), null), (L.T("Originale"), -1), (L.T("Quadrato"), 1), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0),
        ("4:3", 4 / 3.0), ("3:4", 3 / 4.0), ("3:2", 3 / 2.0), ("2:3", 2 / 3.0), ("5:4", 5 / 4.0),
    ];

    private readonly MainWindow _main;
    private string? _path;
    private Bgra? _original, _preview, _small;
    private IExifProfile? _exif;
    private EditState _state = new();
    private EditState _saved = new();
    private readonly List<EditState> _history = new() { new EditState() };
    private int _hpos;
    private string _selected = "exposure";
    private bool _rendering;
    private bool? _pending;
    private bool _comparing;
    private bool _sliding;
    private bool _syncing;
    private readonly Dictionary<string, (RadioButton button, TextBlock value)> _adjButtons = new();
    private readonly ObservableCollection<FilterItem> _filters = new();
    private readonly List<RadioButton> _aspectButtons = new();

    public PhotoEditorPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        foreach (var (key, label, _, _) in PhotoProcessor.Adjustments)
        {
            var value = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Controls.Ui.Res("TextFillColorSecondaryBrush") };
            var content = new StackPanel { Width = 86 };
            content.Children.Add(new SymbolIcon { Symbol = AdjIcons[key], FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(value);
            var rb = new RadioButton { Style = (Style)FindResource("ToolToggle"), Content = content, GroupName = "adj", IsChecked = key == _selected };
            var k = key;
            rb.Checked += (_, _) => SelectAdj(k);
            _adjButtons[key] = (rb, value);
            AdjButtons.Children.Add(rb);
        }
        var auto = new Wpf.Ui.Controls.Button
        {
            Content = new StackPanel { Children = { new SymbolIcon { Symbol = SymbolRegular.Wand24, FontSize = 22, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD6, 0x0A)) }, new TextBlock { Text = L.T("Auto"), Margin = new Thickness(0, 4, 0, 0) } } },
            Width = 76, Margin = new Thickness(0, 0, 8, 0), Appearance = ControlAppearance.Transparent, ToolTip = L.T("Miglioramento automatico"),
        };
        auto.Click += (_, _) => AutoAdjust();
        AdjButtons.Children.Insert(0, auto);

        AdjSlider.ValueChanged += AdjSlider_Changed;
        AdjSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _sliding = true));
        AdjSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _sliding = false;
            CommitAndRender();
        }));
        FilterAmount.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _state.FilterAmount = e.NewValue / 100;
            RequestRender(_sliding);
            if (!_sliding) Commit();
        };
        FilterAmount.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _sliding = true));
        FilterAmount.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _sliding = false;
            CommitAndRender();
        }));
        StraightenSlider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            _state.Straighten = Math.Round(e.NewValue, 1);
            StraightenLabel.Text = $"{_state.Straighten:0.0}°";
            RequestRender(_sliding);
            if (!_sliding) Commit();
        };
        StraightenSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _sliding = true));
        StraightenSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _sliding = false;
            CommitAndRender();
        }));

        foreach (var (key, label) in PhotoProcessor.Filters) _filters.Add(new FilterItem(key, label));
        FilterList.ItemsSource = _filters;
        FilterList.SelectionChanged += (_, _) =>
        {
            if (_syncing || FilterList.SelectedItem is not FilterItem f) return;
            _state.Filter = f.Key;
            if (f.Key != "none" && _state.FilterAmount <= 0) _state.FilterAmount = 1;
            Commit();
            SyncControls();
            RequestRender(false);
        };

        foreach (var (label, ratio) in Aspects)
        {
            var rb = new RadioButton { Content = label, Style = (Style)FindResource("SegmentButton"), GroupName = "aspect", IsChecked = ratio == null };
            var r = ratio;
            rb.Click += (_, _) => SetAspect(r);
            _aspectButtons.Add(rb);
            AspectButtons.Children.Add(rb);
        }
        Canvas.CropCommitted += () =>
        {
            _state.Crop = Canvas.Crop;
            Commit();
        };
        CanvasHost.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) OpenFile(f[0]);
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (ctrl && e.Key == Key.Z) Undo();
            else if (ctrl && e.Key == Key.Y) Redo();
            else if (ctrl && e.Key == Key.S) Save();
            else return;
            e.Handled = true;
        };
        SyncControls();
    }

    // ------------------------------------------------------------------ IToolPage
    public bool Accepts(string path) => Formats.KindOf(path) is FileKind.Image or FileKind.Raw;

    public bool CanClose() => _original == null || _state.SameAs(_saved) || Dlg.Confirm(L.T("La foto ha modifiche non salvate. Vuoi uscire comunque?"));

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var p = Dlg.OpenFile(L.T("Apri foto"), Formats.Filter(L.T("Immagini"), FileKind.Image, FileKind.Raw) + L.T("|Tutti i file|*.*"), "photo");
        if (p != null) OpenFile(p);
    }

    public async void OpenFile(string path)
    {
        if (!CanClose()) return;
        NameText.Text = L.T("Caricamento…");
        try
        {
            var (orig, exif, preview, small) = await Task.Run(() =>
            {
                using var img = ImageIO.Load(path);
                var ex = img.GetExifProfile();
                var o = new Bgra((int)img.Width, (int)img.Height, ImageIO.ToBgra(img));
                var p = PhotoProcessor.Downscale(o, PreviewMax);
                return (o, ex, p, PhotoProcessor.Downscale(p, FastMax));
            });
            _path = path;
            _original = orig;
            _exif = exif;
            _preview = preview;
            _small = small;
            _state = new EditState();
            _saved = _state.Clone();
            _history.Clear();
            _history.Add(_state.Clone());
            _hpos = 0;
            NameText.Text = L.T($"{Path.GetFileName(path)}  ·  {orig.W} × {orig.H} px");
            SyncControls();
            RequestRender(false);
            UpdateFilterThumbs();
        }
        catch (Exception ex)
        {
            NameText.Text = "";
            Dlg.Error(L.T("Impossibile aprire la foto:\n") + ex.Message);
        }
    }

    // ------------------------------------------------------------------ rendering
    private void RequestRender(bool fast)
    {
        if (_preview == null) return;
        _pending = _pending == null ? fast : _pending.Value && fast;
        if (!_rendering) StartRender();
    }

    private async void StartRender()
    {
        var fast = _pending ?? false;
        _pending = null;
        _rendering = true;
        var src = fast ? _small! : _preview!;
        var st = _state.Clone();
        var cropMode = Canvas.CropMode;
        try
        {
            var bmp = await Task.Run(() => PhotoProcessor.Render(PhotoProcessor.ApplyGeometry(src, st, !cropMode), st).ToBitmap());
            if (!_comparing) Canvas.Image = bmp;
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Anteprima foto");
        }
        finally
        {
            _rendering = false;
            if (_pending != null) StartRender();
        }
    }

    private async void UpdateFilterThumbs()
    {
        if (_small == null) return;
        var st = _state.Clone();
        var small = _small;
        var images = await Task.Run(() =>
        {
            var g = PhotoProcessor.ApplyGeometry(small, st);
            var side = Math.Min(g.W, g.H);
            var crop = new Rect((g.W - side) / 2.0 / g.W, (g.H - side) / 2.0 / g.H, side / (double)g.W, side / (double)g.H);
            var sq = PhotoProcessor.Downscale(PhotoProcessor.ApplyGeometry(g, new EditState { Crop = crop }), 96);
            var list = new List<BitmapSource>();
            foreach (var (key, _) in PhotoProcessor.Filters)
            {
                var s = st.Clone();
                s.Filter = key;
                s.FilterAmount = 1;
                foreach (var k in new[] { "sharpness", "definition", "noise" }) s.Adj.Remove(k);
                list.Add(PhotoProcessor.Render(sq, s).ToBitmap());
            }
            return list;
        });
        for (var i = 0; i < images.Count && i < _filters.Count; i++) _filters[i].Image = images[i];
    }

    private void CommitAndRender()
    {
        Commit();
        RequestRender(false);
        if (ModeFilters.IsChecked != true) UpdateFilterThumbs();
    }

    private void Compare_Down(object sender, MouseButtonEventArgs e)
    {
        if (_preview == null) return;
        _comparing = true;
        Canvas.Image = _preview.ToBitmap();
    }

    private void Compare_Up(object sender, RoutedEventArgs e)
    {
        if (!_comparing) return;
        _comparing = false;
        RequestRender(false);
    }

    // ------------------------------------------------------------------ cronologia
    private void Commit()
    {
        if (_original == null || _state.SameAs(_history[_hpos])) return;
        _history.RemoveRange(_hpos + 1, _history.Count - _hpos - 1);
        _history.Add(_state.Clone());
        _hpos++;
        SyncControls();
    }

    private void Undo()
    {
        if (_hpos == 0) return;
        _hpos--;
        AfterHistory();
    }

    private void Redo()
    {
        if (_hpos >= _history.Count - 1) return;
        _hpos++;
        AfterHistory();
    }

    private void AfterHistory()
    {
        _state = _history[_hpos].Clone();
        SyncControls();
        RequestRender(false);
        UpdateFilterThumbs();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        _state = new EditState();
        Commit();
        SyncControls();
        RequestRender(false);
        UpdateFilterThumbs();
    }

    private void SyncControls()
    {
        _syncing = true;
        var has = _original != null;
        foreach (var b in new UIElement[] { CompareBtn, ResetBtn, SaveBtn, Panels }) b.IsEnabled = has;
        UndoBtn.IsEnabled = _hpos > 0;
        RedoBtn.IsEnabled = _hpos < _history.Count - 1;
        foreach (var (key, (_, value)) in _adjButtons)
        {
            var v = _state.A(key);
            value.Text = Math.Abs(v) > 0.004 ? $"{Math.Round(v * 100):+0;-0}" : " ";
        }
        SelectAdj(_selected, false);
        FilterList.SelectedItem = _filters.FirstOrDefault(f => f.Key == _state.Filter);
        FilterAmount.Value = _state.FilterAmount * 100;
        FilterAmount.IsEnabled = _state.Filter != "none";
        StraightenSlider.Value = _state.Straighten;
        StraightenLabel.Text = $"{_state.Straighten:0.0}°";
        Canvas.Crop = _state.Crop;
        Canvas.InvalidateVisual();
        _syncing = false;
    }

    // ------------------------------------------------------------------ regolazioni
    private void SelectAdj(string key, bool fromUser = true)
    {
        _selected = key;
        var meta = PhotoProcessor.Adjustments.First(a => a.key == key);
        var wasSyncing = _syncing;
        _syncing = true;
        AdjName.Text = meta.label;
        AdjSlider.Minimum = meta.min * 100;
        AdjSlider.Maximum = meta.max * 100;
        AdjSlider.Value = Math.Round(_state.A(key) * 100);
        AdjValue.Text = $"{AdjSlider.Value:+0;-0;0}";
        _adjButtons[key].button.IsChecked = true;
        _syncing = wasSyncing;
    }

    private void AdjSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        var v = Math.Round(e.NewValue);
        _state.Adj[_selected] = v / 100;
        AdjValue.Text = $"{v:+0;-0;0}";
        _adjButtons[_selected].value.Text = Math.Abs(v) > 0 ? $"{v:+0;-0}" : " ";
        RequestRender(_sliding);
        if (!_sliding) Commit();
    }

    private void ResetAdj_Click(object sender, RoutedEventArgs e)
    {
        AdjSlider.Value = 0;
        CommitAndRender();
    }

    private void AutoAdjust()
    {
        if (_preview == null) return;
        var adj = PhotoProcessor.Auto(PhotoProcessor.ApplyGeometry(_preview, _state));
        foreach (var k in new[] { "exposure", "brilliance", "highlights", "shadows", "contrast", "brightness", "blackpoint", "vibrance" })
            _state.Adj.Remove(k);
        foreach (var (k, v) in adj) _state.Adj[k] = v;
        Commit();
        SyncControls();
        RequestRender(false);
        MainWindow.Notify(L.T("Regolazioni automatiche applicate"));
    }

    // ------------------------------------------------------------------ modalità e ritaglio
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (AdjustPanel == null) return;
        AdjustPanel.Visibility = ModeAdjust.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FilterPanel.Visibility = ModeFilters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CropPanel.Visibility = ModeCrop.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var crop = ModeCrop.IsChecked == true;
        if (Canvas.CropMode != crop)
        {
            Canvas.CropMode = crop;
            Canvas.Crop = _state.Crop;
            RequestRender(false);
        }
        if (ModeFilters.IsChecked == true) UpdateFilterThumbs();
    }

    private (double w, double h) GeometrySize()
    {
        if (Canvas.CropMode && Canvas.Image is { } img) return (img.PixelWidth, img.PixelHeight);
        var w = _preview!.W;
        var h = _preview.H;
        return _state.Rot90 % 2 == 1 ? (h, w) : (w, h);
    }

    private void SetAspect(double? ratio)
    {
        if (_preview == null) return;
        var (w, h) = GeometrySize();
        if (ratio == -1) ratio = w / h;
        Canvas.Aspect = ratio;
        if (ratio is not { } r) return;
        var imgAspect = w / h;
        double cw, ch;
        if (r >= imgAspect)
        {
            cw = 1;
            ch = imgAspect / r;
        }
        else
        {
            cw = r / imgAspect;
            ch = 1;
        }
        _state.Crop = new Rect((1 - cw) / 2, (1 - ch) / 2, cw, ch);
        Canvas.Crop = _state.Crop;
        Canvas.InvalidateVisual();
        Commit();
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (_preview == null) return;
        _state.Rot90 = (_state.Rot90 + 1) % 4;
        var c = _state.Crop; // il ritaglio ruota insieme alla foto (90° antiorari)
        _state.Crop = new Rect(c.Y, 1 - c.Right, c.Height, c.Width);
        if (Canvas.Aspect is { } a) Canvas.Aspect = 1 / a;
        Commit();
        SyncControls();
        RequestRender(false);
    }

    private void FlipH_Click(object sender, RoutedEventArgs e) => Flip(true);
    private void FlipV_Click(object sender, RoutedEventArgs e) => Flip(false);

    private void Flip(bool horizontal)
    {
        if (_preview == null) return;
        var c = _state.Crop;
        if (horizontal)
        {
            _state.FlipH = !_state.FlipH;
            _state.Crop = new Rect(1 - c.Right, c.Y, c.Width, c.Height);
        }
        else
        {
            _state.FlipV = !_state.FlipV;
            _state.Crop = new Rect(c.X, 1 - c.Bottom, c.Width, c.Height);
        }
        Commit();
        SyncControls();
        RequestRender(false);
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        _aspectButtons[0].IsChecked = true;
        Canvas.Aspect = null;
        _state.Crop = new Rect(0, 0, 1, 1);
        _state.Straighten = 0;
        Commit();
        SyncControls();
        RequestRender(false);
    }

    // ------------------------------------------------------------------ salvataggio
    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private async void Save()
    {
        if (_original == null || _path == null) return;
        var ext = Formats.Ext(_path);
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".avif" or ".tif" or ".tiff" or ".bmp")) ext = ".jpg";
        var suggested = Path.Combine(Path.GetDirectoryName(_path)!, $"{Path.GetFileNameWithoutExtension(_path)}_modificata{ext}");
        var dst = Dlg.SaveFile(L.T("Salva la foto modificata"), suggested,
            "JPEG|*.jpg|PNG|*.png|WEBP|*.webp|AVIF|*.avif|TIFF|*.tif|BMP|*.bmp", "photo_save");
        if (dst == null) return;
        var fmt = Formats.Ext(dst).TrimStart('.') switch { "jpeg" or "" => "jpg", "tif" => "tiff", var f => f };
        if (!ImageIO.WriteFormats.ContainsKey(fmt)) fmt = "jpg";
        var st = _state.Clone();
        var original = _original;
        var exif = _exif;
        SaveBtn.IsEnabled = false;
        NameText.Text = L.T("Salvataggio a piena risoluzione…");
        try
        {
            await Task.Run(() => SaveImage(original, st, exif, dst, fmt));
            _saved = st;
            MainWindow.Notify(L.T("Foto salvata: ") + dst);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Salvataggio non riuscito:\n") + ex.Message);
        }
        finally
        {
            SaveBtn.IsEnabled = true;
            NameText.Text = L.T($"{Path.GetFileName(_path)}  ·  {_original.W} × {_original.H} px");
        }
    }

    public static void SaveImage(Bgra original, EditState st, IExifProfile? exif, string dst, string fmt)
    {
        var result = PhotoProcessor.Render(PhotoProcessor.ApplyGeometry(original, st), st);
        using var img = ImageIO.FromBgra(result.Px, result.W, result.H);
        if (exif != null)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            exif.RemoveThumbnail();
            img.SetProfile(exif);
        }
        ImageIO.Save(img, dst, fmt, 95, true);
    }
}
