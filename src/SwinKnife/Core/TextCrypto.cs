using System.Security.Cryptography;
using System.Text;

namespace SwinKnife.Core;

/// <summary>
/// Cifra e decifra brevi testi con una password, producendo un blocco Base64 condivisibile.
/// AES-256-GCM (autenticato) con chiave derivata via PBKDF2. Formato: salt(16)·nonce(12)·testo·tag(16).
/// </summary>
public static class TextCrypto
{
    public const string Prefix = "SKMSG1:";
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16, Iterations = 210_000;

    public static string Encrypt(string plain, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(L.T("Scrivi una password."));
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(key, TagLen)) gcm.Encrypt(nonce, data, cipher, tag);

        var blob = new byte[SaltLen + NonceLen + cipher.Length + TagLen];
        Buffer.BlockCopy(salt, 0, blob, 0, SaltLen);
        Buffer.BlockCopy(nonce, 0, blob, SaltLen, NonceLen);
        Buffer.BlockCopy(cipher, 0, blob, SaltLen + NonceLen, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, SaltLen + NonceLen + cipher.Length, TagLen);
        return Prefix + Convert.ToBase64String(blob);
    }

    public static string Decrypt(string token, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(L.T("Scrivi una password."));
        token = token.Trim();
        if (token.StartsWith(Prefix, StringComparison.Ordinal)) token = token[Prefix.Length..];
        token = new string(token.Where(c => !char.IsWhiteSpace(c)).ToArray());

        byte[] blob;
        try { blob = Convert.FromBase64String(token); }
        catch { throw new InvalidOperationException(L.T("Il testo cifrato non è valido o è incompleto.")); }
        if (blob.Length < SaltLen + NonceLen + TagLen)
            throw new InvalidOperationException(L.T("Il testo cifrato non è valido o è incompleto."));

        var salt = blob[..SaltLen];
        var nonce = blob[SaltLen..(SaltLen + NonceLen)];
        var tag = blob[^TagLen..];
        var cipher = blob[(SaltLen + NonceLen)..^TagLen];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(nonce, cipher, tag, plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidOperationException(L.T("Password sbagliata, oppure il testo è stato modificato."));
        }
        return Encoding.UTF8.GetString(plain);
    }
}
