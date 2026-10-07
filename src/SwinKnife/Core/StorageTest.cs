using System.Buffers.Binary;
using System.Diagnostics;

namespace SwinKnife.Core;

public sealed record StorageProgress(string Phase, double Fraction, double SpeedMBps, long BytesDone, long BytesTotal);
public sealed record StorageReport(bool Ok, long BytesTested, double WriteMBps, double ReadMBps, long ErrorBytes, long FirstErrorOffset, string Message);

/// <summary>
/// Test di integrità e velocità di una memoria (chiavette, schede SD), nello stile di H2testw: scrive un motivo
/// riconoscibile in tutto lo spazio libero e poi lo rilegge. Smaschera le memorie "farlocche" che dichiarano
/// più capacità di quella reale e trova i settori difettosi. Usa solo lo spazio libero, non tocca i file esistenti.
/// </summary>
public static class StorageTest
{
    public const string FolderName = "SwinKnife-StorageTest";
    private const long FileSize = 1L << 30; // 1 GiB per file
    private const int Chunk = 1 << 20;      // 1 MiB

    /// <summary>Scrive il motivo nello spazio libero di <paramref name="driveRoot"/> (es. "E:\").</summary>
    public static async Task<StorageReport> WriteAsync(string driveRoot, Action<StorageProgress> progress, CancellationToken ct, long? limitBytes = null)
    {
        var folder = Path.Combine(driveRoot, FolderName);
        Directory.CreateDirectory(folder);
        var drive = new DriveInfo(Path.GetPathRoot(folder)!);
        var margin = Math.Max(16L << 20, drive.TotalSize / 1000); // lascio un piccolo margine libero
        var target = Math.Max(0, drive.AvailableFreeSpace - margin);
        if (limitBytes is { } lim) target = Math.Min(target, lim);
        if (target < Chunk) return new StorageReport(false, 0, 0, 0, 0, 0, L.T("Spazio libero insufficiente per il test."));

        var buffer = new byte[Chunk];
        long written = 0;
        var sw = Stopwatch.StartNew();
        var fileIndex = 0;
        try
        {
            while (written < target && !ct.IsCancellationRequested)
            {
                var path = Path.Combine(folder, $"swk-{fileIndex:D5}.bin");
                var thisFile = Math.Min(FileSize, target - written);
                await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, FileOptions.WriteThrough);
                long fileDone = 0;
                while (fileDone < thisFile && !ct.IsCancellationRequested)
                {
                    var n = (int)Math.Min(Chunk, thisFile - fileDone);
                    FillPattern(buffer, written, n);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    fileDone += n;
                    written += n;
                    Report(progress, L.T("Scrittura"), written, target, sw);
                }
                fileIndex++;
            }
            await Task.Yield();
        }
        catch (IOException ex) when ((uint)ex.HResult == 0x80070070) // disco pieno
        {
            // normale: abbiamo riempito lo spazio libero
        }
        var mbps = written / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return new StorageReport(true, written, mbps, 0, 0, -1, L.T($"Scritti {Util.HumanSize(written)}."));
    }

    /// <summary>Rilegge i file di test in <paramref name="folder"/> e verifica il motivo.</summary>
    public static async Task<StorageReport> VerifyAsync(string folder, Action<StorageProgress> progress, CancellationToken ct)
    {
        if (!Directory.Exists(folder)) return new StorageReport(false, 0, 0, 0, 0, 0, L.T("Nessun test da verificare su questa unità."));
        var files = Directory.GetFiles(folder, "swk-*.bin").OrderBy(f => f).ToList();
        if (files.Count == 0) return new StorageReport(false, 0, 0, 0, 0, 0, L.T("Nessun test da verificare su questa unità."));
        var total = files.Sum(f => new FileInfo(f).Length);

        var buffer = new byte[Chunk];
        var expected = new byte[Chunk];
        long read = 0, errorBytes = 0, firstError = -1, globalOffset = 0;
        var sw = Stopwatch.StartNew();
        foreach (var path in files)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan);
            int got;
            while ((got = await fs.ReadAsync(buffer.AsMemory(0, Chunk), ct)) > 0)
            {
                FillPattern(expected, globalOffset, got);
                for (var i = 0; i < got; i++)
                    if (buffer[i] != expected[i])
                    {
                        errorBytes++;
                        if (firstError < 0) firstError = globalOffset + i;
                    }
                globalOffset += got;
                read += got;
                Report(progress, L.T("Verifica"), read, total, sw);
                if (ct.IsCancellationRequested) break;
            }
            if (ct.IsCancellationRequested) break;
        }
        var mbps = read / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        var ok = errorBytes == 0;
        var msg = ok
            ? L.T($"Nessun errore: la memoria è autentica e integra ({Util.HumanSize(read)} verificati).")
            : L.T($"Trovati errori su {Util.HumanSize(errorBytes)} a partire da {Util.HumanSize(firstError)}: la memoria è difettosa o dichiara una capacità falsa.");
        return new StorageReport(ok, read, 0, mbps, errorBytes, firstError, msg);
    }

    public static void DeleteTests(string driveRoot)
    {
        var folder = Path.Combine(driveRoot, FolderName);
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }

    public static bool HasTests(string driveRoot) =>
        Directory.Exists(Path.Combine(driveRoot, FolderName)) &&
        Directory.EnumerateFiles(Path.Combine(driveRoot, FolderName), "swk-*.bin").Any();

    /// <summary>Riempie il buffer con contatori a 64 bit basati sull'offset globale: così un errore o un indirizzo "riciclato" (memorie farlocche) risulta subito.</summary>
    private static void FillPattern(byte[] buffer, long globalOffset, int count)
    {
        var i = 0;
        for (; i + 8 <= count; i += 8)
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(i, 8), (ulong)((globalOffset + i) / 8) ^ 0xA5A5_5A5A_1234_8765UL);
        if (i < count) // coda (ultimo blocco non multiplo di 8)
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, (ulong)((globalOffset + i) / 8) ^ 0xA5A5_5A5A_1234_8765UL);
            tmp[..(count - i)].CopyTo(buffer.AsSpan(i, count - i));
        }
    }

    private static void Report(Action<StorageProgress> progress, string phase, long done, long total, Stopwatch sw)
    {
        var speed = done / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        progress(new StorageProgress(phase, total > 0 ? (double)done / total : 0, speed, done, total));
    }
}
