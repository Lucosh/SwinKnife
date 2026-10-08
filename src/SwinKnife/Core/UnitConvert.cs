using System.Net.Http;
using System.Text.Json;

namespace SwinKnife.Core;

/// <summary>Conversioni di unità di misura e di valute (tassi dalla Banca Centrale Europea, via Frankfurter).</summary>
public static class UnitConvert
{
    public sealed record Unit(string Name, double Factor); // valore_base = valore * Factor

    public sealed record Category(string Name, IReadOnlyList<Unit> Units, bool Temperature = false);

    public static readonly Category[] Categories =
    [
        new(L.T("Lunghezza"),
        [
            new(L.T("Millimetri"), 0.001), new(L.T("Centimetri"), 0.01), new(L.T("Metri"), 1),
            new(L.T("Chilometri"), 1000), new(L.T("Pollici"), 0.0254), new(L.T("Piedi"), 0.3048),
            new(L.T("Iarde"), 0.9144), new(L.T("Miglia"), 1609.344), new(L.T("Miglia nautiche"), 1852),
        ]),
        new(L.T("Peso"),
        [
            new(L.T("Milligrammi"), 0.001), new(L.T("Grammi"), 1), new(L.T("Chilogrammi"), 1000),
            new(L.T("Tonnellate"), 1_000_000), new(L.T("Once"), 28.349523125), new(L.T("Libbre"), 453.59237),
        ]),
        new(L.T("Temperatura"),
        [
            new(L.T("Celsius"), 1), new(L.T("Fahrenheit"), 1), new(L.T("Kelvin"), 1),
        ], Temperature: true),
        new(L.T("Area"),
        [
            new(L.T("Metri quadri"), 1), new(L.T("Centimetri quadri"), 0.0001), new(L.T("Chilometri quadri"), 1_000_000),
            new(L.T("Ettari"), 10_000), new(L.T("Piedi quadri"), 0.09290304), new(L.T("Acri"), 4046.8564224),
        ]),
        new(L.T("Volume"),
        [
            new(L.T("Millilitri"), 0.001), new(L.T("Litri"), 1), new(L.T("Metri cubi"), 1000),
            new(L.T("Galloni (USA)"), 3.785411784), new(L.T("Pinte (USA)"), 0.473176473),
        ]),
        new(L.T("Velocità"),
        [
            new(L.T("Metri al secondo"), 1), new(L.T("Chilometri orari"), 0.277777778),
            new(L.T("Miglia orarie"), 0.44704), new(L.T("Nodi"), 0.514444444),
        ]),
        new(L.T("Dati"),
        [
            new(L.T("Byte"), 1), new(L.T("Kilobyte (KB)"), 1024), new(L.T("Megabyte (MB)"), 1048576),
            new(L.T("Gigabyte (GB)"), 1073741824), new(L.T("Terabyte (TB)"), 1099511627776d),
        ]),
        new(L.T("Tempo"),
        [
            new(L.T("Secondi"), 1), new(L.T("Minuti"), 60), new(L.T("Ore"), 3600),
            new(L.T("Giorni"), 86400), new(L.T("Settimane"), 604800),
        ]),
    ];

    public static double Convert(Category c, Unit from, Unit to, double value)
    {
        if (!c.Temperature) return value * from.Factor / to.Factor;
        // via Celsius
        var celsius = from.Name switch
        {
            var n when n == L.T("Fahrenheit") => (value - 32) * 5 / 9,
            var n when n == L.T("Kelvin") => value - 273.15,
            _ => value,
        };
        return to.Name switch
        {
            var n when n == L.T("Fahrenheit") => celsius * 9 / 5 + 32,
            var n when n == L.T("Kelvin") => celsius + 273.15,
            _ => celsius,
        };
    }

    // ----------------------------------------------------------------- valute
    private static Dictionary<string, double>? _rates;
    private static DateTime _fetched;

    /// <summary>Tassi rispetto all'euro (EUR = 1). In cache per un'ora.</summary>
    public static async Task<Dictionary<string, double>> RatesAsync(CancellationToken ct)
    {
        if (_rates != null && DateTime.UtcNow - _fetched < TimeSpan.FromHours(1)) return _rates;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwinKnife/" + AppInfo.Version);
        var json = await http.GetStringAsync("https://api.frankfurter.app/latest?base=EUR", ct);
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1 };
        foreach (var r in doc.RootElement.GetProperty("rates").EnumerateObject())
            map[r.Name] = r.Value.GetDouble();
        _rates = map;
        _fetched = DateTime.UtcNow;
        return map;
    }

    public static double ConvertCurrency(Dictionary<string, double> rates, string from, string to, double amount)
    {
        if (!rates.TryGetValue(from, out var rf) || !rates.TryGetValue(to, out var rt))
            throw new InvalidOperationException(L.T("Valuta non disponibile."));
        return amount / rf * rt; // via euro
    }
}
