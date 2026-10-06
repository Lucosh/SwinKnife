using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SwinKnife.Core;

/// <summary>
/// Automazione di Word, Excel e PowerPoint via COM (late binding).
/// Le istanze vengono riutilizzate per tutta la sessione e chiuse con Dispose.
/// Tutte le chiamate devono avvenire sullo stesso thread STA (vedi <see cref="Sta"/>).
/// </summary>
public sealed class OfficeSession : IDisposable
{
    private readonly Dictionary<string, dynamic> _apps = new();

    public static bool IsAvailable(string progId)
    {
        using var k = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID");
        return k != null;
    }

    public static bool WordAvailable => IsAvailable("Word.Application");
    public static bool ExcelAvailable => IsAvailable("Excel.Application");
    public static bool PowerPointAvailable => IsAvailable("PowerPoint.Application");

    private dynamic App(string progId)
    {
        if (_apps.TryGetValue(progId, out var app)) return app;
        var type = Type.GetTypeFromProgID(progId)
                   ?? throw new InvalidOperationException(L.T($"{progId.Split('.')[0]} non è installato."));
        app = Activator.CreateInstance(type) ?? throw new InvalidOperationException(L.T($"Impossibile avviare {progId}."));
        try { app.Visible = false; } catch { /* PowerPoint non permette di nascondere la finestra */ }
        try { if (progId.StartsWith("Excel")) app.DisplayAlerts = false; else app.DisplayAlerts = 0; } catch { }
        _apps[progId] = app;
        return app;
    }

    // codici di formato di Office
    private static readonly Dictionary<string, int> WordFormats = new()
        { ["pdf"] = 17, ["docx"] = 16, ["rtf"] = 6, ["odt"] = 23, ["txt"] = 2, ["html"] = 10 };
    private static readonly Dictionary<string, int> ExcelFormats = new()
        { ["xlsx"] = 51, ["ods"] = 60, ["csv"] = 62 };
    private static readonly Dictionary<string, int> PptFormats = new()
        { ["pdf"] = 32, ["pptx"] = 24 };

    public void Word(string src, string dst, string target)
    {
        dynamic word = App("Word.Application");
        dynamic doc = word.Documents.Open(src, false, true, false);
        try
        {
            if (target == "pdf") doc.ExportAsFixedFormat(dst, 17);
            else if (target == "txt") doc.SaveAs2(FileName: dst, FileFormat: 2, Encoding: 65001);
            else doc.SaveAs2(dst, WordFormats[target]);
        }
        finally
        {
            doc.Close(0);
            Marshal.FinalReleaseComObject(doc);
        }
    }

    public void Excel(string src, string dst, string target)
    {
        dynamic excel = App("Excel.Application");
        dynamic wb = excel.Workbooks.Open(src, 0, true);
        try
        {
            if (target == "pdf") wb.ExportAsFixedFormat(0, dst);
            else wb.SaveAs(dst, ExcelFormats[target]);
        }
        finally
        {
            wb.Close(false);
            Marshal.FinalReleaseComObject(wb);
        }
    }

    public void PowerPoint(string src, string dst, string target)
    {
        dynamic ppt = App("PowerPoint.Application");
        dynamic pres = ppt.Presentations.Open(src, -1 /*ReadOnly*/, 0, 0 /*WithWindow*/);
        try
        {
            pres.SaveAs(dst, PptFormats[target]);
        }
        finally
        {
            pres.Close();
            Marshal.FinalReleaseComObject(pres);
        }
    }

    /// <summary>Converte un documento Office (Word/Excel/PowerPoint) con Office o, in mancanza, LibreOffice.</summary>
    public void Convert(string src, string dst, string target)
    {
        src = Path.GetFullPath(src);
        dst = Path.GetFullPath(dst);
        var kind = Formats.KindOf(src);
        var (progId, action) = kind switch
        {
            FileKind.Word or FileKind.Pdf => ("Word.Application", (Action)(() => Word(src, dst, target))),
            FileKind.Excel or FileKind.Table => ("Excel.Application", () => Excel(src, dst, target)),
            FileKind.PowerPoint => ("PowerPoint.Application", () => PowerPoint(src, dst, target)),
            _ => throw new NotSupportedException(L.T("Formato non gestito da Office.")),
        };
        if (IsAvailable(progId)) action();
        else LibreOffice(src, dst, target);
        if (!File.Exists(dst)) throw new IOException(L.T("La conversione non ha prodotto alcun file."));
    }

    public static string? SofficePath()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (root == null) continue;
            var p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void LibreOffice(string src, string dst, string target)
    {
        var exe = SofficePath() ?? throw new InvalidOperationException(
            L.T("Per questa conversione serve Microsoft Office oppure LibreOffice."));
        var tmp = Directory.CreateTempSubdirectory("swk_lo_").FullName;
        try
        {
            var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
            foreach (var a in new[] { "--headless", "--convert-to", target, "--outdir", tmp, src }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(600_000)) p.Kill();
            var produced = Path.Combine(tmp, Path.GetFileNameWithoutExtension(src) + "." + target);
            if (!File.Exists(produced)) throw new IOException(L.T("LibreOffice non è riuscito a convertire il file."));
            File.Move(produced, dst, true);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    public void Dispose()
    {
        foreach (var app in _apps.Values)
        {
            try { app.Quit(); } catch { }
            try { Marshal.FinalReleaseComObject(app); } catch { }
        }
        _apps.Clear();
    }
}

/// <summary>Esegue codice su un thread STA dedicato (necessario per COM e per alcune API WPF).</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    public static Task Run(Action action) => Run(() =>
    {
        action();
        return true;
    });
}
