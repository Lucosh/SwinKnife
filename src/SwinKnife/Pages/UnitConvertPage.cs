using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Converte unità di misura, valute (tassi BCE) e orari tra fusi diversi.</summary>
public sealed class UnitConvertPage : UserControl, IToolPage
{
    public UnitConvertPage(MainWindow main)
    {
        var tabs = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
        tabs.Items.Add(new TabItem { Header = L.T("Unità di misura"), Content = UnitsTab() });
        tabs.Items.Add(new TabItem { Header = L.T("Valute"), Content = CurrencyTab() });
        tabs.Items.Add(new TabItem { Header = L.T("Fuso orario"), Content = TimeZoneTab() });

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Convertitore"), L.T("Unità di misura, valute e fusi orari.")),
            tabs);
    }

    private static string Fmt(double v) => v.ToString("0.######", Util.It);

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Any, Util.It, out v) ||
        double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v);

    // ------------------------------------------------------------------ unità
    private FrameworkElement UnitsTab()
    {
        var cat = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in UnitConvert.Categories) cat.Items.Add(c.Name);
        cat.SelectedIndex = 0;
        var from = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
        var to = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
        var input = new TextBox { Text = "1", Width = 160, VerticalAlignment = VerticalAlignment.Center };
        var result = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorPrimaryBrush") };

        void FillUnits()
        {
            var c = UnitConvert.Categories[cat.SelectedIndex];
            var pf = from.SelectedIndex; var pt = to.SelectedIndex;
            from.Items.Clear(); to.Items.Clear();
            foreach (var u in c.Units) { from.Items.Add(u.Name); to.Items.Add(u.Name); }
            from.SelectedIndex = pf >= 0 && pf < c.Units.Count ? pf : 0;
            to.SelectedIndex = pt >= 0 && pt < c.Units.Count ? pt : Math.Min(1, c.Units.Count - 1);
        }

        void Compute()
        {
            if (from.SelectedIndex < 0 || to.SelectedIndex < 0) return;
            var c = UnitConvert.Categories[cat.SelectedIndex];
            if (!TryNum(input.Text, out var v)) { result.Text = L.T("Scrivi un numero."); return; }
            var r = UnitConvert.Convert(c, c.Units[from.SelectedIndex], c.Units[to.SelectedIndex], v);
            result.Text = $"{Fmt(v)} {c.Units[from.SelectedIndex].Name}  =  {Fmt(r)} {c.Units[to.SelectedIndex].Name}";
        }

        cat.SelectionChanged += (_, _) => { FillUnits(); Compute(); };
        from.SelectionChanged += (_, _) => Compute();
        to.SelectionChanged += (_, _) => Compute();
        input.TextChanged += (_, _) => Compute();
        FillUnits();
        Compute();

        var card = Ui.Card(L.T("Converti"), null,
            Ui.Row(Ui.Label(L.T("Categoria:"), bold: true), new Border { Width = 10 }, cat),
            Ui.Row(input, new Border { Width = 12 }, from, new Border { Width = 12 },
                new SymbolIcon { Symbol = SymbolRegular.ArrowRight24, VerticalAlignment = VerticalAlignment.Center }, new Border { Width = 12 }, to),
            result);
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[0].Margin = new Thickness(0, 10, 0, 0);
        rows[1].Margin = new Thickness(0, 14, 0, 0);
        return card;
    }

    // ------------------------------------------------------------------ valute
    private readonly ComboBox _curFrom = new() { Width = 140, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _curTo = new() { Width = 140, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _curAmount = new() { Text = "1", Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _curResult = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _curNote = Ui.Hint("");
    private Dictionary<string, double>? _rates;

    private FrameworkElement CurrencyTab()
    {
        _curResult.Foreground = Ui.Res("TextFillColorPrimaryBrush");
        _curAmount.TextChanged += (_, _) => Compute();
        _curFrom.SelectionChanged += (_, _) => Compute();
        _curTo.SelectionChanged += (_, _) => Compute();

        var swap = Ui.IconBtn(SymbolRegular.ArrowSwap24, L.T("Inverti"), (_, _) =>
        {
            (_curFrom.SelectedIndex, _curTo.SelectedIndex) = (_curTo.SelectedIndex, _curFrom.SelectedIndex);
        });

        var card = Ui.Card(L.T("Converti valuta"), null,
            Ui.Row(_curAmount, new Border { Width = 12 }, _curFrom, new Border { Width = 8 }, swap, new Border { Width = 8 }, _curTo,
                new Border { Width = 12 }, Ui.IconBtn(SymbolRegular.ArrowClockwise24, L.T("Aggiorna i tassi"), async (_, _) => await Load(force: true))),
            _curResult, _curNote);
        ((StackPanel)card.Child).Children.OfType<StackPanel>().First().Margin = new Thickness(0, 10, 0, 0);
        _curNote.Margin = new Thickness(0, 10, 0, 0);
        _curResult.Text = L.T("Caricamento dei tassi…");
        _ = Load(force: false);
        return card;
    }

    private async Task Load(bool force)
    {
        try
        {
            _curResult.Text = L.T("Caricamento dei tassi…");
            if (force) { } // RatesAsync gestisce la cache
            _rates = await UnitConvert.RatesAsync(CancellationToken.None);
            var codes = _rates.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var pf = _curFrom.SelectedItem as string ?? "EUR";
            var pt = _curTo.SelectedItem as string ?? "USD";
            _curFrom.Items.Clear(); _curTo.Items.Clear();
            foreach (var c in codes) { _curFrom.Items.Add(c); _curTo.Items.Add(c); }
            _curFrom.SelectedItem = codes.Contains(pf) ? pf : "EUR";
            _curTo.SelectedItem = codes.Contains(pt) ? pt : "USD";
            _curNote.Text = L.T("Tassi di cambio della Banca Centrale Europea, aggiornati ogni giorno feriale.");
            Compute();
        }
        catch (Exception ex)
        {
            _curResult.Text = L.T("Tassi non disponibili.");
            _curNote.Text = L.T($"Impossibile scaricare i tassi (serve la connessione a Internet). {ex.Message}");
        }
    }

    private void Compute()
    {
        if (_rates == null || _curFrom.SelectedItem is not string a || _curTo.SelectedItem is not string b) return;
        if (!TryNum(_curAmount.Text, out var amount)) { _curResult.Text = L.T("Scrivi un importo."); return; }
        try
        {
            var r = UnitConvert.ConvertCurrency(_rates, a, b, amount);
            _curResult.Text = $"{Fmt(amount)} {a}  =  {r.ToString("0.00", Util.It)} {b}";
        }
        catch (Exception ex) { _curResult.Text = ex.Message; }
    }

    // ------------------------------------------------------------------ fuso orario
    private FrameworkElement TimeZoneTab()
    {
        var zones = TimeZoneInfo.GetSystemTimeZones().ToList();
        var from = new ComboBox { Width = 360, VerticalAlignment = VerticalAlignment.Center };
        var to = new ComboBox { Width = 360, VerticalAlignment = VerticalAlignment.Center };
        foreach (var z in zones) { from.Items.Add(z.DisplayName); to.Items.Add(z.DisplayName); }
        from.SelectedIndex = zones.FindIndex(z => z.Id == TimeZoneInfo.Local.Id);
        if (from.SelectedIndex < 0) from.SelectedIndex = 0;
        to.SelectedIndex = zones.FindIndex(z => z.Id == "UTC");
        if (to.SelectedIndex < 0) to.SelectedIndex = Math.Min(1, zones.Count - 1);

        var input = new TextBox { Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm", Util.It), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var result = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorPrimaryBrush") };

        void Compute()
        {
            if (from.SelectedIndex < 0 || to.SelectedIndex < 0) return;
            if (!DateTime.TryParse(input.Text.Trim(), Util.It, DateTimeStyles.None, out var dt) &&
                !DateTime.TryParse(input.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            { result.Text = L.T("Scrivi data e ora (es. 31/12/2025 18:30)."); return; }
            var src = zones[from.SelectedIndex];
            var dest = zones[to.SelectedIndex];
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var converted = TimeZoneInfo.ConvertTime(unspecified, src, dest);
            result.Text = $"{converted:dddd dd MMMM yyyy, HH:mm}";
        }

        from.SelectionChanged += (_, _) => Compute();
        to.SelectionChanged += (_, _) => Compute();
        input.TextChanged += (_, _) => Compute();
        Compute();

        var card = Ui.Card(L.T("Converti orario"), null,
            Ui.Row(Ui.Label(L.T("Data e ora:"), bold: true), new Border { Width = 10 }, input,
                new Border { Width = 10 }, Ui.Btn(L.T("Adesso"), SymbolRegular.Clock24, (_, _) => input.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm", Util.It))),
            Ui.Row(Ui.Label(L.T("Da:"), bold: true), new Border { Width = 10 }, from),
            Ui.Row(Ui.Label(L.T("A:"), bold: true), new Border { Width = 10 }, to),
            result);
        var rows = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        rows[0].Margin = new Thickness(0, 10, 0, 0);
        rows[1].Margin = new Thickness(0, 12, 0, 0);
        rows[2].Margin = new Thickness(0, 10, 0, 0);
        return card;
    }
}
