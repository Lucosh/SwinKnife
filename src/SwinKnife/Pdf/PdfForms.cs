using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace SwinKnife.Pdf;

public enum FieldKind { Text, CheckBox, Radio, Combo, List, Other }

public sealed class FormField
{
    public required string Name { get; init; }
    public required FieldKind Kind { get; init; }
    public string Value { get; set; } = "";
    public bool Checked { get; set; }
    public List<string> Options { get; init; } = new();
    public int SelectedIndex { get; set; } = -1;
    public bool ReadOnly { get; init; }
    public bool Multiline { get; init; }
}

/// <summary>Lettura e compilazione dei moduli PDF (AcroForm).</summary>
public static class PdfForms
{
    static PdfForms()
    {
        // i campi di testo vengono ridisegnati con i font di Windows
        try { GlobalFontSettings.UseWindowsFontsUnderWindows = true; } catch { }
    }

    private static List<string> ChoiceOptions(PdfAcroField f)
    {
        var list = new List<string>();
        if (f.Elements.GetArray("/Opt") is not { } opt) return list;
        foreach (var item in opt.Elements)
        {
            var it = item is PdfReference r ? r.Value : item;
            list.Add(it switch
            {
                PdfString s => s.Value,
                PdfArray a when a.Elements.Count >= 2 => a.Elements.GetString(1),
                _ => it?.ToString() ?? "",
            });
        }
        return list;
    }

    private static List<string> RadioOptions(PdfAcroField f)
    {
        var list = new List<string>();
        if (f.Elements.GetArray("/Kids") is not { } kids) return list;
        foreach (var k in kids.Elements)
        {
            if ((k is PdfReference r ? r.Value : k) is not PdfDictionary kid) continue;
            var normal = kid.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            var name = normal?.Elements.Keys.FirstOrDefault(n => n != "/Off");
            if (name != null) list.Add(name.TrimStart('/'));
        }
        return list;
    }

    public static List<FormField> Read(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        var result = new List<FormField>();
        var form = doc.AcroForm;
        if (form == null) return result;
        foreach (var name in form.Fields.DescendantNames)
        {
            var f = form.Fields[name];
            if (f == null || f.HasKids && f is not PdfRadioButtonField) continue;
            FormField field = f switch
            {
                PdfTextField t => new FormField { Name = name, Kind = FieldKind.Text, Value = t.Text ?? "", ReadOnly = t.ReadOnly, Multiline = t.MultiLine },
                PdfCheckBoxField c => new FormField { Name = name, Kind = FieldKind.CheckBox, Checked = c.Checked, ReadOnly = c.ReadOnly },
                PdfRadioButtonField rb => new FormField { Name = name, Kind = FieldKind.Radio, Options = RadioOptions(rb), SelectedIndex = SafeIndex(() => rb.SelectedIndex), ReadOnly = rb.ReadOnly },
                PdfComboBoxField cb => Choice(name, FieldKind.Combo, cb),
                PdfListBoxField lb => Choice(name, FieldKind.List, lb),
                _ => new FormField { Name = name, Kind = FieldKind.Other, ReadOnly = true },
            };
            if (field.Kind != FieldKind.Other) result.Add(field);
        }
        return result;
    }

    private static FormField Choice(string name, FieldKind kind, PdfChoiceField f)
    {
        var options = ChoiceOptions(f);
        var v = f.Elements.GetString("/V");
        return new FormField { Name = name, Kind = kind, Options = options, SelectedIndex = options.IndexOf(v), ReadOnly = f.ReadOnly };
    }

    private static int SafeIndex(Func<int> get)
    {
        try { return get(); }
        catch { return -1; }
    }

    public static byte[] Fill(byte[] pdf, IEnumerable<FormField> fields)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
        var form = doc.AcroForm ?? throw new InvalidOperationException(L.T("Il documento non contiene un modulo."));
        foreach (var field in fields.Where(x => !x.ReadOnly))
        {
            var f = form.Fields[field.Name];
            switch (f)
            {
                case PdfTextField t:
                    t.Text = field.Value;
                    break;
                case PdfCheckBoxField c:
                    c.Checked = field.Checked;
                    break;
                case PdfRadioButtonField rb when field.SelectedIndex >= 0:
                    rb.SelectedIndex = field.SelectedIndex;
                    break;
                case PdfChoiceField ch when field.SelectedIndex >= 0 && field.SelectedIndex < field.Options.Count:
                    // imposto /V a mano: PDFsharp scriverebbe il valore tra parentesi
                    ch.Elements.SetString("/V", field.Options[field.SelectedIndex]);
                    var idx = new PdfArray(doc);
                    idx.Elements.Add(new PdfInteger(field.SelectedIndex));
                    ch.Elements["/I"] = idx;
                    break;
            }
        }
        // chiede ai lettori di ridisegnare i campi con i nuovi valori
        form.Elements["/NeedAppearances"] = new PdfBoolean(true);
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }
}
