using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Clipboard = System.Windows.Clipboard;
using Image = System.Windows.Controls.Image;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Generatore di QR code (link, Wi-Fi, contatti, email, SMS) e di password sicure.</summary>
public sealed class QrPasswordPage : UserControl, IToolPage
{
    private static readonly (string name, string hex)[] Colors =
    [
        (L.T("Nero"), "#000000"), (L.T("Blu notte"), "#1F3A93"), (L.T("Rosso"), "#C42B1C"), (L.T("Verde"), "#107C10"), (L.T("Viola"), "#5C2D91"),
    ];

    private static readonly (string name, QRCodeGenerator.ECCLevel level)[] EccLevels =
    [
        (L.T("Bassa (7%)"), QRCodeGenerator.ECCLevel.L), (L.T("Media (15%)"), QRCodeGenerator.ECCLevel.M),
        (L.T("Alta (25%)"), QRCodeGenerator.ECCLevel.Q), (L.T("Massima (30%)"), QRCodeGenerator.ECCLevel.H),
    ];

    // ---- QR
    private readonly ComboBox _kind = new() { MinWidth = 220 };
    private readonly StackPanel _fields = new();
    private readonly Dictionary<string, string> _values = new();
    private readonly ComboBox _color = new() { MinWidth = 120 };
    private readonly ComboBox _ecc = new() { MinWidth = 120 };
    private readonly CheckBox _transparent = new() { Content = L.T("Sfondo trasparente"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly Image _preview = new() { Width = 240, Height = 240, Stretch = Stretch.Uniform };
    private readonly TextBlock _qrInfo = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _qrButtons;
    private string _payload = "";

    // ---- password
    private readonly Slider _length = new() { Minimum = 6, Maximum = 64, Value = 16, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 260 };
    private readonly TextBlock _lengthText = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _upper = new() { Content = L.T("Maiuscole (A-Z)"), IsChecked = true };
    private readonly CheckBox _lower = new() { Content = L.T("Minuscole (a-z)"), IsChecked = true };
    private readonly CheckBox _digits = new() { Content = L.T("Numeri (0-9)"), IsChecked = true };
    private readonly CheckBox _symbols = new() { Content = L.T("Simboli (!@#…)"), IsChecked = true };
    private readonly CheckBox _noAmbiguous = new() { Content = L.T("Evita caratteri che si confondono (l 1 I O 0)"), IsChecked = true };
    private readonly StackPanel _passwords = new();
    private readonly TextBlock _strength = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly PasswordBox _check = new() { PlaceholderText = L.T("Scrivi una password da verificare"), Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _checkResult = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _checkBar = new() { Maximum = 100, Height = 6, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };

    public QrPasswordPage(MainWindow main)
    {
        foreach (var k in new[] { L.T("Testo o link"), L.T("Rete Wi-Fi"), L.T("Contatto (vCard)"), L.T("Email"), "SMS" }) _kind.Items.Add(k);
        foreach (var (name, _) in Colors) _color.Items.Add(name);
        foreach (var (name, _) in EccLevels) _ecc.Items.Add(name);
        _kind.SelectedIndex = 0;
        _color.SelectedIndex = 0;
        _ecc.SelectedIndex = 1;
        _kind.SelectionChanged += (_, _) => BuildFields();
        _color.SelectionChanged += (_, _) => UpdateQr();
        _ecc.SelectionChanged += (_, _) => UpdateQr();
        _transparent.Click += (_, _) => UpdateQr();
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
        _qrInfo.Style = (Style)Application.Current.FindResource("Hint");

        _qrButtons = Ui.Row(
            Ui.Btn(L.T("Salva PNG"), SymbolRegular.Save24, (_, _) => SavePng(), primary: true),
            Ui.Btn(L.T("Salva SVG"), null, (_, _) => SaveSvg(), L.T("Salva come immagine vettoriale (per la stampa)")),
            Ui.IconBtn(SymbolRegular.Copy24, L.T("Copia negli appunti"), (_, _) => CopyQr()));
        _qrButtons.HorizontalAlignment = HorizontalAlignment.Center;

        var left = new StackPanel();
        left.Children.Add(Ui.Row(Ui.Label(L.T("Tipo")), Spacer(10), _kind));
        left.Children.Add(_fields);
        var opts = Ui.Row(Ui.Label(L.T("Colore")), Spacer(8), _color, Spacer(16), Ui.Label(L.T("Correzione errori")), Spacer(8), _ecc);
        opts.Margin = new Thickness(0, 16, 0, 0);
        left.Children.Add(opts);
        left.Children.Add(_transparent);
        left.Children.Add(new TextBlock
        {
            Text = L.T("Una correzione più alta permette di leggere il codice anche se rovinato o coperto da un logo, ma lo rende più fitto."),
            Style = (Style)Application.Current.FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0),
        });

        var previewPanel = new StackPanel { Margin = new Thickness(24, 0, 0, 0), Width = 280 };
        previewPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center, Child = _preview,
        });
        previewPanel.Children.Add(_qrInfo);
        previewPanel.Children.Add(_qrButtons);

        var qrGrid = new Grid();
        qrGrid.ColumnDefinitions.Add(new ColumnDefinition());
        qrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qrGrid.Children.Add(left);
        Grid.SetColumn(previewPanel, 1);
        qrGrid.Children.Add(previewPanel);

        // ---- password
        _length.ValueChanged += (_, _) => GeneratePasswords();
        foreach (var cb in new[] { _upper, _lower, _digits, _symbols, _noAmbiguous })
        {
            cb.Margin = new Thickness(0, 0, 18, 6);
            cb.Click += (_, _) => GeneratePasswords();
        }
        var classes = new WrapPanel();
        foreach (var cb in new[] { _upper, _lower, _digits, _symbols }) classes.Children.Add(cb);
        var pwPanel = new StackPanel();
        pwPanel.Children.Add(Ui.Row(Ui.Label(L.T("Lunghezza")), Spacer(12), _length, Spacer(10), _lengthText,
            Ui.Btn(L.T("Rigenera"), SymbolRegular.ArrowSync24, (_, _) => GeneratePasswords())));
        classes.Margin = new Thickness(0, 12, 0, 0);
        pwPanel.Children.Add(classes);
        pwPanel.Children.Add(_noAmbiguous);
        _passwords.Margin = new Thickness(0, 10, 0, 0);
        pwPanel.Children.Add(_passwords);
        pwPanel.Children.Add(_strength);

        _check.PasswordChanged += (_, _) => CheckPassword();
        var checkPanel = new StackPanel();
        checkPanel.Children.Add(_check);
        checkPanel.Children.Add(_checkBar);
        checkPanel.Children.Add(_checkResult);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("QR code e password"), L.T("Crea QR code da stampare o condividere e genera password difficili da indovinare.")),
            Ui.Card(L.T("QR code"), L.T("Inquadrandolo con la fotocamera del telefono si apre il link, ci si collega al Wi-Fi o si salva il contatto."), qrGrid),
            Ui.Card(L.T("Generatore di password"), L.T("Le password sono generate sul tuo PC con un generatore crittografico sicuro."), pwPanel),
            Ui.Card(L.T("Quanto è robusta la mia password?"), L.T("La verifica avviene solo sul tuo PC: la password non viene inviata da nessuna parte."), checkPanel));

        BuildFields();
        GeneratePasswords();
        CheckPassword();
    }

    private static FrameworkElement Spacer(double w) => new Border { Width = w };

    // ================================================================== QR
    private void BuildFields()
    {
        _fields.Children.Clear();
        switch (_kind.SelectedIndex)
        {
            case 0:
                Field("text", "Testo o indirizzo web", "https://www.esempio.it", multiline: true);
                break;
            case 1:
                Field("ssid", "Nome della rete (SSID)");
                Field("wpass", "Password della rete");
                Choice("wsec", L.T("Sicurezza"), ["WPA / WPA2 / WPA3", L.T("WEP (vecchie reti)"), L.T("Nessuna (rete aperta)")]);
                Check("whidden", L.T("Rete nascosta"));
                var cur = Ui.Btn(L.T("Usa la rete Wi-Fi a cui sei connesso"), SymbolRegular.Wifi124, (_, _) => FillCurrentWifi());
                cur.Margin = new Thickness(0, 12, 0, 0);
                cur.HorizontalAlignment = HorizontalAlignment.Left;
                _fields.Children.Add(cur);
                break;
            case 2:
                Field("first", "Nome");
                Field("last", "Cognome");
                Field("phone", "Telefono", "+39 …");
                Field("email", "Email");
                Field("org", "Azienda");
                Field("url", "Sito web");
                Field("addr", "Indirizzo");
                break;
            case 3:
                Field("to", "Destinatario", "nome@esempio.it");
                Field("subject", "Oggetto");
                Field("body", "Messaggio", multiline: true);
                break;
            case 4:
                Field("sms", "Numero di telefono", "+39 …");
                Field("smsbody", "Messaggio", multiline: true);
                break;
        }
        UpdateQr();
    }

    private void AddLabeled(string label, UIElement input)
    {
        _fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4), Foreground = Ui.Res("TextFillColorSecondaryBrush") });
        _fields.Children.Add(input);
    }

    private void Field(string key, string label, string placeholder = "", bool multiline = false)
    {
        var t = new TextBox { PlaceholderText = L.T(placeholder), Text = _values.GetValueOrDefault(key, "") };
        if (multiline)
        {
            t.AcceptsReturn = true;
            t.TextWrapping = TextWrapping.Wrap;
            t.MinHeight = 80;
            t.VerticalContentAlignment = VerticalAlignment.Top;
        }
        t.TextChanged += (_, _) =>
        {
            _values[key] = t.Text;
            UpdateQr();
        };
        AddLabeled(L.T(label), t);
    }

    private void Choice(string key, string label, string[] options)
    {
        var c = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        foreach (var o in options) c.Items.Add(o);
        c.SelectedIndex = int.TryParse(_values.GetValueOrDefault(key), out var i) ? i : 0;
        c.SelectionChanged += (_, _) =>
        {
            _values[key] = c.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            UpdateQr();
        };
        AddLabeled(label, c);
    }

    private void Check(string key, string label)
    {
        var c = new CheckBox { Content = label, IsChecked = _values.GetValueOrDefault(key) == "1", Margin = new Thickness(0, 10, 0, 0) };
        c.Click += (_, _) =>
        {
            _values[key] = c.IsChecked == true ? "1" : "0";
            UpdateQr();
        };
        _fields.Children.Add(c);
    }

    private string V(string key) => _values.GetValueOrDefault(key, "").Trim();

    private string Payload()
    {
        switch (_kind.SelectedIndex)
        {
            case 0:
                return _values.GetValueOrDefault("text", "");
            case 1:
                if (V("ssid").Length == 0) return "";
                var auth = V("wsec") switch
                {
                    "1" => PayloadGenerator.WiFi.Authentication.WEP,
                    "2" => PayloadGenerator.WiFi.Authentication.nopass,
                    _ => PayloadGenerator.WiFi.Authentication.WPA,
                };
                return new PayloadGenerator.WiFi(V("ssid"), auth == PayloadGenerator.WiFi.Authentication.nopass ? "" : _values.GetValueOrDefault("wpass", ""),
                    auth, V("whidden") == "1").ToString();
            case 2:
                if ((V("first") + V("last") + V("phone") + V("email") + V("org")).Length == 0) return "";
                static string E(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n").Replace("\r", "");
                var sb = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\n");
                sb.Append($"N:{E(V("last"))};{E(V("first"))};;;\r\n");
                sb.Append($"FN:{E((V("first") + " " + V("last")).Trim() is { Length: > 0 } fn ? fn : V("org"))}\r\n");
                if (V("org").Length > 0) sb.Append($"ORG:{E(V("org"))}\r\n");
                if (V("phone").Length > 0) sb.Append($"TEL;TYPE=CELL:{E(V("phone"))}\r\n");
                if (V("email").Length > 0) sb.Append($"EMAIL:{E(V("email"))}\r\n");
                if (V("url").Length > 0) sb.Append($"URL:{E(V("url"))}\r\n");
                if (V("addr").Length > 0) sb.Append($"ADR:;;{E(V("addr"))};;;;\r\n");
                return sb.Append("END:VCARD").ToString();
            case 3:
                if (V("to").Length == 0) return "";
                var query = new List<string>();
                if (V("subject").Length > 0) query.Add("subject=" + Uri.EscapeDataString(V("subject")));
                if (V("body").Length > 0) query.Add("body=" + Uri.EscapeDataString(V("body")));
                return "mailto:" + V("to") + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            case 4:
                return V("sms").Length == 0 ? "" : $"SMSTO:{V("sms")}:{V("smsbody")}";
        }
        return "";
    }

    private QRCodeGenerator.ECCLevel Ecc => EccLevels[Math.Max(0, _ecc.SelectedIndex)].level;

    private byte[] DarkRgba
    {
        get
        {
            var c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(Colors[Math.Max(0, _color.SelectedIndex)].hex);
            return [c.R, c.G, c.B, 255];
        }
    }

    private byte[] LightRgba => _transparent.IsChecked == true ? [255, 255, 255, 0] : [255, 255, 255, 255];

    private QRCodeData? Create()
    {
        if (_payload.Length == 0) return null;
        using var gen = new QRCodeGenerator();
        return gen.CreateQrCode(_payload, Ecc);
    }

    private void UpdateQr()
    {
        _payload = Payload();
        _qrButtons.IsEnabled = false;
        _preview.Source = null;
        if (_payload.Length == 0)
        {
            _qrInfo.Text = L.T("Compila i campi per creare il QR code.");
            return;
        }
        try
        {
            using var data = Create()!;
            _preview.Source = LoadPng(new PngByteQRCode(data).GetGraphic(8, DarkRgba, LightRgba));
            var modules = data.ModuleMatrix.Count - 8;
            _qrInfo.Text = L.T($"Versione {(modules - 17) / 4} · {modules}×{modules} moduli · {_payload.Length} caratteri");
            _qrButtons.IsEnabled = true;
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("DataTooLong"))
        {
            _qrInfo.Text = L.T("Il testo è troppo lungo per un QR code: accorcialo o abbassa la correzione errori.");
        }
        catch (Exception ex)
        {
            _qrInfo.Text = L.T("Impossibile creare il QR code: ") + ex.Message;
        }
    }

    private static BitmapImage LoadPng(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private string SuggestedName() => _kind.SelectedIndex switch
    {
        1 => "wifi_" + Util.SafeFileName(V("ssid")),
        2 => "contatto_" + Util.SafeFileName((V("first") + "_" + V("last")).Trim('_')),
        _ => "qrcode",
    };

    private void SavePng()
    {
        using var data = Create();
        if (data == null) return;
        var size = Dlg.Choose(L.T("Salva PNG"), L.T("Dimensione dell'immagine:"), [L.T("512 × 512 pixel (schermo)"), L.T("1024 × 1024 pixel"), L.T("2048 × 2048 pixel (stampa)")], 1);
        if (size is not { } s) return;
        var path = Dlg.SaveFile(L.T("Salva QR code"), SuggestedName() + ".png", L.T("Immagine PNG|*.png"), "qr");
        if (path == null) return;
        var px = new[] { 512, 1024, 2048 }[s];
        var ppm = Math.Max(1, px / data.ModuleMatrix.Count);
        File.WriteAllBytes(path, new PngByteQRCode(data).GetGraphic(ppm, DarkRgba, LightRgba));
        MainWindow.Notify(L.T("QR code salvato: ") + Path.GetFileName(path));
    }

    private void SaveSvg()
    {
        using var data = Create();
        if (data == null) return;
        var path = Dlg.SaveFile(L.T("Salva QR code"), SuggestedName() + ".svg", L.T("Immagine SVG|*.svg"), "qr");
        if (path == null) return;
        var svg = new SvgQRCode(data).GetGraphic(10, Colors[Math.Max(0, _color.SelectedIndex)].hex,
            _transparent.IsChecked == true ? "transparent" : "#ffffff");
        File.WriteAllText(path, svg);
        MainWindow.Notify(L.T("QR code salvato: ") + Path.GetFileName(path));
    }

    private void CopyQr()
    {
        using var data = Create();
        if (data == null) return;
        // negli appunti la trasparenza non è affidabile: copio sempre su sfondo bianco
        var png = new PngByteQRCode(data).GetGraphic(Math.Max(1, 1024 / data.ModuleMatrix.Count), DarkRgba, [255, 255, 255, 255]);
        Clipboard.SetImage(LoadPng(png));
        MainWindow.Notify(L.T("QR code copiato negli appunti"));
    }

    private async void FillCurrentWifi()
    {
        try
        {
            var (ssid, key, auth) = await Task.Run(() =>
            {
                var iface = SysInfo.Run("netsh", "wlan show interfaces");
                var profile = SysInfo.Field(iface, "Profilo", "Profile", "Profil", "Perfil");
                var ssid = SysInfo.Field(iface, "SSID") ?? profile;
                if (ssid == null) return ((string?)null, (string?)null, (string?)null);
                var details = SysInfo.Run("netsh", $"wlan show profile name=\"{profile ?? ssid}\" key=clear");
                return (ssid, SysInfo.Field(details, "Contenuto chiave", "Key Content", "Schlüsselinhalt", "Contenido de la clave", "Contenu de la clé"), SysInfo.Field(details, "Autenticazione", "Authentication", "Authentifizierung", "Autenticación", "Authentification"));
            });
            if (ssid == null)
            {
                Dlg.Info(L.T("Il PC non è collegato a una rete Wi-Fi."));
                return;
            }
            _values["ssid"] = ssid;
            _values["wpass"] = key ?? "";
            _values["wsec"] = auth == null ? "0" : auth.Contains("WEP") ? "1" : auth.StartsWith("Apert") || auth.StartsWith("Open") || auth.StartsWith("Offen") || auth.StartsWith("Abiert") || auth.StartsWith("Ouvert") ? "2" : "0";
            BuildFields();
            if (key == null && _values["wsec"] != "2")
                Dlg.Info(L.T("Rete trovata, ma Windows non ha fornito la password: scrivila tu nel campo apposito."));
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Impossibile leggere la rete Wi-Fi attuale:\n") + ex.Message);
        }
    }

    // ================================================================== password
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ", Lower = "abcdefghijklmnopqrstuvwxyz", Digits = "0123456789", Symbols = "!@#$%&*+-=?_.:;~^";

    private List<string> Pools()
    {
        var pools = new List<string>();
        if (_upper.IsChecked == true) pools.Add(Upper);
        if (_lower.IsChecked == true) pools.Add(Lower);
        if (_digits.IsChecked == true) pools.Add(Digits);
        if (_symbols.IsChecked == true) pools.Add(Symbols);
        if (_noAmbiguous.IsChecked == true)
            pools = pools.Select(p => new string(p.Where(c => !"lI1O0o|".Contains(c)).ToArray())).ToList();
        return pools;
    }

    private static string Generate(List<string> pools, int length)
    {
        var all = string.Concat(pools);
        while (true)
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
            // ogni tipo di carattere scelto deve comparire almeno una volta
            if (length < pools.Count || pools.All(p => chars.Any(p.Contains))) return new string(chars);
        }
    }

    private void GeneratePasswords()
    {
        var length = (int)_length.Value;
        _lengthText.Text = length.ToString(CultureInfo.InvariantCulture);
        _passwords.Children.Clear();
        var pools = Pools();
        if (pools.Count == 0)
        {
            _strength.Text = L.T("Scegli almeno un tipo di carattere.");
            return;
        }
        for (var i = 0; i < 5; i++)
        {
            var pw = Generate(pools, length);
            var box = new System.Windows.Controls.TextBox
            {
                Text = pw, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Ui.Res("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center, Width = 560, Padding = new Thickness(4),
            };
            var row = Ui.Row(Ui.IconBtn(SymbolRegular.Copy24, L.T("Copia"), (_, _) =>
            {
                Clipboard.SetText(pw);
                MainWindow.Notify(L.T("Password copiata negli appunti"));
            }), box);
            row.Margin = new Thickness(0, 2, 0, 2);
            _passwords.Children.Add(row);
        }
        var bits = length * Math.Log2(string.Concat(pools).Length);
        _strength.Text = L.T($"Robustezza: {StrengthLabel(bits)} ({bits:0} bit) · per indovinarla servirebbero {CrackTime(bits)}.");
        _strength.Foreground = StrengthBrush(bits);
    }

    private static readonly string[] Common =
    [
        "password", "qwerty", "123456", "admin", "welcome", "letmein", "iloveyou", "ciao", "amore", "juventus", "milan", "inter",
        "napoli", "roma", "lazio", "forza", "calcio", "prova", "segreto", "abc", "asdf", "zxcv", "passw0rd", "estate", "natale",
    ];

    /// <summary>Stima (prudente) dell'entropia di una password scritta da una persona.</summary>
    private static double EstimateBits(string pw)
    {
        if (pw.Length == 0) return 0;
        var pool = 0;
        if (pw.Any(char.IsLower)) pool += 26;
        if (pw.Any(char.IsUpper)) pool += 26;
        if (pw.Any(char.IsDigit)) pool += 10;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) pool += 33;
        // le ripetizioni e le sequenze valgono poco
        var effective = 0.0;
        for (var i = 0; i < pw.Length; i++)
        {
            var repeat = i > 0 && pw[i] == pw[i - 1];
            var sequence = i > 0 && Math.Abs(pw[i] - pw[i - 1]) == 1;
            effective += repeat || sequence ? 0.3 : 1;
        }
        var bits = effective * Math.Log2(Math.Max(pool, 2));
        var lowered = pw.ToLowerInvariant();
        foreach (var w in Common)
            if (lowered.Contains(w)) bits -= (w.Length - 1) * Math.Log2(Math.Max(pool, 2)) * 0.8;
        if (Regex.IsMatch(pw, @"(19|20)\d\d")) bits -= 6; // anni
        return Math.Max(bits, Math.Min(pw.Length, 4));
    }

    private void CheckPassword()
    {
        var pw = _check.Password;
        if (pw.Length == 0)
        {
            _checkBar.Value = 0;
            _checkResult.Text = "";
            return;
        }
        var bits = EstimateBits(pw);
        _checkBar.Value = Math.Min(100, bits);
        _checkBar.Foreground = StrengthBrush(bits);
        var tips = new List<string>();
        if (pw.Length < 12) tips.Add("usa almeno 12 caratteri");
        if (!pw.Any(char.IsUpper) || !pw.Any(char.IsLower)) tips.Add("mescola maiuscole e minuscole");
        if (!pw.Any(char.IsDigit)) tips.Add(L.T("aggiungi dei numeri"));
        if (pw.All(char.IsLetterOrDigit)) tips.Add(L.T("aggiungi dei simboli"));
        if (Common.Any(w => pw.ToLowerInvariant().Contains(w))) tips.Add("evita parole e sequenze comuni");
        _checkResult.Text = L.T($"{StrengthLabel(bits)}: un attacco a forza bruta richiederebbe circa {CrackTime(bits)}.") +
                            (tips.Count > 0 ? L.T("\nPer migliorarla: ") + string.Join(", ", tips) + "." : "") +
                            L.T("\nUsa una password diversa per ogni sito: se un sito viene violato, gli altri account restano al sicuro.");
    }

    private static string StrengthLabel(double bits) => bits switch
    {
        < 28 => L.T("Molto debole"),
        < 40 => L.T("Debole"),
        < 60 => L.T("Discreta"),
        < 80 => L.T("Forte"),
        _ => L.T("Molto forte"),
    };

    private static Brush StrengthBrush(double bits) => new SolidColorBrush(bits switch
    {
        < 40 => System.Windows.Media.Color.FromRgb(0xD1, 0x34, 0x38),
        < 60 => System.Windows.Media.Color.FromRgb(0xE3, 0x8A, 0x00),
        _ => System.Windows.Media.Color.FromRgb(0x10, 0x9C, 0x3B),
    });

    /// <summary>Tempo medio per indovinarla a 10 miliardi di tentativi al secondo.</summary>
    private static string CrackTime(double bits)
    {
        var seconds = Math.Pow(2, bits - 1) / 1e10;
        if (seconds < 1) return L.T("meno di un secondo");
        (double limit, double unit, string one, string many)[] units =
        [
            (60, 1, L.T("1 secondo"), L.T("{0} secondi")), (3600, 60, L.T("1 minuto"), L.T("{0} minuti")), (86400, 3600, L.T("1 ora"), L.T("{0} ore")),
            (86400 * 365.0, 86400, L.T("1 giorno"), L.T("{0} giorni")), (86400 * 365.0 * 1000, 86400 * 365.0, L.T("1 anno"), L.T("{0} anni")),
        ];
        foreach (var (limit, unit, one, many) in units)
            if (seconds < limit)
            {
                var n = Math.Round(seconds / unit);
                return n <= 1 ? one : string.Format(L.Culture, many, n.ToString("N0", L.Culture));
            }
        var years = seconds / (86400 * 365.0);
        return years < 1e6 ? L.T("migliaia di anni") : years < 1e9 ? L.T("milioni di anni") : L.T("miliardi di anni e oltre");
    }
}
