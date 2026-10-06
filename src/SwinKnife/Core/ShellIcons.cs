using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwinKnife.Core;

/// <summary>Icone di file e programmi come le mostra Esplora risorse.</summary>
public static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_TYPENAME = 0x400;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icona del file (se esiste) o del suo tipo. Da chiamare sul thread dell'interfaccia.</summary>
    public static ImageSource? For(string path, bool large = false)
    {
        var ext = Path.GetExtension(path);
        // per i tipi "generici" l'icona dipende solo dall'estensione: la memorizzo
        var perFile = ext.Length == 0 || ext.ToLowerInvariant() is ".exe" or ".lnk" or ".ico" or ".url" or ".msc" or ".cpl" or ".scr";
        var key = (perFile ? path : ext) + (large ? "|L" : "|S");
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var info = new SHFILEINFO();
        var exists = File.Exists(path) || Directory.Exists(path);
        var flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON) | (exists ? 0 : SHGFI_USEFILEATTRIBUTES);
        var attr = Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        ImageSource? result = null;
        if (SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                result = src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        if (Cache.Count > 2000) Cache.Clear();
        Cache[key] = result;
        return result;
    }

    /// <summary>Descrizione del tipo di file ("Documento di Microsoft Word"...).</summary>
    public static string TypeName(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        return info.szTypeName ?? "";
    }
}
