using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace SwinKnife.Core;

public sealed record SavedWifi(string Name, string Authentication, string Password);

/// <summary>Legge le reti Wi-Fi salvate sul PC e le relative password, esportando i profili con netsh.</summary>
public static class WifiPasswords
{
    public static List<SavedWifi> All()
    {
        var dir = Path.Combine(AppInfo.TempDir, "wifi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // netsh scrive un XML per profilo, con la password in chiaro dentro <keyMaterial>
            Run("wlan export profile key=clear folder=\"" + dir + "\"");
            var list = new List<SavedWifi>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
            {
                try
                {
                    var doc = XDocument.Load(file);
                    string? Val(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
                    var name = Val("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var auth = Val("authentication") ?? L.T("sconosciuta");
                    var key = Val("keyMaterial") ?? "";
                    list.Add(new SavedWifi(name, auth, key));
                }
                catch { }
            }
            return list.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void Run(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(15_000);
    }
}
