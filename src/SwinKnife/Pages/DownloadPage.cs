using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Scarica video o audio dal web (yt-dlp). Solo per contenuti di cui hai i diritti.</summary>
public sealed class DownloadPage : UserControl, IToolPage
{
    private readonly TextBox _url = new() { PlaceholderText = L.T("Incolla qui il link del video"), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _mode = new() { Width = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.TextBox _dest = new() { IsReadOnly = true, Width = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 6), Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly Wpf.Ui.Controls.Button _goBtn, _stopBtn;
    private CancellationTokenSource? _cts;

    public DownloadPage(MainWindow main)
    {
        foreach (var s in new[] { L.T("Video (MP4)"), L.T("Solo audio (MP3)") }) _mode.Items.Add(s);
        _mode.SelectedIndex = 0;
        _dest.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
            Title = L.T("Usa solo contenuti tuoi o consentiti"),
            Message = L.T("Scarica soltanto materiale di cui possiedi i diritti o che è liberamente distribuibile. Scaricare contenuti protetti da copyright senza permesso può violare la legge e i termini dei servizi. Al primo uso si scaricano yt-dlp e ffmpeg."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _goBtn = Ui.Btn(L.T("Scarica"), SymbolRegular.ArrowDownload24, async (_, _) => await Run(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;

        var card = Ui.Card(L.T("Link"), null,
            _url,
            Ui.Row(_mode, new Border { Width = 12 }, Ui.Btn(L.T("Cartella…"), SymbolRegular.FolderOpen24, (_, _) => { var d = Dlg.PickFolder(L.T("Dove salvare"), "dl"); if (d != null) _dest.Text = d; }), _dest),
            Ui.Row(_goBtn, _stopBtn),
            _bar, _status);
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        _url.Margin = new Thickness(0, 0, 0, 0);
        rows[0].Margin = new Thickness(0, 10, 0, 0);
        rows[1].Margin = new Thickness(0, 12, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Download video/audio"), L.T("Salva un video o la sua traccia audio da un link.")),
            warn, card);
    }

    private async Task Run()
    {
        var url = _url.Text.Trim();
        if (url.Length == 0) { Dlg.Info(L.T("Incolla un link.")); return; }
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Dlg.Info(L.T("Il link non sembra valido.")); return; }
        var dest = _dest.Text.Trim();
        var audioOnly = _mode.SelectedIndex == 1;

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

            if (!YtDlp.Ready && !Dlg.Confirm(L.T("Al primo uso va scaricato yt-dlp. Procedere?"))) { Reset(); return; }
            await YtDlp.EnsureAsync(new Progress<double>(p => Dispatcher.Invoke(() => _bar.Value = p)), ct);

            _status.Text = L.T("Download in corso…");
            await YtDlp.DownloadAsync(url, dest, audioOnly, Path.GetDirectoryName(ffmpeg)!,
                p => Dispatcher.InvokeAsync(() => _bar.Value = p),
                line => Dispatcher.InvokeAsync(() => _status.Text = Trim(line)), ct);
            _status.Text = L.T("Fatto.");
            Util.OpenFolder(dest);
            MainWindow.Notify(L.T("Download completato."), 8);
        }
        catch (OperationCanceledException) { _status.Text = L.T("Interrotto."); }
        catch (Exception ex) { Dlg.Error(ex.Message); _status.Text = ""; }
        finally { Reset(); }
    }

    private static string Trim(string line) => line.Length > 160 ? line[..160] : line;

    private void Reset()
    {
        _bar.Visibility = Visibility.Collapsed;
        _goBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        _cts?.Dispose();
        _cts = null;
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un download in corso: interromperlo?"));
    public void Shutdown() => _cts?.Cancel();
}
