using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;

namespace SwinKnife.Core;

/// <summary>Lettura e scrittura di dati tabellari: CSV/TSV, Excel (xlsx) e JSON.</summary>
public static class Tables
{
    public sealed class Sheet(string name, List<List<object?>> rows)
    {
        public string Name { get; } = name;
        public List<List<object?>> Rows { get; } = rows;
    }

    // ------------------------------------------------------------------ CSV
    public static char SniffDelimiter(string sample)
    {
        var lines = sample.Split('\n').Take(20).Where(l => l.Length > 0).ToList();
        char best = ',';
        var bestScore = -1;
        foreach (var d in new[] { ';', ',', '\t', '|' })
        {
            var counts = lines.Select(l => l.Count(c => c == d)).ToList();
            if (counts.Count == 0 || counts[0] == 0) continue;
            var score = counts.Count(c => c == counts[0]) * 10 + counts[0];
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        return best;
    }

    public static List<List<object?>> ParseCsv(string text, char delimiter, int maxRows = int.MaxValue)
    {
        var rows = new List<List<object?>>();
        var row = new List<object?>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<object?>();
                if (rows.Count >= maxRows) return rows;
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public static void WriteCsv(IEnumerable<IEnumerable<object?>> rows, string path, char delimiter)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(delimiter, r.Select(v =>
            {
                var s = v switch
                {
                    null => "",
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    double d => d.ToString(Util.It),
                    _ => v.ToString() ?? "",
                };
                return s.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            })));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM: Excel riconosce gli accenti
    }

    /// <summary>"1.234,56" → 1234.56, "42" → 42, altrimenti la stringa.</summary>
    public static object? ParseCell(object? v)
    {
        if (v is not string s) return v;
        s = s.Trim();
        if (s.Length == 0) return null;
        if (s.Length > 1 && s.StartsWith('0') && !s.StartsWith("0,") && !s.StartsWith("0.")) return v;
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^-?\d{1,3}(\.\d{3})+(,\d+)?$|^-?\d+,\d+$") &&
            double.TryParse(s, NumberStyles.Number, Util.It, out var dIt)) return dIt;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^-?\d+\.\d+$") &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return v;
    }

    // ------------------------------------------------------------------ lettura
    public static List<Sheet> Read(string path, int maxRows = int.MaxValue)
    {
        var kind = Formats.KindOf(path);
        var name = Path.GetFileNameWithoutExtension(path);
        switch (kind)
        {
            case FileKind.Table:
            {
                var (text, _) = Formats.ReadText(path);
                var delim = Formats.Ext(path) == ".tsv" ? '\t' : SniffDelimiter(text[..Math.Min(text.Length, 20000)]);
                return [new Sheet(name, ParseCsv(text, delim, maxRows))];
            }
            case FileKind.Json:
            {
                var node = JsonNode.Parse(Formats.ReadText(path).text);
                return [new Sheet(name, JsonRows(node))];
            }
            case FileKind.Excel when Formats.Ext(path) is ".xlsx" or ".xlsm":
            {
                using var wb = new XLWorkbook(path);
                var sheets = new List<Sheet>();
                foreach (var ws in wb.Worksheets)
                {
                    var rows = new List<List<object?>>();
                    var used = ws.RangeUsed();
                    if (used != null)
                    {
                        var lastCol = used.LastColumn().ColumnNumber();
                        foreach (var r in ws.RowsUsed())
                        {
                            if (rows.Count >= maxRows) break;
                            var vals = new List<object?>();
                            for (var c = 1; c <= lastCol; c++)
                            {
                                var cell = r.Cell(c);
                                vals.Add(cell.IsEmpty() ? null : cell.Value.IsNumber ? cell.Value.GetNumber()
                                    : cell.Value.IsDateTime ? cell.Value.GetDateTime() : cell.Value.IsBoolean ? cell.Value.GetBoolean()
                                    : cell.GetFormattedString());
                            }
                            rows.Add(vals);
                        }
                    }
                    sheets.Add(new Sheet(ws.Name, rows));
                }
                return sheets;
            }
            default:
                throw new NotSupportedException(L.T("Formato tabellare non supportato."));
        }
    }

    private static List<List<object?>> JsonRows(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var arr = obj.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault(a => a.Count > 0 && a[0] is JsonObject);
            if (arr != null) node = arr;
            else
            {
                var rows = new List<List<object?>> { new() { "chiave", "valore" } };
                foreach (var kv in obj) rows.Add([kv.Key, JsonValueOf(kv.Value)]);
                return rows;
            }
        }
        if (node is JsonArray list)
        {
            if (list.All(x => x is JsonObject))
            {
                var keys = new List<string>();
                foreach (var o in list.Cast<JsonObject>())
                foreach (var kv in o)
                    if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                var rows = new List<List<object?>> { keys.Cast<object?>().ToList() };
                foreach (var o in list.Cast<JsonObject>())
                    rows.Add(keys.Select(k => JsonValueOf(o[k])).ToList());
                return rows;
            }
            return list.Select(x => x is JsonArray a ? a.Select(JsonValueOf).ToList() : new List<object?> { JsonValueOf(x) }).ToList();
        }
        return [[JsonValueOf(node)]];
    }

    private static object? JsonValueOf(JsonNode? n) => n switch
    {
        null => null,
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<double>(out var d) => d,
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<bool>(out var b) => b,
        System.Text.Json.Nodes.JsonValue v => v.ToString(),
        _ => n.ToJsonString(),
    };

    // ------------------------------------------------------------------ scrittura
    public static List<string> Write(List<Sheet> sheets, string dst, string target, char csvDelimiter = ';')
    {
        var outs = new List<string>();
        switch (target)
        {
            case "csv":
                foreach (var s in sheets)
                {
                    var p = sheets.Count == 1 ? dst : Util.UniquePath(Path.Combine(Path.GetDirectoryName(dst)!,
                        $"{Path.GetFileNameWithoutExtension(dst)}_{Util.SafeFileName(s.Name)}.csv"));
                    WriteCsv(s.Rows, p, csvDelimiter);
                    outs.Add(p);
                }
                break;
            case "xlsx":
            {
                using var wb = new XLWorkbook();
                foreach (var s in sheets)
                {
                    var name = Util.SafeFileName(s.Name).Replace("[", "").Replace("]", "");
                    var ws = wb.Worksheets.Add(string.IsNullOrEmpty(name) ? L.T("Foglio") : name[..Math.Min(31, name.Length)]);
                    for (var r = 0; r < s.Rows.Count; r++)
                    for (var c = 0; c < s.Rows[r].Count; c++)
                    {
                        var v = ParseCell(s.Rows[r][c]);
                        var cell = ws.Cell(r + 1, c + 1);
                        cell.Value = v switch
                        {
                            null => Blank.Value,
                            long l => l,
                            double d => d,
                            bool b => b,
                            DateTime dt => dt,
                            _ => v.ToString(),
                        };
                    }
                    if (s.Rows.Count > 0)
                    {
                        ws.Row(1).Style.Font.Bold = true;
                        ws.Columns().AdjustToContents(1, Math.Min(s.Rows.Count, 200));
                    }
                }
                wb.SaveAs(dst);
                outs.Add(dst);
                break;
            }
            case "json":
            {
                JsonNode Records(Sheet s)
                {
                    var arr = new JsonArray();
                    if (s.Rows.Count == 0) return arr;
                    var head = s.Rows[0].Select((h, i) => h?.ToString() is { Length: > 0 } t ? t : $"col{i + 1}").ToList();
                    foreach (var r in s.Rows.Skip(1))
                    {
                        var o = new JsonObject();
                        for (var i = 0; i < head.Count && i < r.Count; i++)
                            o[head[i]] = ParseCell(r[i]) switch
                            {
                                null => null,
                                long l => System.Text.Json.Nodes.JsonValue.Create(l),
                                double d => System.Text.Json.Nodes.JsonValue.Create(d),
                                bool b => System.Text.Json.Nodes.JsonValue.Create(b),
                                DateTime dt => System.Text.Json.Nodes.JsonValue.Create(dt),
                                var x => System.Text.Json.Nodes.JsonValue.Create(x.ToString()),
                            };
                        arr.Add(o);
                    }
                    return arr;
                }
                JsonNode root = sheets.Count == 1 ? Records(sheets[0]) : new JsonObject(sheets.Select(s => KeyValuePair.Create(s.Name, (JsonNode?)Records(s))));
                File.WriteAllText(dst, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
                outs.Add(dst);
                break;
            }
            default:
                throw new NotSupportedException(target);
        }
        return outs;
    }
}
