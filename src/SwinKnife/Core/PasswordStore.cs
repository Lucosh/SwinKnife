using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwinKnife.Core;

/// <summary>Una voce della cassaforte delle password.</summary>
public sealed class LoginEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public string TotpSecret { get; set; } = "";   // Base32, per i codici 2FA
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>
/// Cassaforte cifrata (AES-256-GCM, chiave derivata dalla password principale con PBKDF2) per i login e i
/// segreti 2FA. La password principale non viene mai salvata: senza di essa i dati non si recuperano.
/// </summary>
public static class PasswordStore
{
    public static readonly string FilePath = Path.Combine(AppInfo.DataDir, "vault", "passwords.skvault");
    private static readonly byte[] Magic = "SKPW1"u8.ToArray();
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16, Iterations = 210_000;

    public static bool Exists => File.Exists(FilePath);

    private static byte[] Key(string master, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(master, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static void Save(List<LoginEntry> entries, string master)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];
        using (var aes = new AesGcm(Key(master, salt), TagLen))
            aes.Encrypt(nonce, plain, cipher, tag);
        using var fs = File.Create(FilePath);
        fs.Write(Magic);
        fs.Write(salt);
        fs.Write(nonce);
        fs.Write(tag);
        fs.Write(cipher);
    }

    public static List<LoginEntry> Load(string master)
    {
        var all = File.ReadAllBytes(FilePath);
        var min = Magic.Length + SaltLen + NonceLen + TagLen;
        if (all.Length < min || !all.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException(L.T("Il file della cassaforte non è valido."));
        var p = Magic.Length;
        var salt = all[p..(p += SaltLen)];
        var nonce = all[p..(p += NonceLen)];
        var tag = all[p..(p += TagLen)];
        var cipher = all[p..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(master, salt), TagLen);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new UnauthorizedAccessException(L.T("Password principale sbagliata."));
        }
        return JsonSerializer.Deserialize<List<LoginEntry>>(plain) ?? new();
    }

    /// <summary>Esporta in chiaro (CSV) — operazione sensibile, solo su richiesta esplicita.</summary>
    public static void ExportCsv(List<LoginEntry> entries, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("title,username,password,url,notes,totp");
        foreach (var e in entries)
            sb.AppendLine(string.Join(",", new[] { e.Title, e.Username, e.Password, e.Url, e.Notes, e.TotpSecret }.Select(Csv)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
}
