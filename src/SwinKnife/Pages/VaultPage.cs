using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Protegge con password singoli file (AES-256): li chiude in un .skv e li riapre solo con la password giusta.</summary>
public sealed class VaultPage : UserControl, IToolPage
{
    // cifra
    private readonly TextBlock _encName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly PasswordBox _pw1 = new() { PlaceholderText = L.T("Password"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly PasswordBox _pw2 = new() { PlaceholderText = L.T("Ripeti la password"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _deleteAfter = new() { Content = L.T("Elimina l'originale dopo la cifratura"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _encBtn;
    private string? _encFile;

    // decifra
    private readonly TextBlock _decName = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly PasswordBox _pwDec = new() { PlaceholderText = L.T("Password"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _decBtn;
    private string? _decFile;

    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = Ui.Hint("");
    private CancellationTokenSource? _cts;

    public VaultPage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Attenzione alla password"),
            Message = L.T("La protezione è AES-256: senza la password giusta il file non si recupera in alcun modo, nemmeno da noi. Se la dimentichi, il contenuto è perso."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var encPick = Ui.Btn(L.T("Scegli file da proteggere…"), SymbolRegular.LockClosed24, (_, _) =>
        {
            var f = Dlg.OpenFile(L.T("File da proteggere"), null, "vault");
            if (f != null) SetEnc(f);
        }, primary: true);
        _encBtn = Ui.Btn(L.T("Proteggi"), SymbolRegular.ShieldKeyhole24, async (_, _) => await Encrypt());
        _encBtn.Margin = new Thickness(0, 10, 0, 0);
        var encCard = Ui.Card(L.T("Proteggi un file"), null,
            Ui.Row(encPick, _encName), _pw1, _pw2, _deleteAfter, Ui.Row(_encBtn));

        var decPick = Ui.Btn(L.T("Scegli file .skv da aprire…"), SymbolRegular.LockOpen24, (_, _) =>
        {
            var f = Dlg.OpenFile(L.T("Cassaforte da aprire"), L.T("Cassaforte SwinKnife|*.skv|Tutti i file|*.*"), "vault");
            if (f != null) SetDec(f);
        }, primary: true);
        _decBtn = Ui.Btn(L.T("Apri"), SymbolRegular.LockOpen24, async (_, _) => await Decrypt());
        _decBtn.Margin = new Thickness(0, 10, 0, 0);
        var decCard = Ui.Card(L.T("Apri una cassaforte"), null,
            Ui.Row(decPick, _decName), _pwDec, Ui.Row(_decBtn));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Cassaforte file"), L.T("Chiudi con una password qualsiasi file e riaprilo solo quando serve.")),
            info, encCard, decCard, _bar, _status);
    }

    public bool Accepts(string path) => File.Exists(path);
    public void OpenFile(string path)
    {
        if (path.EndsWith(FileVault.Extension, StringComparison.OrdinalIgnoreCase)) SetDec(path);
        else SetEnc(path);
    }

    private void SetEnc(string f) { _encFile = f; _encName.Text = Path.GetFileName(f) + "  ·  " + Util.HumanSize(Len(f)); }
    private void SetDec(string f) { _decFile = f; _decName.Text = Path.GetFileName(f) + "  ·  " + Util.HumanSize(Len(f)); }
    private static long Len(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

    private async Task Encrypt()
    {
        var src = _encFile;
        if (src == null) { Dlg.Info(L.T("Scegli prima un file da proteggere.")); return; }
        if (_pw1.Password.Length == 0) { Dlg.Info(L.T("Scrivi una password.")); return; }
        if (_pw1.Password != _pw2.Password) { Dlg.Info(L.T("Le due password non coincidono.")); return; }
        var dst = Dlg.SaveFile(L.T("Salva la cassaforte"), Path.GetFileName(src) + FileVault.Extension,
            L.T("Cassaforte SwinKnife|*.skv"), "vault_save");
        if (dst == null) return;
        var pw = _pw1.Password;
        var ok = await Run(L.T("Protezione in corso…"), ct => FileVault.Encrypt(src, dst, pw, Progress, ct));
        if (ok)
        {
            if (_deleteAfter.IsChecked == true) { try { File.Delete(src); } catch { } }
            _pw1.Password = _pw2.Password = "";
            MainWindow.Notify(L.T($"File protetto: {Path.GetFileName(dst)}"), 10);
            Util.Reveal(dst);
        }
    }

    private async Task Decrypt()
    {
        var src = _decFile;
        if (src == null) { Dlg.Info(L.T("Scegli prima una cassaforte da aprire.")); return; }
        if (_pwDec.Password.Length == 0) { Dlg.Info(L.T("Scrivi la password.")); return; }
        var suggested = Path.GetFileNameWithoutExtension(src);
        if (!Path.HasExtension(suggested)) suggested += ".bin";
        var dst = Dlg.SaveFile(L.T("Salva il file recuperato"), suggested, null, "vault_save");
        if (dst == null) return;
        var pw = _pwDec.Password;
        var ok = await Run(L.T("Apertura in corso…"), ct => FileVault.Decrypt(src, dst, pw, Progress, ct));
        if (ok)
        {
            _pwDec.Password = "";
            MainWindow.Notify(L.T($"File recuperato: {Path.GetFileName(dst)}"), 10);
            Util.Reveal(dst);
        }
    }

    private void Progress(double f) => Dispatcher.InvokeAsync(() => _bar.Value = f * 100);

    private async Task<bool> Run(string title, Action<CancellationToken> work)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _encBtn.IsEnabled = _decBtn.IsEnabled = false;
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        _status.Text = title;
        try
        {
            await Task.Run(() => work(ct), ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (VaultPasswordException ex) { Dlg.Error(ex.Message); return false; }
        catch (Exception ex) { Dlg.Error(L.T("Operazione non riuscita:\n") + ex.Message); return false; }
        finally
        {
            _bar.Visibility = Visibility.Collapsed;
            _status.Text = "";
            _encBtn.IsEnabled = _decBtn.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un'operazione in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
