using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwinKnife.Core;

/// <summary>
/// API ufficiale di Google Gemini (chiave gratuita da Google AI Studio), usata per tradurre i documenti
/// testo per testo mantenendo la formattazione. La chiave è salvata cifrata con l'account di Windows (DPAPI).
/// </summary>
public static class GeminiApi
{
    public const string KeyPage = "https://aistudio.google.com/apikey";
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static string Model
    {
        get => Settings.Get("gemini.model") ?? "gemini-2.5-flash";
        set => Settings.Set("gemini.model", value);
    }

    public static string? Key
    {
        get
        {
            if (Settings.Get("gemini.key") is not { } enc) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
        set => Settings.Set("gemini.key", string.IsNullOrWhiteSpace(value) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser)));
    }

    public static bool HasKey => !string.IsNullOrEmpty(Key);

    /// <summary>Invia la richiesta; ritenta con attese crescenti se il servizio è occupato o si superano i limiti gratuiti al minuto.</summary>
    private static async Task<string> Generate(JsonObject body, CancellationToken ct)
    {
        var key = Key ?? throw new InvalidOperationException(L.T("Manca la chiave API di Gemini."));
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint + Model + ":generateContent")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-goog-api-key", key);
            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                var cands = doc.RootElement.GetProperty("candidates");
                if (cands.GetArrayLength() == 0) throw new InvalidDataException(L.T("Gemini non ha restituito una risposta."));
                var parts = cands[0].GetProperty("content").GetProperty("parts");
                return string.Concat(parts.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
            }
            var retry = resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError or HttpStatusCode.GatewayTimeout;
            if (!retry || attempt >= 5)
            {
                var msg = text;
                try { msg = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("message").GetString() ?? text; } catch { }
                if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden && msg.Contains("API key", StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException(L.T("La chiave API di Gemini non è valida."));
                throw new HttpRequestException($"Gemini ({(int)resp.StatusCode}): {msg}");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 4), ct);
        }
    }

    /// <summary>Verifica la chiave con una richiesta minima.</summary>
    public static async Task TestAsync(CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = "Reply with: OK" }) }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 10 },
        };
        await Generate(body, ct);
    }

    /// <summary>Traduce un gruppo di testi; ogni testo può contenere segnaposto &lt;n&gt;…&lt;/n&gt; per i tratti formattati, da conservare.</summary>
    public static async Task<List<string>> TranslateAsync(IReadOnlyList<string> texts, string language, string? context, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Translate every string of the JSON array below into {language}.");
        prompt.AppendLine("Rules:");
        prompt.AppendLine("- Return ONLY a JSON array of strings with exactly the same number of items, in the same order.");
        prompt.AppendLine("- Some strings contain markers like <1>…</1>: they delimit pieces with special formatting. Keep every marker, with the same numbers, around the translated words they belong to. Do not add or remove markers.");
        prompt.AppendLine("- Keep numbers, URLs, e-mail addresses, code, product names and placeholders unchanged. Keep leading/trailing spaces and line breaks.");
        prompt.AppendLine("- If a string is already in the target language or should not be translated, return it unchanged.");
        if (!string.IsNullOrWhiteSpace(context)) prompt.AppendLine($"- Document context: {context}");
        prompt.AppendLine();
        prompt.Append(JsonSerializer.Serialize(texts));
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt.ToString() }) }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.2,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = new JsonObject { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "STRING" } },
            },
        };
        var json = await Generate(body, ct);
        var result = JsonSerializer.Deserialize<List<string>>(json) ?? [];
        if (result.Count != texts.Count) throw new InvalidDataException(L.T($"Gemini ha restituito {result.Count} testi invece di {texts.Count}."));
        return result;
    }
}
