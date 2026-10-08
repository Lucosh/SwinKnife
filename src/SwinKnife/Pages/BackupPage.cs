using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Copia o sincronizza una cartella in un'altra (backup incrementale, con mirror facoltativo).</summary>
public sealed class BackupPage : UserControl, IToolPage
{
    private readonly TextBox _src = new() { IsReadOnly = true, Width = 460, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _dst = new() { IsReadOnly = true, Width = 460, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _mode = new() { Width = 420, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 0), Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private readonly Wpf.Ui.Controls.Button _runBtn, _stopBtn;
    private CancellationTokenSource? _cts;

    public BackupPage(MainWindow main)
    {
        foreach (var s in new[] { L.T("Backup: copia solo i file nuovi e modificati"), L.T("Mirror: rendi la destinazione identica (elimina in destinazione ciò che non c'è nell'origine)") })
            _mode.Items.Add(s);
        _mode.SelectedIndex = 0;

        _runBtn = Ui.Btn(L.T("Avvia"), SymbolRegular.Play24, async (_, _) => await Run(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;

        var card = Ui.Card(L.T("Cartelle"), null,
            Ui.Row(Ui.Btn(L.T("Origine…"), SymbolRegular.FolderOpen24, (_, _) => { var d = Dlg.PickFolder(L.T("Cartella di origine"), "backup_src"); if (d != null) _src.Text = d; }), _src),
            Ui.Row(Ui.Btn(L.T("Destinazione…"), SymbolRegular.FolderArrowRight24, (_, _) => { var d = Dlg.PickFolder(L.T("Cartella di destinazione"), "backup_dst"); if (d != null) _dst.Text = d; }), _dst),
            _mode,
            Ui.Row(_runBtn, _stopBtn),
            _bar, _status);
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[1].Margin = new Thickness(0, 6, 0, 0);
        rows[2].Margin = new Thickness(0, 14, 0, 0);
        _status.Margin = new Thickness(0, 8, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Backup e sincronizzazione"), L.T("Tieni al sicuro una copia aggiornata delle tue cartelle.")),
            card);
    }

    private async Task Run()
    {
        var src = _src.Text.Trim();
        var dst = _dst.Text.Trim();
        if (src.Length == 0 || dst.Length == 0) { Dlg.Info(L.T("Scegli cartella di origine e destinazione.")); return; }
        if (!Directory.Exists(src)) { Dlg.Error(L.T("La cartella di origine non esiste.")); return; }
        var mirror = _mode.SelectedIndex == 1;
        if (string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dst).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        { Dlg.Error(L.T("Origine e destinazione non possono essere la stessa cartella.")); return; }
        if (mirror && !Dlg.Confirm(L.T("In modalità Mirror verranno ELIMINATI dalla destinazione i file che non ci sono nell'origine. Procedere?"))) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _runBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        var last = DateTime.MinValue;
        void Report(double f, string name) => Dispatcher.InvokeAsync(() =>
        {
            if ((DateTime.Now - last).TotalMilliseconds < 80 && f < 1) return;
            last = DateTime.Now;
            _bar.Value = f;
            _status.Text = name.Length > 0 ? L.T("Copia: ") + name : "";
        });
        try
        {
            var r = await Task.Run(() => BackupSync.Run(src, dst, mirror, Report, ct), ct);
            _status.Text = L.T($"Fatto: {r.Copied} copiati, {r.Skipped} già aggiornati, {r.Deleted} eliminati · {Util.HumanSize(r.Bytes)} trasferiti.");
            MainWindow.Notify(L.T("Backup completato."), 8);
        }
        catch (OperationCanceledException) { _status.Text = L.T("Interrotto."); }
        catch (Exception ex) { Dlg.Error(ex.Message); _status.Text = ""; }
        finally
        {
            _bar.Visibility = Visibility.Collapsed;
            _runBtn.IsEnabled = true;
            _stopBtn.IsEnabled = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un backup in corso: interromperlo?"));
    public void Shutdown() => _cts?.Cancel();
}
