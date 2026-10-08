using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Trasforma le foto di documenti in un PDF pulito: raddrizza, migliora i contrasti, unisce le pagine.</summary>
public sealed class DocScanPage : UserControl, IToolPage
{
    private static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".tif", ".tiff"];

    private readonly List<string> _files = new();
    private readonly StackPanel _list = new();
    private readonly ComboBox _mode = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _deskew = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 6), Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private CancellationTokenSource? _cts;

    public DocScanPage(MainWindow main)
    {
        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) AddFiles(f); };

        foreach (var m in new[] { L.T("Colore migliorato"), L.T("Scala di grigi"), L.T("Bianco e nero") }) _mode.Items.Add(m);
        _mode.SelectedIndex = 0;

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come usarlo"),
            Message = L.T("Aggiungi le foto dei fogli (scattate il più possibile dritte e ben illuminate): SwinKnife le raddrizza, ne migliora la leggibilità e le unisce in un unico PDF, una pagina per foto."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var top = Ui.Card(L.T("Pagine"), null,
            Ui.Row(
                Ui.Btn(L.T("Aggiungi foto…"), SymbolRegular.ImageAdd24, (_, _) => Pick()),
                Ui.Btn(L.T("Svuota"), SymbolRegular.Delete24, (_, _) => { _files.Clear(); Render(); })),
            _list);
        ((StackPanel)top.Child).Children.OfType<StackPanel>().First().Margin = new Thickness(0, 10, 0, 10);

        var opts = Ui.Card(L.T("Resa e salvataggio"), null,
            Ui.Row(Ui.Label(L.T("Aspetto:"), bold: true), new Border { Width = 10 }, _mode,
                new Border { Width = 24 }, Ui.Label(L.T("Raddrizza:"), bold: true), new Border { Width = 8 }, _deskew),
            Ui.Row(
                Ui.Btn(L.T("Salva come PDF"), SymbolRegular.DocumentPdf24, async (_, _) => await SavePdf(), primary: true),
                Ui.Btn(L.T("Salva immagini migliorate"), SymbolRegular.ImageMultiple24, async (_, _) => await SaveImages())),
            _bar, _status);
        var orows = ((StackPanel)opts.Child).Children.OfType<StackPanel>().ToList();
        orows[0].Margin = new Thickness(0, 10, 0, 0);
        orows[1].Margin = new Thickness(0, 14, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Scanner documenti"), L.T("Dalle foto dei fogli a un PDF pulito e leggibile.")),
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
        var files = Dlg.OpenFiles(L.T("Scegli le foto dei documenti"),
            L.T("Immagini|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif;*.bmp;*.tif;*.tiff|Tutti i file|*.*"), "docscan");
        if (files.Length > 0) AddFiles(files);
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
        if (_files.Count == 0) { _list.Children.Add(Ui.Hint(L.T("Nessuna pagina. Aggiungi o trascina qui le foto."))); return; }
        for (var i = 0; i < _files.Count; i++)
        {
            var idx = i;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = L.T($"Pagina {i + 1}"), FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
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

    private DocScan.Mode Mode() => (DocScan.Mode)_mode.SelectedIndex;

    private async Task SavePdf()
    {
        if (_files.Count == 0) { Dlg.Info(L.T("Aggiungi almeno una foto.")); return; }
        var dst = Dlg.SaveFile(L.T("Salva il PDF"), "Documento.pdf", L.T("PDF|*.pdf"), "docscan");
        if (dst == null) return;

        var files = _files.ToList();
        var mode = Mode();
        var deskew = _deskew.IsChecked == true;
        await RunBusy(ct => DocScan.BuildPdf(files, dst, mode, deskew,
            (p, s) => Dispatcher.Invoke(() => { _bar.Value = p; _status.Text = s; })), () =>
        {
            Util.Reveal(dst);
            MainWindow.Notify(L.T("PDF creato."), 8);
        });
    }

    private async Task SaveImages()
    {
        if (_files.Count == 0) { Dlg.Info(L.T("Aggiungi almeno una foto.")); return; }
        var dir = Dlg.PickFolder(L.T("Dove salvare le immagini"), "docscan");
        if (dir == null) return;

        var files = _files.ToList();
        var mode = Mode();
        var deskew = _deskew.IsChecked == true;
        await RunBusy(ct =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var dst = Util.UniquePath(Path.Combine(dir, L.T($"Pagina {i + 1}") + ".jpg"));
                DocScan.Enhance(files[i], dst, mode, deskew);
                var p = (i + 1) / (double)files.Count;
                Dispatcher.Invoke(() => { _bar.Value = p; _status.Text = L.T($"Elaboro la pagina {i + 1}…"); });
            }
        }, () =>
        {
            Util.OpenFolder(dir);
            MainWindow.Notify(L.T("Immagini salvate."), 8);
        });
    }

    private async Task RunBusy(Action<CancellationToken> work, Action onDone)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        _status.Text = L.T("Elaborazione…");
        try
        {
            await Task.Run(() => work(ct), ct);
            _status.Text = L.T("Fatto.");
            onDone();
        }
        catch (OperationCanceledException) { _status.Text = L.T("Interrotto."); }
        catch (Exception ex) { _status.Text = ""; Dlg.Error(ex.Message); }
        finally
        {
            _bar.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Shutdown() => _cts?.Cancel();
}
