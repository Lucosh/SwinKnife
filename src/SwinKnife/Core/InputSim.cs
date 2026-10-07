using System.Runtime.InteropServices;

namespace SwinKnife.Core;

public enum ClickButton { Left, Right, Middle }

/// <summary>Simula tastiera e mouse con SendInput (usato da autoclicker e macro).</summary>
public static class InputSim
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010,
        MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040, WHEEL = 0x0800, ABSOLUTE = 0x8000;
    private const uint KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008, UNICODE = 0x0004, EXTENDED = 0x0001;

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    public static (int x, int y) CursorPos()
    {
        GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static void Click(ClickButton button, bool doubleClick = false)
    {
        Press(button, true);
        Press(button, false);
        if (doubleClick)
        {
            Press(button, true);
            Press(button, false);
        }
    }

    public static void Press(ClickButton button, bool down)
    {
        var flag = button switch
        {
            ClickButton.Right => down ? RIGHTDOWN : RIGHTUP,
            ClickButton.Middle => down ? MIDDLEDOWN : MIDDLEUP,
            _ => down ? LEFTDOWN : LEFTUP,
        };
        Send(new INPUT { type = INPUT_MOUSE, U = { mi = new MOUSEINPUT { dwFlags = flag } } });
    }

    public static void MouseEventAt(int x, int y, uint flags, int wheel = 0)
    {
        if ((flags & MOVE) != 0) SetCursorPos(x, y);
        if ((flags & ~MOVE) != 0 || wheel != 0)
            Send(new INPUT { type = INPUT_MOUSE, U = { mi = new MOUSEINPUT { dwFlags = flags & ~MOVE, mouseData = (uint)wheel } } });
    }

    public static void Key(ushort vk, bool down)
    {
        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYUP } },
        });
    }

    public static void TypeUnicode(string text)
    {
        foreach (var ch in text)
        {
            SendScan(ch, false);
            SendScan(ch, true);
        }
    }

    private static void SendScan(char ch, bool up)
    {
        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = { ki = new KEYBDINPUT { wScan = ch, dwFlags = UNICODE | (up ? KEYUP : 0) } },
        });
    }

    private static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

    // ------------------------------------------------------------------ Win32
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}
