using System.IO.Compression;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace SwinKnife.Core;

/// <summary>Rimuove i dati nascosti (autore, GPS, software, revisioni) da immagini, PDF, documenti Office e file multimediali.</summary>
public static class MetadataCleaner
{
    public static readonly HashSet<string> Images = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp", ".heic", ".heif", ".gif", ".bmp"];
    public static readonly HashSet<string> Office = [".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm"];
    public static readonly HashSet<string> Media = [".mp4", ".mov", ".mkv", ".avi", ".m4v", ".mp3", ".m4a", ".flac", ".wav", ".ogg"];

    public static bool CanClean(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return Images.Contains(e) || Office.Contains(e) || e == ".pdf" || Media.Contains(e);
    }

    public static bool NeedsFfmpeg(string path) => Media.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Pulisce src scrivendo in dst (possono coincidere). Restituisce una breve descrizione.</summary>
    public static string Clean(string src, string dst, string? ffmpeg, CancellationToken ct)
    {
        var ext = Path.GetExtension(src).ToLowerInvariant();
        var sameFile = string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        // scrivo in un file temporaneo con la stessa estensione (ffmpeg deduce il formato dal nome), poi rimpiazzo
        var tmp = sameFile ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dst))!, Path.GetFileNameWithoutExtension(dst) + ".swkclean" + ext) : dst;

        string result;
        if (Images.Contains(ext)) { CleanImage(src, tmp); result = L.T("dati EXIF/GPS e profili rimossi"); }
        else if (ext == ".pdf") { CleanPdf(src, tmp); result = L.T("autore, titolo e metadati del documento rimossi"); }
        else if (Office.Contains(ext)) { CleanOffice(src, tmp); result = L.T("autore, azienda e revisioni rimossi"); }
        else if (Media.Contains(ext))
        {
            if (ffmpeg == null) throw new InvalidOperationException(L.T("Per i file audio e video serve ffmpeg."));
            Ffmpeg.Run(ffmpeg, ["-i", src, "-map_metadata", "-1", "-map_metadata:s", "-1", "-c", "copy", tmp], 0, _ => { }, ct);
            result = L.T("tag e metadati rimossi");
        }
        else throw new NotSupportedException(L.T("Tipo di file non gestito."));

        if (sameFile)
        {
            File.Delete(dst);
            File.Move(tmp, dst);
        }
        return result;
    }

    private static void CleanImage(string src, string dst)
    {
        using var img = new MagickImage(src);
        img.Strip();                         // toglie EXIF, IPTC, XMP, profili e commenti
        img.Write(dst);
    }

    private static void CleanPdf(string src, string dst)
    {
        using var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
        doc.Info.Author = "";
        doc.Info.Title = "";
        doc.Info.Subject = "";
        doc.Info.Keywords = "";
        doc.Info.Creator = "";
        try { doc.Internals.Catalog.Elements.Remove("/Metadata"); } catch { } // XMP
        doc.Save(dst);
    }

    private static void CleanOffice(string src, string dst)
    {
        if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) File.Copy(src, dst, true);
        using var zip = ZipFile.Open(dst, ZipArchiveMode.Update);
        foreach (var name in new[] { "docProps/core.xml", "docProps/app.xml", "docProps/custom.xml" })
            zip.GetEntry(name)?.Delete();
    }
}
