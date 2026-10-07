using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SwinKnife.Core;

/// <summary>Scorciatoia globale: id, nome mostrato, combinazione predefinita e azione.</summary>
public sealed record HotkeyDef(string Id, string Title, string Default, Action Run)
{
    public string Current => Settings.Get("hotkey." + Id) ?? Default;
}

/// <summary>
/// Ciò che SwinKnife fa anche con la finestra chiusa: icona nell'area di notifica, scorciatoie da tastiera globali
/// e ascolto degli appunti. Tutto passa da una finestra invisibile "solo messaggi".
/// </summary>
public static class Resident
{
    private const int WM_HOTKEY = 0x0312, WM_CLIPBOARDUPDATE = 0x031D, WM_TRAY = 0x8001;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400;
    private static HwndSource? _src;
    private static bool _trayAdded;
    private static readonly Dictionary<int, HotkeyDef> Registered = new();
    private static int _taskbarCreated;

    public static IReadOnlyList<HotkeyDef> Hotkeys { get; private set; } = [];
    /// <summary>Esito della registrazione di ogni scorciatoia (false = già usata da un altro programma).</summary>
    public static Dictionary<string, bool> HotkeyStatus { get; } = new();
    public static Func<ContextMenu>? TrayMenu { get; set; }
    public static Action? TrayClick { get; set; }
    public static event Action? ClipboardChanged;

    public static bool TrayEnabled
    {
        get => Settings.Get("tray") != "0";
        set
        {
            Settings.Set("tray", value ? null : "0");
            if (value) AddTray();
            else RemoveTray();
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Avvio con Windows (ridotto a icona nell'area di notifica).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppInfo.Name) != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(AppInfo.Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(AppInfo.Name, false);
        }
    }

    /// <summary>Se l'avvio automatico punta a un altro eseguibile (app aggiornata o spostata) lo corregge.</summary>
    private static void RefreshStartup()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(AppInfo.Name) is string cmd && !cmd.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase))
                StartWithWindows = true;
        }
        catch { }
    }

    public static void Start(IReadOnlyList<HotkeyDef> hotkeys)
    {
        Hotkeys = hotkeys;
        var p = new HwndSourceParameters("SwinKnifeBackground") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 }; // HWND_MESSAGE
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (TrayEnabled) AddTray();
        RegisterHotkeys();
        AddClipboardFormatListener(_src.Handle);
        RefreshStartup();
    }

    public static void Stop()
    {
        if (_src == null) return;
        RemoveTray();
        foreach (var id in Registered.Keys) UnregisterHotKey(_src.Handle, id);
        Registered.Clear();
        RemoveClipboardFormatListener(_src.Handle);
        _src.Dispose();
        _src = null;
    }

    // ------------------------------------------------------------------ scorciatoie
    public static void RegisterHotkeys()
    {
        if (_src == null) return;
        foreach (var id in Registered.Keys) UnregisterHotKey(_src.Handle, id);
        Registered.Clear();
        HotkeyStatus.Clear();
        var n = 1;
        foreach (var h in Hotkeys)
        {
            if (string.IsNullOrEmpty(h.Current) || !TryParse(h.Current, out var mods, out var vk)) continue;
            var id = n++;
            var ok = RegisterHotKey(_src.Handle, id, mods | 0x4000, vk); // MOD_NOREPEAT
            HotkeyStatus[h.Id] = ok;
            if (ok) Registered[id] = h;
        }
    }

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "win": mods |= 8; break;
                case "shift": mods |= 4; break;
                case "ctrl": mods |= 2; break;
                case "alt": mods |= 1; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    /// <summary>Combinazione in forma di testo ("Win+Shift+T") dai tasti premuti, null se manca il tasto principale.</summary>
    public static string? Format(ModifierKeys mods, bool win, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return null;
        var parts = new List<string>();
        if (win) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (parts.Count == 0) return null;
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // ------------------------------------------------------------------ area di notifica
    private static void AddTray()
    {
        if (_src == null || _trayAdded) return;
        var data = TrayData();
        data.uFlags = 0x1 | 0x2 | 0x4; // NIF_MESSAGE | NIF_ICON | NIF_TIP
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = AppIcon();
        data.szTip = L.T("SwinKnife – il coltellino svizzero");
        _trayAdded = Shell_NotifyIcon(0, ref data); // NIM_ADD
        data.uVersion = 4;
        Shell_NotifyIcon(4, ref data); // NIM_SETVERSION
    }

    private static void RemoveTray()
    {
        if (!_trayAdded) return;
        var data = TrayData();
        Shell_NotifyIcon(2, ref data); // NIM_DELETE
        _trayAdded = false;
    }

    public static bool HasTrayIcon => _trayAdded;

    /// <summary>Notifica di Windows dall'icona dell'area di notifica (se c'è).</summary>
    public static bool Balloon(string title, string text)
    {
        if (!_trayAdded) return false;
        var data = TrayData();
        data.uFlags = 0x10; // NIF_INFO
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..252] + "…" : text;
        data.dwInfoFlags = 0x4 | 0x20; // NIIF_USER | NIIF_LARGE_ICON
        data.hBalloonIcon = AppIcon();
        return Shell_NotifyIcon(1, ref data); // NIM_MODIFY
    }

    private static NOTIFYICONDATA TrayData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _src!.Handle,
        uID = 1,
    };

    private static IntPtr _icon;

    private static IntPtr AppIcon()
    {
        if (_icon != IntPtr.Zero) return _icon;
        ExtractIconEx(Environment.ProcessPath!, 0, out _, out _icon, 1);
        return _icon;
    }

    private static void ShowMenu()
    {
        if (TrayMenu?.Invoke() is not { } menu) return;
        GetCursorPos(out var pt);
        var dpi = Application.Current.MainWindow is { } w ? PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1 : 1;
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = pt.X / dpi;
        menu.VerticalOffset = pt.Y / dpi;
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource s) SetForegroundWindow(s.Handle); // si chiude cliccando altrove
        };
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ messaggi
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && Registered.TryGetValue(wParam.ToInt32(), out var h))
        {
            handled = true;
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try { h.Run(); }
                catch (Exception ex) { AppInfo.Log(ex, "Scorciatoia " + h.Id); }
            });
        }
        else if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
        }
        else if (msg == WM_TRAY)
        {
            var ev = lParam.ToInt32() & 0xFFFF;
            if (ev is WM_LBUTTONUP or NIN_SELECT) TrayClick?.Invoke();
            else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            handled = true;
        }
        else if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _trayAdded = false; // Esplora risorse è ripartito: rimetto l'icona
            if (TrayEnabled) AddTray();
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ Win32
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
}
