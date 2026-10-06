using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SwinKnife.Core;

/// <summary>Cattura dello schermo e informazioni su monitor e finestre (coordinate fisiche in pixel).</summary>
public static class ScreenCapture
{
    public sealed record WindowInfo(IntPtr Handle, Int32Rect Bounds, string Title);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Int32Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>Area che comprende tutti i monitor.</summary>
    public static Int32Rect VirtualScreen => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public static Point Cursor
    {
        get
        {
            GetCursorPos(out var p);
            return new Point(p.X, p.Y);
        }
    }

    /// <summary>Rettangolo del monitor che contiene il punto (in pixel fisici).</summary>
    public static Int32Rect MonitorAt(Point p)
    {
        var mon = MonitorFromPoint(new POINT { X = (int)p.X, Y = (int)p.Y }, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref info) ? info.rcMonitor.ToRect() : VirtualScreen;
    }

    /// <summary>Copia i pixel dello schermo nel rettangolo indicato.</summary>
    public static BitmapSource Capture(Int32Rect r)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = SelectObject(mem, bmp);
        try
        {
            const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // l'HBITMAP non ha canale alfa affidabile: converto in BGR32 opaco
            var opaque = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            var copy = new WriteableBitmap(opaque);
            copy.Freeze();
            return copy;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Finestre visibili dall'alto in basso, con i bordi reali (senza ombra).</summary>
    public static List<WindowInfo> Windows(IEnumerable<IntPtr> exclude)
    {
        var skip = exclude.ToHashSet();
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (skip.Contains(h) || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true; // DWMWA_CLOAKED
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 && title.Length == 0) return true;
            if (DwmGetWindowAttribute(h, 9, out RECT rect, Marshal.SizeOf<RECT>()) != 0) return true; // DWMWA_EXTENDED_FRAME_BOUNDS
            var r = rect.ToRect();
            if (r.Width < 20 || r.Height < 20) return true;
            list.Add(new WindowInfo(h, r, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Esclude la finestra da screenshot e registrazioni (Windows 10 2004+).</summary>
    public static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11);

    /// <summary>Rende la finestra trasparente ai clic del mouse.</summary>
    public static void ClickThrough(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Posiziona la finestra in pixel fisici (indipendentemente dai DPI).</summary>
    public static void PlaceWindow(Window w, Int32Rect r)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        // larghezza e altezza 0: sposta soltanto
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | (r.Width == 0 ? SWP_NOSIZE : 0));
    }

    /// <summary>Nome predefinito per screenshot e registrazioni.</summary>
    public static string DefaultName(string prefix, string ext) => $"{prefix} {DateTime.Now:yyyy-MM-dd HH.mm.ss}{ext}";
}
