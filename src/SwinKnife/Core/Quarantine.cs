using System.Text.Json;

namespace SwinKnife.Core;

/// <summary>Una voce in quarantena.</summary>
public sealed class QuarantineItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime QuarantinedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Quarantena: sposta un file sospetto in una cartella protetta e lo "neutralizza" (byte invertiti con XOR),
/// così non può più essere eseguito e l'antivirus non lo ri-rileva. Si può ripristinare o eliminare.
/// Tutto solo su richiesta dell'utente: niente viene fatto in automatico.
/// </summary>
public static class Quarantine
{
    private static readonly string Dir = Path.Combine(AppInfo.DataDir, "quarantine");
    private static readonly string IndexFile = Path.Combine(Dir, "index.json");
    private const byte XorKey = 0xA7; // non è cifratura: serve solo a rendere il file inerte

    public static List<QuarantineItem> Load()
    {
        try
        {
            if (!File.Exists(IndexFile)) return new();
            return JsonSerializer.Deserialize<List<QuarantineItem>>(File.ReadAllText(IndexFile)) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(List<QuarantineItem> items)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(IndexFile, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Mette in quarantena il file (deve non essere in uso). Restituisce la voce creata.</summary>
    public static QuarantineItem Add(string path, string reason)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("Il file non esiste più."), path);
        Directory.CreateDirectory(Dir);
        var fi = new FileInfo(path);
        var item = new QuarantineItem
        {
            OriginalPath = Path.GetFullPath(path),
            Name = fi.Name,
            Size = fi.Length,
            Reason = reason,
            Sha256 = VirusTotal.Sha256(path),
        };
        var dst = Path.Combine(Dir, item.Id + ".quar");
        Transform(path, dst);           // copia neutralizzata
        File.Delete(path);              // rimuove l'originale (può fallire se il file è in uso)
        var items = Load();
        items.Add(item);
        Save(items);
        return item;
    }

    /// <summary>Ripristina il file nella posizione originale (o in dst se indicato) e toglie la voce.</summary>
    public static string Restore(QuarantineItem item, string? dst = null)
    {
        var src = Path.Combine(Dir, item.Id + ".quar");
        if (!File.Exists(src)) throw new FileNotFoundException(L.T("Il file in quarantena non c'è più."));
        var target = dst ?? item.OriginalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Transform(src, target);         // inverte di nuovo lo XOR: file identico all'originale
        File.Delete(src);
        var items = Load();
        items.RemoveAll(i => i.Id == item.Id);
        Save(items);
        return target;
    }

    /// <summary>Elimina definitivamente il file in quarantena.</summary>
    public static void Delete(QuarantineItem item)
    {
        var src = Path.Combine(Dir, item.Id + ".quar");
        try { if (File.Exists(src)) File.Delete(src); } catch { }
        var items = Load();
        items.RemoveAll(i => i.Id == item.Id);
        Save(items);
    }

    private static void Transform(string src, string dst)
    {
        using var input = File.OpenRead(src);
        using var output = File.Create(dst);
        var buf = new byte[1 << 20];
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++) buf[i] ^= XorKey;
            output.Write(buf, 0, n);
        }
    }
}
