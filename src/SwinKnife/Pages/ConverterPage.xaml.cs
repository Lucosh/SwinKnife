using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

public partial class ConverterPage : UserControl, IToolPage
{
    public sealed record FileItem(string Path, string Name, string Info, SymbolRegular Icon);
    private sealed record TargetItem(string Key, string Label);

    private readonly MainWindow _main;
    private readonly ObservableCollection<FileItem> _files = new();
    private readonly Dictionary<string, FrameworkElement> _optionRows = new();
    private CancellationTokenSource? _cts;
    private string? _lastOutput;

    // controlli delle opzioni
    private readonly Slider _quality = new() { Minimum = 30, Maximum = 100, Value = 90, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _qualityLabel = Ui.Hint("90");
    private readonly ComboBox _resize = new();
    private readonly CheckBox _keepExif = new() { Content = L.T("Mantieni i dati EXIF (data, fotocamera, GPS)"), IsChecked = true };
    private readonly CheckBox _merge = new() { Content = L.T("Unisci tutte le immagini in un unico PDF") };
    private readonly ComboBox _dpi = new();
    private readonly ComboBox _engine = new();
    private readonly ComboBox _csvSep = new();
    private readonly ComboBox _bitrate = new();
    private readonly ComboBox _height = new();
    private readonly ComboBox _crf = new();
    private readonly ComboBox _gifWidth = new();

    public ConverterPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        FileList.ItemsSource = _files;
        FileList.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f) AddFiles(f);
            e.Handled = true;
        };
        OutDirBox.Text = Settings.Get("convert.outdir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        BuildOptions();
        Refresh();
    }

    // ------------------------------------------------------------------ opzioni
    private static void Fill(ComboBox c, int selected, params (string label, object? value)[] items)
    {
        foreach (var (label, value) in items) c.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        c.SelectedIndex = selected;
    }

    private static T? Val<T>(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: T v } ? v : default;

    private void AddOption(string key, string? label, FrameworkElement control)
    {
        FrameworkElement row;
        if (label == null)
        {
            row = control;
            control.Margin = new Thickness(0, 4, 0, 8);
        }
        else
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = Ui.Label(label);
            g.Children.Add(l);
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            row = g;
        }
        _optionRows[key] = row;
        OptionsPanel.Children.Add(row);
    }

    private void BuildOptions()
    {
        _quality.ValueChanged += (_, e) => _qualityLabel.Text = ((int)e.NewValue).ToString();
        var q = new DockPanel();
        DockPanel.SetDock(_qualityLabel, Dock.Right);
        _qualityLabel.Width = 30;
        _qualityLabel.TextAlignment = TextAlignment.Right;
        q.Children.Add(_qualityLabel);
        q.Children.Add(_quality);
        AddOption("quality", L.T("Qualità"), q);
        Fill(_resize, 0, (L.T("Dimensione originale"), null), ("75%", ("pct", 75)), ("50%", ("pct", 50)), ("25%", ("pct", 25)),
            (L.T("Lato max 3840 px"), ("max", 3840)), (L.T("Lato max 1920 px"), ("max", 1920)), (L.T("Lato max 1280 px"), ("max", 1280)),
            (L.T("Lato max 800 px"), ("max", 800)));
        AddOption("resize", L.T("Ridimensiona"), _resize);
        AddOption("exif", null, _keepExif);
        AddOption("merge", null, _merge);
        Fill(_dpi, 1, (L.T("72 DPI (schermo)"), 72), ("150 DPI", 150), ("200 DPI", 200), (L.T("300 DPI (stampa)"), 300));
        AddOption("dpi", L.T("Risoluzione"), _dpi);
        Fill(_engine, OfficeSession.WordAvailable ? 0 : 1, (L.T("Microsoft Word (impaginazione e immagini)"), true), (L.T("Solo testo (senza Word)"), false));
        AddOption("engine", L.T("Metodo"), _engine);
        Fill(_csvSep, 0, (L.T("Punto e virgola ( ; ) – Excel italiano"), ';'), (L.T("Virgola ( , )"), ','), (L.T("Tabulazione"), '\t'));
        AddOption("csv", L.T("Separatore"), _csvSep);
        Fill(_bitrate, 1, ("128 kbps", "128k"), ("192 kbps", "192k"), ("256 kbps", "256k"), ("320 kbps", "320k"));
        AddOption("bitrate", L.T("Qualità audio"), _bitrate);
        Fill(_height, 0, (L.T("Originale"), null), ("2160p (4K)", 2160), ("1080p", 1080), ("720p", 720), ("480p", 480));
        AddOption("height", L.T("Risoluzione"), _height);
        Fill(_crf, 1, (L.T("Alta (file più grande)"), 19), (L.T("Bilanciata"), 23), (L.T("Compatta"), 28));
        AddOption("crf", L.T("Qualità video"), _crf);
        Fill(_gifWidth, 1, ("320 px", 320), ("480 px", 480), ("640 px", 640), ("800 px", 800));
        AddOption("gif", L.T("Larghezza GIF"), _gifWidth);
    }

    private ConvertOptions Options() => new()
    {
        Quality = (uint)_quality.Value,
        Resize = Val<(string, int)?>(_resize),
        KeepExif = _keepExif.IsChecked == true,
        MergePdf = _merge.IsChecked == true,
        Dpi = Val<int>(_dpi),
        PdfToWordWithWord = Val<bool>(_engine),
        CsvDelimiter = Val<char>(_csvSep),
        AudioBitrate = Val<string>(_bitrate) ?? "192k",
        VideoHeight = Val<int?>(_height),
        Crf = Val<int>(_crf),
        GifWidth = Val<int>(_gifWidth),
    };

    // ------------------------------------------------------------------ file
    public bool Accepts(string path) => Converter.TargetsFor(path).Count > 0 || Directory.Exists(path);

    public void OpenFile(string path) => AddFiles([path]);

    public void AddFiles(IReadOnlyList<string> paths)
    {
        var skipped = new List<string>();
        foreach (var p0 in paths)
        {
            if (Directory.Exists(p0))
            {
                AddFiles(Directory.EnumerateFiles(p0, "*", SearchOption.AllDirectories).Take(5000).ToList());
                continue;
            }
            var p = Path.GetFullPath(p0);
            if (_files.Any(f => f.Path == p)) continue;
            if (Converter.TargetsFor(p).Count == 0)
            {
                skipped.Add(Path.GetFileName(p));
                continue;
            }
            var kind = Formats.KindOf(p);
            var icon = kind switch
            {
                FileKind.Image or FileKind.Svg or FileKind.Raw => SymbolRegular.Image24,
                FileKind.Pdf => SymbolRegular.DocumentPdf24,
                FileKind.Excel or FileKind.Table => SymbolRegular.Table24,
                FileKind.PowerPoint => SymbolRegular.SlideText24,
                FileKind.Audio => SymbolRegular.MusicNote224,
                FileKind.Video => SymbolRegular.Video24,
                FileKind.Json or FileKind.Html => SymbolRegular.Code24,
                _ => SymbolRegular.DocumentText24,
            };
            _files.Add(new FileItem(p, Path.GetFileName(p), Util.HumanSize(new FileInfo(p).Length), icon));
        }
        if (skipped.Count > 0) MainWindow.Notify(L.T("Formato non convertibile: ") + string.Join(", ", skipped.Take(5)), 8);
        Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddFiles(Dlg.OpenFiles(L.T("Scegli i file da convertire"), key: "convert"));

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in FileList.SelectedItems.Cast<FileItem>().ToList()) _files.Remove(f);
        Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        Refresh();
    }

    private void PickOut_Click(object sender, RoutedEventArgs e)
    {
        var d = Dlg.PickFolder(L.T("Cartella di destinazione"), "convert_out");
        if (d == null) return;
        OutDirBox.Text = d;
        OtherDir.IsChecked = true;
    }

    // ------------------------------------------------------------------ formati
    private void Refresh()
    {
        DropHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var prev = (TargetCombo.SelectedItem as TargetItem)?.Key;
        List<string>? common = null;
        foreach (var f in _files)
        {
            var t = Converter.TargetsFor(f.Path);
            common = common == null ? t : common.Intersect(t).ToList();
        }
        var items = (common ?? []).Select(k => new TargetItem(k, Converter.Labels.GetValueOrDefault(k, k.ToUpperInvariant()))).ToList();
        TargetCombo.ItemsSource = items;
        TargetCombo.SelectedItem = items.FirstOrDefault(i => i.Key == prev) ?? items.FirstOrDefault();
        NoTargetText.Text = _files.Count == 0 ? L.T("Aggiungi dei file per vedere i formati disponibili.")
            : items.Count == 0 ? L.T("I file scelti sono di tipi diversi e non hanno formati di arrivo in comune: convertili separatamente.") : "";
        NoTargetText.Visibility = NoTargetText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoBtn.IsEnabled = items.Count > 0 && _cts == null;
        UpdateOptions();
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e) => UpdateOptions();

    private void UpdateOptions()
    {
        var target = (TargetCombo.SelectedItem as TargetItem)?.Key;
        var kinds = _files.Select(f => Formats.KindOf(f.Path)).ToHashSet();
        var img = kinds.Any(Formats.IsImageLike);
        var media = kinds.Overlaps([FileKind.Audio, FileKind.Video]);
        var video = kinds.Contains(FileKind.Video);
        var visible = new Dictionary<string, bool>
        {
            ["quality"] = img && target != null && ImageIO.IsLossy(target),
            ["resize"] = img && target != "pdf",
            ["exif"] = img && target is "jpg" or "png" or "webp" or "avif" or "jxl" or "heic",
            ["merge"] = img && target == "pdf" && _files.Count > 1,
            ["dpi"] = kinds.Overlaps([FileKind.Pdf, FileKind.PowerPoint]) && target is "png" or "jpg",
            ["engine"] = kinds.Contains(FileKind.Pdf) && target == "docx",
            ["csv"] = target == "csv",
            ["bitrate"] = media && target is "mp3" or "m4a" or "ogg" or "opus" or "mp4" or "mkv" or "mov" or "avi",
            ["height"] = video && target is "mp4" or "mkv" or "mov" or "avi" or "webm",
            ["crf"] = video && target is "mp4" or "mkv" or "mov" or "webm",
            ["gif"] = video && target == "gif",
        };
        foreach (var (k, v) in visible) _optionRows[k].Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        OptionsTitle.Visibility = visible.Values.Any(v => v) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ esecuzione
    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        var target = (TargetCombo.SelectedItem as TargetItem)?.Key;
        if (target == null || _files.Count == 0) return;
        var files = _files.Select(f => f.Path).ToList();
        string? outDir = null;
        if (OtherDir.IsChecked == true)
        {
            outDir = OutDirBox.Text.Trim();
            if (outDir.Length == 0)
            {
                Dlg.Error(L.T("Indica la cartella di destinazione."));
                return;
            }
            Directory.CreateDirectory(outDir);
            Settings.Set("convert.outdir", outDir);
        }
        var opt = Options();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        try
        {
            if (Converter.NeedsFfmpeg(files) && Ffmpeg.Find() == null)
            {
                if (!Dlg.Confirm(L.T("Per convertire audio e video serve FFmpeg (circa 90 MB, gratuito e open source).\nVuoi scaricarlo adesso?")))
                    return;
                var dl = new Progress<(double f, string m)>(p => ReportProgress(p.f, p.m));
                await Ffmpeg.DownloadAsync(dl, _cts.Token);
            }
            var token = _cts.Token;
            var result = await Sta.Run(() => Converter.RunBatch(files, target, outDir, opt,
                (f, m) => Dispatcher.InvokeAsync(() => ReportProgress(f, m)), token));
            var lines = new List<string>();
            if (result.Outputs.Count > 0) lines.Add(L.T($"✔ {result.Outputs.Count} file creati."));
            lines.AddRange(result.Errors.Select(er => $"✖ {Path.GetFileName(er.file)}: {er.error}"));
            if (result.Cancelled) lines.Add(L.T("Conversione annullata."));
            StatusLabel.Text = lines.Count > 0 ? string.Join("\n", lines) : L.T("Nessun file convertito.");
            _lastOutput = result.Outputs.FirstOrDefault();
            OpenOutBtn.Visibility = _lastOutput != null ? Visibility.Visible : Visibility.Collapsed;
            MainWindow.Notify(result.Errors.Count == 0 ? L.T("Conversione completata") : L.T("Conversione completata con errori"));
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = L.T("Operazione annullata.");
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Conversione");
            StatusLabel.Text = L.T("Errore: ") + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void ReportProgress(double f, string msg)
    {
        Progress.IsIndeterminate = f < 0;
        if (f >= 0) Progress.Value = f * 1000;
        if (!string.IsNullOrEmpty(msg)) StatusLabel.Text = msg;
    }

    private void SetRunning(bool on)
    {
        GoBtn.IsEnabled = !on && TargetCombo.Items.Count > 0;
        CancelBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
        if (on)
        {
            OpenOutBtn.Visibility = Visibility.Collapsed;
            StatusLabel.Text = L.T("Avvio…");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusLabel.Text = L.T("Annullamento…");
    }

    private void OpenOut_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput != null) Util.Reveal(_lastOutput);
    }

    public void Shutdown() => _cts?.Cancel();
}
