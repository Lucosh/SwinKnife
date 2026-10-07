namespace SwinKnife.Core;

public enum CompareStatus { Same, Different, OnlyLeft, OnlyRight }

public sealed class CompareItem
{
    public required string RelPath { get; init; }
    public bool IsDir { get; init; }
    public FileInfo? Left { get; init; }
    public FileInfo? Right { get; init; }
    public CompareStatus Status { get; set; }
    /// <summary>Per i file diversi: quale lato è più recente (-1 sinistra, 1 destra, 0 uguale).</summary>
    public int Newer => Left == null || Right == null ? 0 : Math.Sign(Right.LastWriteTimeUtc.CompareTo(Left.LastWriteTimeUtc));
}

/// <summary>Confronto di due cartelle (con le sottocartelle) e copia per sincronizzarle.</summary>
public static class FolderCompare
{
    public static List<CompareItem> Compare(string left, string right, bool content, Action<string> progress, CancellationToken ct)
    {
        var l = Files(left, ct);
        var r = Files(right, ct);
        var result = new List<CompareItem>();
        foreach (var rel in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            l.TryGetValue(rel, out var a);
            r.TryGetValue(rel, out var b);
            var item = new CompareItem { RelPath = rel, Left = a, Right = b };
            if (a == null) item.Status = CompareStatus.OnlyRight;
            else if (b == null) item.Status = CompareStatus.OnlyLeft;
            else if (a.Length != b.Length) item.Status = CompareStatus.Different;
            else if (content)
            {
                progress(L.T($"Confronto il contenuto: {rel}"));
                item.Status = SameContent(a.FullName, b.FullName, ct) ? CompareStatus.Same : CompareStatus.Different;
            }
            else item.Status = Math.Abs((a.LastWriteTimeUtc - b.LastWriteTimeUtc).TotalSeconds) <= 2 ? CompareStatus.Same : CompareStatus.Different; // FAT arrotonda a 2 s
            result.Add(item);
        }
        return result;
    }

    private static Dictionary<string, FileInfo> Files(string root, CancellationToken ct)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var d = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            d[Path.GetRelativePath(root, f.FullName)] = f;
        }
        return d;
    }

    public static bool SameContent(string a, string b, CancellationToken ct = default)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        if (fa.Length != fb.Length) return false;
        var ba = new byte[1 << 16];
        var bb = new byte[1 << 16];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var na = fa.ReadAtLeast(ba, ba.Length, false);
            var nb = fb.ReadAtLeast(bb, bb.Length, false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    /// <summary>Copia il file da un lato all'altro (creando le cartelle) mantenendo la data di modifica.</summary>
    public static void Copy(CompareItem item, string fromRoot, string toRoot)
    {
        var src = Path.Combine(fromRoot, item.RelPath);
        var dst = Path.Combine(toRoot, item.RelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
    }
}
