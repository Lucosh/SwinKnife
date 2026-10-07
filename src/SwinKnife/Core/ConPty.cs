using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwinKnife.Core;

/// <summary>
/// Una pseudo-console di Windows (ConPTY): dà al programma avviato una vera console, così
/// colori, completamento con Tab, Ctrl+C e i programmi interattivi funzionano come in un terminale Linux.
/// Si scrive su <see cref="Input"/> ciò che l'utente digita e si legge da <see cref="Output"/> ciò che il programma stampa.
/// </summary>
public sealed class ConPty : IDisposable
{
    private IntPtr _pc;                 // HPCON
    private IntPtr _process, _thread;   // del processo avviato
    private SafeFileHandle? _inWrite, _outRead;
    private bool _disposed;

    public Stream Input { get; private set; } = Stream.Null;
    public Stream Output { get; private set; } = Stream.Null;

    public event Action<int>? Exited;

    public static bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 17763);

    /// <summary>Avvia <paramref name="commandLine"/> dentro una pseudo-console di <paramref name="cols"/>×<paramref name="rows"/> caratteri.</summary>
    public ConPty(string commandLine, short cols, short rows, string? workingDir = null, IDictionary<string, string>? extraEnv = null)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 25;

        // due pipe: una per l'input del programma, una per il suo output
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out _pc);
        if (hr != 0) throw new Win32Exception(hr, L.T("Impossibile creare la pseudo-console."));

        // le estremità usate dalla console possono essere chiuse da noi: le tiene ConPTY
        inRead.Dispose();
        outWrite.Dispose();
        _inWrite = inWrite;
        _outRead = outRead;
        Input = new FileStream(inWrite, FileAccess.Write);
        Output = new FileStream(outRead, FileAccess.Read);

        StartProcess(commandLine, workingDir, extraEnv);
        WatchForExit();
    }

    private void StartProcess(string commandLine, string? workingDir, IDictionary<string, string>? extraEnv)
    {
        var attrList = BuildAttributeList();
        try
        {
            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            var env = BuildEnvironment(extraEnv);
            var cmd = new StringBuilder(commandLine); // CreateProcess può modificare la stringa
            var ok = CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, env, workingDir, ref si, out var pi);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Impossibile avviare il programma del terminale."));
            _process = pi.hProcess;
            _thread = pi.hThread;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
    }

    private IntPtr BuildAttributeList()
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new Win32Exception();
        }
        if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new Win32Exception();
        }
        return list;
    }

    /// <summary>Blocco d'ambiente (coppie nome=valore, terminate da due \0) con le variabili aggiuntive.</summary>
    private static IntPtr BuildEnvironment(IDictionary<string, string>? extra)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string)(e.Value ?? "");
        vars["TERM"] = "xterm-256color";
        if (extra != null)
            foreach (var kv in extra) vars[kv.Key] = kv.Value;

        var sb = new StringBuilder();
        foreach (var kv in vars.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private void WatchForExit()
    {
        var proc = _process;
        ThreadPool.RegisterWaitForSingleObject(new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(proc, false) },
            (_, _) =>
            {
                GetExitCodeProcess(proc, out var code);
                Exited?.Invoke((int)code);
            }, null, -1, true);
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _pc == IntPtr.Zero) return;
        if (cols < 1) cols = 1;
        if (rows < 1) rows = 1;
        ResizePseudoConsole(_pc, new COORD { X = cols, Y = rows });
    }

    public void Write(string text)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            Input.Write(bytes, 0, bytes.Length);
            Input.Flush();
        }
        catch { /* il programma è terminato */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // chiudere la pseudo-console fa terminare il programma ospitato
        if (_pc != IntPtr.Zero) { ClosePseudoConsole(_pc); _pc = IntPtr.Zero; }
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        _inWrite?.Dispose();
        _outRead?.Dispose();
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        if (_thread != IntPtr.Zero) { CloseHandle(_thread); _thread = IntPtr.Zero; }
    }

    // ------------------------------------------------------------------ P/Invoke
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
