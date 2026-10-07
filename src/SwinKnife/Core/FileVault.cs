using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SwinKnife.Core;

/// <summary>Password sbagliata o file danneggiato durante la decifratura.</summary>
public sealed class VaultPasswordException(string message) : Exception(message);

/// <summary>
/// Cassaforte per singoli file: cifratura autenticata AES-256-GCM a blocchi, con chiave derivata
/// dalla password tramite PBKDF2. Funziona in streaming, quindi va bene anche con file enormi.
/// Formato: "SKV1" · salt(16) · prefisso nonce(8) · [ciphertext(≤64KB) · tag(16)]…
/// </summary>
public static class FileVault
{
    public const string Extension = ".skv";
    private static readonly byte[] Magic = "SKV1"u8.ToArray();
    private const int SaltLen = 16, NoncePrefix = 8, TagLen = 16, Chunk = 64 * 1024, Iterations = 210_000;

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Nonce da 12 byte: prefisso casuale del file + indice del blocco + 1 se è l'ultimo.</summary>
    private static byte[] Nonce(byte[] prefix, uint index, bool last)
    {
        var n = new byte[12];
        Array.Copy(prefix, n, NoncePrefix);
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(8), index);
        if (last) n[8] ^= 0x80; // segna l'ultimo blocco: tagliare il file fa fallire il controllo
        return n;
    }

    public static void Encrypt(string source, string dest, string password, Action<double>? progress, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var prefix = RandomNumberGenerator.GetBytes(NoncePrefix);
        using var key = new AesGcm(DeriveKey(password, salt), TagLen);
        using var input = File.OpenRead(source);
        using var output = File.Create(dest);
        output.Write(Magic);
        output.Write(salt);
        output.Write(prefix);

        var total = Math.Max(1, input.Length);
        var plain = new byte[Chunk];
        var cipher = new byte[Chunk];
        var tag = new byte[TagLen];
        uint index = 0;
        long done = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = ReadFull(input, plain);
            var last = read < Chunk || input.Position >= input.Length;
            key.Encrypt(Nonce(prefix, index, last), plain.AsSpan(0, read), cipher.AsSpan(0, read), tag);
            output.Write(cipher, 0, read);
            output.Write(tag);
            done += read;
            progress?.Invoke(done / (double)total);
            index++;
            if (last) break;
        }
        progress?.Invoke(1);
    }

    public static void Decrypt(string source, string dest, string password, Action<double>? progress, CancellationToken ct)
    {
        using var input = File.OpenRead(source);
        var header = new byte[Magic.Length + SaltLen + NoncePrefix];
        if (ReadFull(input, header) != header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new VaultPasswordException(L.T("Questo file non è una cassaforte di SwinKnife."));
        var salt = header[Magic.Length..(Magic.Length + SaltLen)];
        var prefix = header[(Magic.Length + SaltLen)..];
        using var key = new AesGcm(DeriveKey(password, salt), TagLen);
        using var output = File.Create(dest);

        var total = Math.Max(1, input.Length);
        var record = new byte[Chunk + TagLen];
        var plain = new byte[Chunk];
        uint index = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var got = ReadFull(input, record);
                if (got < TagLen) throw new VaultPasswordException(L.T("Il file è incompleto o danneggiato."));
                var dataLen = got - TagLen;
                var last = input.Position >= input.Length;
                var tag = record.AsSpan(dataLen, TagLen);
                try
                {
                    key.Decrypt(Nonce(prefix, index, last), record.AsSpan(0, dataLen), tag, plain.AsSpan(0, dataLen));
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw new VaultPasswordException(L.T("Password sbagliata, oppure il file è stato modificato o danneggiato."));
                }
                output.Write(plain, 0, dataLen);
                progress?.Invoke(input.Position / (double)total);
                index++;
                if (last) break;
            }
        }
        catch
        {
            output.Dispose();
            try { File.Delete(dest); } catch { }
            throw;
        }
        progress?.Invoke(1);
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        var done = 0;
        int n;
        while (done < buffer.Length && (n = s.Read(buffer, done, buffer.Length - done)) > 0) done += n;
        return done;
    }
}
