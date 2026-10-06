using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace SwinKnife.Core;

public sealed record InfoRow(string Key, string Value);

public sealed record DiskInfo(string Name, string Kind, long Size, string Health, int HealthLevel, List<InfoRow> Details, List<VolumeUsage> Volumes);

public sealed record VolumeUsage(string Letter, string Label, string FileSystem, long Total, long Free);

public sealed record BatteryInfo(int Charge, string Status, List<InfoRow> Details);

public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public string? Executable { get; init; }
    public string Publisher { get; init; } = "";
    public bool Enabled { get; set; }
    public required RegistryKey ApprovedRoot { get; init; }
    public required string ApprovedKey { get; init; }
    public required string ApprovedValue { get; init; }
}

/// <summary>Informazioni su hardware, dischi, batteria, rete e programmi all'avvio.</summary>
public static class SysInfo
{
    // ------------------------------------------------------------------ utilità
    /// <summary>Esegue un comando di sistema e ne restituisce l'output (codifica della console).</summary>
    public static string Run(string exe, string args, int timeoutMs = 10_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.GetEncoding(GetOEMCP()), // codifica della console di Windows
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit(timeoutMs);
        return output.Wait(1000) ? output.Result : "";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    /// <summary>Valore di una riga "Chiave : valore" nell'output di netsh e simili.</summary>
    public static string? Field(string output, params string[] keys)
    {
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i < 0) continue;
            var k = line[..i].Trim();
            if (keys.Any(key => k.Equals(key, StringComparison.OrdinalIgnoreCase))) return line[(i + 1)..].Trim();
        }
        return null;
    }

    private static List<ManagementBaseObject> Wmi(string query, string scope = @"root\cimv2")
    {
        try
        {
            using var s = new ManagementObjectSearcher(scope, query);
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string S(ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static long Num(ManagementBaseObject o, string prop)
    {
        try { return o[prop] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0; }
        catch { return 0; }
    }

    private static void Add(List<InfoRow> rows, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) rows.Add(new InfoRow(key, value.Trim()));
    }

    // ------------------------------------------------------------------ sistema
    public static List<InfoRow> Computer()
    {
        var rows = new List<InfoRow>();
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var product = nt?.GetValue("ProductName") as string ?? "Windows";
        var build = int.TryParse(nt?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        var display = nt?.GetValue("DisplayVersion") as string;
        var ubr = nt?.GetValue("UBR") is int u ? $".{u}" : "";
        Add(rows, L.T("Sistema operativo"), L.T($"{product}{(display != null ? " " + display : "")} (build {build}{ubr}, {(Environment.Is64BitOperatingSystem ? "64 bit" : "32 bit")})"));
        if (nt?.GetValue("InstallDate") is int install)
            Add(rows, L.T("Installato il"), Util.Date(DateTimeOffset.FromUnixTimeSeconds((uint)install).LocalDateTime));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Add(rows, L.T("Acceso da"), up.TotalDays >= 1 ? L.T($"{(int)up.TotalDays} giorni e {up.Hours} ore") : L.T($"{up.Hours} ore e {up.Minutes} minuti"));
        Add(rows, L.T("Nome del computer"), Environment.MachineName);
        Add(rows, L.T("Utente"), $@"{Environment.UserDomainName}\{Environment.UserName}");
        foreach (var cs in Wmi("SELECT Manufacturer, Model, SystemType FROM Win32_ComputerSystem"))
            Add(rows, L.T("Modello"), $"{S(cs, "Manufacturer")} {S(cs, "Model")}");
        foreach (var bb in Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            Add(rows, L.T("Scheda madre"), $"{S(bb, "Manufacturer")} {S(bb, "Product")}");
        foreach (var bios in Wmi("SELECT SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS"))
        {
            var date = S(bios, "ReleaseDate");
            var when = date.Length >= 8 && DateTime.TryParseExact(date[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? L.T($" del {d:d}") : "";
            Add(rows, "BIOS", S(bios, "SMBIOSBIOSVersion") + when);
            var serial = S(bios, "SerialNumber");
            if (serial.Length > 3 && !serial.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) && !serial.Contains("Default", StringComparison.OrdinalIgnoreCase))
                Add(rows, L.T("Numero di serie"), serial);
        }
        // senza privilegi la classe del TPM risponde solo dopo un lungo timeout
        if (RawSource.IsAdmin())
            foreach (var tpm in Wmi("SELECT SpecVersion FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm"))
                Add(rows, "TPM", "versione " + S(tpm, "SpecVersion").Split(',')[0]);
        try
        {
            using var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (sb?.GetValue("UEFISecureBootEnabled") is int sbOn) Add(rows, L.T("Avvio protetto (Secure Boot)"), sbOn == 1 ? L.T("attivo") : L.T("disattivato"));
        }
        catch { }
        return rows;
    }

    public static List<InfoRow> Processor()
    {
        var rows = new List<InfoRow>();
        foreach (var cpu in Wmi("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor"))
        {
            Add(rows, L.T("Processore"), S(cpu, "Name"));
            Add(rows, L.T("Core"), L.T($"{Num(cpu, "NumberOfCores")} core, {Num(cpu, "NumberOfLogicalProcessors")} thread"));
            Add(rows, L.T("Frequenza massima"), $"{Num(cpu, "MaxClockSpeed") / 1000.0:0.00} GHz");
            if (Num(cpu, "L3CacheSize") > 0) Add(rows, L.T("Cache L3"), Util.HumanSize(Num(cpu, "L3CacheSize") * 1024));
        }
        foreach (var os in Wmi("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            var total = Num(os, "TotalVisibleMemorySize") * 1024;
            var free = Num(os, "FreePhysicalMemory") * 1024;
            Add(rows, L.T("Memoria RAM"), L.T($"{Util.HumanSize(total)} (in uso {Util.HumanSize(total - free)}, {100.0 * (total - free) / Math.Max(1, total):0}%)"));
        }
        var modules = Wmi("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, DeviceLocator FROM Win32_PhysicalMemory");
        var slots = Wmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Num(a, "MemoryDevices"));
        if (modules.Count > 0)
        {
            var type = Num(modules[0], "SMBIOSMemoryType") switch
            {
                20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "LPDDR5", 30 => "LPDDR4", 29 => "LPDDR3", _ => "",
            };
            var speed = modules.Max(m => Math.Max(Num(m, "ConfiguredClockSpeed"), Num(m, "Speed")));
            Add(rows, L.T("Moduli di memoria"), $"{modules.Count}{(slots > 0 ? L.T($" su {slots} slot") : "")} · {type} {(speed > 0 ? $"{speed} MT/s" : "")}".Trim());
            var i = 0;
            foreach (var m in modules)
                Add(rows, L.T($"   Modulo {++i}"), $"{Util.HumanSize(Num(m, "Capacity"))} {S(m, "Manufacturer")} {S(m, "PartNumber")}");
        }
        return rows;
    }

    public static List<InfoRow> Graphics()
    {
        var rows = new List<InfoRow>();
        var vram = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var k = cls!.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string desc && k.GetValue("HardwareInformation.qwMemorySize") is long mem) vram[desc] = mem;
                }
                catch { }
            }
        }
        catch { }
        foreach (var gpu in Wmi("SELECT Name, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var name = S(gpu, "Name");
            var mem = vram.TryGetValue(name, out var m) ? m : Num(gpu, "AdapterRAM");
            Add(rows, L.T("Scheda video"), name + (mem > 0 ? $" ({Util.HumanSize(mem)})" : ""));
            Add(rows, L.T("   Driver"), S(gpu, "DriverVersion"));
            if (Num(gpu, "CurrentHorizontalResolution") > 0)
                Add(rows, L.T("   Schermo"), L.T($"{Num(gpu, "CurrentHorizontalResolution")} × {Num(gpu, "CurrentVerticalResolution")} a {Num(gpu, "CurrentRefreshRate")} Hz"));
        }
        foreach (var mon in Wmi("SELECT UserFriendlyName, ManufacturerName FROM WmiMonitorID", @"root\wmi"))
        {
            static string Decode(object? v) => v is ushort[] a ? new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
            var name = Decode(mon["UserFriendlyName"]);
            if (name.Length > 0) Add(rows, L.T("Monitor"), name);
        }
        return rows;
    }

    // ------------------------------------------------------------------ dischi
    public static List<DiskInfo> Disks()
    {
        const string storage = @"root\Microsoft\Windows\Storage";
        var result = new List<DiskInfo>();
        var partitions = Wmi("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", storage)
            .Select(p => (disk: Num(p, "DiskNumber"), letter: p["DriveLetter"] is char c && c != '\0' ? $"{c}:" : ""))
            .Where(p => p.letter.Length > 0).ToList();
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToDictionary(d => d.Name[..2].ToUpperInvariant());
        using var searcher = new ManagementObjectSearcher(storage, "SELECT * FROM MSFT_PhysicalDisk");
        ManagementObjectCollection disks;
        try { disks = searcher.Get(); }
        catch { return result; }
        foreach (ManagementObject d in disks)
        {
            var number = long.TryParse(S(d, "DeviceId"), out var n) ? n : -1;
            var media = Num(d, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
            var bus = Num(d, "BusType") switch
            {
                7 => "USB", 11 => "SATA", 17 => "NVMe", 10 => "SAS", 8 => "RAID", 12 => "SD", 13 => "MMC", 3 => "ATA", 15 => L.T("Spazi di archiviazione"), 14 => L.T("Virtuale"), _ => "",
            };
            var (health, level) = Num(d, "HealthStatus") switch
            {
                0 => (L.T("Buono"), 0),
                1 => (L.T("Attenzione: possibili problemi"), 1),
                2 => (L.T("Guasto: copia subito i dati!"), 2),
                _ => (L.T("Sconosciuto"), -1),
            };
            var details = new List<InfoRow>();
            Add(details, L.T("Tipo"), string.Join(" ", new[] { media, bus }.Where(x => x.Length > 0)));
            Add(details, L.T("Firmware"), S(d, "FirmwareVersion"));
            var counters = false;
            try
            {
                foreach (ManagementObject rc in d.GetRelated("MSFT_StorageReliabilityCounter"))
                {
                    if (Num(rc, "Temperature") > 0) Add(details, L.T("Temperatura"), $"{Num(rc, "Temperature")} °C" + (Num(rc, "TemperatureMax") > 0 ? L.T($" (max {Num(rc, "TemperatureMax")} °C)") : ""));
                    if (rc["Wear"] != null && media != "HDD") Add(details, L.T("Usura"), L.T($"{Num(rc, "Wear")}% della vita stimata"));
                    if (Num(rc, "PowerOnHours") > 0) Add(details, L.T("Ore di accensione"), Util.Number(Num(rc, "PowerOnHours")));
                    if (Num(rc, "StartStopCycleCount") > 0) Add(details, L.T("Accensioni"), Util.Number(Num(rc, "StartStopCycleCount")));
                    var errors = Num(rc, "ReadErrorsUncorrected") + Num(rc, "WriteErrorsUncorrected");
                    if (rc["ReadErrorsUncorrected"] != null) Add(details, L.T("Errori non corretti"), Util.Number(errors));
                    counters = true;
                }
            }
            catch { }
            if (!counters && !RawSource.IsAdmin())
                Add(details, L.T("Temperatura e usura"), "visibili avviando SwinKnife come amministratore");
            var volumes = partitions.Where(p => p.disk == number).Select(p => p.letter).Distinct()
                .Where(drives.ContainsKey).Select(l =>
                {
                    var di = drives[l];
                    return new VolumeUsage(l, Safe(() => di.VolumeLabel), Safe(() => di.DriveFormat), di.TotalSize, di.AvailableFreeSpace);
                }).OrderBy(v => v.Letter).ToList();
            result.Add(new DiskInfo(S(d, "FriendlyName"), string.Join(" ", new[] { media, bus }.Where(x => x.Length > 0)), Num(d, "Size"), health, level, details, volumes));
        }
        return result.OrderBy(r => r.Volumes.FirstOrDefault()?.Letter ?? "Z").ToList();
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ batteria
    public static BatteryInfo? Battery()
    {
        var bat = Wmi("SELECT EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime, Name FROM Win32_Battery").FirstOrDefault();
        if (bat == null) return null;
        var rows = new List<InfoRow>();
        var charge = (int)Num(bat, "EstimatedChargeRemaining");
        var status = Num(bat, "BatteryStatus") switch
        {
            1 => L.T("In uso (scarica)"), 2 => L.T("Collegata alla corrente"), 3 => L.T("Carica completa"), 4 => L.T("Bassa"), 5 => L.T("Critica"),
            6 or 7 or 8 or 9 => L.T("In carica"), 11 => L.T("Parzialmente carica"), _ => "",
        };
        var runtime = Num(bat, "EstimatedRunTime");
        if (runtime is > 0 and < 71582788) Add(rows, L.T("Autonomia stimata"), L.T($"{runtime / 60} h {runtime % 60} min"));
        var design = Wmi("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi").Select(o => Num(o, "DesignedCapacity")).FirstOrDefault();
        var full = Wmi("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi").Select(o => Num(o, "FullChargedCapacity")).FirstOrDefault();
        if (design > 0 && full > 0)
        {
            var health = 100.0 * full / design;
            Add(rows, L.T("Salute"), L.T($"{Math.Min(100, health):0}% della capacità originale") +
                               (health < 60 ? L.T(" (da sostituire)") : health < 80 ? " (usurata)" : ""));
            Add(rows, L.T("Capacità"), L.T($"{full / 1000.0:0.0} Wh su {design / 1000.0:0.0} Wh originali"));
        }
        var cycles = Wmi("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi").Select(o => Num(o, "CycleCount")).FirstOrDefault();
        if (cycles > 0) Add(rows, L.T("Cicli di carica"), cycles.ToString(CultureInfo.InvariantCulture));
        Add(rows, L.T("Modello"), S(bat, "Name"));
        return new BatteryInfo(charge, status, rows);
    }

    /// <summary>Crea il rapporto dettagliato della batteria di Windows (HTML).</summary>
    public static string BatteryReport()
    {
        Directory.CreateDirectory(AppInfo.TempDir);
        var path = Path.Combine(AppInfo.TempDir, "rapporto-batteria.html");
        Run("powercfg", $"/batteryreport /output \"{path}\"", 30_000);
        return File.Exists(path) ? path : throw new InvalidOperationException(L.T("Windows non ha generato il rapporto."));
    }

    // ------------------------------------------------------------------ rete
    public static List<(string title, List<InfoRow> rows)> Network()
    {
        var result = new List<(string, List<InfoRow>)>();
        string? wlan = null;
        var virtualNames = new[] { "Hyper-V", "VirtualBox", "VMware", "Virtual", "WSL", "TAP-", "Npcap", "Loopback" };
        var others = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                     .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                     .OrderBy(n => n.GetIPProperties().GatewayAddresses.Count == 0))
        {
            // le schede virtuali (macchine virtuali, VPN, ...) le elenco solo per nome
            if (virtualNames.Any(v => ni.Description.Contains(v, StringComparison.OrdinalIgnoreCase)) && ni.GetIPProperties().GatewayAddresses.Count == 0)
            {
                others.Add($"{ni.Name} ({ni.Description})");
                continue;
            }
            var rows = new List<InfoRow>();
            var ip = ni.GetIPProperties();
            var wifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            Add(rows, L.T("Tipo"), wifi ? L.T("Wi-Fi") : ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? L.T("Ethernet (cavo)") : ni.NetworkInterfaceType.ToString());
            Add(rows, L.T("Scheda"), ni.Description);
            if (wifi)
            {
                wlan ??= Safe(() => Run("netsh", "wlan show interfaces"));
                Add(rows, L.T("Rete"), Field(wlan, "SSID"));
                Add(rows, L.T("Segnale"), Field(wlan, "Segnale", "Signal", "Señal"));
                Add(rows, L.T("Standard"), Field(wlan, "Tipo frequenza radio", "Radio type", "Funktyp", "Tipo de radio", "Type de radio"));
                Add(rows, L.T("Banda"), Field(wlan, "Banda", "Band", "Bande"));
                Add(rows, L.T("Velocità ricezione"), Field(wlan, "Velocità di ricezione (Mbps)", "Receive rate (Mbps)", "Empfangsrate (MBit/s)", "Velocidad de recepción (Mbps)", "Réception (Mbits/s)") is { } rx ? rx + " Mbps" : null);
            }
            if (ni.Speed > 0) Add(rows, L.T("Velocità collegamento"), ni.Speed >= 1_000_000_000 ? $"{ni.Speed / 1e9:0.#} Gbit/s" : $"{ni.Speed / 1e6:0} Mbit/s");
            Add(rows, L.T("Indirizzo IPv4"), string.Join(", ", ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{a.Address}/{a.PrefixLength}")));
            Add(rows, L.T("Indirizzo IPv6"), string.Join(", ", ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString())));
            Add(rows, L.T("Gateway (router)"), string.Join(", ", ip.GatewayAddresses.Select(g => g.Address.ToString()).Where(a => a != "0.0.0.0")));
            Add(rows, "DNS", string.Join(", ", ip.DnsAddresses.Select(a => a.ToString()).Take(4)));
            var mac = ni.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6) Add(rows, L.T("Indirizzo MAC"), string.Join("-", mac.Select(x => x.ToString("X2"))));
            result.Add((ni.Name, rows));
        }
        if (others.Count > 0) result.Add((L.T("Schede virtuali"), others.Select(o => new InfoRow(L.T("Attiva"), o)).ToList()));
        return result;
    }

    // ------------------------------------------------------------------ avvio
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupItem> StartupItems()
    {
        var items = new List<StartupItem>();
        void FromRun(RegistryKey root, string path, string location, string approvedSub)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    if (k.GetValue(name) is not string cmd || cmd.Length == 0) continue;
                    items.Add(Make(name, cmd, location, ExeFromCommand(cmd), root, Approved + approvedSub, name));
                }
            }
            catch { }
        }
        void FromFolder(string folder, RegistryKey root, string location)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var file = Path.GetFileName(f);
                    if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(Make(Path.GetFileNameWithoutExtension(f), f, location, f, root, Approved + "StartupFolder", file));
                }
            }
            catch { }
        }
        FromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", L.T("Utente corrente"), "Run");
        FromRun(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", L.T("Tutti gli utenti"), "Run");
        FromRun(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", L.T("Tutti gli utenti (32 bit)"), "Run32");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, L.T("Cartella Esecuzione automatica"));
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, L.T("Cartella Esecuzione automatica (tutti)"));
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static StartupItem Make(string name, string cmd, string location, string? exe, RegistryKey root, string approvedKey, string approvedValue)
    {
        var publisher = "";
        var display = name;
        foreach (var suffix in new[] { " - collegamento", " - Collegamento", " - Shortcut" })
            if (display.EndsWith(suffix, StringComparison.Ordinal)) display = display[..^suffix.Length];
        if (exe != null && File.Exists(exe) && Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                publisher = (vi.CompanyName ?? "").Trim();
                var desc = (vi.FileDescription ?? "").Trim();
                if (desc.Length is > 0 and < 60 && !desc.Equals(display, StringComparison.OrdinalIgnoreCase) && !desc.Equals(publisher, StringComparison.OrdinalIgnoreCase))
                    publisher = publisher.Length > 0 ? $"{desc} ({publisher})" : desc;
            }
            catch { }
        }
        var enabled = true;
        try
        {
            using var k = root.OpenSubKey(approvedKey);
            if (k?.GetValue(approvedValue) is byte[] { Length: > 0 } data) enabled = (data[0] & 1) == 0;
        }
        catch { }
        return new StartupItem
        {
            Name = display, Command = cmd, Location = location, Executable = exe, Publisher = publisher, Enabled = enabled,
            ApprovedRoot = root, ApprovedKey = approvedKey, ApprovedValue = approvedValue,
        };
    }

    /// <summary>Attiva o disattiva un programma all'avvio come fa Gestione attività.</summary>
    public static void SetEnabled(StartupItem item, bool enabled)
    {
        using var k = item.ApprovedRoot.CreateSubKey(item.ApprovedKey, true);
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(item.ApprovedValue, data, RegistryValueKind.Binary);
        item.Enabled = enabled;
    }

    public static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        string path;
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            path = end > 0 ? cmd[1..end] : cmd.Trim('"');
        }
        else
        {
            var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
        }
        if (!Path.IsPathRooted(path))
        {
            var sys = Path.Combine(Environment.SystemDirectory, path);
            if (File.Exists(sys)) return sys;
        }
        return path;
    }
}
