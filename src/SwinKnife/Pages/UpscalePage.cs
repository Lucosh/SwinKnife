using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Ingrandisce e ripulisce le foto a bassa risoluzione con l'AI (Real-ESRGAN, sul PC).</summary>
public sealed class UpscalePage : UserControl, IToolPage
{
    private readonly TextBlock _fileName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _scale = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _type = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly Wpf.Ui.Controls.Button _goBtn, _stopBtn;
    private string? _image;
    private CancellationTokenSource? _cts;

    public UpscalePage(MainWindow main)
    {
        foreach (var s in new[] { L.T("2× più grande"), L.T("4× più grande") }) _scale.Items.Add(s);
        _scale.SelectedIndex = 1;
        foreach (var s in new[] { L.T("Foto reali"), L.T("Disegni e anime") }) _type.Items.Add(s);
        _type.SelectedIndex = 0;

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Usa l'intelligenza artificiale sul tuo PC per aumentare la risoluzione e ridurre il rumore. Al primo uso scarica lo strumento (~50 MB). Serve una scheda grafica compatibile con Vulkan (quasi tutte le recenti)."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _goBtn = Ui.Btn(L.T("Ingrandisci"), SymbolRegular.ArrowExpand24, async (_, _) => await Run(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;

        var card = Ui.Card(L.T("Immagine"), null,
            Ui.Row(Ui.Btn(L.T("Scegli immagine…"), SymbolRegular.Image24, (_, _) =>
            {
                var f = Dlg.OpenFile(L.T("Immagine da ingrandire"), L.T("Immagini|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Tutti i file|*.*"), "upscale");
                if (f != null) { _image = f; _fileName.Text = Path.GetFileName(f); }
            }, primary: true), _fileName),
            Ui.Row(Ui.Label(L.T("Ingrandimento:")), new Border { Width = 8 }, _scale, _type),
            Ui.Row(_goBtn, _stopBtn),
            _bar, _status);
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[1].Margin = new Thickness(0, 10, 0, 0);
        rows[2].Margin = new Thickness(0, 12, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Ingrandisci foto (AI)"), L.T("Più dettaglio e meno sfocatura nelle foto piccole, con l'intelligenza artificiale.")),
            info, card);
    }

    public bool Accepts(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
    public void OpenFile(string path) { _image = path; _fileName.Text = Path.GetFileName(path); }

    private async Task Run()
    {
        if (_image == null) { Dlg.Info(L.T("Scegli prima un'immagine.")); return; }
        var dst = Dlg.SaveFile(L.T("Salva l'immagine ingrandita"), Path.GetFileNameWithoutExtension(_image) + "_ingrandito.png", L.T("Immagine PNG|*.png"), "upscale_save");
        if (dst == null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _goBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        try
        {
            if (!Upscale.Ready && !Dlg.Confirm(L.T("Al primo uso va scaricato lo strumento di ingrandimento (~50 MB). Procedere?"))) { Reset(); return; }
            _status.Text = L.T("Preparazione…");
            await Upscale.EnsureAsync(new Progress<double>(p => Dispatcher.Invoke(() => _bar.Value = p)), ct);

            _bar.IsIndeterminate = true;
            _status.Text = L.T("Ingrandimento in corso…");
            var scale = _scale.SelectedIndex == 0 ? 2 : 4;
            var model = _type.SelectedIndex == 1 ? "realesrgan-x4plus-anime" : "realesrgan-x4plus";
            await Upscale.RunAsync(_image, dst, scale, model, line => Dispatcher.InvokeAsync(() => _status.Text = line), ct);
            _status.Text = L.T("Fatto.");
            Util.Reveal(dst);
            MainWindow.Notify(L.T("Immagine ingrandita."), 8);
        }
        catch (OperationCanceledException) { _status.Text = L.T("Interrotto."); }
        catch (Exception ex) { Dlg.Error(ex.Message); _status.Text = ""; }
        finally { Reset(); }
    }

    private void Reset()
    {
        _bar.Visibility = Visibility.Collapsed;
        _bar.IsIndeterminate = false;
        _goBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        _cts?.Dispose();
        _cts = null;
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un'elaborazione in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
