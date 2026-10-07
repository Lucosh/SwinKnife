using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Toglie i dati nascosti (autore, GPS, software, revisioni) da immagini, PDF, documenti Office e file multimediali.</summary>
public sealed class MetadataPage : UserControl, IToolPage
{
    private readonly ListBox _items = new() { MinHeight = 80, MaxHeight = 240 };
    private readonly CheckBox _overwrite = new() { Content = L.T("Sovrascrivi gli originali (altrimenti crea copie col suffisso «_pulito»)"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _log = new();
    private readonly Wpf.Ui.Controls.Button _cleanBtn;
    private CancellationTokenSource? _cts;

    public MetadataPage(MainWindow main)
    {
        _items.SelectionMode = SelectionMode.Extended;
        var add = Ui.Btn(L.T("Aggiungi file…"), SymbolRegular.DocumentAdd24, (_, _) =>
            AddFiles(Dlg.OpenFiles(L.T("File da ripulire"),
                L.T("Supportati|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.webp;*.heic;*.gif;*.bmp;*.pdf;*.docx;*.xlsx;*.pptx;*.mp4;*.mov;*.mkv;*.mp3;*.m4a;*.wav|Tutti i file|*.*"), "meta")));
        var remove = Ui.Btn(L.T("Rimuovi"), SymbolRegular.Dismiss24, (_, _) =>
        {
            foreach (var s in _items.SelectedItems.Cast<object>().ToList()) _items.Items.Remove(s);
        });
        _cleanBtn = Ui.Btn(L.T("Ripulisci"), SymbolRegular.Wand24, async (_, _) => await Clean(), primary: true);

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Cosa viene tolto"),
            Message = L.T("Dalle foto: posizione GPS, modello della fotocamera e data. Dai PDF e dai documenti Office: autore, azienda e revisioni. Da audio e video: tag e metadati. Trascina qui i file o aggiungili col pulsante."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var card = Ui.Card(L.T("File"), null,
            _items, Ui.Row(add, remove), _overwrite, Ui.Row(_cleanBtn), _bar, _log);
        var kids = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        kids[0].Margin = new Thickness(0, 8, 0, 0);   // riga pulsanti
        kids[1].Margin = new Thickness(0, 14, 0, 0);  // riga ripulisci
        _log.Margin = new Thickness(0, 10, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Pulizia metadati"), L.T("Prima di condividere un file, togli i dati nascosti che potrebbe contenere.")),
            info, card);
    }

    public bool Accepts(string path) => MetadataCleaner.CanClean(path);
    public void AddFiles(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
            if (MetadataCleaner.CanClean(p) && !_items.Items.Contains(p)) _items.Items.Add(p);
    }

    private async Task Clean()
    {
        var files = _items.Items.Cast<string>().ToList();
        if (files.Count == 0) { Dlg.Info(L.T("Aggiungi prima qualche file.")); return; }

        string? ffmpeg = null;
        if (files.Any(MetadataCleaner.NeedsFfmpeg))
        {
            ffmpeg = await Ffmpeg.EnsureAsync(
                () => Dlg.Confirm(L.T("Per i file audio e video serve ffmpeg. Scaricarlo ora (una volta sola)?")),
                null, CancellationToken.None);
            if (ffmpeg == null && !files.All(f => !MetadataCleaner.NeedsFfmpeg(f)))
                MainWindow.Notify(L.T("I file audio/video verranno saltati (ffmpeg non disponibile)."), 8);
        }

        _log.Children.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _cleanBtn.IsEnabled = false;
        _bar.Visibility = Visibility.Visible;
        _bar.Maximum = files.Count;
        _bar.Value = 0;
        var overwrite = _overwrite.IsChecked == true;
        var ff = ffmpeg;

        var ok = 0;
        foreach (var src in files)
        {
            if (ct.IsCancellationRequested) break;
            var dst = overwrite ? src : Path.Combine(Path.GetDirectoryName(src)!,
                Path.GetFileNameWithoutExtension(src) + "_pulito" + Path.GetExtension(src));
            try
            {
                if (MetadataCleaner.NeedsFfmpeg(src) && ff == null) { AddLog(src, L.T("saltato (serve ffmpeg)"), false); }
                else
                {
                    var desc = await Task.Run(() => MetadataCleaner.Clean(src, dst, ff, ct), ct);
                    AddLog(Path.GetFileName(dst), desc, true);
                    ok++;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AddLog(src, ex.Message, false); }
            _bar.Value++;
        }

        _bar.Visibility = Visibility.Collapsed;
        _cleanBtn.IsEnabled = true;
        _cts.Dispose();
        _cts = null;
        MainWindow.Notify(L.T($"Ripuliti {ok} file su {files.Count}."), 8);
    }

    private void AddLog(string file, string message, bool good)
    {
        _log.Children.Add(new TextBlock
        {
            Text = (good ? "✓ " : "✗ ") + Path.GetFileName(file) + " — " + message,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            Foreground = good ? Ui.Res("TextFillColorSecondaryBrush") : Ui.Res("SystemFillColorCriticalBrush"),
        });
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è una pulizia in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
