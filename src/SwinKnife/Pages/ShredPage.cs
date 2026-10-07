using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Elimina file e cartelle in modo che nessun programma di recupero li possa ritrovare.</summary>
public sealed class ShredPage : UserControl, IToolPage
{
    private readonly ListBox _list = new() { MinHeight = 160, BorderThickness = new Thickness(0), Background = Brushes.Transparent, SelectionMode = SelectionMode.Extended };
    private readonly ComboBox _passes = new() { MinWidth = 300 };
    private readonly ComboBox _drives = new() { Width = 300 };
    private readonly TextBlock _summary = Ui.Hint();
    private readonly TextBlock _status = Ui.Hint();
    private readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _go, _wipe, _stop;
    private readonly List<string> _items = new();
    private CancellationTokenSource? _cts;

    public ShredPage(MainWindow main)
    {
        _passes.Items.Add(L.T("1 passaggio, dati casuali (consigliato)"));
        _passes.Items.Add(L.T("3 passaggi (casuale, zeri, casuale)"));
        _passes.SelectedIndex = 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                    _drives.Items.Add(new ComboBoxItem { Content = L.T($"{d.Name}  {d.VolumeLabel}  —  {Util.HumanSize(d.AvailableFreeSpace)} liberi"), Tag = d.Name });
            }
            catch { }
        }
        if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;

        _go = Ui.Btn(L.T("Elimina definitivamente"), SymbolRegular.Delete24, async (_, _) => await Shred(), primary: true);
        _wipe = Ui.Btn(L.T("Pulisci lo spazio libero"), SymbolRegular.Broom24, async (_, _) => await Wipe());
        _stop = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stop.Visibility = Visibility.Collapsed;
        var add = Ui.Row(
            Ui.Btn(L.T("Aggiungi file…"), SymbolRegular.DocumentAdd24, (_, _) => AddFiles(Dlg.OpenFiles(L.T("File da eliminare"), key: "shred"))),
            Ui.Btn(L.T("Aggiungi cartella…"), SymbolRegular.FolderAdd24, (_, _) => { if (Dlg.PickFolder(L.T("Cartella da eliminare"), "shred.dir") is { } d) AddFiles([d]); }),
            Ui.Btn(L.T("Togli"), SymbolRegular.Dismiss24, (_, _) =>
            {
                foreach (var p in _list.SelectedItems.OfType<ListBoxItem>().Select(i => (string)i.Tag).ToList()) _items.Remove(p);
                Fill();
            }));
        var filesCard = Ui.Card(L.T("File e cartelle"), L.T("Trascina qui ciò che vuoi far sparire per sempre: il contenuto viene sovrascritto, il nome cambiato e poi il file eliminato. Non passa dal Cestino e non si può annullare."),
            add, new Border { BorderBrush = Ui.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 10, 0, 10), Child = _list },
            Ui.Row(Ui.Label(L.T("Metodo")), new Border { Width = 8 }, _passes, new Border { Width = 16 }, _go, _summary));
        var wipeCard = Ui.Card(L.T("Spazio libero"), L.T("I file che hai già eliminato in passato (anche svuotando il Cestino) restano sul disco finché non vengono sovrascritti. Questa funzione riempie temporaneamente lo spazio libero e poi lo libera: può richiedere molto tempo."),
            Ui.Row(_drives, new Border { Width = 12 }, _wipe));
        var note = Ui.Hint(L.T("Sugli SSD il disco può spostare i dati internamente: la sovrascrittura resta molto efficace ma non è garantita al 100%. Per i dati più delicati usa anche la crittografia (BitLocker)."));
        note.TextWrapping = TextWrapping.Wrap;
        var progress = new StackPanel();
        progress.Children.Add(_progress);
        progress.Children.Add(Ui.Row(_status, new Border { Width = 12 }, _stop));
        Content = Ui.ScrollPage(Ui.Header(L.T("Eliminazione sicura"), L.T("Cancella file e cartelle in modo che nessun programma di recupero li possa ritrovare.")),
            filesCard, wipeCard, progress, note);
        Fill();
    }

    public bool Accepts(string path) => true;
    public void OpenFile(string path) => AddFiles([path]);
    public void AddFiles(IReadOnlyList<string> paths) => AddFiles(paths.ToArray());

    private void AddFiles(string[] paths)
    {
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            // niente radici di unità o cartelle di sistema
            if (Path.GetPathRoot(full) == full || full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
            if (!_items.Contains(full, StringComparer.OrdinalIgnoreCase)) _items.Add(full);
        }
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var p in _items)
            _list.Items.Add(new ListBoxItem
            {
                Tag = p,
                Content = Ui.Row(new Image { Source = ShellIcons.For(p), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }, Ui.Label(p)),
            });
        if (_items.Count == 0) _list.Items.Add(new ListBoxItem { Content = Ui.Hint(L.T("Nessun elemento: trascinali qui.")), IsEnabled = false });
        _go.IsEnabled = _items.Count > 0;
        _summary.Text = _items.Count == 0 ? "" : "  " + L.T($"{_items.Count} elementi");
    }

    private void Busy(bool on)
    {
        _progress.Visibility = _stop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _go.IsEnabled = !on && _items.Count > 0;
        _wipe.IsEnabled = !on;
        _progress.Value = 0;
    }

    private async Task Shred()
    {
        var items = _items.ToList();
        var total = await Task.Run(() => Shredder.TotalSize(items));
        if (!Dlg.Confirm(L.T($"Eliminare DEFINITIVAMENTE {items.Count} elementi ({Util.HumanSize(total)})?\n\nNon passano dal Cestino e non si potranno recuperare in nessun modo."), L.T("Eliminazione sicura"), L.T("Elimina per sempre"), L.T("Annulla")))
            return;
        var passes = _passes.SelectedIndex == 1 ? 3 : 1;
        _cts = new CancellationTokenSource();
        Busy(true);
        try
        {
            var (files, errors) = await Task.Run(() => Shredder.Shred(items, passes, (done, f) => Dispatcher.InvokeAsync(() =>
            {
                _progress.Value = total > 0 ? done / (double)total : 0;
                _status.Text = Path.GetFileName(f);
            }), _cts.Token));
            _items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
            Fill();
            _status.Text = L.T($"{files} file eliminati in modo sicuro.");
            if (errors.Count > 0)
            {
                AppInfo.Log("Eliminazione sicura:\n" + string.Join("\n", errors));
                Dlg.Error(L.T($"{errors.Count} elementi non eliminati (forse aperti in un altro programma):\n\n") + string.Join("\n", errors.Take(8)));
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("Interrotto: i file non ancora elaborati sono rimasti.");
        }
        finally
        {
            Busy(false);
        }
    }

    private async Task Wipe()
    {
        if (_drives.SelectedItem is not ComboBoxItem { Tag: string drive }) return;
        if (!Dlg.Confirm(L.T($"Pulire lo spazio libero di {drive}?\n\nPer qualche tempo il disco risulterà quasi pieno; i tuoi file non vengono toccati. Puoi interrompere quando vuoi."))) return;
        _cts = new CancellationTokenSource();
        Busy(true);
        try
        {
            await Task.Run(() => Shredder.WipeFreeSpace(drive, (p, msg) => Dispatcher.InvokeAsync(() =>
            {
                _progress.Value = p;
                _status.Text = msg;
            }), _cts.Token));
            _status.Text = L.T($"Spazio libero di {drive} pulito.");
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("Interrotto: lo spazio occupato temporaneamente è stato liberato.");
        }
        catch (Exception ex)
        {
            Dlg.Error(ex.Message);
        }
        finally
        {
            Busy(false);
        }
    }
}
