using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SwinKnife.Core;

public sealed record StatSnapshot(double CpuPercent, double RamPercent, long RamUsed, long RamTotal, double NetDownBps, double NetUpBps);

/// <summary>Campiona in tempo reale CPU, memoria e rete senza librerie esterne (API di Windows).</summary>
public sealed class LiveStats
{
    private long _prevIdle, _prevKernel, _prevUser;
    private long _prevRx, _prevTx;
    private DateTime _prevTime;
    private bool _first = true;

    public StatSnapshot Sample()
    {
        var cpu = CpuPercent();
        var (used, total, load) = Memory();
        var (rx, tx) = NetBytes();
        var now = DateTime.UtcNow;

        double down = 0, up = 0;
        if (!_first)
        {
            var dt = Math.Max(0.001, (now - _prevTime).TotalSeconds);
            down = Math.Max(0, (rx - _prevRx) / dt);
            up = Math.Max(0, (tx - _prevTx) / dt);
        }
        _prevRx = rx; _prevTx = tx; _prevTime = now;
        _first = false;
        return new StatSnapshot(cpu, load, used, total, down, up);
    }

    private double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var di = idle - _prevIdle;
        var dk = kernel - _prevKernel;
        var du = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        var total = dk + du; // kernel include l'idle
        if (total <= 0) return 0;
        return Math.Clamp((1.0 - di / (double)total) * 100.0, 0, 100);
    }

    private static (long used, long total, double load) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        var total = (long)m.ullTotalPhys;
        var used = total - (long)m.ullAvailPhys;
        return (used, total, m.dwMemoryLoad);
    }

    private static (long rx, long tx) NetBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    /// <summary>I processi che usano più memoria (per la tabella del monitor).</summary>
    public static List<(string name, long ram)> TopByMemory(int n)
    {
        var list = new List<(string, long)>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add((p.ProcessName, p.WorkingSet64)); } catch { }
            finally { p.Dispose(); }
        }
        return list.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2)))
            .OrderByDescending(x => x.Item2).Take(n).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
