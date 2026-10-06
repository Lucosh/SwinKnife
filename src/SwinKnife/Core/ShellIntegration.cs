using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SwinKnife.Core;

/// <summary>
/// Voci "SwinKnife" nel menu del tasto destro di Esplora risorse (registro utente, nessun privilegio richiesto).
/// Su Windows 11 compaiono in "Mostra altre opzioni" (o Maiusc+F10).
/// </summary>
public static class ShellIntegration
{
    private const string Root = @"Software\Classes";
    private const string Marker = "SwinKnifeManaged";

    private sealed record Verb(string Key, string Label, string Args);

    private sealed record Menu(string Path, string Key, string Label, Verb[] Verbs, string Target = "\"%1\"");

    private static readonly Menu[] ArchiveMenus = new[] { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz" }
        .Select(ext => new Menu($@"SystemFileAssociations\{ext}\shell", "SwinKnifeArchive", L.T("SwinKnife: archivio"),
        [
            new("a_here", L.T("Estrai qui"), "--action extract-here"),
            new("b_page", L.T("Estrai in…"), "--page archive"),
        ])).ToArray();

    private static readonly Menu[] Menus =
    [
        new(@"*\shell", "SwinKnife", "SwinKnife",
        [
            new("a_open", L.T("Apri con SwinKnife"), ""),
            new("b_convert", L.T("Converti…"), "--page convert"),
            new("c_archive", L.T("Aggiungi a un archivio…"), "--page archive"),
            new("d_gemini", L.T("Chiedi a Gemini di riassumerlo"), "--action gemini"),
        ]),
        new(@"SystemFileAssociations\.pdf\shell", "SwinKnifePdf", L.T("SwinKnife: PDF"),
        [
            new("a_edit", L.T("Modifica PDF"), "--page pdf"),
            new("b_compress", L.T("Comprimi PDF"), "--action compress-pdf"),
            new("c_ocr", L.T("Rendi ricercabile (OCR)"), "--action ocr-pdf"),
        ]),
        new(@"SystemFileAssociations\image\shell", "SwinKnifePhoto", L.T("SwinKnife: foto"),
        [
            new("a_edit", L.T("Modifica foto"), "--page photo"),
            new("b_convert", L.T("Converti immagine…"), "--page convert"),
        ]),
        new(@"Directory\shell", "SwinKnife", "SwinKnife",
        [
            new("a_disk", L.T("Analizza spazio occupato"), "--page disk"),
            new("b_dupes", L.T("Cerca duplicati"), "--page dupes"),
            new("c_rename", L.T("Rinomina i file"), "--page rename"),
            new("d_archive", L.T("Comprimi in un archivio…"), "--page archive"),
        ]),
        new(@"Directory\Background\shell", "SwinKnife", "SwinKnife",
        [
            new("a_disk", L.T("Analizza spazio occupato"), "--page disk"),
            new("b_dupes", L.T("Cerca duplicati"), "--page dupes"),
            new("c_rename", L.T("Rinomina i file"), "--page rename"),
        ], "\"%V\""),
        .. ArchiveMenus,
    ];

    private static string Exe => Environment.ProcessPath ?? "";

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\*\shell\SwinKnife");
        return k?.GetValue(Marker) != null;
    }

    public static void Install()
    {
        foreach (var m in Menus)
        {
            var basePath = $@"{Root}\{m.Path}\{m.Key}";
            Registry.CurrentUser.DeleteSubKeyTree(basePath, false);
            using var k = Registry.CurrentUser.CreateSubKey(basePath);
            k.SetValue("MUIVerb", m.Label);
            k.SetValue("Icon", $"\"{Exe}\",0");
            k.SetValue("SubCommands", "");
            k.SetValue(Marker, "1");
            k.SetValue("Lang", L.Code);
            foreach (var v in m.Verbs)
            {
                using var vk = k.CreateSubKey($@"shell\{v.Key}");
                vk.SetValue("MUIVerb", v.Label);
                using var ck = vk.CreateSubKey("command");
                ck.SetValue("", $"\"{Exe}\" {v.Args} {m.Target}".Replace("  ", " "));
            }
        }
        Notify();
    }

    public static void Uninstall()
    {
        foreach (var m in Menus)
        {
            var path = $@"{Root}\{m.Path}\{m.Key}";
            using (var k = Registry.CurrentUser.OpenSubKey(path))
            {
                if (k?.GetValue(Marker) == null) continue; // non è nostro: non lo tocco
            }
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
        Notify();
    }

    /// <summary>Se il menu è installato ma punta a un altro eseguibile (app spostata/aggiornata) lo riscrive.</summary>
    public static void RefreshIfInstalled()
    {
        try
        {
            if (!IsInstalled()) return;
            using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\*\shell\SwinKnife\shell\a_open\command");
            // riscrivo anche se mancano voci aggiunte in una versione successiva
            using var newest = Registry.CurrentUser.OpenSubKey($@"{Root}\SystemFileAssociations\.zip\shell\SwinKnifeArchive");
            using var root = Registry.CurrentUser.OpenSubKey($@"{Root}\*\shell\SwinKnife");
            var langChanged = root?.GetValue("Lang") as string != L.Code;
            if (k?.GetValue("") is string cmd && !cmd.Contains(Exe, StringComparison.OrdinalIgnoreCase) || newest == null || langChanged) Install();
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Aggiornamento menu contestuale");
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
}
