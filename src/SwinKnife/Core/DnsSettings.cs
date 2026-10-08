using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SwinKnife.Core;

/// <summary>Legge e cambia i server DNS delle schede di rete (via netsh, con permessi di amministratore).</summary>
public static class DnsSettings
{
    public sealed record Adapter(string Name, string Description, string CurrentDns);

    public sealed record Preset(string Name, string? V4Primary, string? V4Secondary, string? V6Primary, string? V6Secondary)
    {
        public bool Automatic => V4Primary == null;
    }

    public static readonly Preset[] Presets =
    [
        new(L.T("Automatico (fornito dal router)"), null, null, null, null),
        new("Cloudflare (1.1.1.1)", "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"),
        new(L.T("Cloudflare famiglie (blocca malware e adulti)"), "1.1.1.3", "1.0.0.3", "2606:4700:4700::1113", "2606:4700:4700::1003"),
        new("Google (8.8.8.8)", "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"),
        new("Quad9 (9.9.9.9)", "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"),
        new("AdGuard (blocca pubblicità)", "94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"),
    ];

    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            string dns;
            try
            {
                var servers = ni.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString()).ToList();
                dns = servers.Count > 0 ? string.Join(", ", servers) : L.T("automatico");
            }
            catch { dns = L.T("sconosciuto"); }
            list.Add(new Adapter(ni.Name, ni.Description, dns));
        }
        return list.OrderBy(a => a.Name).ToList();
    }

    /// <summary>Applica un preset a una scheda. Richiede l'amministratore (prompt UAC). Restituisce false se annullato.</summary>
    public static bool Apply(string adapter, Preset p)
    {
        var sb = new StringBuilder();
        var n = adapter.Replace("\"", "");
        if (p.Automatic)
        {
            sb.Append($"netsh interface ipv4 set dnsservers name=\"{n}\" source=dhcp");
            sb.Append($" & netsh interface ipv6 set dnsservers name=\"{n}\" source=dhcp");
        }
        else
        {
            sb.Append($"netsh interface ipv4 set dnsservers name=\"{n}\" static {p.V4Primary} primary validate=no");
            if (p.V4Secondary != null)
                sb.Append($" & netsh interface ipv4 add dnsservers name=\"{n}\" {p.V4Secondary} index=2 validate=no");
            if (p.V6Primary != null)
            {
                sb.Append($" & netsh interface ipv6 set dnsservers name=\"{n}\" static {p.V6Primary} primary validate=no");
                if (p.V6Secondary != null)
                    sb.Append($" & netsh interface ipv6 add dnsservers name=\"{n}\" {p.V6Secondary} index=2 validate=no");
            }
        }
        sb.Append(" & ipconfig /flushdns");
        return RunElevated(sb.ToString());
    }

    private static bool RunElevated(string commands)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + commands)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC rifiutato
    }
}
