using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using ListView = Wpf.Ui.Controls.ListView;
using GridView = Wpf.Ui.Controls.GridView;
using GridViewColumn = Wpf.Ui.Controls.GridViewColumn;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Controls;

/// <summary>Lettore audio/video basato su Media Foundation (MediaElement).</summary>
public sealed class MediaView : DockPanel
{
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform };
    private readonly Slider _pos = new() { Minimum = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _vol = new() { Minimum = 0, Maximum = 1, Value = 0.8, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = Ui.Hint("0:00 / 0:00");
    private readonly Wpf.Ui.Controls.Button _play;
    private readonly FrameworkElement _audioArt;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _playing;
    private bool _dragging;

    public event Action<TimeSpan>? DurationKnown;
    public event Action<string>? Failed;

    public MediaView()
    {
        _play = Ui.IconBtn(SymbolRegular.Play24, L.T("Riproduci / pausa"), (_, _) => Toggle());
        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = Ui.Row(_time, new SymbolIcon { Symbol = SymbolRegular.Speaker224, Margin = new Thickness(14, 0, 6, 0) }, _vol);
        SetDock(_play, Dock.Left);
        SetDock(right, Dock.Right);
        bar.Children.Add(_play);
        bar.Children.Add(right);
        bar.Children.Add(_pos);
        SetDock(bar, Dock.Bottom);
        Children.Add(bar);
        var stage = new Grid { Background = Brushes.Black };
        stage.Children.Add(_media);
        _audioArt = new SymbolIcon { Symbol = SymbolRegular.MusicNote224, FontSize = 120, Foreground = (Brush)Application.Current.FindResource("SwinAccentBrush"), Visibility = Visibility.Collapsed };
        stage.Children.Add(_audioArt);
        Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = stage });
        _media.Volume = 0.8;
        _vol.ValueChanged += (_, e) => _media.Volume = e.NewValue;
        _media.MediaOpened += (_, _) =>
        {
            _audioArt.Visibility = _media.NaturalVideoWidth == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_media.NaturalDuration.HasTimeSpan)
            {
                _pos.Maximum = _media.NaturalDuration.TimeSpan.TotalSeconds;
                DurationKnown?.Invoke(_media.NaturalDuration.TimeSpan);
            }
        };
        _media.MediaEnded += (_, _) =>
        {
            _media.Stop();
            SetPlaying(false);
        };
        _media.MediaFailed += (_, e) =>
        {
            SetPlaying(false);
            Failed?.Invoke(e.ErrorException?.Message ?? "");
        };
        _pos.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _pos.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            _media.Position = TimeSpan.FromSeconds(_pos.Value);
        }));
        _pos.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) _media.Position = TimeSpan.FromSeconds(_pos.Value);
        };
        _timer.Tick += (_, _) =>
        {
            if (!_dragging) _pos.Value = _media.Position.TotalSeconds;
            var total = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan.TotalSeconds : 0;
            _time.Text = $"{Util.HumanTime(_media.Position.TotalSeconds)} / {Util.HumanTime(total)}";
        };
    }

    public void Load(string path, bool autoplay = true)
    {
        _media.Source = new Uri(path);
        _media.Play();
        if (!autoplay) _media.Pause(); // Play+Pause mostra il primo fotogramma
        SetPlaying(autoplay);
        _timer.Start();
    }

    /// <summary>Posizione attuale in secondi.</summary>
    public double Position => _media.Position.TotalSeconds;

    public void Seek(double seconds)
    {
        _media.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        _pos.Value = seconds;
    }

    public void Pause()
    {
        _media.Pause();
        SetPlaying(false);
    }

    public void Stop()
    {
        _media.Stop();
        _media.Close();
        _media.Source = null;
        _timer.Stop();
        SetPlaying(false);
    }

    private void Toggle()
    {
        if (_playing) _media.Pause();
        else _media.Play();
        SetPlaying(!_playing);
    }

    private void SetPlaying(bool on)
    {
        _playing = on;
        _play.Icon = new SymbolIcon { Symbol = on ? SymbolRegular.Pause24 : SymbolRegular.Play24, FontSize = 18 };
    }
}

/// <summary>Tabella (CSV/Excel) con scelta del foglio.</summary>
public sealed class TableView : DockPanel
{
    public const int MaxRows = 100_000;
    private readonly ComboBox _sheet = new() { MinWidth = 180 };
    private readonly TextBlock _info = Ui.Hint();
    private readonly DataGrid _grid = new()
    {
        IsReadOnly = true, AutoGenerateColumns = true, EnableRowVirtualization = true, EnableColumnVirtualization = true,
        CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.All, GridLinesVisibility = DataGridGridLinesVisibility.All,
    };
    private List<Tables.Sheet> _sheets = new();
    private readonly TextBlock _sheetLabel;

    public TableView()
    {
        _sheetLabel = Ui.Label(L.T("Foglio  "));
        var bar = Ui.Row(_sheetLabel, _sheet, new Border { Width = 16 }, _info);
        bar.Margin = new Thickness(0, 0, 0, 8);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_grid);
        _sheet.SelectionChanged += (_, _) => Show(_sheet.SelectedIndex);
        _grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    public void SetSheets(List<Tables.Sheet> sheets)
    {
        _sheets = sheets;
        _sheet.ItemsSource = sheets.Select(s => s.Name).ToList();
        _sheet.Visibility = _sheetLabel.Visibility = sheets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _sheet.SelectedIndex = 0;
        Show(0);
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _sheets.Count) return;
        var rows = _sheets[index].Rows;
        var cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var table = new DataTable();
        for (var c = 0; c < cols; c++) table.Columns.Add(ColumnName(c));
        foreach (var r in rows)
        {
            var dr = table.NewRow();
            for (var c = 0; c < r.Count; c++)
                dr[c] = r[c] switch
                {
                    null => "",
                    double d => d.ToString("G", Util.It),
                    DateTime dt => dt.ToString("g", Util.It),
                    var v => v.ToString(),
                };
            table.Rows.Add(dr);
        }
        _grid.ItemsSource = table.DefaultView;
        _info.Text = L.T($"{Util.Number(rows.Count)} righe · {cols} colonne") + (rows.Count >= MaxRows ? L.T(" (anteprima limitata)") : "");
    }

    private static string ColumnName(int i)
    {
        var name = "";
        for (var n = i + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}

/// <summary>Elenco del contenuto di un archivio (ZIP, 7z, RAR, TAR, GZ…) con estrazione.</summary>
public sealed class ArchiveView : DockPanel
{
    private sealed record Entry(string Name, string Size, string Packed, string Date, string Key, bool IsDir);

    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBlock _info = Ui.Hint();
    private string? _path;

    public ArchiveView()
    {
        var bar = Ui.Row(
            Ui.Btn(L.T("Estrai tutto…"), SymbolRegular.FolderOpen24, (_, _) => Extract(false), primary: true),
            Ui.Btn(L.T("Estrai selezionati…"), null, (_, _) => Extract(true)),
            _info);
        bar.Margin = new Thickness(0, 0, 0, 8);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var gv = new GridView();
        foreach (var (title, prop, w) in new[] { (L.T("Nome"), "Name", 460.0), (L.T("Dimensione"), "Size", 110.0), (L.T("Compresso"), "Packed", 110.0), (L.T("Modificato"), "Date", 140.0) })
            gv.Columns.Add(new GridViewColumn { Header = title, DisplayMemberBinding = new System.Windows.Data.Binding(prop), Width = w });
        _list.View = gv;
        Children.Add(_list);
    }

    public Dictionary<string, string> Load(string path)
    {
        _path = path;
        using var archive = ArchiveFactory.Open(path);
        var entries = new List<Entry>();
        long total = 0;
        var files = 0;
        foreach (var e in archive.Entries)
        {
            if (!e.IsDirectory)
            {
                total += e.Size;
                files++;
            }
            entries.Add(new Entry((e.IsDirectory ? "📁 " : "") + e.Key, e.IsDirectory ? "" : Util.HumanSize(e.Size),
                e.IsDirectory ? "" : Util.HumanSize(e.CompressedSize), e.LastModifiedTime is { } d ? Util.Date(d) : "",
                e.Key ?? "", e.IsDirectory));
        }
        _list.ItemsSource = entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
        _info.Text = L.T($"  {files} file · {Util.HumanSize(total)} estratti");
        return new() { [L.T("Tipo di archivio")] = archive.Type.ToString(), [L.T("Contenuto")] = $"{files} file", [L.T("Dimensione estratta")] = Util.HumanSize(total) };
    }

    private async void Extract(bool selectedOnly)
    {
        if (_path == null) return;
        var keys = selectedOnly ? _list.SelectedItems.Cast<Entry>().Select(e => e.Key).ToHashSet() : null;
        if (keys is { Count: 0 })
        {
            Dlg.Info(L.T("Seleziona prima uno o più elementi."));
            return;
        }
        var dest = Dlg.PickFolder(L.T("Scegli dove estrarre"), "extract");
        if (dest == null) return;
        var path = _path;
        try
        {
            MainWindow.Notify(L.T("Estrazione in corso…"), 0);
            await Task.Run(() =>
            {
                using var archive = ArchiveFactory.Open(path);
                var opt = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
                foreach (var e in archive.Entries.Where(e => !e.IsDirectory))
                {
                    if (keys == null || keys.Contains(e.Key ?? "") || keys.Any(k => (e.Key ?? "").StartsWith(k.TrimEnd('/') + "/")))
                        e.WriteToDirectory(dest, opt);
                }
            });
            MainWindow.Notify(L.T("Estrazione completata"));
            Util.OpenFolder(dest);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Estrazione non riuscita:\n") + ex.Message);
        }
    }
}

/// <summary>Anteprima di un font a varie dimensioni.</summary>
public sealed class FontView : ScrollViewer
{
    public Dictionary<string, string> Load(string path)
    {
        var gt = new GlyphTypeface(new Uri(path));
        var en = CultureInfo.GetCultureInfo("en-US");
        var family = gt.FamilyNames.TryGetValue(en, out var f) ? f : gt.FamilyNames.Values.First();
        var face = gt.FaceNames.TryGetValue(en, out var fn) ? fn : "";
        var ff = new FontFamily(new Uri(Path.GetDirectoryName(path) + Path.DirectorySeparatorChar), "./#" + family);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Ui.Label($"{family} {face}", 22, true));
        panel.Children.Add(new TextBlock
        {
            FontFamily = ff, FontSize = 26, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18),
            Foreground = Ui.Res("TextFillColorPrimaryBrush"),
            Text = L.T("ABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789 àèéìòù €$%&@#!?"),
        });
        foreach (var size in new[] { 12, 18, 24, 36, 48, 72 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = $"{size} pt", Width = 56, Foreground = Ui.Res("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = L.T("Ma la volpe col suo balzo ha raggiunto il quieto Fido"), FontFamily = ff, FontSize = size * 96 / 72.0, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            panel.Children.Add(row);
        }
        Content = panel;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        return new() { [L.T("Famiglia")] = family, [L.T("Stile")] = face, [L.T("Glifi")] = gt.GlyphCount.ToString() };
    }
}
