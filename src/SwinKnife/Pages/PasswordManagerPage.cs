using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageMagick;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Cassaforte delle password cifrata, con autenticatore a due fattori (TOTP) integrato.</summary>
public sealed class PasswordManagerPage : UserControl, IToolPage
{
    private readonly Border _host = new();
    private readonly TextBox _search = new() { PlaceholderText = L.T("Cerca…"), Width = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _totpTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string? _master;
    private List<LoginEntry> _entries = new();
    private readonly Dictionary<string, (TextBlock code, ProgressBar bar)> _totpViews = new();

    public PasswordManagerPage(MainWindow main)
    {
        _search.TextChanged += (_, _) => RenderList();
        _totpTimer.Tick += (_, _) => RefreshTotp();
        Content = Ui.ScrollPage(
            Ui.Header(L.T("Password e 2FA"), L.T("Conserva login e codici dell'autenticazione a due fattori, al sicuro dietro un'unica password.")),
            _host);
        ShowGate();
    }

    public void Activated() { if (_master != null) _totpTimer.Start(); }

    // ------------------------------------------------------------------ sblocco / creazione
    private void ShowGate()
    {
        _totpTimer.Stop();
        var pw1 = new PasswordBox { PlaceholderText = L.T("Password principale"), Margin = new Thickness(0, 8, 0, 0) };
        if (PasswordStore.Exists)
        {
            var unlock = Ui.Btn(L.T("Sblocca"), SymbolRegular.LockOpen24, (_, _) => Unlock(pw1.Password), primary: true);
            pw1.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Unlock(pw1.Password); };
            _host.Child = ((Border)Ui.Card(L.T("Sblocca la cassaforte"),
                L.T("Inserisci la password principale per vedere i tuoi login."), pw1, Ui.Row(unlock)));
            Fix(_host.Child);
        }
        else
        {
            var pw2 = new PasswordBox { PlaceholderText = L.T("Ripeti la password principale"), Margin = new Thickness(0, 6, 0, 0) };
            var create = Ui.Btn(L.T("Crea la cassaforte"), SymbolRegular.LockClosed24, (_, _) => Create(pw1.Password, pw2.Password), primary: true);
            _host.Child = ((Border)Ui.Card(L.T("Crea la cassaforte"),
                L.T("Scegli una password principale forte: è l'unica a proteggere tutto e non è recuperabile se la dimentichi."),
                pw1, pw2, Ui.Row(create)));
            Fix(_host.Child);
        }
    }

    private static void Fix(UIElement card)
    {
        if (card is Border b && b.Child is StackPanel sp)
            sp.Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 12, 0, 0);
    }

    private void Create(string a, string b)
    {
        if (a.Length < 6) { Dlg.Info(L.T("Usa una password di almeno 6 caratteri.")); return; }
        if (a != b) { Dlg.Info(L.T("Le due password non coincidono.")); return; }
        _master = a;
        _entries = new();
        PasswordStore.Save(_entries, _master);
        ShowMain();
    }

    private void Unlock(string master)
    {
        if (master.Length == 0) return;
        try { _entries = PasswordStore.Load(master); _master = master; ShowMain(); }
        catch (UnauthorizedAccessException) { Dlg.Error(L.T("Password principale sbagliata.")); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void Lock()
    {
        _master = null;
        _entries = new();
        _totpViews.Clear();
        ShowGate();
    }

    private void Persist() { if (_master != null) PasswordStore.Save(_entries, _master); }

    // ------------------------------------------------------------------ vista principale
    private void ShowMain()
    {
        var bar = Ui.Row(
            Ui.Btn(L.T("Aggiungi"), SymbolRegular.Add24, (_, _) => Edit(null), primary: true),
            _search,
            Ui.Btn(L.T("Blocca"), SymbolRegular.LockClosed24, (_, _) => Lock()),
            Ui.Btn(L.T("Esporta CSV…"), SymbolRegular.ArrowExport24, (_, _) => ExportCsv()));
        var panel = new StackPanel();
        panel.Children.Add(bar);
        panel.Children.Add(_list);
        _list.Margin = new Thickness(0, 10, 0, 0);
        _host.Child = panel;
        _totpTimer.Start();
        RenderList();
    }

    private void RenderList()
    {
        _list.Children.Clear();
        _totpViews.Clear();
        var q = _search.Text.Trim();
        var items = _entries
            .Where(e => q.Length == 0 || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Username.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (items.Count == 0) { _list.Children.Add(Ui.Hint(_entries.Count == 0 ? L.T("La cassaforte è vuota: aggiungi il primo login.") : L.T("Nessun risultato."))); return; }
        foreach (var e in items) _list.Children.Add(Row(e));
        RefreshTotp();
    }

    private FrameworkElement Row(LoginEntry e)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = e.Title.Length > 0 ? e.Title : L.T("(senza nome)"), FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
        if (e.Username.Length > 0) text.Children.Add(Ui.Hint(e.Username));

        var pwShown = false;
        var pwBlock = new TextBlock { Text = "••••••••", VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"), Foreground = Ui.Res("TextFillColorSecondaryBrush") };
        var pwRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        pwRow.Children.Add(pwBlock);
        pwRow.Children.Add(Ui.IconBtn(SymbolRegular.Eye24, L.T("Mostra/Nascondi"), (_, _) => { pwShown = !pwShown; pwBlock.Text = pwShown ? e.Password : "••••••••"; }));
        if (e.Password.Length > 0) text.Children.Add(pwRow);

        if (Totp.IsValidSecret(e.TotpSecret))
        {
            var code = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"), Foreground = Ui.Res("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            var bar = new ProgressBar { Maximum = 30, Width = 60, Height = 4, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var totpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            totpRow.Children.Add(new TextBlock { Text = "2FA ", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("TextFillColorTertiaryBrush") });
            totpRow.Children.Add(code);
            totpRow.Children.Add(bar);
            totpRow.Children.Add(Ui.IconBtn(SymbolRegular.Copy24, L.T("Copia il codice"), (_, _) => { try { Clipboard.SetText(Totp.Code(e.TotpSecret)); MainWindow.Notify(L.T("Codice 2FA copiato.")); } catch { } }));
            text.Children.Add(totpRow);
            _totpViews[e.Id] = (code, bar);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (e.Username.Length > 0) actions.Children.Add(Ui.Btn(L.T("Utente"), SymbolRegular.Person24, (_, _) => Copy(e.Username, L.T("Nome utente copiato."))));
        if (e.Password.Length > 0) actions.Children.Add(Ui.Btn(L.T("Password"), SymbolRegular.Key24, (_, _) => Copy(e.Password, L.T("Password copiata."))));
        actions.Children.Add(Ui.IconBtn(SymbolRegular.Edit24, L.T("Modifica"), (_, _) => Edit(e)));
        actions.Children.Add(Ui.IconBtn(SymbolRegular.Delete24, L.T("Elimina"), (_, _) =>
        {
            if (!Dlg.Confirm(L.T($"Eliminare «{e.Title}»?"))) return;
            _entries.Remove(e); Persist(); RenderList();
        }));

        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        dock.Children.Add(actions);
        dock.Children.Add(text);
        return new Border { Child = dock, Padding = new Thickness(12, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), Background = Ui.Res("CardBackgroundFillColorDefaultBrush") };
    }

    private static void Copy(string s, string msg) { try { Clipboard.SetText(s); MainWindow.Notify(msg); } catch { } }

    private void RefreshTotp()
    {
        if (_totpViews.Count == 0) return;
        var left = Totp.SecondsLeft();
        foreach (var e in _entries)
            if (_totpViews.TryGetValue(e.Id, out var v) && Totp.IsValidSecret(e.TotpSecret))
            {
                v.code.Text = Totp.Code(e.TotpSecret);
                v.bar.Value = left;
            }
    }

    // ------------------------------------------------------------------ aggiungi / modifica
    private void Edit(LoginEntry? existing)
    {
        var e = existing ?? new LoginEntry();
        var title = new TextBox { Text = e.Title, PlaceholderText = L.T("Nome (es. Gmail)") };
        var user = new TextBox { Text = e.Username, PlaceholderText = L.T("Nome utente o email"), Margin = new Thickness(0, 6, 0, 0) };
        var pass = new TextBox { Text = e.Password, PlaceholderText = L.T("Password"), Margin = new Thickness(0, 6, 0, 0) };
        var url = new TextBox { Text = e.Url, PlaceholderText = L.T("Sito web"), Margin = new Thickness(0, 6, 0, 0) };
        var notes = new TextBox { Text = e.Notes, PlaceholderText = L.T("Note"), AcceptsReturn = true, MinHeight = 60, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var totp = new TextBox { Text = e.TotpSecret, PlaceholderText = L.T("Segreto 2FA (Base32), facoltativo"), Margin = new Thickness(0, 6, 0, 0) };

        var form = new StackPanel();
        form.Children.Add(title);
        form.Children.Add(user);
        form.Children.Add(Ui.Row(pass, Ui.Btn(L.T("Genera"), SymbolRegular.ArrowClockwise24, (_, _) => pass.Text = Generate())));
        ((StackPanel)form.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        form.Children.Add(url);
        form.Children.Add(notes);
        form.Children.Add(Ui.Row(totp));
        ((StackPanel)form.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        form.Children.Add(Ui.Row(
            Ui.Btn(L.T("Importa 2FA da QR (immagine)"), SymbolRegular.Image24, (_, _) => ImportTotpFromImage(totp)),
            Ui.Btn(L.T("Importa 2FA da QR (schermo)"), SymbolRegular.Screenshot24, (_, _) => ImportTotpFromScreen(totp))));
        ((StackPanel)form.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        form.Children.Add(Ui.Row(
            Ui.Btn(L.T("Salva"), SymbolRegular.Save24, (_, _) =>
            {
                if (title.Text.Trim().Length == 0) { Dlg.Info(L.T("Dai un nome alla voce.")); return; }
                if (totp.Text.Trim().Length > 0 && !Totp.IsValidSecret(totp.Text.Trim())) { Dlg.Info(L.T("Il segreto 2FA non è valido.")); return; }
                e.Title = title.Text.Trim(); e.Username = user.Text.Trim(); e.Password = pass.Text;
                e.Url = url.Text.Trim(); e.Notes = notes.Text; e.TotpSecret = totp.Text.Trim(); e.Updated = DateTime.Now;
                if (existing == null) _entries.Add(e);
                Persist(); ShowMain();
            }, primary: true),
            Ui.Btn(L.T("Annulla"), SymbolRegular.Dismiss24, (_, _) => ShowMain())));
        ((StackPanel)form.Children[^1]).Margin = new Thickness(0, 12, 0, 0);

        _host.Child = (Border)Ui.Card(existing == null ? L.T("Nuova voce") : L.T("Modifica voce"), null, form);
        _totpTimer.Stop();
    }

    private static string Generate()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%&*?";
        var b = RandomNumberGenerator.GetBytes(18);
        return new string(b.Select(x => chars[x % chars.Length]).ToArray());
    }

    private void ImportTotpFromImage(TextBox totp)
    {
        var f = Dlg.OpenFile(L.T("Immagine del QR 2FA"), L.T("Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Tutti i file|*.*"), "qr");
        if (f == null) return;
        try { ApplyTotp(QrDecode.FromFile(f), totp); } catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void ImportTotpFromScreen(TextBox totp)
    {
        var w = MainWindow.Instance;
        if (w == null) return;
        w.WindowState = WindowState.Minimized;
        w.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(350);
            string? text = null;
            try
            {
                var bs = ScreenCapture.Capture(ScreenCapture.VirtualScreen);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));
                using var ms = new MemoryStream();
                enc.Save(ms); ms.Position = 0;
                using var img = new MagickImage(ms);
                text = QrDecode.FromMagick(img);
            }
            catch { }
            w.WindowState = WindowState.Normal;
            ApplyTotp(text, totp);
        });
    }

    private void ApplyTotp(string? qr, TextBox totp)
    {
        if (string.IsNullOrEmpty(qr)) { Dlg.Info(L.T("Nessun QR code riconosciuto.")); return; }
        var parsed = Totp.ParseUri(qr);
        if (parsed is { } p) { totp.Text = p.secret; MainWindow.Notify(L.T("Segreto 2FA importato.")); }
        else if (Totp.IsValidSecret(qr)) { totp.Text = qr.Trim(); MainWindow.Notify(L.T("Segreto 2FA importato.")); }
        else Dlg.Info(L.T("Il QR non contiene un codice 2FA."));
    }

    private void ExportCsv()
    {
        if (_entries.Count == 0) { Dlg.Info(L.T("Non c'è niente da esportare.")); return; }
        if (!Dlg.Confirm(L.T("Il file CSV conterrà tutte le password IN CHIARO. Chiunque lo apra le vedrà. Procedere?"))) return;
        var dst = Dlg.SaveFile(L.T("Esporta le password"), "password.csv", L.T("File CSV|*.csv"), "pw_export");
        if (dst == null) return;
        try { PasswordStore.ExportCsv(_entries, dst); Util.Reveal(dst); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    public void Shutdown() => _totpTimer.Stop();
}
