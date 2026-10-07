using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Registra l'audio dal microfono e lo salva in WAV (o MP3 se è disponibile ffmpeg).</summary>
public sealed class AudioRecorderPage : UserControl, IToolPage
{
    private readonly Wpf.Ui.Controls.Button _recBtn, _playBtn, _saveBtn, _mp3Btn;
    private readonly TextBlock _time = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly MediaPlayer _player = new();

    private AudioRec? _rec;
    private DateTime _started;
    private string? _tempWav;

    public AudioRecorderPage(MainWindow main)
    {
        _recBtn = Ui.Btn(L.T("Registra"), SymbolRegular.Record24, (_, _) => Toggle(), primary: true);
        _playBtn = Ui.Btn(L.T("Ascolta"), SymbolRegular.Play24, (_, _) => Play());
        _saveBtn = Ui.Btn(L.T("Salva WAV…"), SymbolRegular.Save24, (_, _) => Save(false));
        _mp3Btn = Ui.Btn(L.T("Salva MP3…"), SymbolRegular.MusicNote224, async (_, _) => await Save(true));
        _playBtn.IsEnabled = _saveBtn.IsEnabled = _mp3Btn.IsEnabled = false;

        _timer.Tick += (_, _) => _time.Text = (DateTime.Now - _started).ToString(@"mm\:ss");

        var card = Ui.Card(L.T("Microfono"),
            L.T("Premi Registra, parla, poi premi di nuovo per fermarti. Puoi riascoltare e salvare."),
            _time, Ui.Row(_recBtn, _playBtn, _saveBtn, _mp3Btn), _status);
        ((StackPanel)card.Child).Children.OfType<StackPanel>().First().Margin = new Thickness(0, 10, 0, 0);
        _status.Margin = new Thickness(0, 10, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Registratore audio"), L.T("Memo vocali e registrazioni dal microfono, senza installare nulla.")),
            card);
    }

    private void Toggle()
    {
        if (_rec != null) StopRec();
        else StartRec();
    }

    private void StartRec()
    {
        try
        {
            _player.Stop();
            _rec = new AudioRec();
            _rec.Start();
            _started = DateTime.Now;
            _timer.Start();
            _time.Text = "00:00";
            _recBtn.Content = L.T("Ferma");
            _recBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24 };
            _playBtn.IsEnabled = _saveBtn.IsEnabled = _mp3Btn.IsEnabled = false;
            _status.Text = L.T("Registrazione in corso…");
        }
        catch (Exception ex)
        {
            _rec?.Dispose();
            _rec = null;
            Dlg.Error(L.T("Impossibile avviare la registrazione (controlla che ci sia un microfono):\n") + ex.Message);
        }
    }

    private void StopRec()
    {
        _timer.Stop();
        try
        {
            Directory.CreateDirectory(AppInfo.TempDir);
            var tmp = Path.Combine(AppInfo.TempDir, "rec_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
            _rec!.Stop(tmp);
            _tempWav = tmp;
            _playBtn.IsEnabled = _saveBtn.IsEnabled = true;
            _mp3Btn.IsEnabled = true;
            _status.Text = L.T("Registrazione pronta. Riascoltala o salvala.");
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
        finally
        {
            _rec?.Dispose();
            _rec = null;
            _recBtn.Content = L.T("Registra");
            _recBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Record24 };
        }
    }

    private void Play()
    {
        if (_tempWav == null || !File.Exists(_tempWav)) return;
        _player.Open(new Uri(_tempWav));
        _player.Play();
        _status.Text = L.T("Riproduzione…");
    }

    private async Task Save(bool mp3)
    {
        if (_tempWav == null || !File.Exists(_tempWav)) return;
        if (!mp3)
        {
            var dst = Dlg.SaveFile(L.T("Salva la registrazione"), "registrazione.wav", L.T("Audio WAV|*.wav"), "rec_save");
            if (dst == null) return;
            try { File.Copy(_tempWav, dst, true); Util.Reveal(dst); }
            catch (Exception ex) { Dlg.Error(ex.Message); }
            return;
        }
        var exe = await Ffmpeg.EnsureAsync(() => Dlg.Confirm(L.T("Per salvare in MP3 serve ffmpeg. Scaricarlo ora (una volta sola)?")), null, CancellationToken.None);
        if (exe == null) return;
        var out3 = Dlg.SaveFile(L.T("Salva la registrazione"), "registrazione.mp3", L.T("Audio MP3|*.mp3"), "rec_save");
        if (out3 == null) return;
        try
        {
            var src = _tempWav;
            await Task.Run(() => Ffmpeg.Run(exe, ["-i", src, "-codec:a", "libmp3lame", "-qscale:a", "2", out3], 0, _ => { }, CancellationToken.None));
            Util.Reveal(out3);
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    public bool CanClose()
    {
        if (_rec != null) { StopRec(); }
        return true;
    }

    public void Shutdown()
    {
        _timer.Stop();
        _rec?.Dispose();
        _player.Close();
    }
}
