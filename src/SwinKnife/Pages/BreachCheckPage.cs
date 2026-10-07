using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Controlla se una password è finita in una fuga di dati nota, in modo anonimo (k-anonymity di Have I Been Pwned).</summary>
public sealed class BreachCheckPage : UserControl, IToolPage
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly PasswordBox _pw = new() { PlaceholderText = L.T("Password da controllare"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _result = new() { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly Wpf.Ui.Controls.Button _btn;

    public BreachCheckPage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come fa a essere sicuro"),
            Message = L.T("La password non lascia mai il PC. Se ne calcola l'impronta (SHA-1) e si invia al servizio solo i primi 5 caratteri dell'impronta: nessuno può risalire alla password. È la tecnica \"k-anonymity\" di Have I Been Pwned."),
            Margin = new Thickness(0, 0, 0, 14),
        };
        _btn = Ui.Btn(L.T("Controlla"), SymbolRegular.ShieldTask24, async (_, _) => await Check(), primary: true);
        _pw.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Check(); };

        var card = Ui.Card(L.T("Controlla una password"),
            L.T("Scopri se questa password compare negli elenchi di password trapelate da attacchi informatici."),
            _pw, Ui.Row(_btn), _result);
        ((StackPanel)card.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 10, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Password compromesse"), L.T("Verifica se una password è già nota agli attaccanti. Richiede una connessione a Internet.")),
            info, card);
    }

    private async Task Check()
    {
        var password = _pw.Password;
        if (password.Length == 0) { _result.Text = L.T("Scrivi una password da controllare."); return; }
        _btn.IsEnabled = false;
        _result.Foreground = Ui.Res("TextFillColorSecondaryBrush");
        _result.Text = L.T("Controllo in corso…");
        try
        {
            var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
            var prefix = sha1[..5];
            var suffix = sha1[5..];
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.pwnedpasswords.com/range/" + prefix);
            req.Headers.Add("Add-Padding", "true");
            req.Headers.UserAgent.ParseAdd("SwinKnife/" + AppInfo.Version);
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            long count = 0;
            foreach (var line in body.Split('\n'))
            {
                var parts = line.Split(':');
                if (parts.Length == 2 && parts[0].Trim().Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(parts[1].Trim(), out var c)) { count = c; break; }
            }
            if (count > 0)
            {
                _result.Text = L.T($"⚠ Questa password è comparsa in {Util.Number(count)} fughe di dati. Non usarla: cambiala ovunque la stai usando.");
                _result.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x4B, 0x4B));
            }
            else
            {
                _result.Text = L.T("✓ Questa password non risulta in nessuna fuga di dati nota. Resta comunque importante usarne una diversa per ogni servizio.");
                _result.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            }
        }
        catch (Exception ex)
        {
            _result.Foreground = Ui.Res("TextFillColorSecondaryBrush");
            _result.Text = L.T("Controllo non riuscito (serve una connessione a Internet):\n") + ex.Message;
        }
        finally { _btn.IsEnabled = true; }
    }
}
