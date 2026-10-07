using System.Runtime.InteropServices;

namespace SwinKnife.Core;

public enum MacroKind { KeyDown, KeyUp, MouseMove, MouseDown, MouseUp, Wheel }

/// <summary>Un evento di tastiera o mouse in una macro. DelayMs è l'attesa prima di eseguirlo.</summary>
public sealed class MacroEvent
{
    public MacroKind Kind { get; set; }
    public int Code { get; set; }   // tasto virtuale, oppure 0=sinistro 1=destro 2=centrale per il mouse
    public int X { get; set; }
    public int Y { get; set; }
    public int Data { get; set; }   // rotella
    public int DelayMs { get; set; }
}

/// <summary>Registra tastiera e mouse con hook di basso livello, su un thread dedicato con ciclo dei messaggi.</summary>
public sealed class InputRecorder
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A;
    private const int WM_QUIT = 0x12;

    private readonly List<MacroEvent> _events = new();
    private readonly HashSet<int> _ignore;
    private HookProc? _kbProc, _msProc;
    private IntPtr _kbHook, _msHook;
    private Thread? _thread;
    private uint _threadId;
    private long _last;

    public bool CaptureMouseMove { get; set; }

    public InputRecorder(IEnumerable<int>? ignoreKeys = null) => _ignore = new HashSet<int>(ignoreKeys ?? []);

    public void Start()
    {
        _events.Clear();
        _last = Environment.TickCount64;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
            var mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { /* pompa i messaggi per gli hook */ }
        }) { IsBackground = true, Name = "SwinKnifeMacroRec" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    public List<MacroEvent> Stop()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        _kbHook = _msHook = IntPtr.Zero;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _kbProc = _msProc = null;
        return new List<MacroEvent>(_events);
    }

    private int NextDelay()
    {
        var now = Environment.TickCount64;
        var d = (int)Math.Min(60_000, now - _last);
        _last = now;
        return d;
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();
            if (!_ignore.Contains((int)data.vkCode))
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                    _events.Add(new MacroEvent { Kind = MacroKind.KeyDown, Code = (int)data.vkCode, DelayMs = NextDelay() });
                else if (msg is WM_KEYUP or WM_SYSKEYUP)
                    _events.Add(new MacroEvent { Kind = MacroKind.KeyUp, Code = (int)data.vkCode, DelayMs = NextDelay() });
            }
        }
        return CallNextHookEx(_kbHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            switch (wParam.ToInt32())
            {
                case WM_MOUSEMOVE:
                    if (CaptureMouseMove) _events.Add(new MacroEvent { Kind = MacroKind.MouseMove, X = data.pt.X, Y = data.pt.Y, DelayMs = NextDelay() });
                    break;
                case WM_LBUTTONDOWN: AddMouse(MacroKind.MouseDown, 0, data); break;
                case WM_LBUTTONUP: AddMouse(MacroKind.MouseUp, 0, data); break;
                case WM_RBUTTONDOWN: AddMouse(MacroKind.MouseDown, 1, data); break;
                case WM_RBUTTONUP: AddMouse(MacroKind.MouseUp, 1, data); break;
                case WM_MBUTTONDOWN: AddMouse(MacroKind.MouseDown, 2, data); break;
                case WM_MBUTTONUP: AddMouse(MacroKind.MouseUp, 2, data); break;
                case WM_MOUSEWHEEL:
                    _events.Add(new MacroEvent { Kind = MacroKind.Wheel, X = data.pt.X, Y = data.pt.Y, Data = (short)(data.mouseData >> 16), DelayMs = NextDelay() });
                    break;
            }
        }
        return CallNextHookEx(_msHook, code, wParam, lParam);
    }

    private void AddMouse(MacroKind kind, int button, MSLLHOOKSTRUCT data) =>
        _events.Add(new MacroEvent { Kind = kind, Code = button, X = data.pt.X, Y = data.pt.Y, DelayMs = NextDelay() });

    /// <summary>Riproduce una macro. speed 1 = velocità originale, 2 = doppia. Ripetizioni ≤0 = all'infinito finché non si annulla.</summary>
    public static void Play(IReadOnlyList<MacroEvent> events, int repeat, double speed, CancellationToken ct)
    {
        speed = Math.Clamp(speed, 0.1, 20);
        for (var r = 0; (repeat <= 0 || r < repeat) && !ct.IsCancellationRequested; r++)
        {
            foreach (var e in events)
            {
                if (ct.IsCancellationRequested) return;
                var wait = (int)(e.DelayMs / speed);
                if (wait > 0) { if (ct.WaitHandle.WaitOne(wait)) return; }
                switch (e.Kind)
                {
                    case MacroKind.KeyDown: InputSim.Key((ushort)e.Code, true); break;
                    case MacroKind.KeyUp: InputSim.Key((ushort)e.Code, false); break;
                    case MacroKind.MouseMove: InputSim.MoveTo(e.X, e.Y); break;
                    case MacroKind.MouseDown: InputSim.MoveTo(e.X, e.Y); InputSim.Press((ClickButton)e.Code, true); break;
                    case MacroKind.MouseUp: InputSim.MoveTo(e.X, e.Y); InputSim.Press((ClickButton)e.Code, false); break;
                    case MacroKind.Wheel: InputSim.MouseEventAt(e.X, e.Y, 0x0800, e.Data); break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ Win32
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
