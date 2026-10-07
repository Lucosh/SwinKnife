using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using ListBox = System.Windows.Controls.ListBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Editor video veloce: taglia, comprimi per WhatsApp o e-mail, estrai l'audio, crea GIF, togli l'audio, ruota, unisci.</summary>
public sealed class VideoPage : UserControl, IToolPage
{
    private readonly MediaView _player = new();
    private readonly TextBlock _file = Ui.Label("", 14, true);
    private readonly TextBlock _info = Ui.Hint();
    private readonly TextBlock _range = Ui.Label("");
    private readonly CheckBox _useRange = new() { Content = L.T("Applica solo al tratto selezionato"), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _fastTrim = new() { Content = new TextBlock { Text = L.T("Taglio veloce senza ricodifica (l'inizio può spostarsi al fotogramma chiave più vicino)"), TextWrapping = TextWrapping.Wrap } };
    private readonly ComboBox _target = new() { Width = 250 };
    private readonly ListBox _merge = new() { MinHeight = 90, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = Ui.Hint();
    private readonly StackPanel _tools;
    private readonly FrameworkElement _empty;
    private readonly Grid _editor;
    private string? _path;
    private double _duration, _from, _to;
    private int _width, _height;
    private bool _audio;
    private CancellationTokenSource? _cts;

    private static readonly (string label, double mb)[] Targets =
    [
        (L.T("WhatsApp (fino a 16 MB)"), 15.5), (L.T("E-mail (fino a 20 MB)"), 19.5), (L.T("Discord (fino a 10 MB)"), 9.5), (L.T("Metà del peso"), -0.5), (L.T("Un quarto del peso"), -0.25),
    ];

    public VideoPage(MainWindow main)
    {
        foreach (var (l, _) in Targets) _target.Items.Add(l);
        _target.SelectedIndex = 0;
        _player.DurationKnown += d =>
        {
            if (_duration <= 0) _duration = d.TotalSeconds;
            if (_to <= 0) _to = _duration;
            UpdateRange();
        };

        var setStart = Ui.Btn(L.T("Inizio qui"), SymbolRegular.ArrowStepIn24, (_, _) => { _from = Math.Min(_player.Position, _to > 0 ? _to - 0.1 : _player.Position); UpdateRange(); }, L.T("Il tratto selezionato comincia dalla posizione attuale"));
        var setEnd = Ui.Btn(L.T("Fine qui"), SymbolRegular.ArrowStepOut24, (_, _) => { _to = Math.Max(_player.Position, _from + 0.1); UpdateRange(); }, L.T("Il tratto selezionato finisce alla posizione attuale"));
        var reset = Ui.Btn(L.T("Tutto il video"), SymbolRegular.ArrowReset24, (_, _) => { _from = 0; _to = _duration; UpdateRange(); });
        var goStart = Ui.IconBtn(SymbolRegular.Previous24, L.T("Vai all'inizio del tratto"), (_, _) => _player.Seek(_from));
        var rangeBar = Ui.Row(setStart, setEnd, reset, goStart, new Border { Width = 12 }, _range);
        rangeBar.Margin = new Thickness(0, 10, 0, 0);

        // ---- operazioni
        Border Op(string title, string desc, params UIElement[] content)
        {
            var c = Ui.Card(title, desc, content);
            c.Margin = new Thickness(0, 0, 0, 10);
            return c;
        }
        _tools = new StackPanel();
        _tools.Children.Add(Op(L.T("Taglia"), L.T("Salva solo il tratto selezionato."), _fastTrim,
            Pad(Ui.Btn(L.T("Salva il tratto…"), SymbolRegular.Cut24, async (_, _) => await Trim(), primary: true))));
        _tools.Children.Add(Op(L.T("Comprimi"), L.T("Riduce il peso per mandarlo in chat o per e-mail, abbassando la risoluzione se serve."),
            Wrap(_target, Ui.Btn(L.T("Comprimi…"), SymbolRegular.ArrowMinimize24, async (_, _) => await Compress()))));
        _tools.Children.Add(Op(L.T("Audio"), L.T("Salva solo l'audio, oppure un video muto."),
            Wrap(Ui.Btn(L.T("Estrai MP3…"), SymbolRegular.MusicNote224, async (_, _) => await Audio(".mp3")),
                Ui.Btn(L.T("Estrai M4A…"), SymbolRegular.MusicNote224, async (_, _) => await Audio(".m4a")),
                Ui.Btn(L.T("Togli l'audio…"), SymbolRegular.SpeakerMute24, async (_, _) => await Mute()))));
        _tools.Children.Add(Op(L.T("GIF animata"), L.T("Dal tratto selezionato (meglio se breve, pochi secondi)."),
            Wrap(Ui.Btn(L.T("Crea GIF piccola (480 px)…"), SymbolRegular.Gif24, async (_, _) => await Gif(480, 12)),
                Ui.Btn(L.T("Crea GIF grande (800 px)…"), SymbolRegular.Gif24, async (_, _) => await Gif(800, 15)))));
        _tools.Children.Add(Op(L.T("Ruota"), L.T("Per i video girati in verticale o capovolti."),
            Wrap(Ui.Btn(L.T("90° a destra"), SymbolRegular.ArrowRotateClockwise24, async (_, _) => await Rotate(90)),
                Ui.Btn(L.T("90° a sinistra"), SymbolRegular.ArrowRotateCounterclockwise24, async (_, _) => await Rotate(270)),
                Ui.Btn("180°", SymbolRegular.ArrowRepeatAll24, async (_, _) => await Rotate(180)))));
        _tools.Children.Add(Op(L.T("Unisci"), L.T("Mette più video uno dopo l'altro (il primo è quello aperto; trascina qui gli altri)."),
            Ui.Row(Ui.Btn(L.T("Aggiungi video…"), SymbolRegular.Add24, (_, _) =>
                {
                    foreach (var f in Dlg.OpenFiles(L.T("Video da aggiungere"), L.T("Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv|") + Dlg.AllFiles, "video")) AddMerge(f);
                }),
                Ui.Btn(L.T("Togli"), SymbolRegular.Dismiss24, (_, _) => { if (_merge.SelectedItem != null) _merge.Items.Remove(_merge.SelectedItem); }),
                Ui.Btn(L.T("Unisci…"), SymbolRegular.Merge24, async (_, _) => await Merge())),
            new Border { BorderBrush = Ui.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 8, 0, 0), Child = _merge }));

        // ---- disposizione
        var left = new DockPanel();
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(_file);
        head.Children.Add(_info);
        DockPanel.SetDock(head, Dock.Top);
        var under = new StackPanel();
        under.Children.Add(rangeBar);
        under.Children.Add(_useRange);
        under.Children.Add(_progress);
        under.Children.Add(_status);
        DockPanel.SetDock(under, Dock.Bottom);
        left.Children.Add(head);
        left.Children.Add(under);
        left.Children.Add(_player);
        _editor = new Grid();
        _editor.ColumnDefinitions.Add(new ColumnDefinition());
        _editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        _editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(460) });
        _editor.Children.Add(left);
        var right = new ScrollViewer { Content = _tools, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(right, 2);
        _editor.Children.Add(right);
        _editor.Visibility = Visibility.Collapsed;

        _empty = Ui.Placeholder(SymbolRegular.Video24, L.T("Trascina qui un video (MP4, MOV, MKV, AVI, WEBM…) o clicca \"Apri video\"."), out _);
        var open = Ui.Btn(L.T("Apri video…"), SymbolRegular.FolderOpen24, (_, _) =>
        {
            if (Dlg.OpenFile(L.T("Apri video"), L.T("Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.3gp;*.mts|") + Dlg.AllFiles, "video") is { } f) OpenFile(f);
        }, primary: true);
        open.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        var header = Ui.Header(L.T("Editor video"), L.T("Taglia, comprimi per WhatsApp o e-mail, estrai l'audio, crea GIF, ruota e unisci video."));
        foreach (var e in new UIElement[] { header, open }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        var host = new Grid();
        host.Children.Add(_editor);
        host.Children.Add(_empty);
        dock.Children.Add(host);
        Content = dock;
    }

    /// <summary>Pulsanti che vanno a capo se il pannello è stretto.</summary>
    private static WrapPanel Wrap(params UIElement[] items)
    {
        var w = new WrapPanel();
        foreach (var i in items)
        {
            if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 6, 6);
            w.Children.Add(i);
        }
        return w;
    }

    private static UIElement Pad(UIElement e)
    {
        if (e is FrameworkElement f) f.Margin = new Thickness(0, 8, 0, 0);
        return e;
    }

    public bool Accepts(string path) => Formats.KindOf(path) == FileKind.Video;

    public void OpenFile(string path) => _ = Load(path);

    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (_path == null) _ = Load(paths[0]);
        foreach (var p in paths.Skip(_path == null ? 1 : 0)) AddMerge(p);
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        _player.Stop();
    }

    private async Task Load(string path)
    {
        _path = path;
        _file.Text = Path.GetFileName(path);
        _merge.Items.Clear();
        _from = 0;
        _to = _duration = 0;
        _editor.Visibility = Visibility.Visible;
        _empty.Visibility = Visibility.Collapsed;
        _player.Load(path, autoplay: false);
        var ff = Ffmpeg.Find();
        if (ff != null)
        {
            var (d, w, h, a) = await Task.Run(() => Ffmpeg.Probe(ff, path));
            (_duration, _width, _height, _audio) = (d, w, h, a);
            _to = _duration;
            _info.Text = L.T($"{w} × {h} · {Util.HumanTime(d)} · {Util.HumanSize(new FileInfo(path).Length)}") + (a ? "" : L.T(" · senza audio"));
        }
        else _info.Text = Util.HumanSize(new FileInfo(path).Length);
        UpdateRange();
    }

    private void AddMerge(string path)
    {
        if (Formats.KindOf(path) != FileKind.Video) return;
        _merge.Items.Add(new ListBoxItem { Content = Path.GetFileName(path), Tag = path });
    }

    private void UpdateRange()
    {
        var len = Math.Max(0, (_to > 0 ? _to : _duration) - _from);
        _range.Text = L.T($"Tratto: {Clock(_from)} → {Clock(_to > 0 ? _to : _duration)}  ({Clock(len)})");
    }

    private static string Clock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    private (double from, double to) Span => _useRange.IsChecked == true && (_from > 0.05 || (_to > 0 && _to < _duration - 0.05)) ? (_from, _to) : (0, 0);

    // ------------------------------------------------------------------ esecuzione
    private async Task<string?> Exe()
    {
        try
        {
            return await Ffmpeg.EnsureAsync(
                () => Dlg.Confirm(L.T("Per modificare i video serve FFmpeg (circa 100 MB, si scarica una volta sola). Scaricarlo adesso?")),
                new Progress<(double, string)>(p => _status.Text = p.Item2), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Download non riuscito:\n") + ex.Message);
            return null;
        }
    }

    private string? Ask(string suffix, string ext, string filter)
    {
        if (_path == null) return null;
        var name = Path.GetFileNameWithoutExtension(_path) + suffix + ext;
        return Dlg.SaveFile(L.T("Salva come"), name, filter, "video.save");
    }

    private async Task Exec(string dst, Func<string, List<string>> args, double length)
    {
        var exe = await Exe();
        if (exe == null) return;
        _player.Pause();
        _cts = new CancellationTokenSource();
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = L.T("Elaborazione in corso…");
        _tools.IsEnabled = false;
        try
        {
            var list = args(exe);
            await Task.Run(() => Ffmpeg.Run(exe, list, length, p => Dispatcher.InvokeAsync(() => _progress.Value = p), _cts.Token));
            _status.Text = L.T($"Fatto: {Path.GetFileName(dst)} ({Util.HumanSize(new FileInfo(dst).Length)})");
            if (Dlg.Confirm(_status.Text, AppInfo.Name, L.T("Mostra nella cartella"), L.T("Chiudi"))) Util.Reveal(dst);
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("Annullato.");
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Editor video");
            _status.Text = L.T("Errore: ") + ex.Message.Split('\n')[0];
            Dlg.Error(L.T("Operazione non riuscita:\n") + ex.Message);
        }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
            _tools.IsEnabled = true;
        }
    }

    private double Length(double from, double to) => (to > 0 ? to : _duration) - from;

    private async Task Trim()
    {
        var (from, to) = (_from, _to);
        if (Ask(L.T("_tagliato"), Path.GetExtension(_path!).ToLowerInvariant() is ".mkv" or ".mov" or ".webm" ? Path.GetExtension(_path!) : ".mp4", L.T("Video|*.mp4;*.mkv;*.mov;*.webm")) is not { } dst) return;
        var fast = _fastTrim.IsChecked == true;
        await Exec(dst, _ => VideoOps.Trim(_path!, dst, from, to, fast), Length(from, to));
    }

    private async Task Compress()
    {
        var (from, to) = Span;
        var target = Targets[_target.SelectedIndex].mb;
        var len = Length(from, to);
        var mb = target > 0 ? target : new FileInfo(_path!).Length / 1048576.0 * -target * (len / Math.Max(1, _duration));
        if (Ask(L.T("_compresso"), ".mp4", "MP4|*.mp4") is not { } dst) return;
        await Exec(dst, _ => VideoOps.Compress(_path!, dst, from, to, _duration, mb, _width, _height, _audio), len);
    }

    private async Task Audio(string ext)
    {
        if (!_audio && _duration > 0) { Dlg.Info(L.T("Questo video non ha l'audio.")); return; }
        var (from, to) = Span;
        if (Ask("", ext, ext == ".mp3" ? "MP3|*.mp3" : "M4A|*.m4a") is not { } dst) return;
        await Exec(dst, _ => VideoOps.ExtractAudio(_path!, dst, from, to), Length(from, to));
    }

    private async Task Mute()
    {
        var (from, to) = Span;
        if (Ask(L.T("_muto"), Path.GetExtension(_path!), L.T("Video|*") + Path.GetExtension(_path!)) is not { } dst) return;
        await Exec(dst, _ => VideoOps.Mute(_path!, dst, from, to), Length(from, to));
    }

    private async Task Gif(int width, int fps)
    {
        var (from, to) = Span;
        if (Length(from, to) > 30 && !Dlg.Confirm(L.T("Il tratto è lungo più di 30 secondi: la GIF sarà molto pesante. Continuare? (Conviene selezionare un tratto breve con \"Inizio qui\" e \"Fine qui\".)"))) return;
        if (Ask("", ".gif", "GIF|*.gif") is not { } dst) return;
        await Exec(dst, _ => VideoOps.Gif(_path!, dst, from, to, Math.Min(width, _width > 0 ? _width : width), fps), Length(from, to));
    }

    private async Task Rotate(int degrees)
    {
        if (Ask(L.T("_ruotato"), ".mp4", "MP4|*.mp4") is not { } dst) return;
        await Exec(dst, _ => VideoOps.Rotate(_path!, dst, degrees), _duration);
    }

    private async Task Merge()
    {
        var files = new List<string> { _path! };
        files.AddRange(_merge.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag));
        if (files.Count < 2)
        {
            Dlg.Info(L.T("Aggiungi almeno un altro video da unire a quello aperto."));
            return;
        }
        var exe = await Exe();
        if (exe == null) return;
        var probes = await Task.Run(() => files.Select(f => Ffmpeg.Probe(exe, f)).ToList());
        var allAudio = probes.All(p => p.audio);
        if (!allAudio && probes.Any(p => p.audio) && !Dlg.Confirm(L.T("Alcuni video non hanno l'audio: il video unito sarà muto. Continuare?"))) return;
        if (Ask(L.T("_unito"), ".mp4", "MP4|*.mp4") is not { } dst) return;
        var (w, h) = (probes[0].width > 0 ? probes[0].width : 1280, probes[0].height > 0 ? probes[0].height : 720);
        await Exec(dst, _ => VideoOps.Concat(files, dst, w, h, allAudio), probes.Sum(p => p.duration));
    }
}
