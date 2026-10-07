using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SwinKnife.Core;

/// <summary>Una scorciatoia globale indipendente, con la sua finestra "solo messaggi". Da creare e usare sul thread della UI.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312, ID = 0x51A7;
    private readonly HwndSource _src;
    private readonly Action _onPressed;
    public bool Ok { get; }

    public GlobalHotkey(uint mods, uint vk, Action onPressed)
    {
        _onPressed = onPressed;
        var p = new HwndSourceParameters("SwinKnifeHotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
        _src = new HwndSource(p);
        _src.AddHook(Hook);
        Ok = RegisterHotKey(_src.Handle, ID, mods | 0x4000, vk); // MOD_NOREPEAT
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == ID)
        {
            handled = true;
            try { _onPressed(); } catch (Exception ex) { AppInfo.Log(ex, "GlobalHotkey"); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_src.Handle, ID);
        _src.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
