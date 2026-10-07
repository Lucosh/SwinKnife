using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Verifica l'integrità e la velocità di chiavette e schede SD (stile H2testw): smaschera le memorie farlocche.</summary>
public sealed class StorageTestPage : UserControl, IToolPage
{
    private readonly ComboBox _drives = new() { MinWidth = 320, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _testBtn, _verifyBtn, _deleteBtn, _refreshBtn;
    private readonly ProgressBar _bar = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = Ui.Label("");
    private readonly TextBlock _report = Ui.Label("");
    private CancellationTokenSource? _cts;

    public StorageTestPage(MainWindow main)
    {
        _refreshBtn = Ui.IconBtn(SymbolRegular.ArrowClockwise24, L.T("Aggiorna l'elenco"), (_, _) => LoadDrives());
        _testBtn = Ui.Btn(L.T("Test completo"), SymbolRegular.Play24, async (_, _) => await RunFull(), primary: true);
        _verifyBtn = Ui.Btn(L.T("Solo verifica"), SymbolRegular.CheckmarkCircle24, async (_, _) => await RunVerify());
        _deleteBtn = Ui.Btn(L.T("Elimina file di test"), SymbolRegular.Delete24, (_, _) => DeleteTests());

        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Il test riempie lo spazio libero dell'unità con dati riconoscibili e poi li rilegge. Non tocca i file già presenti, ma usa quasi tutto lo spazio libero e può richiedere parecchio tempo. Alla fine puoi eliminare i file di test."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _report.FontSize = 14;
        _status.Foreground = Ui.Res("TextFillColorSecondaryBrush");

        var card = Ui.Card(L.T("Unità da controllare"), null,
            Ui.Row(_drives, _refreshBtn),
            Ui.Row(_testBtn, _verifyBtn, _deleteBtn),
            _bar, _status, _report);
        ((StackPanel)card.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 10, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Test della memoria"), L.T("Controlla se una chiavetta o una scheda SD è autentica e integra, e misurane la velocità.")),
            info, card);

        Loaded += (_, _) => { if (_drives.Items.Count == 0) LoadDrives(); };
    }

    public void Shutdown() => _cts?.Cancel();

    private void LoadDrives()
    {
        _drives.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                var kind = d.DriveType == DriveType.Removable ? L.T("Rimovibile") : L.T("Disco");
                _drives.Items.Add(new DriveItem(d.RootDirectory.FullName,
                    $"{d.RootDirectory.FullName}  {(string.IsNullOrEmpty(d.VolumeLabel) ? kind : d.VolumeLabel)}  ·  {Util.HumanSize(d.AvailableFreeSpace)} " + L.T("liberi") + $" / {Util.HumanSize(d.TotalSize)}  ·  {kind}"));
            }
            catch { }
        }
        if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
    }

    private sealed record DriveItem(string Root, string Text) { public override string ToString() => Text; }

    private string? SelectedRoot => (_drives.SelectedItem as DriveItem)?.Root;

    private async Task RunFull()
    {
        var root = SelectedRoot;
        if (root == null) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (!Dlg.Confirm(
                L.T($"Il test userà quasi tutto lo spazio libero ({Util.HumanSize(free)}) di {root} e poi lo rileggerà. I tuoi file non vengono toccati. Procedere?"),
                AppInfo.Name, L.T("Avvia il test"), L.T("Annulla"))) return;

        Busy(true);
        _cts = new CancellationTokenSource();
        try
        {
            _report.Text = "";
            var w = await StorageTest.WriteAsync(root, OnProgress, _cts.Token);
            if (!w.Ok) { _report.Text = w.Message; return; }
            if (_cts.IsCancellationRequested) { _report.Text = L.T("Scrittura interrotta. Puoi comunque verificare i dati già scritti."); }
            var v = await StorageTest.VerifyAsync(System.IO.Path.Combine(root, StorageTest.FolderName), OnProgress, _cts.Token);
            ShowResult(w, v);
        }
        catch (Exception ex) { _report.Text = L.T("Errore: ") + ex.Message; }
        finally { Busy(false); }
    }

    private async Task RunVerify()
    {
        var root = SelectedRoot;
        if (root == null) return;
        Busy(true);
        _cts = new CancellationTokenSource();
        try
        {
            var v = await StorageTest.VerifyAsync(System.IO.Path.Combine(root, StorageTest.FolderName), OnProgress, _cts.Token);
            _report.Text = v.Message + (v.ReadMBps > 0 ? "\n" + L.T($"Lettura: {v.ReadMBps:0} MB/s") : "");
            _report.Foreground = Ui.Res(v.Ok ? "TextFillColorPrimaryBrush" : "SystemFillColorCriticalBrush");
        }
        catch (Exception ex) { _report.Text = L.T("Errore: ") + ex.Message; }
        finally { Busy(false); }
    }

    private void DeleteTests()
    {
        var root = SelectedRoot;
        if (root == null) return;
        StorageTest.DeleteTests(root);
        _report.Text = L.T("File di test eliminati.");
        LoadDrives();
    }

    private void ShowResult(StorageReport w, StorageReport v)
    {
        _report.Foreground = Ui.Res(v.Ok ? "TextFillColorPrimaryBrush" : "SystemFillColorCriticalBrush");
        _report.Text = (v.Ok ? "✓ " : "⚠ ") + v.Message
            + $"\n{L.T("Scrittura")}: {w.WriteMBps:0} MB/s · {L.T("Lettura")}: {v.ReadMBps:0} MB/s";
    }

    private void OnProgress(StorageProgress p) => Dispatcher.InvokeAsync(() =>
    {
        _bar.Value = p.Fraction;
        _status.Text = $"{p.Phase}: {(int)(p.Fraction * 100)}%  ·  {p.SpeedMBps:0} MB/s  ·  {Util.HumanSize(p.BytesDone)} / {Util.HumanSize(p.BytesTotal)}";
    });

    private void Busy(bool on)
    {
        _testBtn.IsEnabled = _verifyBtn.IsEnabled = _deleteBtn.IsEnabled = _drives.IsEnabled = _refreshBtn.IsEnabled = !on;
        _bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) _status.Text = "";
    }
}
