using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;

namespace SwinKnife.Pages;

/// <summary>Cifra e decifra un testo con una password, in un blocco sicuro da copiare e condividere.</summary>
public sealed class TextCryptoPage : UserControl, IToolPage
{
    private readonly TextBox _input = new()
    {
        PlaceholderText = L.T("Scrivi qui il messaggio da cifrare, oppure incolla il testo cifrato da aprire."),
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly PasswordBox _pw = new() { PlaceholderText = L.T("Password") };
    private readonly TextBox _output = new()
    {
        PlaceholderText = L.T("Qui comparirà il risultato."), IsReadOnly = true,
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public TextCryptoPage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Il messaggio viene cifrato con AES-256 usando la tua password. Condividi il blocco risultante (per e-mail, chat…): chi lo riceve potrà leggerlo solo con la stessa password. Senza password non è recuperabile, quindi non dimenticarla."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var inCard = Ui.Card(L.T("Testo"), null, _input);
        var pwCard = Ui.Card(L.T("Password"), null, _pw,
            Ui.Row(
                Ui.Btn(L.T("Cifra"), SymbolRegular.LockClosed24, (_, _) => Do(encrypt: true), primary: true),
                Ui.Btn(L.T("Decifra"), SymbolRegular.LockOpen24, (_, _) => Do(encrypt: false))));
        ((StackPanel)pwCard.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 12, 0, 0);
        _pw.Margin = new Thickness(0, 4, 0, 0);

        var outCard = Ui.Card(L.T("Risultato"), null, _output,
            Ui.Row(
                Ui.Btn(L.T("Copia"), SymbolRegular.Copy24, (_, _) => Copy()),
                Ui.Btn(L.T("Svuota"), SymbolRegular.Eraser24, (_, _) => { _input.Text = ""; _output.Text = ""; })));
        ((StackPanel)outCard.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 12, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Crittografia testo"), L.T("Cifra un messaggio con una password e condividilo in sicurezza.")),
            info, inCard, pwCard, outCard);
    }

    private void Do(bool encrypt)
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) { Dlg.Info(L.T("Scrivi o incolla un testo.")); return; }
        try
        {
            _output.Text = encrypt ? TextCrypto.Encrypt(text, _pw.Password) : TextCrypto.Decrypt(text, _pw.Password);
            MainWindow.Notify(encrypt ? L.T("Testo cifrato.") : L.T("Testo decifrato."));
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void Copy()
    {
        if (_output.Text.Length == 0) return;
        try { Clipboard.SetText(_output.Text); MainWindow.Notify(L.T("Copiato negli appunti.")); } catch { }
    }
}
