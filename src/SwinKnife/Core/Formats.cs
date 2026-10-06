using System.Text;

namespace SwinKnife.Core;

public enum FileKind
{
    Unknown, Image, Svg, Raw, Pdf, Xps, Ebook, Word, Excel, PowerPoint, Table, Json, Markdown, Html, Text,
    Audio, Video, Archive, Font,
}

/// <summary>Classificazione dei file per estensione, condivisa da tutti gli strumenti.</summary>
public static class Formats
{
    private static readonly Dictionary<FileKind, string[]> Map = new()
    {
        [FileKind.Image] = [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp",
            ".heic", ".heif", ".avif", ".jxl", ".ico", ".ppm", ".pgm", ".pbm", ".pnm", ".tga", ".psd", ".pcx", ".dds",
            ".jp2", ".j2k", ".exr", ".hdr"],
        [FileKind.Svg] = [".svg", ".svgz"],
        [FileKind.Raw] = [".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".srw", ".pef"],
        [FileKind.Pdf] = [".pdf"],
        [FileKind.Xps] = [".xps", ".oxps"],
        [FileKind.Ebook] = [".epub", ".mobi", ".fb2"],
        [FileKind.Word] = [".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt"],
        [FileKind.Excel] = [".xls", ".xlsx", ".xlsm", ".xlsb", ".ods"],
        [FileKind.PowerPoint] = [".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp"],
        [FileKind.Table] = [".csv", ".tsv"],
        [FileKind.Json] = [".json"],
        [FileKind.Markdown] = [".md", ".markdown"],
        [FileKind.Html] = [".html", ".htm", ".xhtml"],
        [FileKind.Text] = [".txt", ".log", ".ini", ".cfg", ".conf", ".py", ".pyw", ".js", ".mjs", ".tsx", ".jsx",
            ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".cs", ".csproj", ".sln", ".slnx", ".xaml", ".go", ".rs", ".rb",
            ".php", ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".xml", ".yaml", ".yml", ".toml", ".sql", ".css", ".scss",
            ".less", ".vue", ".kt", ".swift", ".lua", ".r", ".pl", ".tex", ".srt", ".vtt", ".properties", ".gitignore",
            ".env", ".reg", ".nfo", ".vb", ".dart", ".scala", ".asm", ".rst", ".jsonl", ".gradle", ".cmake", ".props",
            ".targets", ".config", ".manifest"],
        [FileKind.Audio] = [".mp3", ".wav", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif",
            ".amr", ".ac3", ".mka"],
        [FileKind.Video] = [".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".3gp", ".mpg", ".mpeg",
            ".ts", ".mts", ".m2ts", ".vob", ".ogv", ".asf"],
        [FileKind.Archive] = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".jar",
            ".apk", ".nupkg", ".whl", ".cbz", ".cbr", ".lz", ".zst"],
        [FileKind.Font] = [".ttf", ".otf", ".ttc"],
    };

    private static readonly Dictionary<string, FileKind> ByExt =
        Map.SelectMany(kv => kv.Value.Select(e => (e, kv.Key))).ToDictionary(x => x.e, x => x.Key);

    public static readonly Dictionary<FileKind, string> Labels = new()
    {
        [FileKind.Image] = L.T("Immagine"), [FileKind.Svg] = L.T("Immagine vettoriale (SVG)"), [FileKind.Raw] = L.T("Foto RAW"),
        [FileKind.Pdf] = L.T("Documento PDF"), [FileKind.Xps] = L.T("Documento XPS"), [FileKind.Ebook] = "E-book",
        [FileKind.Word] = L.T("Documento di testo (Word)"), [FileKind.Excel] = L.T("Foglio di calcolo"),
        [FileKind.PowerPoint] = L.T("Presentazione"), [FileKind.Table] = L.T("Tabella CSV"), [FileKind.Json] = L.T("Dati JSON"),
        [FileKind.Markdown] = L.T("Markdown"), [FileKind.Html] = L.T("Pagina HTML"), [FileKind.Text] = L.T("Testo / codice"),
        [FileKind.Audio] = L.T("Audio"), [FileKind.Video] = L.T("Video"), [FileKind.Archive] = L.T("Archivio compresso"),
        [FileKind.Font] = L.T("Font"), [FileKind.Unknown] = L.T("File"),
    };

    public static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    public static FileKind KindOf(string path)
    {
        var ext = Ext(path);
        if (ext == ".ts") return IsMpegTs(path) ? FileKind.Video : FileKind.Text; // video MPEG-TS o TypeScript
        return ByExt.TryGetValue(ext, out var k) ? k : FileKind.Unknown;
    }

    private static bool IsMpegTs(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[189];
            return fs.Read(buf) == 189 && buf[0] == 0x47 && buf[188] == 0x47;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsImageLike(FileKind k) => k is FileKind.Image or FileKind.Svg or FileKind.Raw;

    public static string[] Extensions(params FileKind[] kinds) => kinds.SelectMany(k => Map[k]).ToArray();

    /// <summary>Filtro per i dialoghi di Windows, es. "Immagini|*.jpg;*.png".</summary>
    public static string Filter(string label, params FileKind[] kinds) =>
        $"{label}|{string.Join(";", Extensions(kinds).Select(e => "*" + e))}";

    /// <summary>Euristica: niente byte NUL e testo UTF-8 valido (o quasi solo caratteri stampabili).</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0) return true;
        if (sample.StartsWith(new byte[] { 0xFF, 0xFE }) || sample.StartsWith(new byte[] { 0xFE, 0xFF }) ||
            sample.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return true;
        if (sample.IndexOf((byte)0) >= 0) return false;
        try
        {
            new UTF8Encoding(false, true).GetString(sample[..Math.Max(0, sample.Length - 4)]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            var control = 0;
            foreach (var b in sample)
                if (b < 9 || (b > 13 && b < 32)) control++;
            return control < sample.Length * 0.02;
        }
    }

    public static (string text, Encoding encoding) ReadText(string path, long maxBytes = long.MaxValue)
    {
        byte[] raw;
        using (var fs = File.OpenRead(path))
        {
            var n = (int)Math.Min(fs.Length, maxBytes);
            raw = new byte[n];
            fs.ReadExactly(raw);
        }
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return (Encoding.UTF8.GetString(raw, 3, raw.Length - 3), new UTF8Encoding(true));
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return (Encoding.Unicode.GetString(raw, 2, raw.Length - 2), Encoding.Unicode);
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2), Encoding.BigEndianUnicode);
        try
        {
            return (new UTF8Encoding(false, true).GetString(raw), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            var latin = Encoding.GetEncoding(1252);
            return (latin.GetString(raw), latin);
        }
    }
}
