using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Calcola gli hash (impronte) di un file, verifica che un download sia integro e dice se due file sono identici.</summary>
public sealed class HashPage : UserControl, IToolPage
{
    private static readonly (string Name, Func<HashAlgorithm> Make)[] Algos =
    [
        ("SHA-256", SHA256.Create), ("SHA-1", SHA1.Create), ("MD5", MD5.Create), ("SHA-512", SHA512.Create),
    ];

    private readonly TextBlock _fileName = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox[] _checks;
    private readonly StackPanel _results = new();
    private readonly TextBox _expected = new() { PlaceholderText = L.T("Incolla qui l'hash atteso per verificarlo"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _verdict = new() { Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private string? _file;
    private readonly Dictionary<string, string> _computed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public HashPage(MainWindow main)
    {
        _checks = Algos.Select((a, i) => new CheckBox { Content = a.Name, IsChecked = i == 0, Margin = new Thickness(0, 0, 18, 0) }).ToArray();
        var algRow = new WrapPanel();
        foreach (var c in _checks) algRow.Children.Add(c);

        var pick = Ui.Btn(L.T("Scegli file…"), SymbolRegular.DocumentArrowRight24, (_, _) =>
        {
            var f = Dlg.OpenFile(L.T("File di cui calcolare l'hash"), null, "hash");
            if (f != null) { SetFile(f); _ = Compute(); }
        }, primary: true);
        _expected.TextChanged += (_, _) => CheckExpected();

        var card = Ui.Card(L.T("Impronta di un file"),
            L.T("L'hash è un'impronta: se due file hanno lo stesso hash sono identici, bit per bit."),
            Ui.Row(pick, _fileName), algRow, _bar, _results,
            _expected, _verdict);

        var compare = Ui.Card(L.T("Due file sono identici?"),
            L.T("Confronta il contenuto di due file (non conta il nome)."),
            Ui.Row(Ui.Btn(L.T("Confronta due file…"), SymbolRegular.BranchCompare24, async (_, _) => await CompareTwo())));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Hash e verifica"), L.T("Calcola l'impronta di un file e controlla se un download è autentico e integro.")),
            card, compare);
    }

    public bool Accepts(string path) => File.Exists(path);
    public void OpenFile(string path) { SetFile(path); _ = Compute(); }

    private void SetFile(string path)
    {
        _file = path;
        _fileName.Text = Path.GetFileName(path) + "  ·  " + Util.HumanSize(SafeLen(path));
        _results.Children.Clear();
        _computed.Clear();
        _verdict.Text = "";
    }

    private static long SafeLen(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }

    private async Task Compute()
    {
        var path = _file;
        if (path == null) return;
        var chosen = Algos.Where((_, i) => _checks[i].IsChecked == true).ToArray();
        if (chosen.Length == 0) { Dlg.Info(L.T("Scegli almeno un algoritmo.")); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _results.Children.Clear();
        _computed.Clear();
        _bar.Visibility = Visibility.Visible;
        _bar.Value = 0;
        try
        {
            var map = await Task.Run(() => HashFile(path, chosen, f => Dispatcher.Invoke(() => _bar.Value = f * 100), ct), ct);
            foreach (var (name, hex) in map)
            {
                _computed[name] = hex;
                _results.Children.Add(HashRow(name, hex));
            }
            CheckExpected();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Dlg.Error(L.T("Impossibile leggere il file:\n") + ex.Message); }
        finally { _bar.Visibility = Visibility.Collapsed; }
    }

    private static List<(string, string)> HashFile(string path, (string Name, Func<HashAlgorithm> Make)[] algos, Action<double> progress, CancellationToken ct)
    {
        var hashers = algos.Select(a => a.Make()).ToArray();
        using var fs = File.OpenRead(path);
        var total = Math.Max(1, fs.Length);
        var buffer = new byte[1 << 20];
        long done = 0;
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var h in hashers) h.TransformBlock(buffer, 0, n, null, 0);
            done += n;
            progress(done / (double)total);
        }
        var result = new List<(string, string)>();
        for (var i = 0; i < hashers.Length; i++)
        {
            hashers[i].TransformFinalBlock([], 0, 0);
            result.Add((algos[i].Name, Convert.ToHexString(hashers[i].Hash!).ToLowerInvariant()));
            hashers[i].Dispose();
        }
        return result;
    }

    private FrameworkElement HashRow(string name, string hex)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var value = new System.Windows.Controls.TextBox
        {
            Text = hex, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas, monospace"), TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res("TextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = Ui.IconBtn(SymbolRegular.Copy24, L.T("Copia"), (_, _) => { try { Clipboard.SetText(hex); MainWindow.Notify(L.T("Hash copiato.")); } catch { } });
        Grid.SetColumn(value, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(label);
        grid.Children.Add(value);
        grid.Children.Add(copy);
        return grid;
    }

    private void CheckExpected()
    {
        var exp = _expected.Text.Trim().Replace(" ", "");
        if (exp.Length == 0 || _computed.Count == 0) { _verdict.Text = ""; return; }
        var match = _computed.FirstOrDefault(kv => string.Equals(kv.Value, exp, StringComparison.OrdinalIgnoreCase));
        if (match.Key != null)
        {
            _verdict.Text = L.T($"✓ Corrisponde all'hash {match.Key}: il file è autentico e integro.");
            _verdict.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        }
        else
        {
            _verdict.Text = L.T("✗ Non corrisponde a nessuno degli hash calcolati: il file è diverso da quello atteso.");
            _verdict.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x4B, 0x4B));
        }
    }

    private async Task CompareTwo()
    {
        var a = Dlg.OpenFile(L.T("Primo file"), null, "hash");
        if (a == null) return;
        var b = Dlg.OpenFile(L.T("Secondo file"), null, "hash");
        if (b == null) return;
        try
        {
            MainWindow.Notify(L.T("Confronto in corso…"), 0);
            var (ha, hb) = await Task.Run(() =>
            {
                static string H(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
                return (H(a), H(b));
            });
            MainWindow.Notify("");
            if (ha == hb) Dlg.Info(L.T("I due file sono identici (stesso contenuto)."), L.T("Identici"));
            else Dlg.Info(L.T("I due file sono diversi."), L.T("Diversi"));
        }
        catch (Exception ex) { MainWindow.Notify(""); Dlg.Error(ex.Message); }
    }

    public void Shutdown() => _cts?.Cancel();
}
