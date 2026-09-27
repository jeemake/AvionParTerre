using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvionParTerre.Core.Ai;

public sealed record LlmMessage(string Role, string Content);

public sealed class LlmRequest
{
    public string Model { get; set; } = "";
    public List<LlmMessage> Messages { get; } = new();
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 8000;
    /// <summary>Demande une réponse JSON (response_format). Réessayé sans si le modèle ne le prend pas en charge.</summary>
    public bool JsonMode { get; set; } = true;
    /// <summary>Recherche internet OpenRouter (plugin « web »).</summary>
    public bool WebSearch { get; set; }
    public int WebMaxResults { get; set; } = 3;
}

public sealed class LlmResponse
{
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double? Cost { get; set; }
    public List<string> Citations { get; } = new();
}

public sealed class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public sealed record ModelInfo(string Id, string Name, int ContextLength, double? PromptPricePerMTok, double? CompletionPricePerMTok, bool SupportsJson)
{
    public string Display =>
        $"{Id}  —  {Name}" + (PromptPricePerMTok is { } p && CompletionPricePerMTok is { } c && p >= 0
            ? string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"  ({p:0.##} $ / {c:0.##} $ par M jetons)")
            : "");
}

/// <summary>
/// Client OpenRouter (https://openrouter.ai) : un seul point d'accès pour choisir librement le modèle (Anthropic, OpenAI, Google, Mistral…).
/// La clé n'est jamais journalisée.
/// </summary>
public sealed class OpenRouterClient : ILlmClient, IDisposable
{
    public const string BaseUrl = "https://openrouter.ai/api/v1/";
    private readonly HttpClient _http;

    public OpenRouterClient(string? apiKey, TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = timeout ?? TimeSpan.FromSeconds(180) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://www.koffi-diabate.com");
        _http.DefaultRequestHeaders.Add("X-Title", "Avion par terre (K&D)");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Liste publique des modèles disponibles (ne nécessite pas de clé).</summary>
    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("models", ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new LlmException($"OpenRouter : liste des modèles indisponible ({(int)resp.StatusCode}).");
        var list = new List<ModelInfo>();
        foreach (var m in JsonNode.Parse(body)?["data"]?.AsArray() ?? new JsonArray())
        {
            if (m == null) continue;
            double? Price(string k) =>
                double.TryParse(m["pricing"]?[k]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * 1_000_000 : null;
            var sp = m["supported_parameters"]?.AsArray().Select(x => x?.GetValue<string>()).ToList() ?? new List<string?>();
            list.Add(new ModelInfo(
                m["id"]?.GetValue<string>() ?? "",
                m["name"]?.GetValue<string>() ?? "",
                JsonExtract.Int(m["context_length"]) ?? 0,
                Price("prompt"), Price("completion"),
                sp.Contains("response_format") || sp.Contains("structured_outputs")));
        }
        return list.Where(m => m.Id.Length > 0).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Vérifie la clé (crédits restants) sans consommer de jetons.</summary>
    public async Task<string> CheckKeyAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("key", ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new LlmException($"Clé OpenRouter refusée ({(int)resp.StatusCode}) : {ErrorMessage(body)}");
        var d = JsonNode.Parse(body)?["data"];
        var usage = JsonExtract.Dbl(d?["usage"]);
        var limit = JsonExtract.Dbl(d?["limit"]);
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        return $"Clé valide — consommé : {(usage ?? 0).ToString("0.##", fr)} $" +
               (limit.HasValue ? $" sur une limite de {limit.Value.ToString("0.##", fr)} $" : " (sans limite)");
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (_http.DefaultRequestHeaders.Authorization == null)
            throw new LlmException("Aucune clé OpenRouter : la renseigner dans Avion par terre > Paramètres.");
        try
        {
            return await SendAsync(request, request.JsonMode, ct).ConfigureAwait(false);
        }
        catch (LlmException ex) when (request.JsonMode && ex.Message.Contains("response_format", StringComparison.OrdinalIgnoreCase))
        {
            return await SendAsync(request, false, ct).ConfigureAwait(false);
        }
    }

    private async Task<LlmResponse> SendAsync(LlmRequest request, bool jsonMode, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (jsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (request.WebSearch)
            body["plugins"] = new JsonArray(new JsonObject { ["id"] = "web", ["max_results"] = request.WebMaxResults });

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync("chat/completions", content, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("OpenRouter : délai de réponse dépassé.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException("OpenRouter injoignable (réseau) : " + ex.Message, ex);
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LlmException($"OpenRouter ({(int)resp.StatusCode}) : {ErrorMessage(text)}");
            var root = JsonNode.Parse(text);
            if (root?["error"] != null) throw new LlmException("OpenRouter : " + ErrorMessage(text));
            var msg = root?["choices"]?[0]?["message"];
            var r = new LlmResponse
            {
                Content = msg?["content"]?.GetValue<string>() ?? "",
                Model = root?["model"]?.GetValue<string>() ?? request.Model,
                PromptTokens = JsonExtract.Int(root?["usage"]?["prompt_tokens"]) ?? 0,
                CompletionTokens = JsonExtract.Int(root?["usage"]?["completion_tokens"]) ?? 0,
                Cost = JsonExtract.Dbl(root?["usage"]?["cost"]),
            };
            foreach (var a in msg?["annotations"]?.AsArray() ?? new JsonArray())
            {
                var url = a?["url_citation"]?["url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(url) && !r.Citations.Contains(url)) r.Citations.Add(url);
            }
            return r;
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            var e = JsonNode.Parse(body)?["error"];
            var m = e?["message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(m)) return m;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] + "…" : body;
    }
}

/// <summary>Extraction robuste d'un objet JSON dans une réponse de modèle (blocs ```json, texte autour).</summary>
public static class JsonExtract
{
    public static JsonNode? FirstObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try { return JsonNode.Parse(text[start..(i + 1)]); }
                    catch (JsonException) { break; }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    public static double? Dbl(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public static int? Int(JsonNode? n) => Dbl(n) is { } d ? (int)d : null;

    public static string? Str(JsonNode? n, string key)
    {
        var v = n?[key];
        if (v == null) return null;
        try
        {
            var s = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
            s = s.Trim();
            return s.Length == 0 || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
        }
        catch (InvalidOperationException) { return null; }
    }

    public static List<string> StrList(JsonNode? n, string key)
    {
        var list = new List<string>();
        if (n?[key] is JsonArray a)
            foreach (var x in a)
                if (x != null && x.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetValue<string>()))
                    list.Add(x.GetValue<string>().Trim());
        return list;
    }
}
