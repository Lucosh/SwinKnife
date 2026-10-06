using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SwinKnife.Core;

namespace SwinKnife;

/// <summary>
/// Dimensione e posizione della finestra: alla prima apertura si adatta allo schermo (anche con scala 125-150 %
/// o schermi piccoli), poi ricorda dove l'hai lasciata. Lavora in pixel fisici, così vale anche con più monitor a DPI diversi.
/// </summary>
public partial class MainWindow
{
    private const double DefaultWidth = 1420, DefaultHeight = 900; // in pixel "logici" (96 DPI)
    private RECT? _normalBounds;
    private bool _managed;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private static (RECT work, double scale) MonitorOf(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return (info.rcWork, scale);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // posizione già decisa dal codice (es. test fuori schermo): non la tocco e non la salvo
        if (ReadLocalValue(LeftProperty) != DependencyProperty.UnsetValue || ReadLocalValue(TopProperty) != DependencyProperty.UnsetValue) return;
        _managed = true;
        try
        {
            PlaceWindow();
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Posizione della finestra");
        }
    }

    private void PlaceWindow()
    {
        var maximized = false;
        RECT r;
        var saved = Settings.Get("window")?.Split(',');
        if (saved is { Length: 5 } && saved.Take(4).All(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            var v = saved.Take(4).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            r = new RECT { Left = v[0], Top = v[1], Right = v[0] + v[2], Bottom = v[1] + v[3] };
            maximized = saved[4] == "1";
        }
        else
        {
            // prima apertura: dimensione predefinita, ma mai più grande del 94 % dello schermo, centrata
            GetCursorPos(out var pt);
            var (work, scale) = MonitorOf(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
            int ww = work.Right - work.Left, wh = work.Bottom - work.Top;
            var w = (int)Math.Min(DefaultWidth * scale, ww * 0.94);
            var h = (int)Math.Min(DefaultHeight * scale, wh * 0.94);
            r = new RECT { Left = work.Left + (ww - w) / 2, Top = work.Top + (wh - h) / 2 };
            r.Right = r.Left + w;
            r.Bottom = r.Top + h;
        }
        r = FitOnScreen(r);
        SetWindowPos(Hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        _normalBounds = r;
        if (maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Riporta il rettangolo tutto dentro l'area utile del monitor più vicino (schermo staccato, risoluzione cambiata…).</summary>
    private RECT FitOnScreen(RECT r)
    {
        var (work, scale) = MonitorOf(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST));
        int ww = work.Right - work.Left, wh = work.Bottom - work.Top;
        // le dimensioni minime non possono superare lo schermo
        MinWidth = Math.Min(1040, ww / scale);
        MinHeight = Math.Min(660, wh / scale);
        var w = Math.Clamp(r.Right - r.Left, (int)(MinWidth * scale), ww);
        var h = Math.Clamp(r.Bottom - r.Top, (int)(MinHeight * scale), wh);
        var x = Math.Clamp(r.Left, work.Left, work.Right - w);
        var y = Math.Clamp(r.Top, work.Top, work.Bottom - h);
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RememberBounds();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RememberBounds();
    }

    private void RememberBounds()
    {
        if (WindowState == WindowState.Normal && Hwnd != IntPtr.Zero && GetWindowRect(Hwnd, out var r)) _normalBounds = r;
    }

    private void SavePlacement()
    {
        if (!_managed || _normalBounds is not { } r) return;
        Settings.Set("window", string.Join(',', r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, WindowState == WindowState.Maximized ? 1 : 0));
    }
}
