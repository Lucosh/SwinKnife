using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace SwinKnife.Core;

public sealed class RenameItem : INotifyPropertyChanged
{
    public required string Path { get; set; }
    public required DateTime Modified { get; init; }
    public DateTime? Taken { get; set; }

    private string _newName = "", _status = "";
    private bool _problem;

    public string OldName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    public string NewName
    {
        get => _newName;
        set => Set(ref _newName, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool Problem
    {
        get => _problem;
        set => Set(ref _problem, value);
    }

    public bool Changed => !string.Equals(NewName, OldName, StringComparison.Ordinal);

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OldName)));

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum CaseMode { Keep, Lower, Upper, Title }

public enum ExtMode { Keep, Lower, Upper, Replace }

public enum RenameOrder { List, Name, Date }

public sealed class RenameRules
{
    public string Template { get; set; } = BulkRename.Tok("name");
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool Regex { get; set; }
    public bool MatchCase { get; set; }
    public CaseMode Case { get; set; }
    public int Start { get; set; } = 1;
    public int Step { get; set; } = 1;
    public int Digits { get; set; } = 3;
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public ExtMode Ext { get; set; }
    public string NewExt { get; set; } = "";
    public bool SpacesToUnderscore { get; set; }
    public bool RemoveAccents { get; set; }
    public RenameOrder Order { get; set; }
}

/// <summary>Calcolo dei nuovi nomi e rinomina sicura (anche con scambi di nomi).</summary>
public static class BulkRename
{
    private static readonly HashSet<string> PhotoExts = [".jpg", ".jpeg", ".tif", ".tiff", ".heic", ".heif", ".png", ".jfif", ".dng", ".nef", ".cr2", ".arw"];

    // segnaposto: nome canonico (inglese), nome italiano e nome nella lingua dell'interfaccia
    private static readonly (string canonical, string italian)[] Tokens =
        [("name", "nome"), ("num", "num"), ("date", "data"), ("time", "ora"), ("folder", "cartella"), ("ext", "ext")];

    private static string Localized(string italian) => italian switch
    {
        "nome" => L.T("nome"), "data" => L.T("data"), "ora" => L.T("ora"), "cartella" => L.T("cartella"), _ => italian,
    };

    /// <summary>Segnaposto come va scritto nella lingua dell'interfaccia, es. {data}.</summary>
    public static string Tok(string canonical) => "{" + Localized(Tokens.First(t => t.canonical == canonical).italian) + "}";

    private static readonly Dictionary<string, string> TokenMap = BuildTokenMap();

    private static Dictionary<string, string> BuildTokenMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, italian) in Tokens)
        {
            map[canonical] = canonical;
            map[italian] = canonical;
            map[Localized(italian)] = canonical;
        }
        return map;
    }

    private static readonly Regex Token = new(@"\{(" + string.Join("|", TokenMap.Keys.Select(System.Text.RegularExpressions.Regex.Escape)) + @")\}",
        RegexOptions.IgnoreCase);

    /// <summary>Data di scatto dai metadati EXIF (se presente).</summary>
    public static DateTime? DateTaken(string path)
    {
        if (!PhotoExts.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (dec.Frames[0].Metadata is BitmapMetadata { DateTaken: { } s } &&
                DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d)) return d;
        }
        catch { }
        try
        {
            // ripiego per i formati che WIC non legge (HEIC senza estensione, RAW...)
            using var img = new ImageMagick.MagickImage();
            img.Ping(path);
            var v = img.GetExifProfile()?.GetValue(ImageMagick.ExifTag.DateTimeOriginal)?.Value;
            if (v != null && DateTime.TryParseExact(v.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) return d2;
        }
        catch { }
        return null;
    }

    private static string StripAccents(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Calcola i nuovi nomi e segnala conflitti ed errori. Restituisce il messaggio d'errore delle regole, se c'è.</summary>
    public static string? Preview(IReadOnlyList<RenameItem> items, RenameRules r)
    {
        Regex? find = null;
        if (r.Find.Length > 0)
        {
            try
            {
                var pattern = r.Regex ? r.Find : System.Text.RegularExpressions.Regex.Escape(r.Find);
                find = new Regex(pattern, r.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return L.T("Espressione regolare non valida: ") + ex.Message;
            }
        }
        var ordered = r.Order switch
        {
            RenameOrder.Name => items.OrderBy(i => i.OldName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            RenameOrder.Date => items.OrderBy(i => i.Taken ?? i.Modified).ToList(),
            _ => items.ToList(),
        };
        var counter = r.Start;
        foreach (var item in ordered)
        {
            var oldExt = System.IO.Path.GetExtension(item.OldName);
            var stem = System.IO.Path.GetFileNameWithoutExtension(item.OldName);
            var date = item.Taken ?? item.Modified;
            var n = counter;
            var name = Token.Replace(r.Template.Length == 0 ? Tok("name") : r.Template, m => TokenMap[m.Groups[1].Value] switch
            {
                "name" => stem,
                "num" => n.ToString(new string('0', Math.Clamp(r.Digits, 1, 9)), CultureInfo.InvariantCulture),
                "date" => date.ToString(r.DateFormat, CultureInfo.InvariantCulture),
                "time" => date.ToString("HH.mm.ss", CultureInfo.InvariantCulture),
                "folder" => System.IO.Path.GetFileName(item.Folder),
                "ext" => oldExt.TrimStart('.'),
                _ => m.Value,
            });
            counter += r.Step;
            if (find != null)
            {
                try { name = find.Replace(name, r.Replace); }
                catch (RegexMatchTimeoutException) { }
            }
            name = r.Case switch
            {
                CaseMode.Lower => name.ToLower(CultureInfo.CurrentCulture),
                CaseMode.Upper => name.ToUpper(CultureInfo.CurrentCulture),
                CaseMode.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower(CultureInfo.CurrentCulture)),
                _ => name,
            };
            if (r.RemoveAccents) name = StripAccents(name);
            if (r.SpacesToUnderscore) name = name.Replace(' ', '_');
            var ext = r.Ext switch
            {
                ExtMode.Lower => oldExt.ToLowerInvariant(),
                ExtMode.Upper => oldExt.ToUpperInvariant(),
                ExtMode.Replace => r.NewExt.Trim().Length == 0 ? "" : "." + r.NewExt.Trim().TrimStart('.'),
                _ => oldExt,
            };
            item.NewName = name.Trim() + ext;
        }

        // controlli: caratteri non validi, nomi vuoti, doppioni, file già esistenti
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var targets = items.GroupBy(i => System.IO.Path.Combine(i.Folder, i.NewName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var sources = items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var target = System.IO.Path.Combine(item.Folder, item.NewName);
            string status;
            if (System.IO.Path.GetFileNameWithoutExtension(item.NewName).Trim().Length == 0) status = L.T("Nome vuoto");
            else if (item.NewName.IndexOfAny(invalid) >= 0) status = L.T("Contiene caratteri non ammessi (\\ / : * ? \" < > |)");
            else if (item.NewName.EndsWith('.') || item.NewName.EndsWith(' ')) status = L.T("Non può finire con un punto o uno spazio");
            else if (target.Length >= 260) status = L.T("Percorso troppo lungo");
            else if (targets[target] > 1) status = L.T("Nome uguale a un altro file");
            else if (!item.Changed) status = L.T("Invariato");
            else if (!sources.Contains(target) && (File.Exists(target) || Directory.Exists(target)) &&
                     !string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase)) status = L.T("Esiste già un file con questo nome");
            else status = "";
            item.Status = status;
            item.Problem = status.Length > 0 && status != L.T("Invariato");
        }
        return null;
    }

    /// <summary>
    /// Rinomina in due passaggi (prima nomi temporanei, poi quelli finali) così anche gli scambi
    /// tipo a→b, b→a funzionano. Restituisce le coppie (vecchio, nuovo) riuscite, per poter annullare.
    /// </summary>
    public static List<(string from, string to)> Apply(IEnumerable<(string from, string to)> moves, out List<string> errors)
    {
        errors = new List<string>();
        var list = moves.Where(m => !string.Equals(m.from, m.to, StringComparison.Ordinal)).ToList();
        var temp = new List<(string from, string tmp, string to)>();
        foreach (var (from, to) in list)
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(from)!, $"~swk{Guid.NewGuid():N}.tmp");
            try
            {
                File.Move(from, tmp);
                temp.Add((from, tmp, to));
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(from)}: {ex.Message}");
            }
        }
        var done = new List<(string, string)>();
        foreach (var (from, tmp, to) in temp)
        {
            try
            {
                File.Move(tmp, to);
                done.Add((from, to));
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(from)}: {ex.Message}");
                try { File.Move(tmp, from); }
                catch { }
            }
        }
        return done;
    }
}
