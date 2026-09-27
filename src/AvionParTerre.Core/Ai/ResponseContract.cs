using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvionParTerre.Core.Ai;

/// <summary>Cheap local verification remains mandatory even when a provider supports strict JSON schemas.</summary>
internal sealed class ResponseContract
{
    private readonly string? _array;
    private readonly string _id;
    private readonly HashSet<string>? _ids;
    private readonly string[] _fields;
    public JsonObject Schema { get; }

    private ResponseContract(string? array, string id, IEnumerable<string>? ids, params string[] fields)
    {
        _array = array;
        _id = id;
        _ids = ids?.ToHashSet(StringComparer.Ordinal);
        _fields = fields;
        var properties = new JsonObject { [id] = new JsonObject { ["type"] = "string" } };
        foreach (var field in fields)
            properties[field] = field == "sources"
                ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                : new JsonObject { ["type"] = new JsonArray("string", "null") };
        var item = Object(properties);
        Schema = array == null ? item : Object(new JsonObject
        {
            [array] = new JsonObject { ["type"] = "array", ["items"] = item },
        });
    }

    private static JsonObject Object(JsonObject properties) => new()
    {
        ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray()),
        ["additionalProperties"] = false,
    };

    public static ResponseContract Library() => new(null, "bibliotheque", null, "justification");
    public static ResponseContract Rooms(IEnumerable<string> ids) => new("pieces", "id", ids,
        "famille", "profil", "sol", "mur", "plafond", "justification", "sources");
    public static ResponseContract Joinery(IEnumerable<string> ids) => new("menuiseries", "repere", ids,
        "lot", "prescriptions", "justification");

    public string? Validate(JsonNode? node)
    {
        if (node is not JsonObject root) return "objet JSON manquant";
        IEnumerable<JsonNode?> rows;
        if (_array == null) rows = new[] { root };
        else if (root[_array] is JsonArray a) rows = a;
        else return $"tableau {_array} manquant";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) return "entrée non objet";
            if (obj[_id] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
                return $"identifiant {_id} manquant ou non textuel";
            if (_ids != null && !_ids.Contains(id)) return "identifiant non demandé";
            if (!seen.Add(id)) return "identifiant dupliqué";
            foreach (var field in _fields)
            {
                var value = obj[field];
                if (value == null) continue; // An explicit abstention is valid; never invent missing decisions.
                if (field == "sources")
                {
                    if (value is not JsonArray sources || sources.Any(s => s?.GetValueKind() != JsonValueKind.String))
                        return "sources doit être une liste de textes";
                }
                else if (value.GetValueKind() != JsonValueKind.String) return $"{field} doit être textuel ou null";
            }
        }
        return _ids != null && !seen.SetEquals(_ids) ? "identifiants manquants" : null;
    }
}
