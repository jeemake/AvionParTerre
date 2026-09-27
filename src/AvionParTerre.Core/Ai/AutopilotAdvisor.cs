using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Core.Ai;

public sealed class AiOptions
{
    public string Model { get; set; } = "anthropic/claude-sonnet-5";
    public bool WebSearch { get; set; } = true;
    public int WebMaxResults { get; set; } = 3;
    public double Temperature { get; set; } = 0.2;
    public int BatchSize { get; set; } = 20;
}

public sealed class ProjectContext
{
    public string Document { get; set; } = "";
    public string? ProjectName { get; set; }
    public string? Client { get; set; }
    public string? Address { get; set; }
    public string? Number { get; set; }
    public List<string> Levels { get; set; } = new();
    public List<string> RoomNames { get; set; } = new();

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Maquette : {Document}");
        if (!string.IsNullOrWhiteSpace(ProjectName)) sb.AppendLine($"Projet : {ProjectName}");
        if (!string.IsNullOrWhiteSpace(Client)) sb.AppendLine($"Maître d'ouvrage : {Client}");
        if (!string.IsNullOrWhiteSpace(Address)) sb.AppendLine($"Adresse : {Address}");
        if (Levels.Count > 0) sb.AppendLine($"Niveaux : {string.Join(", ", Levels)}");
        if (RoomNames.Count > 0) sb.AppendLine($"Locaux (extrait) : {string.Join(" ; ", RoomNames.Take(60))}");
        return sb.ToString();
    }
}

public sealed class RoomInput
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Level { get; set; } = "";
    public double AreaM2 { get; set; }
    public string? FamilyCode { get; set; }
    public List<string> CandidateProfiles { get; set; } = new();
    /// <summary>Valeurs déjà décidées (utilisateur ou maquette) : jamais modifiées.</summary>
    public Dictionary<string, string> Known { get; set; } = new();
}

public sealed class FinishChoice
{
    public string Support { get; set; } = "";
    public string Designation { get; set; } = "";
    public string? Code { get; set; }
    public bool HorsReferentiel => Code == null;
}

public sealed class RoomAdvice
{
    public string Key { get; set; } = "";
    public string? FamilyCode { get; set; }
    public string? ProfileId { get; set; }
    public Dictionary<string, FinishChoice> Finishes { get; set; } = new();
    public string? Justification { get; set; }
    public List<string> Sources { get; set; } = new();
}

public sealed class JoineryInput
{
    public string Mark { get; set; } = "";
    public string Family { get; set; } = "";
    public string Category { get; set; } = "";
    public double? WidthMm { get; set; }
    public double? HeightMm { get; set; }
    public int Count { get; set; }
    public string? Lot { get; set; }
}

public sealed class JoineryAdvice
{
    public string Mark { get; set; } = "";
    public string? Lot { get; set; }
    public string? Prescriptions { get; set; }
    public string? Justification { get; set; }
}

/// <summary>
/// Décisions du mode « avion par terre » confiées au modèle choisi sur OpenRouter : classement des locaux non classés,
/// finitions manquantes (référentiel K&amp;D d'abord, recherche internet si rien ne convient), lot et prescriptions des menuiseries.
/// Les réponses sont validées contre le référentiel : un code inconnu est rejeté, une désignation libre est marquée « hors référentiel ».
/// </summary>
public sealed class AutopilotAdvisor
{
    private readonly ILlmClient _llm;
    private readonly FinishCatalogue _cat;
    private readonly JoineryRules _joinery;
    private readonly AiOptions _opt;
    private readonly DecisionLog _log;

    public List<string> Errors { get; } = new();

    public AutopilotAdvisor(ILlmClient llm, FinishCatalogue cat, JoineryRules joinery, AiOptions opt, DecisionLog log)
    {
        _llm = llm;
        _cat = cat;
        _joinery = joinery;
        _opt = opt;
        _log = log;
        _log.Modele ??= opt.Model;
    }

    public const string SystemPrompt =
        "Tu es l'assistant « Avion par terre » de Koffi & Diabaté Architectes (Abidjan, Côte d'Ivoire). " +
        "Tu prépares le dossier de consultation des entreprises (DCE) architecture d'un projet. " +
        "Tu décides uniquement ce que l'architecte ou la maquette n'a pas déjà décidé : les valeurs déjà décidées te sont données pour contexte et ne doivent jamais être modifiées. " +
        "Règles : 1) utilise en priorité le référentiel K&D fourni (familles de locaux, profils, finitions) en recopiant exactement ses codes et désignations ; " +
        "2) ne propose une finition hors référentiel que si aucune ne convient, en choisissant une solution courante et disponible en Afrique de l'Ouest ; " +
        "3) n'invente jamais de dimensions, de quantités, de marques ni de références commerciales ; " +
        "4) justifie chaque décision en une phrase, en français ; " +
        "5) réponds uniquement par un objet JSON valide conforme au schéma demandé, sans texte autour.";

    // -----------------------------------------------------------------------------------------
    // Bibliothèque de référence
    // -----------------------------------------------------------------------------------------

    public async Task<string?> ChooseLibraryAsync(ProjectContext ctx, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Choisis la bibliothèque de finitions K&D la plus proche de ce projet (gamme et programme).");
        sb.AppendLine();
        sb.AppendLine(ctx.Describe());
        sb.AppendLine("Bibliothèques disponibles :");
        foreach (var b in _cat.Bibliotheques)
            sb.AppendLine($"- {b.Code} : {b.Display}");
        sb.AppendLine();
        sb.AppendLine("Schéma de réponse : {\"bibliotheque\": \"CODE\", \"justification\": \"...\"}");
        try
        {
            var node = await AskAsync(sb.ToString(), web: false, ct).ConfigureAwait(false);
            var code = JsonExtract.Str(node, "bibliotheque");
            var lib = _cat.Bibliotheques.FirstOrDefault(b => string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase));
            if (lib == null)
            {
                Errors.Add($"Bibliothèque proposée inconnue : « {code} ».");
                return null;
            }
            _log.Add("Finitions", "Bibliothèque de référence du projet", lib.Display, DecisionSource.Ia, JsonExtract.Str(node, "justification"));
            return lib.Code;
        }
        catch (Exception ex) when (ex is LlmException or JsonException)
        {
            Errors.Add("Choix de la bibliothèque : " + ex.Message);
            return null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Pièces
    // -----------------------------------------------------------------------------------------

    public async Task<List<RoomAdvice>> AdviseRoomsAsync(IReadOnlyList<RoomInput> rooms, string? library, ProjectContext ctx,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<RoomAdvice>();
        // Une question par libellé distinct (les « Circ. » ou « Toilette PMR » répétés reçoivent la même réponse)
        var groups = rooms.GroupBy(r => (TextNorm.Normalize(r.Name), string.Join("|", r.Known.OrderBy(k => k.Key).Select(k => k.Key)))).ToList();
        // Les locaux non classés profitent de la recherche internet ; les autres non
        var batches = groups.Where(g => g.First().FamilyCode == null).Chunk(Math.Max(1, _opt.BatchSize)).Select(b => (Web: _opt.WebSearch, Items: b))
            .Concat(groups.Where(g => g.First().FamilyCode != null).Chunk(Math.Max(1, _opt.BatchSize)).Select(b => (Web: false, Items: b)))
            .ToList();
        int done = 0;
        foreach (var (web, items) in batches)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Finitions : {done}/{groups.Count} libellés traités" + (web ? " (avec recherche internet)" : ""));
            try
            {
                var node = await AskAsync(RoomsPrompt(items.Select(g => g.First()).ToList(), library, ctx), web, ct).ConfigureAwait(false);
                var answers = node?["pieces"] as JsonArray ?? new JsonArray();
                foreach (var g in items)
                {
                    var head = g.First();
                    var a = answers.FirstOrDefault(x => JsonExtract.Str(x, "id") == head.Key);
                    if (a == null)
                    {
                        Errors.Add($"Pas de réponse pour « {head.Name} ».");
                        continue;
                    }
                    var advice = Parse(a, head);
                    foreach (var r in g)
                        result.Add(new RoomAdvice
                        {
                            Key = r.Key, FamilyCode = advice.FamilyCode, ProfileId = advice.ProfileId,
                            Finishes = advice.Finishes.Where(f => !r.Known.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => f.Value),
                            Justification = advice.Justification, Sources = advice.Sources,
                        });
                }
            }
            catch (Exception ex) when (ex is LlmException or JsonException)
            {
                Errors.Add($"Finitions ({items.Length} libellés) : {ex.Message}");
            }
            done += items.Length;
        }
        progress?.Report($"Finitions : {groups.Count}/{groups.Count} libellés traités");
        return result;
    }

    private string RoomsPrompt(List<RoomInput> rooms, string? library, ProjectContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Pour chaque local ci-dessous : classe-le dans une famille, choisis le profil de finitions K&D le plus adapté, " +
                      "puis donne les finitions de sol, mur et plafond manquantes (désignations recopiées du référentiel ; hors référentiel seulement si rien ne convient). " +
                      "Si le nom du local est ambigu ou inconnu, utilise la recherche internet pour comprendre son usage.");
        sb.AppendLine();
        sb.AppendLine(ctx.Describe());
        var lib = library == null ? null : _cat.LibraryOf(library);
        sb.AppendLine(lib == null ? "Bibliothèque : toutes (aucune retenue)." : $"Bibliothèque retenue pour le projet : {lib.Code} — {lib.Display}. Préfère ses profils.");
        sb.AppendLine();
        sb.AppendLine("FAMILLES DE LOCAUX (code : libellé)");
        foreach (var f in _cat.FamillesLocaux) sb.AppendLine($"{f.Code} : {f.Libelle}");
        sb.AppendLine();
        sb.AppendLine("PROFILS DE FINITIONS (id | bibliothèque | libellé | famille | sol | mur | plafond ; ND = non défini dans la source)");
        foreach (var p in _cat.Profils.OrderBy(p => lib != null && p.Bibliotheque != lib.Code).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var s = FinishSummary.ForRoom(_cat, p.Applications);
            sb.AppendLine($"{p.Id} | {p.Bibliotheque} | {p.Libelle} | {p.FamilleLocal} | {s["Sol"] ?? "ND"} | {s["Mur"] ?? "ND"} | {s["Plafond"] ?? "ND"}");
        }
        sb.AppendLine();
        sb.AppendLine("FINITIONS DU RÉFÉRENTIEL (support : désignation)");
        foreach (var f in _cat.Finitions.Where(f => FinishSummary.RoomSupports.Contains(f.Support)).OrderBy(f => Array.IndexOf(FinishSummary.RoomSupports, f.Support)).ThenBy(f => f.Designation))
            sb.AppendLine($"{f.Support} : {f.Designation}");
        sb.AppendLine();
        sb.AppendLine("LOCAUX À DÉCIDER");
        var arr = new JsonArray();
        foreach (var r in rooms)
        {
            var o = new JsonObject
            {
                ["id"] = r.Key,
                ["nom"] = r.Name,
                ["niveau"] = r.Level,
                ["surface_m2"] = Math.Round(r.AreaM2, 1),
                ["famille_detectee"] = r.FamilyCode,
                ["profils_candidats"] = new JsonArray(r.CandidateProfiles.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                ["deja_decide"] = new JsonObject(r.Known.Select(k => KeyValuePair.Create(k.Key.ToLowerInvariant(), (JsonNode?)JsonValue.Create(k.Value)))),
                ["a_decider"] = new JsonArray(FinishSummary.RoomSupports.Where(s => !r.Known.ContainsKey(s)).Select(s => (JsonNode)JsonValue.Create(s.ToLowerInvariant())!).ToArray()),
            };
            arr.Add(o);
        }
        sb.AppendLine(arr.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        sb.AppendLine();
        sb.AppendLine("Schéma de réponse : {\"pieces\": [{\"id\": \"...\", \"famille\": \"code\", \"profil\": \"id ou null\", " +
                      "\"sol\": \"désignation\", \"mur\": \"désignation\", \"plafond\": \"désignation\", " +
                      "\"justification\": \"une phrase\", \"sources\": [\"url consultée\"]}]} — ne renseigne que les supports listés dans « a_decider ».");
        return sb.ToString();
    }

    private RoomAdvice Parse(JsonNode a, RoomInput input)
    {
        var fam = JsonExtract.Str(a, "famille");
        var profile = _cat.Profile(JsonExtract.Str(a, "profil"));
        var advice = new RoomAdvice
        {
            Key = input.Key,
            FamilyCode = _cat.Family(fam)?.Code ?? input.FamilyCode,
            ProfileId = profile?.Id,
            Justification = JsonExtract.Str(a, "justification"),
            Sources = JsonExtract.StrList(a, "sources"),
        };
        if (fam != null && _cat.Family(fam) == null) Errors.Add($"« {input.Name} » : famille inconnue « {fam} » ignorée.");
        var fromProfile = profile == null ? null : FinishSummary.ForRoom(_cat, profile.Applications);
        foreach (var support in FinishSummary.RoomSupports)
        {
            if (input.Known.ContainsKey(support)) continue;
            var text = JsonExtract.Str(a, support.ToLowerInvariant()) ?? fromProfile?[support];
            if (string.IsNullOrWhiteSpace(text)) continue;
            advice.Finishes[support] = ResolveFinish(support, text);
        }
        return advice;
    }

    /// <summary>Rattache une désignation au référentiel (désignation, alias ou code) ; sinon finition libre « hors référentiel ».</summary>
    public FinishChoice ResolveFinish(string support, string text)
    {
        var parts = text.Split(" + ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        Finish? Match(string t)
        {
            var k = TextNorm.ResourceKey(t);
            return _cat.Finitions.FirstOrDefault(f => f.Support == support &&
                (TextNorm.ResourceKey(f.Designation) == k || TextNorm.ResourceKey(f.Code) == k || f.AliasSources.Any(x => TextNorm.ResourceKey(x) == k)));
        }
        var whole = Match(text);
        if (whole != null) return new FinishChoice { Support = support, Designation = whole.Designation, Code = whole.Code };
        // Combinaison de finitions connues (« Peinture … + Faïence … ») : conservée telle quelle, première finition comme code
        var matched = parts.Select(Match).ToList();
        if (parts.Count > 1 && matched.All(m => m != null))
            return new FinishChoice { Support = support, Designation = string.Join(" + ", matched.Select(m => m!.Designation)), Code = matched[0]!.Code };
        return new FinishChoice { Support = support, Designation = text.Trim() };
    }

    // -----------------------------------------------------------------------------------------
    // Menuiseries
    // -----------------------------------------------------------------------------------------

    public async Task<List<JoineryAdvice>> AdviseJoineryAsync(IReadOnlyList<JoineryInput> items, ProjectContext ctx,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<JoineryAdvice>();
        int done = 0;
        foreach (var batch in items.Chunk(Math.Max(1, _opt.BatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Menuiseries : {done}/{items.Count} types traités");
            var sb = new StringBuilder();
            sb.AppendLine("Pour chaque type de menuiserie : si le lot est inconnu, attribue-le (codes ci-dessous) d'après le repère, la famille et la catégorie ; " +
                          "puis rédige les prescriptions techniques de la fiche DCE (3 à 6 lignes courtes : matériau et profilés, vitrage ou remplissage, finition, quincaillerie, pose). " +
                          "Pas de dimensions, de quantités ni de marques : elles sont gérées par la maquette.");
            sb.AppendLine();
            sb.AppendLine(ctx.Describe());
            sb.AppendLine("LOTS : " + string.Join(" ; ", _joinery.Lots.Select(l => $"{l.Code} = {l.Libelle} (préfixes {string.Join(", ", l.Prefixes)})")));
            sb.AppendLine();
            var arr = new JsonArray();
            foreach (var j in batch)
                arr.Add(new JsonObject
                {
                    ["repere"] = j.Mark, ["famille"] = j.Family, ["categorie"] = j.Category, ["lot"] = j.Lot,
                    ["largeur_mm"] = j.WidthMm is { } w ? Math.Round(w) : null, ["hauteur_mm"] = j.HeightMm is { } h ? Math.Round(h) : null,
                    ["quantite"] = j.Count,
                });
            sb.AppendLine(arr.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            sb.AppendLine();
            sb.AppendLine("Schéma de réponse : {\"menuiseries\": [{\"repere\": \"...\", \"lot\": \"CAL|CB|CS\", \"prescriptions\": \"ligne 1\\nligne 2…\", \"justification\": \"une phrase\"}]}");
            try
            {
                var node = await AskAsync(sb.ToString(), web: false, ct).ConfigureAwait(false);
                var answers = node?["menuiseries"] as JsonArray ?? new JsonArray();
                foreach (var j in batch)
                {
                    var a = answers.FirstOrDefault(x => string.Equals(JsonExtract.Str(x, "repere"), j.Mark, StringComparison.OrdinalIgnoreCase));
                    if (a == null) { Errors.Add($"Pas de réponse pour la menuiserie {j.Mark}."); continue; }
                    var lot = JsonExtract.Str(a, "lot");
                    if (lot != null && _joinery.Lots.All(l => !string.Equals(l.Code, lot, StringComparison.OrdinalIgnoreCase)))
                    {
                        Errors.Add($"{j.Mark} : lot inconnu « {lot} » ignoré.");
                        lot = null;
                    }
                    result.Add(new JoineryAdvice
                    {
                        Mark = j.Mark,
                        Lot = j.Lot ?? _joinery.Lots.FirstOrDefault(l => string.Equals(l.Code, lot, StringComparison.OrdinalIgnoreCase))?.Code,
                        Prescriptions = CleanPrescriptions(JsonExtract.Str(a, "prescriptions")),
                        Justification = JsonExtract.Str(a, "justification"),
                    });
                }
            }
            catch (Exception ex) when (ex is LlmException or JsonException)
            {
                Errors.Add($"Menuiseries ({batch.Length} types) : {ex.Message}");
            }
            done += batch.Length;
        }
        progress?.Report($"Menuiseries : {items.Count}/{items.Count} types traités");
        return result;
    }

    private static string? CleanPrescriptions(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var lines = s.Replace("\\n", "\n").Split('\n').Select(l => l.Trim().TrimStart('-', '•', '*', '—').Trim()).Where(l => l.Length > 0).Take(8);
        return string.Join("\n", lines.Select(l => "— " + l));
    }

    // -----------------------------------------------------------------------------------------

    private async Task<JsonNode?> AskAsync(string prompt, bool web, CancellationToken ct)
    {
        var req = new LlmRequest
        {
            Model = _opt.Model,
            Temperature = _opt.Temperature,
            WebSearch = web,
            WebMaxResults = _opt.WebMaxResults,
        };
        req.Messages.Add(new LlmMessage("system", SystemPrompt));
        req.Messages.Add(new LlmMessage("user", prompt));
        var r = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
        _log.Account(r);
        var node = JsonExtract.FirstObject(r.Content) ?? throw new LlmException("réponse du modèle sans JSON exploitable.");
        // Citations de la recherche internet : ajoutées aux sources des pièces qui n'en citent pas
        if (r.Citations.Count > 0 && node["pieces"] is JsonArray arr)
            foreach (var p in arr.OfType<JsonObject>())
                if (p["sources"] is not JsonArray s || s.Count == 0)
                    p["sources"] = new JsonArray(r.Citations.Take(3).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        return node;
    }

    public static string Money(double? usd) =>
        usd.HasValue ? usd.Value.ToString("0.0000", CultureInfo.GetCultureInfo("fr-FR")) + " $" : "n.c.";
}
