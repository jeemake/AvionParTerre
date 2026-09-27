using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Profiles;
using Xunit;

namespace AvionParTerre.Core.Tests;

public class LlmReliabilityTests
{
    private static readonly FinishCatalogue Cat = FinishCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "data", "catalogue.json"));
    private sealed class FakeLlm(Func<LlmRequest, CancellationToken, Task<LlmResponse>> complete) : ILlmClient
    {
        public int Calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return complete(request, ct);
        }
    }
    private static LlmResponse Answer(string text) => new() { Content = text, PromptTokens = 10, CompletionTokens = 5, Cost = 0.001 };
    private static AutopilotAdvisor Advisor(ILlmClient llm, AiOptions? options = null, DecisionLog? log = null) =>
        new(llm, Cat, new JoineryRules(), options ?? new AiOptions { WebSearch = false }, log ?? new DecisionLog());
    private static RoomInput Room(string id = "r1") => new() { Key = id, Name = "Bureau", Level = "RDC", AreaM2 = 20 };

    [Fact]
    public async Task Same_name_different_known_values_are_not_merged()
    {
        var llm = new FakeLlm((_, _) => Task.FromResult(Answer("""{"pieces":[{"id":"r1","mur":"Mur A"},{"id":"r2","mur":"Mur B"}]}""")));
        var r1 = Room(); r1.Known["Sol"] = "Carrelage";
        var r2 = Room("r2"); r2.Known["Sol"] = "Parquet";
        var result = await Advisor(llm).AdviseRoomsAsync(new[] { r1, r2 }, null, new());
        Assert.Equal("Mur A", result[0].Finishes["Mur"].Designation);
        Assert.Equal("Mur B", result[1].Finishes["Mur"].Designation);
        Assert.Equal(1, llm.Calls);
    }

    [Theory]
    [InlineData("level")]
    [InlineData("area")]
    [InlineData("family")]
    [InlineData("candidates")]
    public async Task Grouping_keeps_decision_relevant_context(string difference)
    {
        var llm = new FakeLlm((request, _) => Task.FromResult(Answer(JsonSerializer.Serialize(new
        {
            pieces = new[] { "r1", "r2" }.Where(id => request.Messages[1].Content.Contains("\"id\":\"" + id + "\""))
                .Select(id => new { id }).ToArray(),
        }))));
        var a = Room(); var b = Room("r2");
        if (difference == "level") b.Level = "R+1";
        if (difference == "area") b.AreaM2 = 40;
        if (difference == "family") b.FamilyCode = Cat.FamillesLocaux.First().Code;
        if (difference == "candidates") b.CandidateProfiles.Add(Cat.Profils.First().Id);
        Assert.Equal(2, (await Advisor(llm).AdviseRoomsAsync(new[] { a, b }, null, new())).Count);
        Assert.Equal(difference == "family" ? 2 : 1, llm.Calls);
    }

    [Fact]
    public async Task Complete_rooms_make_no_model_request()
    {
        var llm = new FakeLlm((_, _) => throw new Exception("Should not call"));
        var r = Room(); foreach (var s in FinishSummary.RoomSupports) r.Known[s] = "Existing";
        Assert.Empty(await Advisor(llm).AdviseRoomsAsync(new[] { r }, null, new()));
        Assert.Equal(0, llm.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"pieces\":[17]}")]
    [InlineData("{\"pieces\":[]}")]
    [InlineData("{\"pieces\":[{\"id\":\"r1\"},{\"id\":\"r1\"}]}")]
    [InlineData("{\"pieces\":[{\"id\":\"alien\"}]}")]
    [InlineData("{\"pieces\":[{\"id\":\"r1\",\"sol\":{\"bad\":true}}]}")]
    public async Task Invalid_batch_gets_one_repair_then_succeeds(string invalid)
    {
        var count = 0;
        var llm = new FakeLlm((r, _) =>
        {
            Assert.NotNull(r.ResponseSchema);
            return Task.FromResult(Answer(++count == 1 ? invalid : "{\"pieces\":[{\"id\":\"r1\",\"mur\":\"Enduit\"}]}"));
        });
        var log = new DecisionLog();
        var result = await Advisor(llm, log: log).AdviseRoomsAsync(new[] { Room() }, null, new());
        Assert.Single(result);
        Assert.Equal(2, llm.Calls);
        Assert.Equal(1, log.Reparations);
        Assert.Equal(20, log.JetonsEntree);
    }

    [Fact]
    public async Task Repeated_invalid_batch_is_rejected_without_looping()
    {
        var llm = new FakeLlm((_, _) => Task.FromResult(Answer("{\"pieces\":[false]}")));
        var advisor = Advisor(llm);
        Assert.Empty(await advisor.AdviseRoomsAsync(new[] { Room() }, null, new()));
        Assert.NotEmpty(advisor.Errors);
        Assert.Equal(2, llm.Calls);
    }

    [Fact]
    public async Task Known_family_and_finishes_cannot_be_overridden()
    {
        var family = Cat.FamillesLocaux[0].Code;
        var other = Cat.Profils.First(p => p.FamilleLocal != family);
        var llm = new FakeLlm((_, _) => Task.FromResult(Answer(JsonSerializer.Serialize(new { pieces = new[] { new
            { id = "r1", famille = other.FamilleLocal, profil = other.Id, sol = "Overwrite" } } }))));
        var r = Room(); r.FamilyCode = family; r.Known["Sol"] = "Existing";
        var advisor = Advisor(llm);
        var result = Assert.Single(await advisor.AdviseRoomsAsync(new[] { r }, null, new()));
        Assert.Equal(family, result.FamilyCode);
        Assert.Null(result.ProfileId);
        Assert.Empty(result.Finishes);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Unsupported_finish_code_does_not_become_free_text()
    {
        var llm = new FakeLlm((_, _) => Task.FromResult(Answer("{\"pieces\":[{\"id\":\"r1\",\"sol\":\"SOL-9999\"}]}")));
        var result = Assert.Single(await Advisor(llm).AdviseRoomsAsync(new[] { Room() }, null, new()));
        Assert.False(result.Finishes.ContainsKey("Sol"));
    }

    [Fact]
    public async Task Fabricated_citations_are_not_saved_as_evidence()
    {
        var llm = new FakeLlm((_, _) =>
        {
            var answer = Answer("{\"pieces\":[{\"id\":\"r1\",\"sources\":[\"https://invented.test\",\"https://retrieved.test\"]}]}");
            answer.Citations.Add("https://retrieved.test"); return Task.FromResult(answer);
        });
        var r = Assert.Single(await Advisor(llm).AdviseRoomsAsync(new[] { Room() }, null, new()));
        Assert.Equal(new[] { "https://retrieved.test" }, r.Sources);
    }

    [Fact]
    public async Task Parallel_batches_are_bounded_and_accounted_in_input_order()
    {
        var active = 0; var peak = 0;
        var llm = new FakeLlm(async (r, ct) =>
        {
            var n = Interlocked.Increment(ref active); peak = Math.Max(peak, n);
            await Task.Delay(20, ct);
            var prompt = r.Messages[1].Content;
            var id = Enumerable.Range(0, 6).Select(i => "r" + i).Single(id => prompt.Contains("\"id\":\"" + id + "\""));
            Interlocked.Decrement(ref active);
            return Answer("{\"pieces\":[{\"id\":\"" + id + "\"}]}");
        });
        var rooms = Enumerable.Range(0, 6).Select(i => { var r = Room("r" + i); r.Name = "Bureau " + i; return r; }).ToArray();
        var log = new DecisionLog();
        var result = await Advisor(llm, new AiOptions { BatchSize = 1, MaxConcurrentRequests = 2 }, log).AdviseRoomsAsync(rooms, null, new());
        Assert.Equal(2, peak);
        Assert.Equal(rooms.Select(r => r.Key), result.Select(r => r.Key));
        Assert.Equal(6, log.Requetes);
        Assert.Equal(60, log.JetonsEntree);
    }

    [Fact]
    public async Task Slow_batch_does_not_hold_back_the_next_ones()
    {
        var slowDone = false; var startedWhileSlow = 0;
        var llm = new FakeLlm(async (r, ct) =>
        {
            var prompt = r.Messages[1].Content;
            var id = Enumerable.Range(0, 3).Select(i => "r" + i).Single(x => prompt.Contains("\"id\":\"" + x + "\""));
            if (id != "r0" && !Volatile.Read(ref slowDone)) Interlocked.Increment(ref startedWhileSlow);
            await Task.Delay(id == "r0" ? 400 : 20, ct);
            if (id == "r0") Volatile.Write(ref slowDone, true);
            return Answer("{\"pieces\":[{\"id\":\"" + id + "\"}]}");
        });
        var rooms = Enumerable.Range(0, 3).Select(i => { var r = Room("r" + i); r.Name = "Bureau " + i; return r; }).ToArray();
        var result = await Advisor(llm, new AiOptions { BatchSize = 1, MaxConcurrentRequests = 2 }).AdviseRoomsAsync(rooms, null, new());
        Assert.Equal(2, startedWhileSlow); // r1 puis r2 partent pendant que r0 est en cours
        Assert.Equal(rooms.Select(r => r.Key), result.Select(r => r.Key));
    }

    [Fact]
    public async Task Cancellation_propagates_without_repair()
    {
        using var cts = new CancellationTokenSource();
        var llm = new FakeLlm(async (_, ct) => { cts.Cancel(); await Task.Delay(1000, ct); return Answer("{}"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Advisor(llm).AdviseRoomsAsync(new[] { Room() }, null, new(), ct: cts.Token));
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Duplicate_joinery_marks_are_not_silently_assigned_to_first_family()
    {
        var llm = new FakeLlm((_, _) => throw new Exception("Should not call"));
        var advisor = Advisor(llm);
        Assert.Empty(await advisor.AdviseJoineryAsync(new[] { new JoineryInput { Mark = "A" }, new JoineryInput { Mark = "a" } }, new()));
        Assert.NotEmpty(advisor.Errors);
    }

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ++Calls, ct);
    }
    private static HttpResponseMessage Http(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static LlmRequest Request() => new() { Model = "test/model", ResponseSchema = new JsonObject { ["type"] = "object" } };
    private const string Ok = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}]}";

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"refusal\":\"No\",\"content\":\"{}\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")]
    [InlineData("{\"error\":{\"code\":500,\"message\":\"failed\"}}")]
    [InlineData("not json")]
    public async Task Invalid_provider_responses_fail_closed(string body)
    {
        var handler = new Handler((_, _, _) => Task.FromResult(Http(body)));
        using var client = new OpenRouterClient("test-key", handler: handler);
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(Request()));
        Assert.Equal(1, handler.Calls);
    }

    private static string? Format(JsonNode body) => body["response_format"]?["type"]?.GetValue<string>();

    [Fact]
    public async Task Schema_sent_and_explicit_unsupported_format_cached_for_session()
    {
        var formats = new List<string?>();
        var handler = new Handler(async (req, n, ct) =>
        {
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            formats.Add(Format(body));
            if (n == 1)
            {
                Assert.True(body["provider"]!["require_parameters"]!.GetValue<bool>());
                return Http("{\"error\":{\"message\":\"response_format unsupported\"}}", HttpStatusCode.BadRequest);
            }
            Assert.Null(body["provider"]); // json_object : aucun fournisseur écarté
            return Http(Ok);
        });
        using var client = new OpenRouterClient("test-key", handler: handler);
        await client.CompleteAsync(Request()); await client.CompleteAsync(Request());
        Assert.Equal(new[] { "json_schema", "json_object", "json_object" }, formats);
    }

    [Fact]
    public async Task No_endpoint_for_required_parameters_falls_back_instead_of_failing()
    {
        // Réponse réelle d'OpenRouter quand require_parameters écarte tous les fournisseurs du modèle
        const string noEndpoint = "{\"error\":{\"message\":\"No endpoints found that can handle the requested parameters. To learn more about provider routing, visit: https://openrouter.ai/docs/provider-routing\",\"code\":404}}";
        var formats = new List<string?>();
        var handler = new Handler(async (req, _, ct) =>
        {
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            formats.Add(Format(body));
            return Format(body) == "json_schema" ? Http(noEndpoint, HttpStatusCode.NotFound) : Http(Ok);
        });
        using var client = new OpenRouterClient("test-key", handler: handler);
        Assert.Equal("{}", (await client.CompleteAsync(Request())).Content);
        await client.CompleteAsync(Request());
        Assert.Equal(new[] { "json_schema", "json_object", "json_object" }, formats);
    }

    [Fact]
    public async Task Json_object_also_refused_falls_back_to_plain_text()
    {
        var formats = new List<string?>();
        var handler = new Handler(async (req, _, ct) =>
        {
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            formats.Add(Format(body));
            return Format(body) != null ? Http("{\"error\":{\"message\":\"This model does not support response_format\"}}", HttpStatusCode.BadRequest) : Http(Ok);
        });
        using var client = new OpenRouterClient("test-key", handler: handler);
        await client.CompleteAsync(Request()); await client.CompleteAsync(Request());
        Assert.Equal(new[] { "json_schema", "json_object", null, null }, formats);
    }

    [Fact]
    public async Task Truncated_answer_is_retried_once_with_a_larger_budget()
    {
        var budgets = new List<int>();
        var llm = new FakeLlm((r, _) =>
        {
            budgets.Add(r.MaxTokens);
            if (budgets.Count == 1) throw new LlmException("coupée", truncated: true);
            return Task.FromResult(Answer("{\"pieces\":[{\"id\":\"r1\"}]}"));
        });
        var log = new DecisionLog();
        Assert.Single(await Advisor(llm, log: log).AdviseRoomsAsync(new[] { Room() }, null, new()));
        Assert.Equal(2, budgets.Count);
        Assert.Equal(budgets[0] * 2, budgets[1]);
        Assert.Equal(1, log.Reparations);
    }

    [Fact]
    public async Task Repeated_room_names_share_one_context_across_levels()
    {
        var contexts = new List<int>();
        var llm = new FakeLlm((r, _) =>
        {
            var ids = System.Text.RegularExpressions.Regex.Matches(r.Messages[1].Content, "\"id\":\"(r\\d+)\"").Select(m => m.Groups[1].Value).ToList();
            contexts.Add(ids.Count);
            return Task.FromResult(Answer(JsonSerializer.Serialize(new { pieces = ids.Select(id => new { id, sol = "Carrelage grès cérame" }) })));
        });
        // 40 chambres sur 4 niveaux, surfaces voisines ; une suite parentale nettement plus grande reste à part
        var rooms = Enumerable.Range(0, 40).Select(i => { var r = Room("r" + i); r.Name = "Chambre"; r.Level = "R+" + i % 4; r.AreaM2 = 12 + i * 0.013; return r; })
            .Append(new RoomInput { Key = "r99", Name = "Chambre", Level = "R+1", AreaM2 = 45 }).ToArray();
        var result = await Advisor(llm).AdviseRoomsAsync(rooms, null, new());
        Assert.Equal(1, llm.Calls);
        Assert.Equal(new[] { 2 }, contexts);
        Assert.Equal(41, result.Count);
        Assert.All(result, a => Assert.Equal("Carrelage grès cérame", a.Finishes["Sol"].Designation));
    }

    [Fact]
    public async Task Web_sources_survive_repair_and_url_variants()
    {
        var llm = new FakeLlm((r, _) => Task.FromResult(r.Messages.Count == 2
            ? new LlmResponse { Content = "pas du JSON", Citations = { "https://www.exemple.ci/local-technique/" } }
            : Answer("{\"pieces\":[{\"id\":\"r1\",\"sources\":[\"http://exemple.ci/local-technique\",\"https://inventee.example\"]}]}")));
        var result = await Advisor(llm, new AiOptions { WebSearch = true }).AdviseRoomsAsync(new[] { Room() }, null, new());
        Assert.Equal(new[] { "http://exemple.ci/local-technique" }, result.Single().Sources);
    }

    [Theory]
    [InlineData(1.5, 0)]
    [InlineData(12, 2)]
    [InlineData(24.9, 2)]
    [InlineData(25, 3)]
    [InlineData(400, 5)]
    public void Size_classes(double area, int expected) => Assert.Equal(expected, AutopilotAdvisor.SizeClass(area));

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    public async Task Authentication_and_credit_failures_are_not_retried(int status)
    {
        var handler = new Handler((_, _, _) => Task.FromResult(Http("{\"error\":{\"message\":\"response_format unsupported\"}}", (HttpStatusCode)status)));
        using var client = new OpenRouterClient("test-key", handler: handler);
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(Request()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Transient_http_failure_retried_once()
    {
        var handler = new Handler((_, n, _) => Task.FromResult(n == 1 ? Http("unavailable", HttpStatusCode.ServiceUnavailable) : Http(Ok)));
        using var client = new OpenRouterClient("test-key", handler: handler);
        Assert.Equal("{}", (await client.CompleteAsync(Request())).Content);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Long_retry_after_is_honored_by_returning_control()
    {
        var handler = new Handler((_, _, _) =>
        {
            var r = Http("rate limited", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(r);
        });
        using var client = new OpenRouterClient("test-key", handler: handler);
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(Request()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Http_timeout_covers_the_whole_operation()
    {
        var handler = new Handler(async (_, _, ct) => { await Task.Delay(10000, ct); return Http(Ok); });
        using var client = new OpenRouterClient("test-key", TimeSpan.FromMilliseconds(50), handler);
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync(Request()));
        Assert.Equal(1, handler.Calls);
    }
}
