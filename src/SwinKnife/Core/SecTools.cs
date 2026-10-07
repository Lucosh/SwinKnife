using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SwinKnife.Core;

public sealed record PortResult(int Port, bool Open, string Service, string Banner);
public sealed record DnsResult(string Name, List<string> Addresses, string? ReverseName, string Error);
public sealed record Hop(int Number, string Address, string Host, long Ms, bool Reached);
public sealed record LocalPort(string Protocol, int Port, string Address, string State, string Process, int Pid);

/// <summary>
/// Strumenti di rete per imparare e per controlli di sicurezza <b>sulle proprie reti</b> (porte, DNS, WHOIS, traceroute):
/// solo ricognizione e diagnostica, come ping o nslookup. Da usare esclusivamente su sistemi propri o autorizzati.
/// </summary>
public static class SecTools
{
    /// <summary>Le porte più comuni, con il nome del servizio.</summary>
    public static readonly (int port, string name)[] CommonPorts =
    [
        (20, "FTP-data"), (21, "FTP"), (22, "SSH"), (23, "Telnet"), (25, "SMTP"), (53, "DNS"), (67, "DHCP"), (68, "DHCP"),
        (80, "HTTP"), (110, "POP3"), (111, "RPC"), (123, "NTP"), (135, "RPC/DCOM"), (137, "NetBIOS"), (138, "NetBIOS"),
        (139, "NetBIOS"), (143, "IMAP"), (161, "SNMP"), (389, "LDAP"), (443, "HTTPS"), (445, "SMB"), (465, "SMTPS"),
        (514, "Syslog"), (515, "Stampa"), (587, "SMTP"), (631, "IPP/Stampa"), (636, "LDAPS"), (993, "IMAPS"), (995, "POP3S"),
        (1080, "SOCKS"), (1433, "SQL Server"), (1521, "Oracle"), (1723, "PPTP"), (1883, "MQTT"), (2049, "NFS"), (3000, "Dev/Web"),
        (3306, "MySQL"), (3389, "Desktop remoto"), (5060, "SIP"), (5432, "PostgreSQL"), (5900, "VNC"), (5985, "WinRM"),
        (6379, "Redis"), (8000, "Web"), (8080, "Web/Proxy"), (8443, "HTTPS"), (8888, "Web"), (9000, "Web"), (9200, "Elasticsearch"),
        (27017, "MongoDB"), (32400, "Plex"), (49152, "UPnP"),
    ];

    private static readonly Dictionary<int, string> ServiceByPort = CommonPorts.ToDictionary(p => p.port, p => p.name);

    public static string ServiceName(int port) => ServiceByPort.GetValueOrDefault(port, "");

    // ------------------------------------------------------------------ scansione porte (TCP connect)
    public static async Task<List<PortResult>> ScanPortsAsync(string host, IEnumerable<int> ports, int timeoutMs,
        Action<double, int>? progress, CancellationToken ct)
    {
        var addr = await ResolveAsync(host, ct);
        var list = ports.Distinct().OrderBy(p => p).ToList();
        var open = new System.Collections.Concurrent.ConcurrentBag<PortResult>();
        var done = 0; var found = 0;
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = 200, CancellationToken = ct }, async (port, token) =>
        {
            if (await IsOpenAsync(addr, port, timeoutMs, token))
            {
                open.Add(new PortResult(port, true, ServiceName(port), ""));
                Interlocked.Increment(ref found);
            }
            progress?.Invoke(Interlocked.Increment(ref done) / (double)list.Count, found);
        });
        return open.OrderBy(p => p.Port).ToList();
    }

    private static async Task<bool> IsOpenAsync(IPAddress addr, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient(addr.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(addr, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var entry = await Dns.GetHostAddressesAsync(host, ct);
        return entry.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? entry.FirstOrDefault()
            ?? throw new InvalidOperationException(L.T($"Nome non risolto: {host}"));
    }

    public static IEnumerable<int> ParsePortSpec(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b))
                for (var p = Math.Max(1, a); p <= Math.Min(65535, b); p++) yield return p;
            else if (int.TryParse(part, out var one) && one is >= 1 and <= 65535) yield return one;
        }
    }

    // ------------------------------------------------------------------ DNS
    public static async Task<DnsResult> LookupAsync(string host, CancellationToken ct)
    {
        try
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                string? name = null;
                try { name = (await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(3), ct)).HostName; } catch { }
                return new DnsResult(host, [host], name, "");
            }
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            return new DnsResult(host, addrs.Select(a => a.ToString()).ToList(), null, "");
        }
        catch (Exception ex)
        {
            return new DnsResult(host, [], null, ex.Message);
        }
    }

    // ------------------------------------------------------------------ WHOIS (porta 43)
    public static async Task<string> WhoisAsync(string domain, CancellationToken ct)
    {
        domain = domain.Trim().ToLowerInvariant();
        var server = "whois.iana.org";
        var tld = domain.Contains('.') ? domain[(domain.LastIndexOf('.') + 1)..] : domain;
        var first = await QueryWhois(server, tld, ct);
        var refer = Regex.Match(first, @"refer:\s*(\S+)", RegexOptions.IgnoreCase);
        if (!refer.Success) return first;
        var detail = await QueryWhois(refer.Groups[1].Value, domain, ct);
        return detail.Length > 0 ? detail : first;
    }

    private static async Task<string> QueryWhois(string server, string query, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(8000);
            await client.ConnectAsync(server, 43, timeout.Token);
            await using var s = client.GetStream();
            var q = Encoding.ASCII.GetBytes(query + "\r\n");
            await s.WriteAsync(q, timeout.Token);
            using var r = new StreamReader(s, Encoding.UTF8);
            return await r.ReadToEndAsync(timeout.Token);
        }
        catch (Exception ex) { return L.T("Errore WHOIS: ") + ex.Message; }
    }

    // ------------------------------------------------------------------ traceroute
    public static async Task TracerouteAsync(string host, int maxHops, Action<Hop> onHop, CancellationToken ct)
    {
        var target = await ResolveAsync(host, ct);
        var buffer = Encoding.ASCII.GetBytes("SwinKnife-traceroute");
        for (var ttl = 1; ttl <= maxHops && !ct.IsCancellationRequested; ttl++)
        {
            using var ping = new Ping();
            var sw = Stopwatch.StartNew();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromSeconds(3), buffer, new PingOptions(ttl, true), ct);
                sw.Stop();
                var reached = reply.Status == IPStatus.Success;
                var addr = reply.Address?.ToString() ?? "*";
                if (reply.Status is IPStatus.TimedOut) addr = "*";
                var name = "";
                if (addr != "*") { try { name = (await Dns.GetHostEntryAsync(reply.Address!).WaitAsync(TimeSpan.FromSeconds(1), ct)).HostName; } catch { } }
                onHop(new Hop(ttl, addr, name, sw.ElapsedMilliseconds, reached));
                if (reached) break;
            }
            catch (OperationCanceledException) { break; }
            catch { onHop(new Hop(ttl, "*", "", 0, false)); }
        }
    }

    // ------------------------------------------------------------------ porte aperte sul PC
    public static List<LocalPort> LocalListeningPorts()
    {
        var list = new List<LocalPort>();
        var byPid = new Dictionary<int, string>();
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.ASCII };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (Match m in Regex.Matches(text, @"^\s*(TCP|UDP)\s+(\S+):(\d+)\s+\S+\s+(\w*)\s+(\d+)", RegexOptions.Multiline))
            {
                var proto = m.Groups[1].Value;
                var state = m.Groups[4].Value;
                if (proto == "TCP" && state != "LISTENING") continue; // solo le porte in ascolto
                var pid = int.Parse(m.Groups[5].Value);
                if (!byPid.TryGetValue(pid, out var name))
                {
                    try { name = Process.GetProcessById(pid).ProcessName; } catch { name = ""; }
                    byPid[pid] = name;
                }
                list.Add(new LocalPort(proto, int.Parse(m.Groups[3].Value), m.Groups[2].Value, proto == "UDP" ? "" : state, name, pid));
            }
        }
        catch { }
        return list.GroupBy(x => (x.Protocol, x.Port)).Select(g => g.First()).OrderBy(x => x.Port).ToList();
    }

    // ------------------------------------------------------------------ nmap (se installato)
    public static string? NmapPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try { var f = Path.Combine(dir.Trim('"'), "nmap.exe"); if (File.Exists(f)) return f; } catch { }
        foreach (var cand in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nmap", "nmap.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Nmap", "nmap.exe"),
                 })
            if (File.Exists(cand)) return cand;
        return null;
    }

    public static async Task RunNmapAsync(string arguments, Action<string> onLine, CancellationToken ct)
    {
        var nmap = NmapPath() ?? throw new InvalidOperationException(L.T("nmap non è installato."));
        var psi = new ProcessStartInfo(nmap, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
    }
}
