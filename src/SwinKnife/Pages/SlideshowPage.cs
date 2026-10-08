using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Crea un video (slideshow) dalle tue foto, con durata per immagine e musica di sottofondo.</summary>
public sealed class SlideshowPage : UserControl, IToolPage
{
    private static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff"];

    private readonly List<string> _files = new();
    private readonly StackPanel _list = new();
    private readonly NumberBox _seconds = new() { Value = 3, Minimum = 0.5, Maximum = 60, Width = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _res = new() { Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.TextBox _music = new() { IsReadOnly = true, Width = 340, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 6), Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly Wpf.Ui.Controls.Button _goBtn, _stopBtn;
    private CancellationTokenSource? _cts;

    public SlideshowPage(MainWindow main)
    {
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) AddFiles(f); };

        foreach (var r in new[] { L.T("Full HD (1080p)"), L.T("HD (720p)"), "4K (2160p)" }) _res.Items.Add(r);
        _res.SelectedIndex = 0;

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Aggiungi le foto nell'ordine che vuoi, scegli quanti secondi mostrare ciascuna e, se vuoi, una canzone di sottofondo. SwinKnife crea un video MP4. Al primo uso si scarica ffmpeg."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var top = Ui.Card(L.T("Foto"), null,
            Ui.Row(
                Ui.Btn(L.T("Aggiungi foto…"), SymbolRegular.ImageAdd24, (_, _) => Pick()),
                Ui.Btn(L.T("Svuota"), SymbolRegular.Delete24, (_, _) => { _files.Clear(); Render(); })),
            _list);
        ((StackPanel)top.Child).Children.OfType<StackPanel>().First().Margin = new Thickness(0, 10, 0, 10);

        _goBtn = Ui.Btn(L.T("Crea il video"), SymbolRegular.VideoClip24, async (_, _) => await Run(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;

        var opts = Ui.Card(L.T("Impostazioni"), null,
            Ui.Row(Ui.Label(L.T("Secondi per foto:"), bold: true), new Border { Width = 8 }, _seconds,
                new Border { Width = 24 }, Ui.Label(L.T("Risoluzione:"), bold: true), new Border { Width = 8 }, _res),
            Ui.Row(Ui.Btn(L.T("Musica…"), SymbolRegular.MusicNote224, (_, _) => PickMusic()), _music,
                new Border { Width = 8 }, Ui.IconBtn(SymbolRegular.Dismiss24, L.T("Togli la musica"), (_, _) => _music.Text = "")),
            Ui.Row(_goBtn, _stopBtn),
            _bar, _status);
        var orows = ((StackPanel)opts.Child).Children.OfType<StackPanel>().ToList();
        orows[0].Margin = new Thickness(0, 10, 0, 0);
        orows[1].Margin = new Thickness(0, 12, 0, 0);
        orows[2].Margin = new Thickness(0, 14, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Slideshow video"), L.T("Monta le tue foto in un video con musica.")),
            info, top, opts);
        Render();
    }

    public bool Accepts(string path) => Exts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void AddFiles(IReadOnlyList<string> paths)
    {
        foreach (var p in paths.Where(Accepts)) if (!_files.Contains(p)) _files.Add(p);
        Render();
    }

    private void Pick()
    {
        var files = Dlg.OpenFiles(L.T("Scegli le foto"),
            L.T("Immagini|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif;*.bmp;*.tif;*.tiff|Tutti i file|*.*"), "slideshow");
        if (files.Length > 0) AddFiles(files);
    }

    private void PickMusic()
    {
        var f = Dlg.OpenFile(L.T("Scegli la musica di sottofondo"),
            L.T("Audio|*.mp3;*.wav;*.m4a;*.flac;*.aac;*.ogg|Tutti i file|*.*"), "slideshowmusic");
        if (f != null) _music.Text = f;
    }

    private void Move(int i, int delta)
    {
        var j = i + delta;
        if (j < 0 || j >= _files.Count) return;
        (_files[i], _files[j]) = (_files[j], _files[i]);
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_files.Count == 0) { _list.Children.Add(Ui.Hint(L.T("Nessuna foto. Aggiungi o trascina qui le immagini."))); return; }
        for (var i = 0; i < _files.Count; i++)
        {
            var idx = i;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = L.T($"Foto {i + 1}"), FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            text.Children.Add(Ui.Hint(Path.GetFileName(_files[i])));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Ui.IconBtn(SymbolRegular.ArrowUp24, L.T("Su"), (_, _) => Move(idx, -1)));
            actions.Children.Add(Ui.IconBtn(SymbolRegular.ArrowDown24, L.T("Giù"), (_, _) => Move(idx, 1)));
            actions.Children.Add(Ui.IconBtn(SymbolRegular.Dismiss24, L.T("Rimuovi"), (_, _) => { _files.RemoveAt(idx); Render(); }));

            var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
            dock.Children.Add(text);
            _list.Children.Add(new Border { Child = dock, Padding = new Thickness(12, 2, 4, 2), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), Background = Ui.Res("CardBackgroundFillColorDefaultBrush") });
        }
    }

    private async Task Run()
    {
        if (_files.Count == 0) { Dlg.Info(L.T("Aggiungi almeno una foto.")); return; }
        var dst = Dlg.SaveFile(L.T("Salva il video"), "Slideshow.mp4", L.T("Video MP4|*.mp4"), "slideshow");
        if (dst == null) return;

        var files = _files.ToList();
        var seconds = _seconds.Value ?? 3;
        var (w, h) = Slideshow.Resolution(_res.SelectedIndex switch { 1 => "720p", 2 => "4k", _ => "1080p" });
        var music = _music.Text.Trim().Length > 0 ? _music.Text.Trim() : null;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _goBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        try
        {
            _status.Text = L.T("Preparazione…");
            var ffmpeg = await Ffmpeg.EnsureAsync(() => Dlg.Confirm(L.T("Serve ffmpeg. Scaricarlo ora (una volta sola)?")),
                new Progress<(double, string)>(x => Dispatcher.Invoke(() => { _bar.Value = x.Item1; _status.Text = x.Item2; })), ct);
            if (ffmpeg == null) { Reset(); return; }

            _status.Text = L.T("Creazione del video…");
            await Task.Run(() => Slideshow.Build(ffmpeg, files, seconds, music, w, h, dst,
                p => Dispatcher.Invoke(() => _bar.Value = p), ct), ct);
            _status.Text = L.T("Fatto.");
            Util.Reveal(dst);
            MainWindow.Notify(L.T("Video creato."), 8);
        }
        catch (OperationCanceledException) { _status.Text = L.T("Interrotto."); }
        catch (Exception ex) { _status.Text = ""; Dlg.Error(ex.Message); }
        finally { Reset(); }
    }

    private void Reset()
    {
        _bar.Visibility = Visibility.Collapsed;
        _goBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        _cts?.Dispose();
        _cts = null;
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un'elaborazione in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
