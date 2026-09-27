using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Profiles;

// Synthetic scheduler/prompt benchmark only: no API key, provider call, or semantic-accuracy claim.
var cat = FinishCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "data", "catalogue.json"));
var llm = new DelayedModel();
var advisor = new AutopilotAdvisor(llm, cat, new JoineryRules(), new AiOptions { BatchSize = 10, WebSearch = false }, new DecisionLog());
var rooms = Enumerable.Range(0, 80).Select(i => new RoomInput
{
    Key = "r" + i, Name = "Bureau " + i, FamilyCode = "administration", Level = "RDC", AreaM2 = 20,
}).ToArray();
var watch = Stopwatch.StartNew();
var result = await advisor.AdviseRoomsAsync(rooms, null, new ProjectContext());
Console.WriteLine(JsonSerializer.Serialize(new
{
    rooms = result.Count, requests = llm.Calls, promptCharacters = llm.Characters,
    peakConcurrent = llm.Peak, elapsedMs = watch.ElapsedMilliseconds,
    simulatedProviderDelayMs = 80, validationErrors = advisor.Errors.Count,
}));

sealed class DelayedModel : ILlmClient
{
    public int Calls, Characters, Peak;
    private int _active;
    private readonly object _gate = new();
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        lock (_gate) { Calls++; Characters += request.Messages.Sum(m => m.Content.Length); Peak = Math.Max(Peak, ++_active); }
        try
        {
            await Task.Delay(80, ct);
            var prompt = request.Messages[1].Content;
            var start = prompt.IndexOf('[', prompt.IndexOf("LOCAUX À DÉCIDER", StringComparison.Ordinal));
            var end = prompt.IndexOf("\nSchéma de réponse", start, StringComparison.Ordinal);
            var rows = JsonNode.Parse(prompt[start..end])!.AsArray();
            return new LlmResponse { Content = JsonSerializer.Serialize(new
            {
                pieces = rows.Select(row => new { id = row!["id"]!.GetValue<string>(), famille = "administration", justification = "Simulation" }),
            }) };
        }
        finally { lock (_gate) _active--; }
    }
}
