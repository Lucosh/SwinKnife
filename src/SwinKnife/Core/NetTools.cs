using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwinKnife.Core;

public sealed record SpeedResult(double DownloadMbps, double UploadMbps, double PingMs, double JitterMs, string Server, string Provider);
public sealed record LanDevice(string Ip, string Name, string Mac, string Kind);
public sealed record WifiNetwork(string Ssid, string Bssid, int SignalPercent, int Rssi, int Channel, string Band, bool Secured);
public sealed record WifiConnection(string Ssid, string Bssid, int SignalPercent, int Channel, string Band, double RxMbps, double TxMbps, string Phy);

/// <summary>Strumenti di rete: test di velocità, dispositivi nella rete di casa, reti Wi-Fi vicine.</summary>
public static class NetTools
{
    // ------------------------------------------------------------------ test di velocità (server di Cloudflare)
    private const string Speed = "https://speed.cloudflare.com";

    public static async Task<SpeedResult> SpeedTestAsync(Action<string, double> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SwinKnife/{AppInfo.Version}");
        string server = "Cloudflare", provider = "";
        try
        {
            using var meta = JsonDocument.Parse(await http.GetStringAsync(Speed + "/meta", ct));
            var r = meta.RootElement;
            server = $"Cloudflare {(r.TryGetProperty("colo", out var c) ? c.GetString() : "")} {(r.TryGetProperty("city", out var city) ? city.GetString() : "")}".Trim();
            provider = r.TryGetProperty("asOrganization", out var org) ? org.GetString() ?? "" : "";
        }
        catch { }

        // latenza: tante richieste minime, scarto la prima (apre la connessione)
        progress(L.T("Latenza…"), 0);
        var pings = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            var sw = Stopwatch.StartNew();
            using (var resp = await http.GetAsync(Speed + "/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct)) { }
            if (i > 0) pings.Add(sw.Elapsed.TotalMilliseconds);
        }
        pings.Sort();
        var ping = pings.Take(pings.Count * 2 / 3).Average();
        var jitter = pings.Zip(pings.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Average();

        // download: più connessioni in parallelo per circa 8 secondi
        var down = await Measure(async (token, add) =>
        {
            while (!token.IsCancellationRequested)
            {
                using var resp = await http.GetAsync(Speed + "/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, token);
                await using var s = await resp.Content.ReadAsStreamAsync(token);
                var buf = new byte[1 << 16];
                int n;
                while ((n = await s.ReadAsync(buf, token)) > 0) add(n);
            }
        }, 8, p => progress(L.T("Download…"), 0.1 + p * 0.5), ct);
        progress(L.T($"Download: {down:0.0} Mbit/s"), 0.6);

        // upload
        var payload = new byte[4_000_000];
        Random.Shared.NextBytes(payload);
        var up = await Measure(async (token, add) =>
        {
            while (!token.IsCancellationRequested)
            {
                using var content = new ProgressContent(payload, add);
                using var resp = await http.PostAsync(Speed + "/__up", content, token);
            }
        }, 7, p => progress(L.T("Upload…"), 0.6 + p * 0.4), ct);
        progress(L.T($"Upload: {up:0.0} Mbit/s"), 1);
        return new SpeedResult(down, up, ping, jitter, server, provider);
    }

    /// <summary>Esegue più flussi in parallelo per alcuni secondi e restituisce i Mbit/s (scartando il primo secondo di "riscaldamento").</summary>
    private static async Task<double> Measure(Func<CancellationToken, Action<int>, Task> stream, int seconds, Action<double> progress, CancellationToken ct)
    {
        long bytes = 0, warm = 0;
        var sw = Stopwatch.StartNew();
        var started = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Add(int n)
        {
            Interlocked.Add(ref bytes, n);
            if (!started && sw.Elapsed.TotalSeconds >= 1)
            {
                started = true;
                warm = Interlocked.Read(ref bytes);
            }
        }
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { await stream(stop.Token, Add); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        })).ToList();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(250, ct);
            progress(sw.Elapsed.TotalSeconds / seconds);
        }
        var measured = Interlocked.Read(ref bytes) - warm;
        var time = sw.Elapsed.TotalSeconds - 1;
        stop.Cancel();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        return measured * 8 / 1e6 / Math.Max(0.5, time);
    }

    private sealed class ProgressContent(byte[] data, Action<int> report) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var o = 0; o < data.Length; o += 65536)
            {
                var n = Math.Min(65536, data.Length - o);
                await stream.WriteAsync(data.AsMemory(o, n));
                report(n);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }
    }

    // ------------------------------------------------------------------ dispositivi nella rete locale
    public static async Task<List<LanDevice>> ScanLanAsync(Action<double> progress, CancellationToken ct)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(n => (n, p: n.GetIPProperties()))
            .FirstOrDefault(x => x.p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork));
        if (nic.n == null) throw new InvalidOperationException(L.T("Il PC non è collegato a una rete locale (Wi-Fi o cavo)."));
        var addr = nic.p.UnicastAddresses.First(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        var gateway = nic.p.GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Address;
        var me = addr.Address.GetAddressBytes();
        // al massimo le 254 macchine della /24 in cui si trova il PC
        var hosts = Enumerable.Range(1, 254).Select(i => new IPAddress([me[0], me[1], me[2], (byte)i])).ToList();
        var alive = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var done = 0;
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, token) =>
        {
            try
            {
                using var ping = new Ping();
                var r = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(800), cancellationToken: token);
                if (r.Status == IPStatus.Success) alive[ip.ToString()] = true;
            }
            catch { }
            progress(Interlocked.Increment(ref done) / (double)hosts.Count * 0.8);
        });
        // i dispositivi che non rispondono al ping compaiono comunque nella tabella ARP
        var macs = Arp();
        foreach (var ip in macs.Keys.Where(k => k.StartsWith($"{me[0]}.{me[1]}.{me[2]}.") && !k.EndsWith(".255"))) alive[ip] = true;
        alive[addr.Address.ToString()] = true;
        var list = new System.Collections.Concurrent.ConcurrentBag<LanDevice>();
        var names = 0;
        await Parallel.ForEachAsync(alive.Keys, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct }, async (ip, token) =>
        {
            var name = "";
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip, token).WaitAsync(TimeSpan.FromSeconds(2), token);
                if (entry.HostName != ip) name = entry.HostName;
            }
            catch { }
            var kind = ip == addr.Address.ToString() ? L.T("Questo PC") : ip == gateway.ToString() ? L.T("Router") : "";
            if (ip == addr.Address.ToString() && name.Length == 0) name = Environment.MachineName;
            var mac = ip == addr.Address.ToString() ? FormatMac(nic.n.GetPhysicalAddress().GetAddressBytes()) : macs.GetValueOrDefault(ip, "");
            list.Add(new LanDevice(ip, name, mac, kind));
            progress(0.8 + Interlocked.Increment(ref names) / (double)alive.Count * 0.2);
        });
        return list.OrderBy(d => IPAddress.Parse(d.Ip).GetAddressBytes()[3]).ToList();
    }

    private static string FormatMac(byte[] b) => string.Join(":", b.Select(x => x.ToString("X2")));

    /// <summary>Tabella ARP (indirizzo IP → MAC) letta da "arp -a": il formato dei numeri non dipende dalla lingua.</summary>
    private static Dictionary<string, string> Arp()
    {
        var d = new Dictionary<string, string>();
        try
        {
            var psi = new ProcessStartInfo("arp", "-a") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (Match m in Regex.Matches(text, @"(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})"))
                if (!m.Groups[2].Value.StartsWith("ff-ff", StringComparison.OrdinalIgnoreCase) && !m.Groups[2].Value.StartsWith("01-00-5e", StringComparison.OrdinalIgnoreCase))
                    d[m.Groups[1].Value] = m.Groups[2].Value.Replace('-', ':').ToUpperInvariant();
        }
        catch { }
        return d;
    }

    // ------------------------------------------------------------------ Wi-Fi (Native Wifi API, indipendente dalla lingua)
    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint client, IntPtr reserved, out uint version, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool secOnly, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

    public sealed class WifiAccessDenied() : Exception(L.T("Windows non consente di leggere le reti Wi-Fi: attiva la posizione in Impostazioni › Privacy e sicurezza › Posizione, compreso \"Consenti alle app desktop di accedere alla posizione\"."));

    private static (int channel, string band) Channel(uint khz)
    {
        var mhz = (int)(khz / 1000);
        if (mhz >= 2412 && mhz <= 2484) return (mhz == 2484 ? 14 : (mhz - 2407) / 5, "2,4 GHz");
        if (mhz >= 5955) return ((mhz - 5950) / 5, "6 GHz");
        if (mhz >= 5000) return ((mhz - 5000) / 5, "5 GHz");
        return (0, "");
    }

    private static string Ssid(IntPtr p)
    {
        var len = Math.Min(32, Marshal.ReadInt32(p));
        var b = new byte[len];
        Marshal.Copy(p + 4, b, 0, len);
        return Encoding.UTF8.GetString(b);
    }

    private static string Mac(IntPtr p)
    {
        var b = new byte[6];
        Marshal.Copy(p, b, 0, 6);
        return FormatMac(b);
    }

    /// <summary>Reti visibili (dopo una nuova scansione) e connessione attuale.</summary>
    public static async Task<(List<WifiNetwork> networks, WifiConnection? current)> WifiAsync(bool rescan)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return ([], null); // nessuna scheda Wi-Fi o servizio WLAN fermo
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var ifList) != 0) return ([], null);
            var guids = new List<Guid>();
            try
            {
                var n = Marshal.ReadInt32(ifList);
                for (var i = 0; i < n; i++) guids.Add(Marshal.PtrToStructure<Guid>(ifList + 8 + i * 532)); // WLAN_INTERFACE_INFO = 532 byte
            }
            finally { WlanFreeMemory(ifList); }
            if (guids.Count == 0) return ([], null);
            if (rescan)
            {
                foreach (var g in guids)
                {
                    var gg = g;
                    WlanScan(h, ref gg, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
                await Task.Delay(3500); // la scansione richiede qualche secondo
            }
            var networks = new List<WifiNetwork>();
            WifiConnection? current = null;
            foreach (var g in guids)
            {
                var gg = g;
                var rc = WlanGetNetworkBssList(h, ref gg, IntPtr.Zero, 3, false, IntPtr.Zero, out var bss);
                if (rc == 5) throw new WifiAccessDenied();
                if (rc == 0)
                {
                    try
                    {
                        var count = Marshal.ReadInt32(bss + 4);
                        for (var i = 0; i < count; i++)
                        {
                            var e = bss + 8 + i * 360; // WLAN_BSS_ENTRY = 360 byte
                            var rssi = Marshal.ReadInt32(e + 56);
                            var quality = Marshal.ReadInt32(e + 60);
                            var cap = (ushort)Marshal.ReadInt16(e + 88);
                            var (ch, band) = Channel((uint)Marshal.ReadInt32(e + 92));
                            networks.Add(new WifiNetwork(Ssid(e), Mac(e + 40), quality, rssi, ch, band, (cap & 0x10) != 0));
                        }
                    }
                    finally { WlanFreeMemory(bss); }
                }
                // connessione attuale (opcode 7 = wlan_intf_opcode_current_connection)
                if (current == null && WlanQueryInterface(h, ref gg, 7, IntPtr.Zero, out _, out var conn, IntPtr.Zero) == 0)
                {
                    try
                    {
                        if (Marshal.ReadInt32(conn) == 1) // wlan_interface_state_connected
                        {
                            var bssid = Mac(conn + 560);
                            var match = networks.FirstOrDefault(x => x.Bssid == bssid);
                            current = new WifiConnection(Ssid(conn + 520), bssid, Marshal.ReadInt32(conn + 576), match?.Channel ?? 0, match?.Band ?? "",
                                Marshal.ReadInt32(conn + 580) / 1000.0, Marshal.ReadInt32(conn + 584) / 1000.0, Phy(Marshal.ReadInt32(conn + 568)));
                        }
                    }
                    finally { WlanFreeMemory(conn); }
                }
            }
            return (networks.OrderByDescending(n => n.SignalPercent).ToList(), current);
        }
        finally
        {
            WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    private static string Phy(int t) => t switch
    {
        7 => "Wi-Fi 4 (802.11n)", 8 => "Wi-Fi 5 (802.11ac)", 9 => "802.11ad", 10 => "Wi-Fi 6 (802.11ax)", 11 => "Wi-Fi 7 (802.11be)",
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", _ => "",
    };

    /// <summary>Per la banda 2,4 GHz il canale meno affollato fra 1, 6 e 11 (gli unici che non si sovrappongono).</summary>
    public static int? BestChannel24(List<WifiNetwork> nets)
    {
        var on24 = nets.Where(n => n.Band.StartsWith("2")).ToList();
        if (on24.Count == 0) return null;
        // ogni rete disturba anche i canali vicini (±4), di più se il segnale è forte
        return new[] { 1, 6, 11 }.OrderBy(c => on24.Where(n => Math.Abs(n.Channel - c) <= 4).Sum(n => n.SignalPercent)).First();
    }
}
