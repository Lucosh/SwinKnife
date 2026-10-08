using System.Net;
using System.Net.Sockets;

namespace SwinKnife.Core;

/// <summary>Invia un "magic packet" per accendere un PC in rete locale (Wake-on-LAN).</summary>
public static class WakeOnLan
{
    public static byte[] ParseMac(string mac)
    {
        var parts = mac.Split(new[] { ':', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Length == 12) // formato senza separatori
            parts = Enumerable.Range(0, 6).Select(i => parts[0].Substring(i * 2, 2)).ToArray();
        if (parts.Length != 6) throw new FormatException(L.T("Indirizzo MAC non valido (es. 00:11:22:33:44:55)."));
        return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
    }

    public static void Send(string mac, string broadcast = "255.255.255.255", int port = 9)
    {
        var m = ParseMac(mac);
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var j = 0; j < 16; j++) Array.Copy(m, 0, packet, 6 + j * 6, 6);
        using var udp = new UdpClient { EnableBroadcast = true };
        var ip = IPAddress.Parse(broadcast);
        udp.Send(packet, packet.Length, new IPEndPoint(ip, port));
        // anche sulla porta 7, usata da alcuni dispositivi
        if (port == 9) udp.Send(packet, packet.Length, new IPEndPoint(ip, 7));
    }
}
