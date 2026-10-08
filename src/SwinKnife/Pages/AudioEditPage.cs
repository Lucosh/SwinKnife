using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Modifiche audio di base: taglio, dissolvenze, volume, normalizzazione e riduzione del rumore (ffmpeg).</summary>
public sealed class AudioEditPage : UserControl, IToolPage
{
    private static readonly string[] AudioExts = [".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".wma", ".opus", ".aiff"];

    private readonly System.Windows.Controls.TextBox _src = new() { IsReadOnly = true, Width = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _trimStart = new() { Value = 0, Minimum = 0, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _trimEnd = new() { Minimum = 0, Width = 110, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = L.T("fine") };
    private readonly NumberBox _fadeIn = new() { Value = 0, Minimum = 0, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _fadeOut = new() { Value = 0, Minimum = 0, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _gain = new() { Value = 0, Minimum = -40, Maximum = 40, Width = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _normalize = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _denoise = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _format = new() { Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 6), Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly Wpf.Ui.Controls.Button _goBtn, _stopBtn;

    private string? _file;
    private double _duration;
    private CancellationTokenSource? _cts;

    public AudioEditPage(MainWindow main)
    {
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) OpenFile(f[0]); };

        foreach (var f in new[] { "MP3", "WAV", "M4A", "FLAC" }) _format.Items.Add(f);
        _format.SelectedIndex = 0;

        var pick = Ui.Card(L.T("File audio"), null,
            Ui.Row(Ui.Btn(L.T("Scegli file…"), SymbolRegular.FolderOpen24, (_, _) => Pick()), _src));
        ((StackPanel)pick.Child).Children.OfType<StackPanel>().First().Margin = new Thickness(0, 10, 0, 0);

        var trim = Ui.Card(L.T("Modifiche"), null,
            Ui.Row(Ui.Label(L.T("Taglia da (s):"), bold: true), new Border { Width = 8 }, _trimStart,
                new Border { Width = 18 }, Ui.Label(L.T("a (s):"), bold: true), new Border { Width = 8 }, _trimEnd),
            Ui.Row(Ui.Label(L.T("Dissolvenza in entrata (s):"), bold: true), new Border { Width = 8 }, _fadeIn,
                new Border { Width = 18 }, Ui.Label(L.T("in uscita (s):"), bold: true), new Border { Width = 8 }, _fadeOut),
            Ui.Row(Ui.Label(L.T("Volume (dB):"), bold: true), new Border { Width = 8 }, _gain,
                new Border { Width = 24 }, Ui.Label(L.T("Normalizza:"), bold: true), new Border { Width = 8 }, _normalize,
                new Border { Width = 24 }, Ui.Label(L.T("Riduci rumore:"), bold: true), new Border { Width = 8 }, _denoise));
        foreach (var r in ((StackPanel)trim.Child).Children.OfType<StackPanel>()) r.Margin = new Thickness(0, 10, 0, 0);

        _goBtn = Ui.Btn(L.T("Elabora e salva"), SymbolRegular.Save24, async (_, _) => await Run(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;
        var save = Ui.Card(L.T("Esporta"), null,
            Ui.Row(Ui.Label(L.T("Formato:"), bold: true), new Border { Width = 8 }, _format,
                new Border { Width = 18 }, _goBtn, _stopBtn),
            _bar, _status);
        ((StackPanel)save.Child).Children.OfType<StackPanel>().First().Margin = new Thickness(0, 10, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Editor audio"), L.T("Taglia, aggiungi dissolvenze, regola il volume e pulisci l'audio.")),
            pick, trim, save);
    }

    public bool Accepts(string path) => AudioExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void OpenFile(string path)
    {
        if (!Accepts(path)) return;
        _file = path;
        _src.Text = path;
        _duration = 0;
        var ff = Ffmpeg.Find();
        if (ff != null)
        {
            try { _duration = Ffmpeg.Duration(ff, path); } catch { }
        }
        _status.Text = _duration > 0 ? L.T($"Durata: {Util.HumanTime(_duration)}") : "";
    }

    private void Pick()
    {
        var f = Dlg.OpenFile(L.T("Scegli un file audio"),
            L.T("Audio|*.mp3;*.wav;*.m4a;*.flac;*.aac;*.ogg;*.wma;*.opus;*.aiff|Tutti i file|*.*"), "audioedit");
        if (f != null) OpenFile(f);
    }

    private async Task Run()
    {
        if (_file == null) { Dlg.Info(L.T("Scegli un file audio.")); return; }
        var fmt = ((string)_format.SelectedItem).ToLowerInvariant();
        var suggested = Path.GetFileNameWithoutExtension(_file) + L.T(" (modificato)") + AudioEdit.Extension(fmt);
        var dst = Dlg.SaveFile(L.T("Salva l'audio"), suggested,
            $"{_format.SelectedItem}|*{AudioEdit.Extension(fmt)}", "audioedit");
        if (dst == null) return;

        var opts = new AudioEdit.Options(
            TrimStart: _trimStart.Value is > 0 ? _trimStart.Value : null,
            TrimEnd: _trimEnd.Value is > 0 ? _trimEnd.Value : null,
            FadeIn: _fadeIn.Value ?? 0, FadeOut: _fadeOut.Value ?? 0,
            Normalize: _normalize.IsChecked == true, Denoise: _denoise.IsChecked == true,
            GainDb: _gain.Value ?? 0, Format: fmt);

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
            if (_duration <= 0) { try { _duration = Ffmpeg.Duration(ffmpeg, _file); } catch { } }

            _status.Text = L.T("Elaborazione…");
            await Task.Run(() => AudioEdit.Process(ffmpeg, _file, dst, opts, _duration,
                p => Dispatcher.Invoke(() => _bar.Value = p), ct), ct);
            _status.Text = L.T("Fatto.");
            Util.Reveal(dst);
            MainWindow.Notify(L.T("Audio salvato."), 8);
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
