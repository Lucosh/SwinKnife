using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SwinKnife.Controls;

/// <summary>Aggancia la finestra di un'app esterna (Chrome) dentro l'interfaccia WPF.</summary>
public sealed class ChromeHost(IntPtr child) : HwndHost
{
    private const int GWL_STYLE = -16;
    private const long WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000,
        WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const uint WM_CLOSE = 0x0010;

    private IntPtr _oldStyle;
    private bool _detached;

    public IntPtr Child => child;

    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _oldStyle = GetWindowLongPtr(child, GWL_STYLE);
        var style = ((long)_oldStyle & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(child, GWL_STYLE, (IntPtr)style);
        SetParent(child, hwndParent.Handle);
        return new HandleRef(this, child);
    }

    /// <summary>Riporta la finestra a essere indipendente.</summary>
    public void Detach()
    {
        if (_detached || !IsWindow(child)) return;
        _detached = true;
        SetParent(child, IntPtr.Zero);
        SetWindowLongPtr(child, GWL_STYLE, _oldStyle);
        SetWindowPos(child, IntPtr.Zero, 120, 120, 1100, 800, 0x0020 | 0x0040); // FRAMECHANGED | SHOWWINDOW
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (!_detached && IsWindow(child)) PostMessage(child, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ ricerca di Chrome
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private static string ProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Finestre visibili di Chrome (escluse le app Electron, che usano la stessa classe).</summary>
    public static Dictionary<IntPtr, string> ChromeWindows()
    {
        var result = new Dictionary<IntPtr, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, 512);
            if (title.Length > 0 && ProcessName(h) == "chrome") result[h] = title.ToString();
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string? FindChrome()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (k?.GetValue(null) is string p && File.Exists(p)) return p;
        }
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var b = Environment.GetEnvironmentVariable(env);
            if (b == null) continue;
            var p = Path.Combine(b, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
