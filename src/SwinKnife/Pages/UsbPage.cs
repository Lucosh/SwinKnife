using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Crea chiavette USB avviabili da un'immagine ISO/IMG (come Rufus) e fa il backup/ripristino di chiavette e schede SD.</summary>
public sealed class UsbPage : UserControl, IToolPage
{
    private readonly ComboBox _disks = new() { MinWidth = 360, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showFixed = new() { Content = L.T("Mostra anche i dischi esterni fissi"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _imagePath = Ui.Hint(L.T("Nessun file scelto"));
    private readonly Button _pickBtn, _writeBtn, _backupBtn, _refreshBtn;
    private readonly ProgressBar _bar = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = Ui.Label("");
    private string? _image;
    private CancellationTokenSource? _cts;

    public UsbPage(MainWindow main)
    {
        if (!RawSource.IsAdmin())
        {
            Content = NeedsAdmin();
            return;
        }

        _refreshBtn = Ui.IconBtn(SymbolRegular.ArrowClockwise24, L.T("Aggiorna l'elenco"), (_, _) => LoadDisks());
        _showFixed.Checked += (_, _) => LoadDisks();
        _showFixed.Unchecked += (_, _) => LoadDisks();

        _pickBtn = Ui.Btn(L.T("Scegli immagine…"), SymbolRegular.DocumentArrowDown24, (_, _) => PickImage());
        _writeBtn = Ui.Btn(L.T("Scrivi sulla USB"), SymbolRegular.Flash24, async (_, _) => await Write(), primary: true);
        _backupBtn = Ui.Btn(L.T("Backup della USB…"), SymbolRegular.Save24, async (_, _) => await Backup());

        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
            Title = L.T("Attenzione: cancella tutto"),
            Message = L.T("Scrivere un'immagine cancella completamente la chiavetta scelta. Controlla bene di aver selezionato l'unità giusta."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var writeCard = Ui.Card(L.T("Crea una USB avviabile"),
            L.T("Scrivi un file ISO o IMG (Windows, Linux, strumenti di avvio) sulla chiavetta per renderla avviabile."),
            Ui.Row(_pickBtn, _imagePath), Ui.Row(_writeBtn));

        var backupCard = Ui.Card(L.T("Backup e ripristino"),
            L.T("Salva una copia esatta della chiavetta o scheda SD in un file .img, da riscrivere all'occorrenza (il ripristino usa «Scrivi sulla USB» scegliendo il file .img)."),
            Ui.Row(_backupBtn));

        var diskCard = Ui.Card(L.T("Unità USB"), null, Ui.Row(_disks, _refreshBtn), Ui.Row(_showFixed));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("USB avviabile"), L.T("Crea chiavette avviabili e fai il backup di chiavette e schede SD.")),
            warn, diskCard, writeCard, backupCard, _bar, _status);

        Loaded += (_, _) => { if (_disks.Items.Count == 0) LoadDisks(); };
    }

    public void Shutdown() => _cts?.Cancel();

    private FrameworkElement NeedsAdmin()
    {
        var p = Ui.Placeholder(SymbolRegular.ShieldKeyhole24,
            L.T("Per creare una USB avviabile servono i privilegi di amministratore."), out _);
        var btn = Ui.Btn(L.T("Riavvia come amministratore"), SymbolRegular.ArrowClockwise24,
            (_, _) => { if (RawSource.RelaunchAsAdmin("--page usb")) Application.Current.Shutdown(); }, primary: true);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(p);
        var row = Ui.Row(btn);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(row);
        return new Grid { Children = { panel } };
    }

    private void LoadDisks()
    {
        _disks.Items.Clear();
        foreach (var d in UsbImaging.List(_showFixed.IsChecked == true))
            _disks.Items.Add(new DiskItem(d));
        if (_disks.Items.Count > 0) _disks.SelectedIndex = 0;
        else _status.Text = L.T("Nessuna chiavetta USB collegata.");
    }

    private sealed record DiskItem(UsbDisk Disk) { public override string ToString() => Disk.Title; }

    private UsbDisk? Selected => (_disks.SelectedItem as DiskItem)?.Disk;

    private void PickImage()
    {
        var file = Dlg.OpenFile(L.T("Scegli un'immagine"), L.T("Immagini disco|*.iso;*.img;*.bin|Tutti i file|*.*"));
        if (file == null) return;
        _image = file;
        _imagePath.Text = System.IO.Path.GetFileName(file) + "  ·  " + Util.HumanSize(new System.IO.FileInfo(file).Length);
    }

    private async Task Write()
    {
        var disk = Selected;
        if (disk == null) { Dlg.Error(L.T("Scegli prima una chiavetta USB.")); return; }
        if (_image == null) { Dlg.Error(L.T("Scegli prima un file immagine (ISO/IMG).")); return; }

        // doppia conferma: la seconda richiede di confermare esplicitamente il nome del disco
        if (!Dlg.Confirm(L.T($"Verrà CANCELLATO tutto il contenuto di:\n\n{disk.Title}\n\ne sostituito con l'immagine scelta. Continuare?"),
                AppInfo.Name, L.T("Continua"), L.T("Annulla"))) return;
        var confirm = Dlg.Prompt(L.T("Conferma finale"),
            L.T($"Per confermare la cancellazione, scrivi SI maiuscolo.\nUnità: {disk.Title}"));
        if (confirm?.Trim().ToUpperInvariant() is not ("SI" or "SÌ" or "SÍ" or "YES" or "OUI" or "JA")) return;

        Busy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => UsbImaging.WriteImageAsync(disk, _image!,
                (f, mb) => Dispatcher.InvokeAsync(() => { _bar.Value = f; _status.Text = L.T($"Scrittura: {(int)(f * 100)}%  ·  {mb:0} MB/s"); }), _cts.Token));
            _status.Text = L.T("Fatto! La chiavetta è pronta. Puoi rimuoverla in sicurezza.");
            Dlg.Info(L.T("Scrittura completata."));
            LoadDisks();
        }
        catch (OperationCanceledException) { _status.Text = L.T("Operazione annullata."); }
        catch (Exception ex) { _status.Text = L.T("Errore: ") + ex.Message; Dlg.Error(_status.Text); }
        finally { Busy(false); }
    }

    private async Task Backup()
    {
        var disk = Selected;
        if (disk == null) { Dlg.Error(L.T("Scegli prima una chiavetta USB.")); return; }
        var outPath = Dlg.SaveFile(L.T("Salva l'immagine"), $"{Util.SafeFileName(disk.Model)}.img", L.T("Immagine disco|*.img"));
        if (outPath == null) return;

        Busy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => UsbImaging.ReadImageAsync(disk, outPath,
                (f, mb) => Dispatcher.InvokeAsync(() => { _bar.Value = f; _status.Text = L.T($"Backup: {(int)(f * 100)}%  ·  {mb:0} MB/s"); }), _cts.Token));
            _status.Text = L.T("Backup completato.");
            Dlg.Info(_status.Text);
        }
        catch (OperationCanceledException) { _status.Text = L.T("Operazione annullata."); }
        catch (Exception ex) { _status.Text = L.T("Errore: ") + ex.Message; Dlg.Error(_status.Text); }
        finally { Busy(false); }
    }

    private void Busy(bool on)
    {
        _writeBtn.IsEnabled = _backupBtn.IsEnabled = _pickBtn.IsEnabled = _disks.IsEnabled = _refreshBtn.IsEnabled = _showFixed.IsEnabled = !on;
        _bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }
}
