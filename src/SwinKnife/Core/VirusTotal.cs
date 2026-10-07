using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwinKnife.Core;

public enum VtStatus { Unknown, Clean, Suspicious, Malicious, NoKey, BadKey, RateLimited, Error }

/// <summary>Esito di una ricerca su VirusTotal.</summary>
public sealed record VtResult(VtStatus Status, int Malicious, int Suspicious, int Total, string? Link, string? Message = null);

/// <summary>
/// Client di VirusTotal (API v3). Si invia solo l'hash SHA-256 del file, mai il file,
/// a meno che non si usi esplicitamente <see cref="UploadAsync"/>. La chiave API sta nelle impostazioni.
/// </summary>
public static class VirusTotal
{
    private const string Base = "https://www.virustotal.com/api/v3/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string? ApiKey
    {
        get => Settings.Get("virustotal.key");
        set => Settings.Set("virustotal.key", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public static bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static async Task<VtResult> LookupFileAsync(string path, CancellationToken ct)
    {
        string hash;
        try { hash = await Task.Run(() => Sha256(path), ct); }
        catch (Exception ex) { return new VtResult(VtStatus.Error, 0, 0, 0, null, ex.Message); }
        return await LookupHashAsync(hash, ct);
    }

    public static async Task<VtResult> LookupHashAsync(string sha256, CancellationToken ct)
    {
        var key = ApiKey;
        if (string.IsNullOrWhiteSpace(key)) return new VtResult(VtStatus.NoKey, 0, 0, 0, null);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Base + "files/" + sha256);
            req.Headers.Add("x-apikey", key);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new VtResult(VtStatus.Unknown, 0, 0, 0, "https://www.virustotal.com/gui/file/" + sha256);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return new VtResult(VtStatus.BadKey, 0, 0, 0, null, L.T("Chiave VirusTotal non valida."));
            if ((int)resp.StatusCode == 429)
                return new VtResult(VtStatus.RateLimited, 0, 0, 0, null, L.T("Limite di richieste di VirusTotal raggiunto (riprova tra un minuto)."));
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var stats = doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("last_analysis_stats");
            int Get(string k) => stats.TryGetProperty(k, out var v) ? v.GetInt32() : 0;
            var mal = Get("malicious");
            var susp = Get("suspicious");
            var total = mal + susp + Get("harmless") + Get("undetected") + Get("timeout");
            var link = "https://www.virustotal.com/gui/file/" + sha256;
            var status = mal > 0 ? VtStatus.Malicious : susp > 0 ? VtStatus.Suspicious : VtStatus.Clean;
            return new VtResult(status, mal, susp, total, link);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new VtResult(VtStatus.Error, 0, 0, 0, null, ex.Message); }
    }

    /// <summary>Verifica che la chiave funzioni leggendo un hash noto.</summary>
    public static async Task<VtResult> TestKeyAsync(CancellationToken ct)
        => await LookupHashAsync("da39a3ee5e6b4b0d3255bfef95601890afd80709", ct); // SHA-1 del file vuoto: esiste sempre su VT
}
