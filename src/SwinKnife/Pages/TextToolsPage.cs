using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using SwinKnife.Controls;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Piccoli strumenti per il testo: Base64, URL, JSON/XML, maiuscole/minuscole, hash, conteggi, date.</summary>
public sealed class TextToolsPage : UserControl, IToolPage
{
    private readonly TextBox _in = Area(L.T("Scrivi o incolla qui il testo…"));
    private readonly TextBox _out = Area(L.T("Il risultato appare qui."));
    private readonly TextBlock _stats = Ui.Hint("");

    public TextToolsPage(MainWindow main)
    {
        _out.IsReadOnly = true;
        _in.TextChanged += (_, _) => UpdateStats();
        _out.Background = Ui.Res("CardBackgroundFillColorDefaultBrush");

        WrapPanel Group(string title, params UIElement[] btns)
        {
            var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            foreach (var b in btns) p.Children.Add(b);
            return p;
        }
        Wpf.Ui.Controls.Button B(string t, Func<string, string> f) => Ui.Btn(t, null, (_, _) => Apply(f));

        var ops = new StackPanel();
        ops.Children.Add(Group(L.T("Base64"),
            B(L.T("Codifica"), s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
            B(L.T("Decodifica"), s => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())))));
        ops.Children.Add(Group("URL",
            B(L.T("Codifica"), Uri.EscapeDataString),
            B(L.T("Decodifica"), Uri.UnescapeDataString)));
        ops.Children.Add(Group(L.T("Maiuscole"),
            B(L.T("TUTTO MAIUSCOLO"), s => s.ToUpper(CultureInfo.CurrentCulture)),
            B(L.T("tutto minuscolo"), s => s.ToLower(CultureInfo.CurrentCulture)),
            B(L.T("Iniziali Maiuscole"), s => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture)))));
        ops.Children.Add(Group(L.T("JSON / XML"),
            B(L.T("Formatta JSON"), FormatJson),
            B(L.T("Compatta JSON"), MinifyJson),
            B(L.T("Formatta XML"), s => XDocument.Parse(s).ToString())));
        ops.Children.Add(Group(L.T("Hash del testo"),
            B("SHA-256", s => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(s)))),
            B("SHA-1", s => Hex(SHA1.HashData(Encoding.UTF8.GetBytes(s)))),
            B("MD5", s => Hex(MD5.HashData(Encoding.UTF8.GetBytes(s))))));
        ops.Children.Add(Group(L.T("Righe"),
            B(L.T("Ordina A→Z"), s => string.Join('\n', Lines(s).OrderBy(x => x, StringComparer.CurrentCulture))),
            B(L.T("Inverti ordine"), s => string.Join('\n', Lines(s).Reverse())),
            B(L.T("Togli doppioni"), s => string.Join('\n', Lines(s).Distinct())),
            B(L.T("Togli righe vuote"), s => string.Join('\n', Lines(s).Where(x => x.Trim().Length > 0)))));
        ops.Children.Add(Group(L.T("Spazi"),
            B(L.T("Rimuovi spazi extra"), s => string.Join('\n', Lines(s).Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))))),
            B(L.T("Elimina spazi ai lati"), s => string.Join('\n', Lines(s).Select(l => l.Trim())))));
        ops.Children.Add(Group(L.T("Data e ora"),
            Ui.Btn(L.T("Timestamp adesso"), null, (_, _) => _out.Text = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()),
            B(L.T("Timestamp → data"), UnixToDate)));

        var inCard = Ui.Card(L.T("Testo"), null, _in, _stats);
        var opsCard = Ui.Card(L.T("Operazioni"), null, ops);
        var outCard = Ui.Card(L.T("Risultato"), null,
            Ui.Row(Ui.Btn(L.T("Copia"), SymbolRegular.Copy24, (_, _) => { try { Clipboard.SetText(_out.Text); MainWindow.Notify(L.T("Copiato.")); } catch { } }),
                   Ui.Btn(L.T("Usa come testo"), SymbolRegular.ArrowUp24, (_, _) => { _in.Text = _out.Text; })),
            _out);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Cassetta attrezzi testo"), L.T("Conversioni rapide su testo: Base64, URL, JSON/XML, maiuscole, hash, righe e date.")),
            inCard, opsCard, outCard);
        UpdateStats();
    }

    private static TextBox Area(string placeholder) => new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 260,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas, monospace"),
        Padding = new Thickness(8), Tag = placeholder,
    };

    private static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static string FormatJson(string s) =>
        JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement, new JsonSerializerOptions { WriteIndented = true });
    private static string MinifyJson(string s) =>
        JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement);

    private static string UnixToDate(string s)
    {
        s = s.Trim();
        if (!long.TryParse(s, out var v)) throw new FormatException(L.T("Non è un timestamp valido."));
        var dto = Math.Abs(v) > 100_000_000_000L ? DateTimeOffset.FromUnixTimeMilliseconds(v) : DateTimeOffset.FromUnixTimeSeconds(v);
        return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void Apply(Func<string, string> f)
    {
        try { _out.Text = f(_in.Text); }
        catch (Exception ex) { _out.Text = "⚠ " + ex.Message; }
    }

    private void UpdateStats()
    {
        var t = _in.Text;
        var words = t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = t.Length == 0 ? 0 : Lines(t).Length;
        _stats.Text = L.T($"{t.Length} caratteri · {words} parole · {lines} righe");
    }
}
