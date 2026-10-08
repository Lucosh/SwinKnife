using System.Security.Cryptography;

namespace SwinKnife.Core;

/// <summary>Genera i codici a 6 cifre dell'autenticazione a due fattori (TOTP, RFC 6238).</summary>
public static class Totp
{
    public static bool IsValidSecret(string secret)
    {
        try { return Base32Decode(secret).Length > 0; } catch { return false; }
    }

    /// <summary>Codice attuale (6 cifre) per un segreto in Base32.</summary>
    public static string Code(string base32Secret, int digits = 6, int periodSeconds = 30)
    {
        var key = Base32Decode(base32Secret);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / periodSeconds;
        Span<byte> cb = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(cb, counter);
        var hash = HMACSHA1.HashData(key, cb);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = (bin % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
        return code;
    }

    /// <summary>Secondi che mancano al prossimo codice.</summary>
    public static int SecondsLeft(int periodSeconds = 30) =>
        periodSeconds - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % periodSeconds);

    /// <summary>Estrae il segreto da un URI otpauth:// (quello dentro i QR dei siti). Null se non è un TOTP.</summary>
    public static (string secret, string? label, string? issuer)? ParseUri(string uri)
    {
        if (!uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var u = new Uri(uri);
            var q = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToLowerInvariant(), p => Uri.UnescapeDataString(p[1]), StringComparer.OrdinalIgnoreCase);
            if (!q.TryGetValue("secret", out var secret) || string.IsNullOrEmpty(secret)) return null;
            var label = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
            return (secret, label.Length > 0 ? label : null, q.GetValueOrDefault("issuer"));
        }
        catch { return null; }
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        if (input.Length == 0) throw new FormatException(L.T("Segreto 2FA vuoto."));
        var bits = 0;
        var value = 0;
        var output = new List<byte>(input.Length * 5 / 8);
        foreach (var c in input)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException(L.T("Il segreto 2FA non è un codice Base32 valido."));
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
