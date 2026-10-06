using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SwinKnife.Core;

/// <summary>Sorgente di byte ad accesso casuale (volume, immagine disco o memoria).</summary>
public interface IByteSource
{
    long Size { get; }
    byte[] Read(long offset, int count);
}

public sealed class MemorySource(byte[] data) : IByteSource
{
    public long Size => data.Length;

    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || offset >= data.Length) return [];
        var n = (int)Math.Min(count, data.Length - offset);
        return data.AsSpan((int)offset, n).ToArray();
    }
}

public sealed record VolumeInfo(string Letter, string Label, string FileSystem, long Total, string Type)
{
    public string Title => $"{Letter}: {(string.IsNullOrEmpty(Label) ? Type : Label)}  ·  {FileSystem}  ·  {Util.HumanSize(Total)}  ·  {Type}";
}

/// <summary>Lettura in sola lettura, con cache a blocchi, di un volume (es. "E:") o di un file immagine disco.</summary>
public sealed class RawSource : IByteSource, IDisposable
{
    private const int Block = 1 << 20;
    private const int CacheBlocks = 48;
    private readonly SafeFileHandle _handle;
    private readonly bool _isVolume;
    private readonly int _sector = 512;
    private readonly Dictionary<long, LinkedListNode<(long index, byte[] data)>> _cache = new();
    private readonly LinkedList<(long index, byte[] data)> _lru = new();

    public long Size { get; }
    public int ReadErrors { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out long outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    public RawSource(string target)
    {
        if (target.Length == 2 && target[1] == ':')
        {
            _isVolume = true;
            _handle = CreateFile($@"\\.\{target.ToUpperInvariant()}", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_handle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                throw err == 5
                    ? new UnauthorizedAccessException(L.T("Accesso negato: servono i privilegi di amministratore."))
                    : new IOException(L.T($"Impossibile aprire l'unità {target} (errore {err})."));
            }
            if (!DeviceIoControl(_handle, 0x0007405C, IntPtr.Zero, 0, out long length, 8, out _, IntPtr.Zero))
                throw new IOException(L.T("Impossibile leggere la dimensione dell'unità."));
            Size = length;
            var geo = new byte[24];
            if (DeviceIoControl(_handle, 0x00070000, IntPtr.Zero, 0, geo, 24, out _, IntPtr.Zero))
                _sector = Math.Max(512, BitConverter.ToInt32(geo, 20));
        }
        else
        {
            _handle = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Size = RandomAccess.GetLength(_handle);
        }
    }

    private byte[] ReadBlock(long index)
    {
        if (_cache.TryGetValue(index, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.data;
        }
        var off = index * Block;
        var n = (int)Math.Min(Block, Size - off);
        if (n <= 0) return [];
        var len = _isVolume ? (n + _sector - 1) / _sector * _sector : n;
        var buf = new byte[len];
        try
        {
            var got = RandomAccess.Read(_handle, buf, off);
            if (got < n) Array.Resize(ref buf, Math.Max(0, got));
            else if (len != n) Array.Resize(ref buf, n);
        }
        catch (IOException)
        {
            ReadErrors++; // settore danneggiato: proseguo con zeri
            buf = new byte[n];
        }
        var nn = _lru.AddFirst((index, buf));
        _cache[index] = nn;
        if (_lru.Count > CacheBlocks)
        {
            _cache.Remove(_lru.Last!.Value.index);
            _lru.RemoveLast();
        }
        return buf;
    }

    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || offset >= Size || count <= 0) return [];
        count = (int)Math.Min(count, Size - offset);
        var result = new byte[count];
        var done = 0;
        while (done < count)
        {
            var pos = offset + done;
            var block = ReadBlock(pos / Block);
            var inBlock = (int)(pos % Block);
            if (inBlock >= block.Length) break;
            var n = Math.Min(count - done, block.Length - inBlock);
            Buffer.BlockCopy(block, inBlock, result, done, n);
            done += n;
        }
        return done == count ? result : result[..done];
    }

    public void Dispose() => _handle.Dispose();

    // ------------------------------------------------------------------ utilità di sistema
    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RelaunchAsAdmin(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch
        {
            return false; // l'utente ha rifiutato il prompt UAC
        }
    }

    public static List<VolumeInfo> Volumes()
    {
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.Network or DriveType.CDRom) continue;
                var type = d.DriveType switch { DriveType.Removable => L.T("Rimovibile"), DriveType.Fixed => L.T("Disco locale"), _ => L.T("Unità") };
                list.Add(new VolumeInfo(d.Name[..2], d.VolumeLabel, d.DriveFormat, d.TotalSize, type));
            }
            catch { }
        }
        return list.OrderBy(v => v.Type != L.T("Rimovibile")).ThenBy(v => v.Letter).ToList();
    }
}
