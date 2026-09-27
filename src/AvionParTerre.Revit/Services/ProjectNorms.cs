using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Joinery;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

/// <summary>Convention de fiches d'un lot relevée dans la maquette (ex. CAL → « AL-nn », nom de feuille = repère).</summary>
internal sealed class FicheNorm
{
    public string Lot { get; init; } = "";
    public string Prefix { get; init; } = "";
    public int Digits { get; init; } = 2;
    public int NextIndex { get; set; } = 1;
    public ElementId? TitleBlock { get; init; }
    public string? Phase { get; init; }
    public string? Lot2 { get; init; }
    /// <summary>Titre de la fiche : note textuelle portant le repère (type, position depuis le coin bas-gauche du cartouche, en mm).</summary>
    public ElementId? TitleTextType { get; init; }
    public (double X, double Y)? TitleOffsetMm { get; init; }
    public int Samples { get; init; }

    public string Number(int index) => Prefix + index.ToString(new string('0', Digits));
}

/// <summary>
/// Normes documentaires de la maquette ouverte, relevées sur les feuilles existantes de l'agence : cartouche et numérotation
/// des plans généraux, niveaux déjà couverts, convention des fiches menuiseries par lot, nomenclatures « Tableau quantitatif_… ».
/// Ce que le projet a déjà décidé l'emporte sur les valeurs par défaut du profil.
/// </summary>
internal sealed class ProjectNorms
{
    private static readonly Regex Thematic = new("revetement|faux plafond|peinture|menuiserie|reperage|masse|etat des lieux|calepin|implantation|coupe|facade|toiture|terrasse|carnet|detail",
        RegexOptions.Compiled);

    public ElementId? PlanTitleBlock { get; private set; }
    public string? PlanPhase { get; private set; }
    public string? PlanLot { get; private set; }
    public int? PlanScale { get; private set; }
    public ElementId? PlanViewportType { get; private set; }
    /// <summary>Numérotation des plans : préfixe et premier index (« 1. » + 10 pour 1.10, 1.11…).</summary>
    public string? PlanNumberPrefix { get; private set; }
    public int PlanNumberDigits { get; private set; } = 2;
    /// <summary>Niveau → feuille de plan général existante de l'agence (non gérée par le plugin).</summary>
    public Dictionary<ElementId, ViewSheet> PlanSheetByLevel { get; } = new();
    public Dictionary<string, FicheNorm> Fiches { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Repère → feuille de fiche existante de l'agence (nom de feuille = repère).</summary>
    public Dictionary<string, ViewSheet> FicheSheetByMark { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Catégorie (portes / fenêtres) → nomenclature « Tableau quantitatif_… » servant de modèle.</summary>
    public Dictionary<ElementId, ViewSchedule> QuantitySchedule { get; } = new();

    public static string QuantityScheduleName(string mark) => $"Tableau quantitatif_{mark}";

    /// <summary>Profil documentaire « normes du projet » : format et échelle des plans existants.</summary>
    public Core.Profiles.DceProfile? PlanProfile(Document doc, KdResources res, Core.Profiles.DceProfile basis)
    {
        if (PlanTitleBlock == null || doc.GetElement(PlanTitleBlock) is not FamilySymbol tb) return null;
        var format = res.PaperFormat(tb) ?? basis.FormatPlans;
        return new Core.Profiles.DceProfile
        {
            Code = "NORMES_PROJET",
            Libelle = $"Normes du projet — {format}, 1:{PlanScale ?? basis.EchellePlans} ({tb.FamilyName})",
            FormatPlans = format,
            EchellePlans = PlanScale ?? basis.EchellePlans,
            FormatCarnets = basis.FormatCarnets,
            EchellesCarnets = basis.EchellesCarnets,
            TitleBlockId = tb.Id.Value,
        };
    }

    public static ProjectNorms Detect(Document doc, PluginData data)
    {
        var n = new ProjectNorms();
        var managed = Identity.All(doc).Select(x => x.Element.Id).ToHashSet();
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder && !managed.Contains(s.Id)).ToList();
        n.DetectPlans(doc, data, sheets);
        n.DetectFiches(doc, data, sheets);
        return n;
    }

    private static string? Param(ViewSheet s, IEnumerable<string> names)
    {
        var v = RevitUtil.ParamText(s, names);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static T? MostCommon<T>(IEnumerable<T> items) =>
        items.Where(i => i != null).GroupBy(i => i).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    private void DetectPlans(Document doc, PluginData data, List<ViewSheet> sheets)
    {
        var rooms = RevitUtil.Rooms(doc).Where(RevitUtil.IsEnclosed).ToList();
        var plans = new List<(ViewSheet Sheet, ViewPlan View)>();
        foreach (var s in sheets)
        {
            var name = TextNorm.Normalize(s.Name);
            if (!name.StartsWith("plan") || Thematic.IsMatch(name)) continue;
            var vp = s.GetAllViewports().Select(id => doc.GetElement(id) as Viewport)
                .Select(v => v == null ? null : doc.GetElement(v.ViewId) as ViewPlan)
                .FirstOrDefault(v => v != null && v.ViewType == ViewType.FloorPlan);
            if (vp != null) plans.Add((s, vp));
        }
        if (plans.Count == 0) return;
        PlanTitleBlock = MostCommon(plans.Select(p => RevitUtil.TitleBlockOf(doc, p.Sheet)?.Symbol.Id));
        PlanPhase = MostCommon(plans.Select(p => Param(p.Sheet, data.Profile.Parameter("feuille_phase"))));
        PlanLot = MostCommon(plans.Select(p => Param(p.Sheet, data.Profile.Parameter("feuille_lot"))));
        PlanScale = MostCommon(plans.Select(p => (int?)p.View.Scale));
        PlanViewportType = MostCommon(plans.SelectMany(p => p.Sheet.GetAllViewports()).Select(id => (doc.GetElement(id) as Viewport)?.GetTypeId()));
        var num = plans.Select(p => Regex.Match(p.Sheet.SheetNumber, @"^(.*?\D)?(\d+)$")).Where(m => m.Success).ToList();
        PlanNumberPrefix = MostCommon(num.Select(m => m.Groups[1].Value));
        PlanNumberDigits = MostCommon(num.Where(m => m.Groups[1].Value == PlanNumberPrefix).Select(m => (int?)m.Groups[2].Value.Length)) ?? 2;

        // Un niveau est couvert si la majorité de ses pièces est visible dans une vue de plan général existante
        foreach (var g in rooms.GroupBy(r => r.LevelId))
        {
            var ids = g.Select(r => r.Id).ToHashSet();
            foreach (var (sheet, view) in plans)
            {
                if (view.GenLevel?.Id == g.Key) { PlanSheetByLevel[g.Key] = sheet; break; }
                var visible = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).ToElementIds().Count(ids.Contains);
                if (visible * 2 > ids.Count) { PlanSheetByLevel[g.Key] = sheet; break; }
            }
        }
    }

    private void DetectFiches(Document doc, PluginData data, List<ViewSheet> sheets)
    {
        var classifier = new JoineryClassifier(data.Profile.Menuiseries);
        var marks = new FilteredElementCollector(doc)
            .WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Doors), new ElementCategoryFilter(BuiltInCategory.OST_Windows)))
            .WhereElementIsElementType().Select(e => e.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fiches = new List<(ViewSheet Sheet, string Lot, Match Num)>();
        foreach (var s in sheets)
        {
            var name = s.Name.Trim();
            if (!marks.Contains(name)) continue;
            FicheSheetByMark[name] = s;
            var lot = classifier.Classify(name)?.Lot;
            var m = Regex.Match(s.SheetNumber, @"^(.*?\D)(\d+)$");
            if (lot != null && m.Success) fiches.Add((s, lot, m));
        }
        var numbered = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Select(s => Regex.Match(s.SheetNumber, @"^(.*?\D)(\d+)$")).Where(m => m.Success).ToList();
        foreach (var g in fiches.GroupBy(f => f.Lot))
        {
            var prefix = MostCommon(g.Select(f => f.Num.Groups[1].Value))!;
            var digits = MostCommon(g.Where(f => f.Num.Groups[1].Value == prefix).Select(f => (int?)f.Num.Groups[2].Value.Length)) ?? 2;
            var next = numbered.Where(m => string.Equals(m.Groups[1].Value, prefix, StringComparison.OrdinalIgnoreCase))
                .Select(m => int.Parse(m.Groups[2].Value)).DefaultIfEmpty(0).Max() + 1;
            // Titre : note textuelle contenant le repère
            ElementId? textType = null;
            (double, double)? offset = null;
            foreach (var (sheet, _, _) in g)
            {
                var note = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
                    .FirstOrDefault(t => TextNorm.SameResource(t.Text.Trim().TrimEnd('/').Trim(), sheet.Name.Trim()));
                var tb = RevitUtil.TitleBlockOf(doc, sheet);
                var bb = tb?.get_BoundingBox(sheet);
                if (note == null || bb == null) continue;
                textType = note.GetTypeId();
                offset = (Units.ToMm(note.Coord.X - bb.Min.X), Units.ToMm(note.Coord.Y - bb.Min.Y));
                break;
            }
            Fiches[g.Key] = new FicheNorm
            {
                Lot = g.Key,
                Prefix = prefix,
                Digits = digits,
                NextIndex = next,
                TitleBlock = MostCommon(g.Select(f => RevitUtil.TitleBlockOf(doc, f.Sheet)?.Symbol.Id)),
                Phase = MostCommon(g.Select(f => Param(f.Sheet, data.Profile.Parameter("feuille_phase")))),
                Lot2 = MostCommon(g.Select(f => Param(f.Sheet, data.Profile.Parameter("feuille_lot")))),
                TitleTextType = textType,
                TitleOffsetMm = offset,
                Samples = g.Count(),
            };
        }
        foreach (var vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            if (vs.IsTemplate || vs.IsTitleblockRevisionSchedule || !vs.Name.StartsWith("Tableau quantitatif_", StringComparison.OrdinalIgnoreCase)) continue;
            var def = vs.Definition;
            var hasTypeFilter = def.GetFilters().Any(f => f.IsElementIdValue);
            if (!hasTypeFilter || QuantitySchedule.ContainsKey(def.CategoryId)) continue;
            QuantitySchedule[def.CategoryId] = vs;
        }
    }

    /// <summary>Résumé lisible des normes relevées (paramètres, rapport).</summary>
    public IEnumerable<string> Describe(Document doc)
    {
        string Name(ElementId? id) => id == null ? "—" : doc.GetElement(id) is FamilySymbol s ? KdResources.SymbolName(s) : doc.GetElement(id)?.Name ?? "—";
        if (PlanTitleBlock != null)
        {
            yield return $"Plans généraux : cartouche « {Name(PlanTitleBlock)} », 1:{PlanScale}, numéros « {PlanNumberPrefix}{new string('n', PlanNumberDigits)} », " +
                         $"Phase « {PlanPhase ?? "—"} », Lot « {PlanLot ?? "—"} »";
            foreach (var (lvl, s) in PlanSheetByLevel)
                yield return $"  niveau « {doc.GetElement(lvl)?.Name} » déjà couvert par la feuille {s.SheetNumber} {s.Name}";
        }
        else yield return "Plans généraux : aucune feuille de plan existante (convention du profil).";
        foreach (var f in Fiches.Values.OrderBy(f => f.Lot))
            yield return $"Fiches {f.Lot} : numéros « {f.Prefix}{new string('n', f.Digits)} » (prochain {f.Number(f.NextIndex)}), nom de feuille = repère, " +
                         $"cartouche « {Name(f.TitleBlock)} », Phase « {f.Phase ?? "—"} », titre « {Name(f.TitleTextType)} » — {f.Samples} fiche(s) existante(s)";
        foreach (var (cat, vs) in QuantitySchedule)
            yield return $"Nomenclature de localisation ({Category.GetCategory(doc, cat)?.Name}) : modèle « {vs.Name} »";
    }
}
