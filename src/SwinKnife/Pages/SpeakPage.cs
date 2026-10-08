using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Legge ad alta voce un testo (sintesi vocale di Windows) e lo salva come WAV o MP3.</summary>
public sealed class SpeakPage : UserControl, IToolPage
{
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 320,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8),
    };
    private readonly ComboBox _voice = new() { Width = 320, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _rate = new() { Minimum = -10, Maximum = 10, Value = 0, Width = 180, VerticalAlignment = VerticalAlignment.Center, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly SpeechSynthesizer _synth = new();
    private readonly Wpf.Ui.Controls.Button _speakBtn;

    public SpeakPage(MainWindow main)
    {
        foreach (var v in InstalledVoices()) _voice.Items.Add(v);
        if (_voice.Items.Count > 0) _voice.SelectedIndex = 0;
        _text.Text = L.T("Scrivi qui il testo da ascoltare.");

        _speakBtn = Ui.Btn(L.T("Leggi"), SymbolRegular.Play24, (_, _) => Speak(), primary: true);
        var card = Ui.Card(L.T("Testo"), null,
            _text,
            Ui.Row(Ui.Label(L.T("Voce:")), new Border { Width = 8 }, _voice, new Border { Width = 16 }, Ui.Label(L.T("Velocità:")), new Border { Width = 8 }, _rate),
            Ui.Row(_speakBtn,
                   Ui.Btn(L.T("Ferma"), SymbolRegular.Stop24, (_, _) => _synth.SpeakAsyncCancelAll()),
                   Ui.Btn(L.T("Salva WAV…"), SymbolRegular.Save24, async (_, _) => await Save(false)),
                   Ui.Btn(L.T("Salva MP3…"), SymbolRegular.MusicNote224, async (_, _) => await Save(true))));
        var kids = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        kids[0].Margin = new Thickness(0, 10, 0, 0);
        kids[1].Margin = new Thickness(0, 12, 0, 0);

        if (_voice.Items.Count == 0)
            ((StackPanel)card.Child).Children.Insert(0, new InfoBar
            {
                Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
                Title = L.T("Nessuna voce installata"),
                Message = L.T("Aggiungi una voce da Impostazioni di Windows › Ora e lingua › Voce."),
                Margin = new Thickness(0, 0, 0, 10),
            });

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Sintesi vocale"), L.T("Trasforma un testo in voce e salvalo come file audio.")),
            card);
    }

    private static List<string> InstalledVoices()
    {
        try { using var s = new SpeechSynthesizer(); return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList(); }
        catch { return new(); }
    }

    private void Prepare(SpeechSynthesizer s)
    {
        if (_voice.SelectedItem is string v) try { s.SelectVoice(v); } catch { }
        s.Rate = (int)_rate.Value;
    }

    private void Speak()
    {
        var t = _text.Text;
        if (t.Trim().Length == 0) return;
        _synth.SpeakAsyncCancelAll();
        Prepare(_synth);
        _synth.SpeakAsync(t);
    }

    private async Task Save(bool mp3)
    {
        var t = _text.Text;
        if (t.Trim().Length == 0) { Dlg.Info(L.T("Scrivi prima un testo.")); return; }
        var voice = _voice.SelectedItem as string;
        var rate = (int)_rate.Value;

        string? mp3Out = null, exe = null;
        string? wavOut;
        if (mp3)
        {
            exe = await Ffmpeg.EnsureAsync(() => Dlg.Confirm(L.T("Per salvare in MP3 serve ffmpeg. Scaricarlo ora (una volta sola)?")), null, CancellationToken.None);
            if (exe == null) return;
            mp3Out = Dlg.SaveFile(L.T("Salva l'audio"), "voce.mp3", L.T("Audio MP3|*.mp3"), "tts_save");
            if (mp3Out == null) return;
            wavOut = Path.Combine(AppInfo.TempDir, "tts_" + Guid.NewGuid().ToString("N") + ".wav");
            Directory.CreateDirectory(AppInfo.TempDir);
        }
        else
        {
            wavOut = Dlg.SaveFile(L.T("Salva l'audio"), "voce.wav", L.T("Audio WAV|*.wav"), "tts_save");
            if (wavOut == null) return;
        }

        try
        {
            await Task.Run(() =>
            {
                using var s = new SpeechSynthesizer();
                if (voice != null) try { s.SelectVoice(voice); } catch { }
                s.Rate = rate;
                s.SetOutputToWaveFile(wavOut);
                s.Speak(t);
                s.SetOutputToNull();
                if (mp3 && exe != null && mp3Out != null)
                {
                    Ffmpeg.Run(exe, ["-i", wavOut, "-codec:a", "libmp3lame", "-qscale:a", "2", mp3Out], 0, _ => { }, CancellationToken.None);
                    try { File.Delete(wavOut); } catch { }
                }
            });
            Util.Reveal(mp3 ? mp3Out! : wavOut);
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    public bool CanClose() { _synth.SpeakAsyncCancelAll(); return true; }
    public void Shutdown() { try { _synth.SpeakAsyncCancelAll(); _synth.Dispose(); } catch { } }
}
