using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Trascrive audio e video in testo e sottotitoli (.srt), offline, con Whisper.</summary>
public sealed class TranscribePage : UserControl, IToolPage
{
    private static readonly (string label, string code)[] Languages =
    {
        ("Rilevamento automatico", "auto"), ("Italiano", "it"), ("English", "en"), ("Español", "es"),
        ("Français", "fr"), ("Deutsch", "de"), ("Português", "pt"), ("Русский", "ru"), ("中文", "zh"), ("日本語", "ja"),
    };

    private readonly TextBlock _fileName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _lang = new() { Width = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _goBtn, _stopBtn;
    private string? _media;
    private string? _srt;
    private CancellationTokenSource? _cts;

    public TranscribePage(MainWindow main)
    {
        foreach (var (label, _) in Languages) _lang.Items.Add(label);
        _lang.SelectedIndex = 0;

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Tutto sul tuo PC"),
            Message = L.T("La trascrizione avviene offline. Al primo utilizzo si scaricano il motore e il modello linguistico (circa 150 MB in tutto). File lunghi possono richiedere diversi minuti."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _goBtn = Ui.Btn(L.T("Trascrivi"), SymbolRegular.TextBulletListSquare24, async (_, _) => await Run(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;

        var card = Ui.Card(L.T("Audio o video"), null,
            Ui.Row(Ui.Btn(L.T("Scegli file…"), SymbolRegular.Open24, (_, _) =>
            {
                var f = Dlg.OpenFile(L.T("Audio o video da trascrivere"), L.T("Audio e video|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.mp4;*.mov;*.mkv;*.avi;*.webm|Tutti i file|*.*"), "trans");
                if (f != null) { _media = f; _fileName.Text = Path.GetFileName(f); }
            }, primary: true), _fileName),
            Ui.Row(Ui.Label(L.T("Lingua:")), new Border { Width = 8 }, _lang),
            Ui.Row(_goBtn, _stopBtn),
            _bar, _status, _output,
            Ui.Row(Ui.Btn(L.T("Copia testo"), SymbolRegular.Copy24, (_, _) => { try { Clipboard.SetText(_output.Text); MainWindow.Notify(L.T("Copiato.")); } catch { } }),
                   Ui.Btn(L.T("Apri i sottotitoli (.srt)"), SymbolRegular.FolderOpen24, (_, _) => { if (_srt != null && File.Exists(_srt)) Util.Reveal(_srt); })));
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[1].Margin = new Thickness(0, 10, 0, 0);
        rows[2].Margin = new Thickness(0, 12, 0, 0);
        rows[^1].Margin = new Thickness(0, 10, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Trascrizione"), L.T("Trasforma audio e video in testo e sottotitoli, senza inviare nulla online.")),
            info, card);
    }

    public bool Accepts(string path) => true;
    public void OpenFile(string path) { _media = path; _fileName.Text = Path.GetFileName(path); }

    private async Task Run()
    {
        if (_media == null) { Dlg.Info(L.T("Scegli prima un file.")); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _goBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _output.Text = "";
        _srt = null;
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = false;
        _bar.Value = 0;
        try
        {
            _status.Text = L.T("Preparazione di ffmpeg…");
            var ffmpeg = await Ffmpeg.EnsureAsync(() => Dlg.Confirm(L.T("Serve ffmpeg per leggere l'audio. Scaricarlo ora (una volta sola)?")),
                new Progress<(double, string)>(x => Dispatcher.Invoke(() => { _bar.Value = x.Item1; _status.Text = x.Item2; })), ct);
            if (ffmpeg == null) { Reset(); return; }

            if (!Whisper.Ready && !Dlg.Confirm(L.T("Al primo uso vanno scaricati il motore di trascrizione e il modello (circa 150 MB). Procedere?"))) { Reset(); return; }
            await Whisper.EnsureAsync(new Progress<(double, string)>(x => Dispatcher.Invoke(() => { _bar.Value = x.Item1; _status.Text = x.Item2; })), ct);

            _bar.IsIndeterminate = true;
            _status.Text = L.T("Trascrizione in corso…");
            var lang = Languages[_lang.SelectedIndex].code;
            var (txt, srt) = await Whisper.TranscribeAsync(_media, lang, ffmpeg,
                line => Dispatcher.InvokeAsync(() => _status.Text = Trim(line)), ct);
            _srt = srt;
            _output.Text = File.Exists(txt) ? await File.ReadAllTextAsync(txt, ct) : L.T("(nessun testo prodotto)");
            _status.Text = L.T("Fatto.");
            MainWindow.Notify(L.T("Trascrizione completata."), 8);
        }
        catch (OperationCanceledException) { _status.Text = L.T("Interrotto."); }
        catch (Exception ex) { Dlg.Error(ex.Message); _status.Text = ""; }
        finally { Reset(); }
    }

    private static string Trim(string line) => line.Length > 160 ? line[..160] : line;

    private void Reset()
    {
        _bar.Visibility = Visibility.Collapsed;
        _bar.IsIndeterminate = false;
        _goBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        _cts?.Dispose();
        _cts = null;
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è una trascrizione in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
