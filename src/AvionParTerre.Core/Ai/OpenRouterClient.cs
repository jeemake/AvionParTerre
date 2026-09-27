using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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
    public JsonObject? ResponseSchema { get; set; }
    /// <summary>Recherche internet OpenRouter (plugin « web »).</summary>
    public bool WebSearch { get; set; }
    public int WebMaxResults { get; set; } = 3;
}

public sealed class LlmResponse
{
    public string Content { get; set; } = "";
    public long ElapsedMilliseconds { get; set; }
    public string Model { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double? Cost { get; set; }
    public List<string> Citations { get; } = new();
}

public sealed class LlmException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public LlmException(string message, Exception? inner = null, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
        : base(message, inner) { StatusCode = statusCode; RetryAfter = retryAfter; }
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
    // Pool connections across commands without sharing per-user authorization headers.
    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, byte> _plainJsonModels = new(StringComparer.Ordinal);

    public OpenRouterClient(string? apiKey, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _http = new HttpClient(handler ?? SharedHandler, disposeHandler: handler != null)
            { BaseAddress = new Uri(BaseUrl), Timeout = _timeout };
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
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_timeout);
        var clock = Stopwatch.StartNew();
        var jsonMode = request.JsonMode && !_plainJsonModels.ContainsKey(request.Model);
        var retried = false;
        try
        {
            while (true)
            {
                try
                {
                    var response = await SendAsync(request, jsonMode, budget.Token).ConfigureAwait(false);
                    response.ElapsedMilliseconds = clock.ElapsedMilliseconds;
                    return response;
                }
                catch (LlmException ex) when (jsonMode && UnsupportedFormat(ex))
                {
                    // Explicit unsupported-format rejection only; never downgrade auth, quota, or malformed-schema errors.
                    jsonMode = false;
                    _plainJsonModels.TryAdd(request.Model, 0);
                }
                catch (LlmException ex) when (!retried && ex.StatusCode is HttpStatusCode.TooManyRequests or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    retried = true;
                    var delay = ex.RetryAfter ?? TimeSpan.FromMilliseconds(300);
                    if (delay > TimeSpan.FromSeconds(2)) throw; // Respect Retry-After; no long hidden wait.
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, budget.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("OpenRouter : délai de réponse dépassé (réessais compris).", ex);
        }
        catch (HttpRequestException ex)
        {
            // A connection failure may occur after generation started: don't blindly duplicate a billable request.
            throw new LlmException("OpenRouter injoignable (réseau).", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new LlmException("OpenRouter : réponse API invalide.", ex);
        }
    }

    private static bool UnsupportedFormat(LlmException ex) =>
        ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity &&
        (ex.Message.Contains("response_format", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("structured output", StringComparison.OrdinalIgnoreCase)) &&
        (ex.Message.Contains("not support", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));

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
        if (jsonMode)
        {
            body["response_format"] = request.ResponseSchema == null
                ? new JsonObject { ["type"] = "json_object" }
                : new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "avion_decisions", ["strict"] = true, ["schema"] = request.ResponseSchema.DeepClone() },
                };
            body["provider"] = new JsonObject { ["require_parameters"] = true };
        }
        if (request.WebSearch)
            body["plugins"] = new JsonArray(new JsonObject { ["id"] = "web", ["max_results"] = request.WebMaxResults });

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("chat/completions", content, ct).ConfigureAwait(false);
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LlmException($"OpenRouter ({(int)resp.StatusCode}) : {ErrorMessage(text)}", statusCode: resp.StatusCode,
                    retryAfter: resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
            var root = JsonNode.Parse(text);
            if (root?["error"] != null) throw new LlmException("OpenRouter : " + ErrorMessage(text));
            if (root?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
                throw new LlmException("OpenRouter : aucune réponse du modèle.");
            var finishReason = JsonExtract.Str(choice, "finish_reason");
            if (finishReason is "length" or "content_filter" or "error")
                throw new LlmException($"OpenRouter : réponse incomplète ou refusée ({finishReason}), aucune décision appliquée.");
            var msg = choice["message"] as JsonObject;
            if (msg == null || !string.IsNullOrEmpty(JsonExtract.Str(msg, "refusal")))
                throw new LlmException("OpenRouter : réponse refusée ou absente.");
            var responseText = JsonExtract.Str(msg, "content");
            if (string.IsNullOrWhiteSpace(responseText)) throw new LlmException("OpenRouter : contenu vide.");
            var r = new LlmResponse
            {
                Content = responseText,
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
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

    public static int? Int(JsonNode? n) => Dbl(n) is { } d && double.IsFinite(d) && d >= int.MinValue && d <= int.MaxValue && Math.Truncate(d) == d ? (int)d : null;

    public static string? Str(JsonNode? n, string key)
    {
        var v = (n as JsonObject)?[key];
        if (v == null) return null;
        try
        {
            var s = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
            s = s.Trim();
            return s.Length == 0 || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
        }
        catch (InvalidOperationException) { return null; }
    }

    public static List<string> StrList(JsonNode? n, string key)
    {
        var list = new List<string>();
        if ((n as JsonObject)?[key] is JsonArray a)
            foreach (var x in a)
                if (x != null && x.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetValue<string>()))
                    list.Add(x.GetValue<string>().Trim());
        return list;
    }
}
