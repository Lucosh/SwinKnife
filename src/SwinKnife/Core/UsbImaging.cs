using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwinKnife.Core;

public sealed record UsbDisk(int Index, string DeviceId, string Model, long Size, string Bus, bool Removable, string[] Letters)
{
    public string Title => $"{Model}  ·  {Util.HumanSize(Size)}" + (Letters.Length > 0 ? "  ·  " + string.Join(" ", Letters.Select(l => l + ":")) : "") + $"  ·  {Bus}";
}

/// <summary>
/// Scrive un'immagine ISO/IMG su una chiavetta USB per renderla avviabile (come Rufus) e crea/ripristina
/// l'immagine completa di una chiavetta o scheda SD. Operazioni distruttive: cancellano tutto il contenuto.
/// </summary>
public static class UsbImaging
{
    // ------------------------------------------------------------------ elenco dischi
    /// <summary>Dischi rimovibili (chiavette, schede SD). Con <paramref name="includeFixed"/> anche i dischi fissi esterni.</summary>
    public static List<UsbDisk> List(bool includeFixed = false)
    {
        var disks = new List<UsbDisk>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, DeviceID, Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
            foreach (ManagementObject d in searcher.Get())
            {
                var index = Convert.ToInt32(d["Index"]);
                var media = (d["MediaType"] as string) ?? "";
                var bus = (d["InterfaceType"] as string) ?? "";
                var removable = media.Contains("Removable", StringComparison.OrdinalIgnoreCase) || bus.Equals("USB", StringComparison.OrdinalIgnoreCase);
                if (!removable && !includeFixed) continue;
                var size = d["Size"] != null ? Convert.ToInt64(d["Size"]) : 0;
                disks.Add(new UsbDisk(index, (d["DeviceID"] as string) ?? $@"\\.\PHYSICALDRIVE{index}",
                    (d["Model"] as string)?.Trim() ?? L.T("Disco"), size, bus.Length > 0 ? bus : "?", removable, LettersFor(index)));
            }
        }
        catch (Exception ex) { AppInfo.Log(ex, "Elenco dischi USB"); }
        return disks.OrderBy(d => d.Index).ToList();
    }

    private static string[] LettersFor(int diskIndex)
    {
        var letters = new List<string>();
        try
        {
            using var parts = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{diskIndex}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            foreach (ManagementObject part in parts.Get())
                using (var logs = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{((string)part["DeviceID"]).Replace("\\", "\\\\")}'}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                    foreach (ManagementObject log in logs.Get())
                        if (log["DeviceID"] is string id) letters.Add(id.TrimEnd(':'));
        }
        catch { }
        return letters.ToArray();
    }

    // ------------------------------------------------------------------ scrittura immagine
    public static async Task WriteImageAsync(UsbDisk disk, string imagePath, Action<double, double> progress, CancellationToken ct)
    {
        var imageLen = new FileInfo(imagePath).Length;
        if (imageLen > disk.Size && disk.Size > 0)
            throw new InvalidOperationException(L.T($"L'immagine ({Util.HumanSize(imageLen)}) è più grande della chiavetta ({Util.HumanSize(disk.Size)})."));

        var locks = LockVolumes(disk.Letters);
        SafeFileHandle? drive = null;
        try
        {
            drive = OpenDrive(disk.Index, write: true);
            var sector = Geometry(drive);
            await using var img = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var buffer = new byte[4 << 20];
            long pos = 0;
            var sw = Stopwatch.StartNew();
            int read;
            while ((read = await img.ReadAsync(buffer, ct)) > 0)
            {
                var toWrite = read;
                if (read % sector != 0) // ultimo blocco: arrotondo al settore con zeri
                {
                    toWrite = (read / sector + 1) * sector;
                    Array.Clear(buffer, read, toWrite - read);
                }
                RandomAccess.Write(drive, buffer.AsSpan(0, toWrite), pos);
                pos += toWrite;
                progress(Math.Min(1, (double)pos / imageLen), pos / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds));
            }
            FlushFileBuffers(drive);
            UpdateProperties(drive);
        }
        finally
        {
            drive?.Dispose();
            foreach (var h in locks) { try { Unlock(h); } catch { } h.Dispose(); }
        }
    }

    // ------------------------------------------------------------------ backup immagine (lettura)
    public static async Task ReadImageAsync(UsbDisk disk, string outPath, Action<double, double> progress, CancellationToken ct)
    {
        using var drive = OpenDrive(disk.Index, write: false);
        var sector = Geometry(drive);
        var size = disk.Size > 0 ? disk.Size / sector * sector : long.MaxValue;
        await using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[4 << 20];
        long pos = 0;
        var sw = Stopwatch.StartNew();
        while (pos < size && !ct.IsCancellationRequested)
        {
            var want = (int)Math.Min(buffer.Length, size - pos);
            want = want / sector * sector;
            if (want == 0) break;
            int got;
            try { got = RandomAccess.Read(drive, buffer.AsSpan(0, want), pos); }
            catch (IOException) { break; } // fine supporto
            if (got <= 0) break;
            await outFs.WriteAsync(buffer.AsMemory(0, got), ct);
            pos += got;
            progress(disk.Size > 0 ? (double)pos / disk.Size : 0, pos / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds));
        }
    }

    // ------------------------------------------------------------------ interop
    private static List<SafeFileHandle> LockVolumes(string[] letters)
    {
        var handles = new List<SafeFileHandle>();
        foreach (var letter in letters)
        {
            var h = CreateFile($@"\\.\{letter}:", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            DeviceIoControl(h, 0x00090018, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // FSCTL_LOCK_VOLUME
            DeviceIoControl(h, 0x00090020, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // FSCTL_DISMOUNT_VOLUME
            handles.Add(h);
        }
        return handles;
    }

    private static void Unlock(SafeFileHandle h) => DeviceIoControl(h, 0x0009001C, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // FSCTL_UNLOCK_VOLUME

    private static SafeFileHandle OpenDrive(int index, bool write)
    {
        var access = write ? 0xC0000000 : 0x80000000; // GENERIC_READ|WRITE : GENERIC_READ
        var h = CreateFile($@"\\.\PHYSICALDRIVE{index}", access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw err == 5
                ? new UnauthorizedAccessException(L.T("Accesso negato: servono i privilegi di amministratore."))
                : new IOException(L.T($"Impossibile aprire il disco (errore {err})."));
        }
        return h;
    }

    private static int Geometry(SafeFileHandle h)
    {
        var geo = new byte[24];
        if (DeviceIoControl(h, 0x00070000, IntPtr.Zero, 0, geo, 24, out _, IntPtr.Zero)) // IOCTL_DISK_GET_DRIVE_GEOMETRY
            return Math.Max(512, BitConverter.ToInt32(geo, 20));
        return 512;
    }

    private static void UpdateProperties(SafeFileHandle h) =>
        DeviceIoControl(h, 0x00070140, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // IOCTL_DISK_UPDATE_PROPERTIES

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle h);
}
