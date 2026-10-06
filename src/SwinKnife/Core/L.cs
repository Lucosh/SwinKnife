using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Markup;

namespace SwinKnife.Core;

/// <summary>
/// Traduzioni dell'interfaccia. I testi nel codice sono in italiano e fanno da chiave:
/// <c>L.T("Salva")</c>, <c>L.T($"Trovati {n} file")</c> (la chiave diventa "Trovati {0} file").
/// Le traduzioni stanno in Lang/xx.json (risorse incorporate); un testo mancante resta in italiano.
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages =
        [("it", "Italiano"), ("en", "English"), ("de", "Deutsch"), ("es", "Español"), ("fr", "Français")];

    /// <summary>Lingua in uso ("it", "en", ...).</summary>
    public static string Code { get; }

    public static CultureInfo Culture { get; }

    private static readonly Dictionary<string, string> Map;
    private static readonly ConcurrentDictionary<string, byte>? Missing;

    static L()
    {
        // SWINKNIFE_LANG forza una lingua (utile per provare le traduzioni)
        Code = Resolve(Environment.GetEnvironmentVariable("SWINKNIFE_LANG") is { Length: > 0 } forced ? forced : Settings.Get("language"));
        Culture = CultureInfo.GetCultureInfo(Code switch
        {
            "en" => "en-US", "de" => "de-DE", "es" => "es-ES", "fr" => "fr-FR", _ => "it-IT",
        });
        Map = Load(Code);
        // modalità per chi traduce: elenca i testi senza traduzione
        if (Code != "it" && Environment.GetEnvironmentVariable("SWINKNIFE_MISSING") is { Length: > 0 }) Missing = new();
    }

    /// <summary>Lingua scelta nelle impostazioni, altrimenti quella di Windows (inglese se non è tra quelle disponibili).</summary>
    public static string Resolve(string? setting)
    {
        if (setting != null && Languages.Any(l => l.Code == setting)) return setting;
        var os = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == os) ? os : "en";
    }

    private static Dictionary<string, string> Load(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code == "it") return map;
        try
        {
            using var s = typeof(L).Assembly.GetManifestResourceStream($"SwinKnife.Lang.{code}.json");
            if (s == null) return map;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            foreach (var (k, v) in data)
                if (!string.IsNullOrEmpty(v) && !k.StartsWith("//", StringComparison.Ordinal)) map[k] = v;
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Caricamento traduzioni " + code);
        }
        return map;
    }

    /// <summary>Applica la lingua a thread, formattazione di numeri/date e WPF. Da chiamare all'avvio.</summary>
    public static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        try
        {
            System.Windows.FrameworkElement.LanguageProperty.OverrideMetadata(typeof(System.Windows.FrameworkElement),
                new System.Windows.FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Culture.IetfLanguageTag)));
        }
        catch (ArgumentException)
        {
            // già impostato (es. Apply chiamato due volte): le finestre impostano comunque Language
        }
    }

    /// <summary>Lingua per la formattazione di numeri e date nei binding WPF.</summary>
    public static XmlLanguage Xml => XmlLanguage.GetLanguage(Culture.IetfLanguageTag);

    public static string T(string text)
    {
        if (Map.TryGetValue(text, out var t)) return t;
        Missing?.TryAdd(text, 0);
        return text;
    }

    public static string T(LocalizedString text) => text.ToStringAndClear();

    /// <summary>Testo con segnaposto {0}, {1}… (per i casi in cui la stringa non è scritta direttamente nel codice).</summary>
    public static string F(string format, params object?[] args)
    {
        var f = T(format);
        try { return string.Format(Culture, f, args); }
        catch (FormatException) { return string.Format(Culture, format, args); }
    }

    /// <summary>Scrive i testi senza traduzione (solo con SWINKNIFE_MISSING impostata).</summary>
    public static void DumpMissing()
    {
        if (Missing == null || Missing.IsEmpty) return;
        var path = Path.Combine(AppInfo.DataDir, $"missing-{Code}.txt");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        File.WriteAllLines(path, existing.Concat(Missing.Keys.Select(k => k.Replace("\r", "\\r").Replace("\n", "\\n"))).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>Raccoglie le parti di <c>$"..."</c>: il testo fisso con {0}, {1}… diventa la chiave di traduzione.</summary>
    [InterpolatedStringHandler]
    public ref struct LocalizedString
    {
        private readonly StringBuilder _key;
        private readonly List<object?> _args;

        public LocalizedString(int literalLength, int formattedCount)
        {
            _key = new StringBuilder(literalLength + formattedCount * 4);
            _args = new List<object?>(formattedCount);
        }

        public void AppendLiteral(string s) => _key.Append(s.Replace("{", "{{").Replace("}", "}}"));

        public void AppendFormatted<TValue>(TValue value) => Hole(value, 0, null);

        public void AppendFormatted<TValue>(TValue value, string? format) => Hole(value, 0, format);

        public void AppendFormatted<TValue>(TValue value, int alignment) => Hole(value, alignment, null);

        public void AppendFormatted<TValue>(TValue value, int alignment, string? format) => Hole(value, alignment, format);

        public void AppendFormatted(string? value) => Hole(value, 0, null);

        private void Hole(object? value, int alignment, string? format)
        {
            _key.Append('{').Append(_args.Count);
            if (alignment != 0) _key.Append(',').Append(alignment);
            if (format != null) _key.Append(':').Append(format);
            _key.Append('}');
            _args.Add(value);
        }

        public string ToStringAndClear()
        {
            var key = _key.ToString();
            var args = _args.ToArray();
            var f = T(key);
            try { return string.Format(Culture, f, args); }
            catch (FormatException) { return string.Format(Culture, key, args); }
        }
    }
}

/// <summary>Testo tradotto in XAML: <c>Text="{l:T 'Apri file'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
