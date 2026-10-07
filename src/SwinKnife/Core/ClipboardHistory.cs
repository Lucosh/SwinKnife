using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;

namespace SwinKnife.Core;

public enum ClipKind { Text, Image, Files }

public sealed class ClipEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";          // testo, oppure elenco dei file (uno per riga)
    public string? Image { get; set; }              // nome del PNG nella cartella della cronologia
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
    public string? Source { get; set; }             // programma da cui è stato copiato
}

/// <summary>
/// Cronologia degli appunti: ogni volta che si copia qualcosa (testo, immagine, file) viene salvato qui.
/// I contenuti che le app segnano come privati (password manager) non vengono registrati.
/// </summary>
public static class ClipboardHistory
{
    public const int MaxItems = 300;
    private const int MaxImagePixels = 4096 * 4096;
    public static readonly string Dir = Path.Combine(AppInfo.DataDir, "clipboard");
    private static readonly string IndexFile = Path.Combine(Dir, "history.json");
    private static List<ClipEntry>? _items;
    private static bool _ignoreNext;
    private static DispatcherTimer? _debounce;

    public static event Action? Changed;

    public static bool Enabled
    {
        get => Settings.Get("clipboard.enabled") != "0";
        set => Settings.Set("clipboard.enabled", value ? null : "0");
    }

    public static List<ClipEntry> Items
    {
        get
        {
            if (_items != null) return _items;
            try
            {
                _items = File.Exists(IndexFile) ? JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(IndexFile)) ?? [] : [];
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Cronologia appunti");
                _items = [];
            }
            return _items;
        }
    }

    public static void Start() => Resident.ClipboardChanged += () =>
    {
        // alcune app scrivono negli appunti più volte di seguito: aspetto che abbiano finito
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Capture();
        }, Application.Current.Dispatcher);
        _debounce.Stop();
        _debounce.Start();
    };

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(IndexFile, JsonSerializer.Serialize(Items));
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Salvataggio cronologia appunti");
        }
        Changed?.Invoke();
    }

    private static IDataObject? Read()
    {
        for (var i = 0; i < 5; i++)
        {
            try { return Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(40); } // appunti occupati da un altro programma
        }
        return null;
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return true;
        foreach (var f in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!data.GetDataPresent(f)) continue;
            try
            {
                if (data.GetData(f) is MemoryStream ms && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static void Capture()
    {
        if (_ignoreNext)
        {
            _ignoreNext = false;
            return;
        }
        if (!Enabled) return;
        try
        {
            var data = Read();
            if (data == null || IsPrivate(data)) return;
            ClipEntry? e = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                e = new ClipEntry { Kind = ClipKind.Files, Text = string.Join("\n", files) };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
                e = new ClipEntry { Kind = ClipKind.Text, Text = text.Length > 200_000 ? text[..200_000] : text };
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img && (long)img.PixelWidth * img.PixelHeight <= MaxImagePixels)
            {
                Directory.CreateDirectory(Dir);
                var name = Guid.NewGuid().ToString("N") + ".png";
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(Path.Combine(Dir, name))) enc.Save(fs);
                e = new ClipEntry { Kind = ClipKind.Image, Image = name, Width = img.PixelWidth, Height = img.PixelHeight, Text = $"{img.PixelWidth} × {img.PixelHeight}" };
            }
            if (e == null) return;
            e.Source = ForegroundProcess();
            // stesso contenuto dell'ultima voce: la riporto in cima invece di duplicarla
            var same = e.Kind != ClipKind.Image ? Items.FirstOrDefault(x => x.Kind == e.Kind && x.Text == e.Text) : null;
            if (same != null)
            {
                Items.Remove(same);
                same.Time = DateTime.Now;
                Items.Insert(0, same);
            }
            else Items.Insert(0, e);
            Trim();
            Save();
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Lettura appunti");
        }
    }

    private static void Trim()
    {
        var extra = Items.Where(x => !x.Pinned).Skip(MaxItems).ToList();
        foreach (var x in extra) Remove(x, save: false);
    }

    public static void Remove(ClipEntry e, bool save = true)
    {
        Items.Remove(e);
        if (e.Image != null)
            try { File.Delete(Path.Combine(Dir, e.Image)); } catch { }
        if (save) Save();
    }

    public static void ClearUnpinned()
    {
        foreach (var x in Items.Where(x => !x.Pinned).ToList()) Remove(x, save: false);
        Save();
    }

    public static void TogglePin(ClipEntry e)
    {
        e.Pinned = !e.Pinned;
        Save();
    }

    public static BitmapSource? LoadImage(ClipEntry e, int decodeWidth = 0)
    {
        if (e.Image == null) return null;
        var path = Path.Combine(Dir, e.Image);
        if (!File.Exists(path)) return null;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        if (decodeWidth > 0) bi.DecodePixelWidth = Math.Min(decodeWidth, e.Width);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>Rimette la voce negli appunti (senza registrarla di nuovo).</summary>
    public static void CopyBack(ClipEntry e)
    {
        _ignoreNext = true;
        try
        {
            switch (e.Kind)
            {
                case ClipKind.Text:
                    Clipboard.SetText(e.Text);
                    break;
                case ClipKind.Image when LoadImage(e) is { } img:
                    Clipboard.SetImage(img);
                    break;
                case ClipKind.Files:
                    var list = new StringCollection();
                    list.AddRange(e.Text.Split('\n').Where(File.Exists).Concat(e.Text.Split('\n').Where(Directory.Exists)).Distinct().ToArray());
                    Clipboard.SetFileDropList(list);
                    break;
            }
        }
        catch (Exception ex)
        {
            _ignoreNext = false;
            AppInfo.Log(ex, "Copia dalla cronologia");
            throw;
        }
        // la voce torna in cima
        Items.Remove(e);
        e.Time = DateTime.Now;
        Items.Insert(0, e);
        Save();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private static string? ForegroundProcess()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : p.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
