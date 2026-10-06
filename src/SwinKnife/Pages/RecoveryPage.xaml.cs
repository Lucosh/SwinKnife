using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ImageMagick;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;

namespace SwinKnife.Pages;

public partial class RecoveryPage : UserControl, IToolPage
{
    public sealed record ResultRow(int Number, FoundFile File)
    {
        public string Type => File.Ext.ToUpperInvariant();
        public string Category => File.Category;
        public long Size => File.Size;
        public string SizeText => Util.HumanSize(File.Size);
        public long Offset => File.Offset;
        public string OffsetText => Util.Number(File.Offset);
        public string State => File.Complete ? L.T("Completo") : L.T("Parziale?");
    }

    public sealed record NameRow(DeletedFile File)
    {
        public string Name => File.Name;
        public string Folder => File.Folder.Length == 0 ? "(radice)" : File.Folder;
        public long Size => File.Size;
        public string SizeText => Util.HumanSize(File.Size);
        public DateTime Modified => File.Modified ?? DateTime.MinValue;
        public string ModifiedText => File.Modified is { } d ? Util.Date(d) : "";
        public string State => File.State;
        public string Category => CategoryOf(File.Ext);
    }

    private static string CategoryOf(string ext) => Formats.KindOf("x." + ext) switch
    {
        FileKind.Image or FileKind.Raw or FileKind.Svg => L.T("Immagini"),
        FileKind.Audio => L.T("Audio"),
        FileKind.Video => L.T("Video"),
        FileKind.Archive => L.T("Archivi"),
        FileKind.Unknown or FileKind.Font => L.T("Altro"),
        _ => L.T("Documenti"),
    };

    private sealed record SourceItem(string Label, string Target);

    private static readonly HashSet<string> Previewable = ["jpg", "png", "gif", "bmp", "webp", "tif", "heic", "avif"];

    private readonly MainWindow _main;
    private readonly ObservableCollection<ResultRow> _rows = new();
    private readonly ListCollectionView _view;
    private readonly ObservableCollection<NameRow> _names = new();
    private readonly ListCollectionView _nameView;
    private readonly Dictionary<string, CheckBox> _categories = new();
    private CancellationTokenSource? _cts;
    private string? _scanTarget;
    private RawSource? _previewSource;

    public RecoveryPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _view = new ListCollectionView(_rows);
        Results.ItemsSource = _view;
        _nameView = new ListCollectionView(_names);
        NameResults.ItemsSource = _nameView;
        foreach (var (cat, desc) in Carver.Categories)
        {
            var cb = new CheckBox { Content = cat, ToolTip = desc, IsChecked = cat == L.T("Immagini") || cat == L.T("Documenti") || cat == L.T("Video"), Margin = new Thickness(0, 0, 14, 0) };
            _categories[cat] = cb;
            CategoryPanel.Children.Add(cb);
        }
        foreach (var (label, value) in new[] { (L.T("nessun limite"), 0L), ("10 KB", 10_240L), ("50 KB", 51_200L), ("1 MB", 1_048_576L) })
            MinSizeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        MinSizeCombo.SelectedIndex = 1;
        FilterCombo.Items.Add(new ComboBoxItem { Content = L.T("Tutti i tipi"), Tag = "" });
        foreach (var cat in Carver.Categories.Keys.Append(L.T("Altro"))) FilterCombo.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
        FilterCombo.SelectedIndex = 0;
        OutBox.Text = Settings.Get("recovery.out", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), L.T("File recuperati")));
        AdminBanner.Visibility = RawSource.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
        FillSources();
        UpdateButtons();
    }

    private void FillSources()
    {
        var items = RawSource.Volumes().Select(v => new SourceItem(v.Title, v.Letter)).ToList();
        if (SourceCombo.ItemsSource is List<SourceItem> old) items.InsertRange(0, old.Where(o => o.Target.Length > 2));
        SourceCombo.ItemsSource = items;
        SourceCombo.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => FillSources();

    private bool NamesMode => ModeNames.IsChecked == true;

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (NameResults == null) return;
        WhatLabel.Visibility = WhatPanel.Visibility = NamesMode ? Visibility.Collapsed : Visibility.Visible;
        NameResults.Visibility = NamesMode ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = NamesMode ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    private async Task ScanNames(SourceItem src)
    {
        _names.Clear();
        ClosePreview();
        _scanTarget = src.Target;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetRunning(true);
        Progress.IsIndeterminate = true;
        try
        {
            var files = await Task.Run(() =>
            {
                using var source = new RawSource(src.Target);
                var fs = FsRecovery.Detect(source) ?? throw new NotSupportedException(
                    L.T("File system non riconosciuto (forse il supporto è stato formattato o è danneggiato): usa la scansione profonda."));
                Dispatcher.InvokeAsync(() => StatusText.Text = L.T($"File system {fs}: ricerca dei file eliminati…"));
                return FsRecovery.Scan(source, m => Dispatcher.InvokeAsync(() => StatusText.Text = m), token);
            });
            foreach (var f in files.OrderBy(f => f.OverwrittenFraction).ThenBy(f => f.FullPath)) _names.Add(new NameRow(f));
            StatusText.Text = files.Count == 0
                ? L.T("Nessun file eliminato trovato con questo metodo: prova la scansione profonda.")
                : L.T($"Trovati {files.Count} file eliminati, {files.Count(f => f.OverwrittenFraction <= 0)} in buono stato.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("Ricerca interrotta.");
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Ricerca non riuscita:") + Environment.NewLine + ex.Message);
            StatusText.Text = "";
        }
        finally
        {
            Progress.IsIndeterminate = false;
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void Image_Click(object sender, RoutedEventArgs e)
    {
        var p = Dlg.OpenFile(L.T("Scegli un'immagine disco"), L.T("Immagini disco|*.img;*.dd;*.bin;*.raw;*.iso;*.dmg|Tutti i file|*.*"), "recovery_img");
        if (p == null) return;
        var items = ((List<SourceItem>)SourceCombo.ItemsSource).ToList();
        items.Insert(0, new SourceItem(L.T("Immagine: ") + p, p));
        SourceCombo.ItemsSource = items;
        SourceCombo.SelectedIndex = 0;
    }

    private void PickOut_Click(object sender, RoutedEventArgs e)
    {
        var d = Dlg.PickFolder(L.T("Cartella in cui salvare i file recuperati"), "recovery_out");
        if (d != null) OutBox.Text = d;
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.CanCloseAllPages()) return;
        if (RawSource.RelaunchAsAdmin("--page recovery --elevated")) Application.Current.Shutdown();
        else Dlg.Error(L.T("Non è stato possibile ottenere i privilegi di amministratore."));
    }

    // ------------------------------------------------------------------ scansione
    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not SourceItem src)
        {
            Dlg.Info(L.T("Scegli un'unità o un'immagine disco."));
            return;
        }
        var cats = _categories.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        if (cats.Count == 0 && !NamesMode)
        {
            Dlg.Info(L.T("Scegli almeno un tipo di file da cercare."));
            return;
        }
        var isVolume = src.Target.Length == 2;
        if (isVolume && !RawSource.IsAdmin())
        {
            Dlg.Info(L.T("Per analizzare un'unità serve avviare SwinKnife come amministratore (pulsante in alto)."));
            return;
        }
        if (NamesMode)
        {
            await ScanNames(src);
            return;
        }
        var system = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        if (isVolume && src.Target.Equals(system, StringComparison.OrdinalIgnoreCase) &&
            !Dlg.Confirm(L.T("Hai scelto il disco di sistema: la scansione sarà lunga e troverà anche moltissimi file ancora presenti. Continuare?")))
            return;
        _rows.Clear();
        ClosePreview();
        _scanTarget = src.Target;
        var minSize = (long)((ComboBoxItem)MinSizeCombo.SelectedItem).Tag;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetRunning(true);
        var sw = Stopwatch.StartNew();
        var pending = new List<FoundFile>();
        var lastFlush = Stopwatch.StartNew();
        try
        {
            var stats = await Task.Run(() =>
            {
                using var source = new RawSource(src.Target);
                Carver.Scan(source, cats, minSize, f =>
                {
                    lock (pending) pending.Add(f);
                }, pos =>
                {
                    if (lastFlush.ElapsedMilliseconds < 300) return;
                    lastFlush.Restart();
                    var frac = pos / (double)Math.Max(1, source.Size);
                    Dispatcher.InvokeAsync(() =>
                    {
                        Flush(pending);
                        Progress.Value = frac * 1000;
                        var eta = frac > 0.01 ? L.T($" · circa {Util.HumanTime(sw.Elapsed.TotalSeconds / frac - sw.Elapsed.TotalSeconds)} rimanenti") : "";
                        StatusText.Text = L.T($"{Util.HumanSize(pos)} di {Util.HumanSize(source.Size)} analizzati{eta}");
                    });
                }, token);
                return source.ReadErrors;
            });
            Flush(pending);
            Progress.Value = token.IsCancellationRequested ? Progress.Value : 1000;
            StatusText.Text = (token.IsCancellationRequested ? L.T("Scansione interrotta: ") : L.T($"Scansione completata in {Util.HumanTime(sw.Elapsed.TotalSeconds)}: ")) +
                              L.T($"{_rows.Count} file trovati") + (stats > 0 ? L.T($" · {stats} blocchi illeggibili") : "") + ".";
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Scansione non riuscita:\n") + ex.Message);
            StatusText.Text = "";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void Flush(List<FoundFile> pending)
    {
        List<FoundFile> batch;
        lock (pending)
        {
            batch = pending.ToList();
            pending.Clear();
        }
        foreach (var f in batch) _rows.Add(new ResultRow(_rows.Count + 1, f));
        UpdateButtons();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = L.T("Interruzione…");
    }

    private void SetRunning(bool on)
    {
        GoBtn.IsEnabled = !on;
        StopBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) Progress.Value = 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var busy = _cts != null;
        var (view, total, grid) = NamesMode ? (_nameView, _names.Count, (System.Windows.Controls.DataGrid)NameResults) : (_view, _rows.Count, Results);
        CountText.Text = total > 0 ? L.T($"{view.Count} file mostrati su {total} trovati") : "";
        RecoverAllBtn.IsEnabled = !busy && view.Count > 0;
        RecoverSelBtn.IsEnabled = !busy && grid.SelectedItems.Count > 0;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_view == null || _nameView == null) return;
        var cat = (string)((ComboBoxItem)FilterCombo.SelectedItem).Tag;
        _view.Filter = cat.Length == 0 ? null : o => ((ResultRow)o).Category == cat;
        _nameView.Filter = cat.Length == 0 ? null : o => ((NameRow)o).Category == cat;
        UpdateButtons();
    }

    // ------------------------------------------------------------------ anteprima
    private void ClosePreview()
    {
        _previewSource?.Dispose();
        _previewSource = null;
        PreviewImage.Source = null;
        PreviewInfo.Text = "";
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        string ext;
        long size;
        Func<RawSource, byte[]> read;
        if (sender == NameResults)
        {
            if (NameResults.SelectedItem is not NameRow nr) return;
            var f = nr.File;
            (ext, size) = (f.Ext, f.Size);
            PreviewInfo.Text = L.T($"Nome: {f.Name}\nCartella: {nr.Folder}\nDimensione: {nr.SizeText}\nModificato: {nr.ModifiedText}\nStato: {f.State}") +
                               (f.OverwrittenFraction > 0 ? L.T("\n\nParte dei dati è stata riutilizzata da altri file: il recupero potrebbe essere incompleto.") : "");
            read = src =>
            {
                using var ms = new MemoryStream();
                FsRecovery.Extract(src, f, ms);
                return ms.ToArray();
            };
        }
        else
        {
            if (Results.SelectedItem is not ResultRow row) return;
            var f = row.File;
            (ext, size) = (f.Ext, f.Size);
            PreviewInfo.Text = L.T($"Tipo: {row.Type}\nDimensione: {row.SizeText}\nPosizione: byte {row.OffsetText}") +
                               (f.Complete ? "" : L.T("\n\nIl file potrebbe essere incompleto o danneggiato."));
            read = src => src.Read(f.Offset, (int)f.Size);
        }
        PreviewImage.Source = null;
        PreviewIcon.Symbol = SymbolRegular.Document24;
        if (ext is "jpeg") ext = "jpg";
        if (ext is "tiff") ext = "tif";
        if (!Previewable.Contains(ext) || size > 60 << 20 || _scanTarget == null) return;
        try
        {
            _previewSource ??= new RawSource(_scanTarget);
            using var img = new MagickImage(read(_previewSource));
            img.AutoOrient();
            img.Thumbnail(new MagickGeometry(600, 600));
            PreviewImage.Source = ImageIO.ToBitmapSource(img);
        }
        catch
        {
            PreviewIcon.Symbol = SymbolRegular.Warning24;
            PreviewInfo.Text += L.T("\n\nAnteprima non disponibile (file danneggiato?).");
        }
    }

    // ------------------------------------------------------------------ recupero
    /// <summary>Un file da recuperare: percorso relativo alla cartella di destinazione e come scriverlo.</summary>
    private sealed record Job(string RelativePath, long Size, Action<RawSource, Stream, CancellationToken> Write);

    private static Job ToJob(object row) => row switch
    {
        // recupero con i nomi: ricreo la struttura delle cartelle originale
        NameRow n => new Job(
            Path.Combine(n.File.Folder.Split('\\', '/').Where(p => p.Length > 0).Select(Util.SafeFileName).Append(Util.SafeFileName(n.Name)).ToArray()),
            n.Size, (src, fs, ct) => FsRecovery.Extract(src, n.File, fs, ct)),
        ResultRow r => new Job(Path.Combine(r.Category, L.T("recuperato_") + $"{r.Number:00000}.{r.File.Ext}"), r.Size, (src, fs, ct) =>
        {
            for (long pos = 0; pos < r.Size && !ct.IsCancellationRequested;)
            {
                var chunk = src.Read(r.Offset + pos, (int)Math.Min(8 << 20, r.Size - pos));
                if (chunk.Length == 0) break;
                fs.Write(chunk);
                pos += chunk.Length;
            }
        }),
        _ => throw new ArgumentException(null, nameof(row)),
    };

    private void RecoverSel_Click(object sender, RoutedEventArgs e) =>
        Recover((NamesMode ? NameResults.SelectedItems : Results.SelectedItems).Cast<object>().Select(ToJob).ToList());

    private void RecoverAll_Click(object sender, RoutedEventArgs e) =>
        Recover((NamesMode ? _nameView : _view).Cast<object>().Select(ToJob).ToList());

    private async void Recover(List<Job> items)
    {
        if (items.Count == 0 || _scanTarget == null) return;
        var outDir = OutBox.Text.Trim();
        if (outDir.Length == 0)
        {
            Dlg.Error(L.T("Indica la cartella di destinazione."));
            return;
        }
        if (_scanTarget.Length == 2 && Path.GetPathRoot(Path.GetFullPath(outDir))!.StartsWith(_scanTarget, StringComparison.OrdinalIgnoreCase))
        {
            Dlg.Error(L.T("Non salvare i file recuperati sulla stessa unità che stai analizzando: rischieresti di sovrascrivere proprio i dati da recuperare.\nScegli una cartella su un'altra unità."));
            return;
        }
        var total = items.Sum(j => j.Size);
        if (!Dlg.Confirm(L.T($"Recuperare {items.Count} file ({Util.HumanSize(total)}) in:\n{outDir}?"))) return;
        Settings.Set("recovery.out", outDir);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var target = _scanTarget;
        SetRunning(true);
        try
        {
            var (count, bytes) = await Task.Run(() =>
            {
                using var src = new RawSource(target);
                long done = 0;
                var n = 0;
                var lastUpdate = Stopwatch.StartNew();
                foreach (var job in items)
                {
                    if (token.IsCancellationRequested) break;
                    var dst = Util.UniquePath(Path.Combine(outDir, job.RelativePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (n == 0 || lastUpdate.ElapsedMilliseconds > 200)
                    {
                        lastUpdate.Restart();
                        var frac = done / (double)Math.Max(1, total);
                        var label = L.T($"Recupero {n + 1} di {items.Count}: {Path.GetFileName(dst)}");
                        Dispatcher.InvokeAsync(() =>
                        {
                            Progress.Value = frac * 1000;
                            StatusText.Text = label;
                        });
                    }
                    using (var fs = File.Create(dst)) job.Write(src, fs, token);
                    done += job.Size;
                    n++;
                }
                return (n, done);
            });
            Progress.Value = 1000;
            StatusText.Text = L.T($"Recuperati {count} file ({Util.HumanSize(bytes)}).");
            Util.OpenFolder(outDir);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Recupero non riuscito:\n") + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        ClosePreview();
    }
}
