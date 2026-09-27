using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

/// <summary>Affectation validée de finitions à une pièce du projet traité (stockée sur la pièce).</summary>
internal sealed class RoomFinishRecord
{
    public string Profil { get; set; } = "";
    public string Bibliotheque { get; set; } = "";
    public List<FinishApplication> Applications { get; set; } = new();
    /// <summary>Texte écrit par support (y compris saisies libres et propositions hors référentiel).</summary>
    public Dictionary<string, string> Valeurs { get; set; } = new();
    public List<string> HorsReferentiel { get; set; } = new();
    public string? FamilleLocal { get; set; }
    /// <summary>utilisateur, referentiel ou ia.</summary>
    public string Source { get; set; } = "utilisateur";
    public string? Modele { get; set; }
    public string? Justification { get; set; }
    public List<string> Sources { get; set; } = new();
    public string Statut { get; set; } = "valide_pour_projet";
    public string ValidePar { get; set; } = "";
    public DateTime Date { get; set; }
    public string SourceCatalogue { get; set; } = "";
}

/// <summary>Finitions à écrire sur une pièce : profil de référence éventuel et texte par support (choisi, tapé ou décidé par l'IA).</summary>
internal sealed class FinishAssignment
{
    public Room Room { get; init; } = null!;
    public ReferenceProfile? Profile { get; init; }
    public Dictionary<string, string?> Values { get; init; } = new();
    public string? FamilyCode { get; init; }
    public DecisionSource Source { get; init; } = DecisionSource.Utilisateur;
    public string? Model { get; init; }
    public string? Justification { get; init; }
    public List<string> Sources { get; init; } = new();
}

internal sealed class FinishService
{
    private static readonly (string Support, BuiltInParameter Bip)[] Params =
    {
        (FinishSummary.Sol, BuiltInParameter.ROOM_FINISH_FLOOR),
        (FinishSummary.Mur, BuiltInParameter.ROOM_FINISH_WALL),
        (FinishSummary.Plafond, BuiltInParameter.ROOM_FINISH_CEILING),
    };

    private readonly Document _doc;
    private readonly FinishCatalogue _cat;

    public FinishService(Document doc, FinishCatalogue cat)
    {
        _doc = doc;
        _cat = cat;
    }

    public static string Key(Room r) => $"FINITIONS|{r.UniqueId}";

    public static string Current(Room r, string support) =>
        r.get_Parameter(Params.First(p => p.Support == support).Bip)?.AsString() ?? "";

    public static RoomFinishRecord? Record(Room r)
    {
        var d = Identity.Get(r);
        if (d == null || d.Role != "finitions" || string.IsNullOrEmpty(d.Payload)) return null;
        try { return Json.Deserialize<RoomFinishRecord>(d.Payload); } catch { return null; }
    }

    /// <summary>Valeurs saisies à la main (hors plugin, ou retouchées depuis la dernière écriture du plugin).</summary>
    public static Dictionary<string, string> ManualValues(Room r)
    {
        var d = Identity.Get(r);
        var props = d?.Role == "finitions" ? ManagedProps.Parse(d.Generated) : new ManagedProps();
        var res = new Dictionary<string, string>();
        foreach (var s in FinishSummary.RoomSupports)
        {
            var cur = Current(r, s);
            if (cur.Length == 0) continue;
            if (!props.Values.TryGetValue(s, out var last) || last != cur) res[s] = cur;
        }
        return res;
    }

    /// <summary>État des valeurs actuelles : vide, écrites par le plugin ou saisies/retouchées manuellement.</summary>
    public static string State(Room r)
    {
        var d = Identity.Get(r);
        var props = d?.Role == "finitions" ? ManagedProps.Parse(d.Generated) : new ManagedProps();
        var values = FinishSummary.RoomSupports.Select(s => (s, Current(r, s))).ToList();
        if (values.All(v => string.IsNullOrWhiteSpace(v.Item2))) return "vide";
        if (d?.Role != "finitions") return "saisie manuelle";
        if (!values.All(v => props.Values.TryGetValue(v.s, out var last) ? last == v.Item2 : string.IsNullOrEmpty(v.Item2)))
            return "retouché manuellement";
        var rec = Record(r);
        var p = string.IsNullOrEmpty(rec?.Profil) ? "hors profil" : rec!.Profil;
        return rec?.Source == "ia" ? $"décidé par IA ({p}) — à valider" : $"validé ({p})";
    }

    /// <summary>Codes du référentiel correspondant à un texte (désignation, alias, combinaison « A + B »).</summary>
    public List<FinishApplication> ApplicationsFor(string support, string text, ReferenceProfile? profile)
    {
        if (profile != null && FinishSummary.For(_cat, profile.Applications, support) == text)
            return profile.Applications.Where(a => a.Support == support && a.Finition != null).ToList();
        var list = new List<FinishApplication>();
        foreach (var part in text.Split(" + ").Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var k = TextNorm.ResourceKey(part.Split(" (")[0]);
            var f = _cat.Finitions.FirstOrDefault(x => x.Support == support &&
                (TextNorm.ResourceKey(x.Designation) == k || x.AliasSources.Any(a => TextNorm.ResourceKey(a) == k)));
            if (f != null) list.Add(new FinishApplication { Support = support, Finition = f.Code, Etat = "affecte" });
        }
        return list;
    }

    public void Apply(IEnumerable<FinishAssignment> items, bool overwriteManual, string author, Report report, WarningCollector w, DecisionLog? log = null)
    {
        using var t = WarningCollector.Start(_doc, "Avion par terre — finitions", w);
        foreach (var it in items)
        {
            var room = it.Room;
            var d = Identity.Get(room);
            var props = d?.Role == "finitions" ? ManagedProps.Parse(d.Generated) : new ManagedProps();
            var label = RevitUtil.RoomLabel(room);
            var changed = new List<string>();
            var apps = new List<FinishApplication>();
            var free = new List<string>();
            var written = new Dictionary<string, string>();
            foreach (var (support, bip) in Params)
            {
                it.Values.TryGetValue(support, out var desired);
                var p = room.get_Parameter(bip);
                if (p == null || p.IsReadOnly) continue;
                var current = p.AsString() ?? "";
                if (string.IsNullOrWhiteSpace(desired))
                {
                    if (current.Length == 0)
                        report.Issue(Severity.EmissionBloquee, "finition_a_definir", $"{label} / {support}",
                            it.Profile != null ? $"Le profil {it.Profile.Id} n'indique pas de finition pour ce support." : "Aucune finition choisie pour ce support.",
                            "Définir la prestation (aucune valeur par défaut) ou lancer le mode avion par terre.", room.Id.Value);
                    else written[support] = current;
                    continue;
                }
                desired = desired.Trim();
                var manual = current.Length > 0 && (!props.Values.TryGetValue(support, out var last) || last != current);
                if (current == desired) { written[support] = current; continue; }
                if (manual && !overwriteManual)
                {
                    report.Kept.Add($"{label} / {support} : « {current} » conservé (saisie manuelle) — proposé : « {desired} »");
                    written[support] = current;
                    continue;
                }
                p.Set(desired);
                props.Values[support] = desired;
                written[support] = desired;
                changed.Add(support);
            }
            // Record the actual model values, including preserved and read-only supports.
            foreach (var (support, _) in Params)
            {
                var actual = Current(room, support);
                if (string.IsNullOrWhiteSpace(actual)) continue;
                written[support] = actual;
                var applications = ApplicationsFor(support, actual, it.Profile);
                apps.AddRange(applications);
                if (applications.Count == 0) free.Add(support);
            }
            var src = it.Source == DecisionSource.Ia ? "ia" : it.Source == DecisionSource.Referentiel ? "referentiel" : "utilisateur";
            var rec = new RoomFinishRecord
            {
                Profil = it.Profile?.Id ?? "",
                Bibliotheque = it.Profile?.Bibliotheque ?? "",
                Applications = apps,
                Valeurs = written,
                HorsReferentiel = free,
                FamilleLocal = it.FamilyCode,
                Source = src,
                Modele = it.Model,
                Justification = it.Justification,
                Sources = it.Sources,
                Statut = it.Source == DecisionSource.Ia ? "propose_ia_a_valider" : "valide_pour_projet",
                ValidePar = author,
                Date = DateTime.Now,
                SourceCatalogue = _cat.Version,
            };
            Identity.Set(room, new IdentityData { Key = Key(room), Role = "finitions", Generated = props.Serialize(), Payload = Json.Serialize(rec) });
            var what = it.Profile?.Id ?? (free.Count > 0 ? "saisie libre" : "référentiel");
            if (changed.Count > 0) report.Updated.Add($"{label} ← {what} ({string.Join(", ", changed)})" + (it.Source == DecisionSource.Ia ? " [IA]" : ""));
            else report.Unchanged.Add($"{label} ({what})");
            if (free.Count > 0)
                report.Issue(Severity.Information, "finition_hors_referentiel", label,
                    $"Finition hors référentiel K&D ({string.Join(", ", free)}) : « {string.Join(" / ", free.Select(f => written.GetValueOrDefault(f)))} ».",
                    "À valider ; l'ajouter au référentiel si elle doit être réutilisée.", room.Id.Value);
            if (!string.IsNullOrEmpty(it.Profile?.Remarque))
                report.Issue(Severity.Information, "remarque_profil", $"{label} / {it.Profile!.Id}", it.Profile.Remarque!, "À examiner lors de la validation.", room.Id.Value);
            log?.Add("Finitions", label, string.Join(" | ", FinishSummary.RoomSupports.Select(s => $"{s} : {written.GetValueOrDefault(s) ?? "à définir"}")),
                it.Source, it.Justification, it.Sources, it.Model, room.Id.Value);
        }
        t.Commit();
    }

    /// <summary>Grille de finition (présentation dérivée §10.4) : une ligne par pièce, une colonne par finition, « X » si affectée.</summary>
    public string ExportGrid(string path)
    {
        var rows = RevitUtil.Rooms(_doc).Select(r => (Room: r, Rec: Record(r))).Where(x => x.Rec != null)
            .OrderBy(x => (_doc.GetElement(x.Room.LevelId) as Level)?.Elevation ?? 0).ThenBy(x => x.Room.Number).ToList();
        var codes = rows.SelectMany(x => x.Rec!.Applications).Where(a => a.Finition != null).Select(a => a.Finition!)
            .Distinct().Select(c => _cat.Finish(c)).Where(f => f != null)
            .OrderBy(f => Array.IndexOf(new[] { "Sol", "Mur", "Plafond", "Façade" }, f!.Support)).ThenBy(f => f!.Designation).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Niveau;N°;Local;Surface (m²);Profil;Source;Hors référentiel;" + string.Join(";", codes.Select(f => DocumentRegister.Csv($"{f!.Support} — {f.Designation}"))));
        foreach (var (room, rec) in rows)
        {
            var set = rec!.Applications.Select(a => a.Finition).ToHashSet();
            sb.AppendLine(string.Join(";", new[]
            {
                (_doc.GetElement(room.LevelId) as Level)?.Name ?? "", room.Number, RevitUtil.RoomName(room),
                Units.ToM2(room.Area).ToString("0.00"), rec.Profil, rec.Source,
                string.Join(" / ", rec.HorsReferentiel.Select(s => $"{s} : {rec.Valeurs.GetValueOrDefault(s)}")),
            }.Select(DocumentRegister.Csv).Concat(codes.Select(f => set.Contains(f!.Code) ? "X" : ""))));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }
}
