using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwinKnife.Core;

/// <summary>Spegnimento, riavvio, sospensione e blocco del PC (anche programmati).</summary>
public static partial class PowerActions
{
    public enum Kind { Shutdown, Restart, Sleep, Hibernate, Lock }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    /// <summary>true se l'azione viene gestita da shutdown.exe (quindi sopravvive alla chiusura dell'app).</summary>
    public static bool IsSystemScheduled(Kind k) => k is Kind.Shutdown or Kind.Restart;

    /// <summary>Programma spegnimento o riavvio tramite shutdown.exe; le altre azioni sono immediate.</summary>
    public static void Schedule(Kind k, int seconds)
    {
        switch (k)
        {
            case Kind.Shutdown: Run("shutdown.exe", $"/s /t {Math.Max(0, seconds)}"); break;
            case Kind.Restart: Run("shutdown.exe", $"/r /t {Math.Max(0, seconds)}"); break;
            default: Now(k); break; // Sleep/Hibernate/Lock non hanno timer di sistema
        }
    }

    public static void Abort()
    {
        try { Run("shutdown.exe", "/a"); } catch { }
    }

    public static void Now(Kind k)
    {
        switch (k)
        {
            case Kind.Shutdown: Run("shutdown.exe", "/s /t 0"); break;
            case Kind.Restart: Run("shutdown.exe", "/r /t 0"); break;
            case Kind.Lock: LockWorkStation(); break;
            case Kind.Sleep: SetSuspendState(false, false, false); break;
            case Kind.Hibernate: SetSuspendState(true, false, false); break;
        }
    }

    private static void Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
}
