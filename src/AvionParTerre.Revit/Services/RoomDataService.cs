using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

internal sealed class RoomDataProposal
{
    public Room Room { get; init; } = null!;
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public DecisionSource Source { get; init; }
    public string Justification { get; init; } = "";
}

/// <summary>
/// Données de pièces manquantes (Niv, H.S.P, H.S.D) déduites du projet : même local déjà renseigné, valeur usuelle du niveau,
/// altitude du niveau par rapport au rez-de-chaussée, faux plafond ou plancher haut modélisés. Rien n'est inventé : sans source, la valeur reste vide.
/// Format de l'agence : Niv « +/-0.00 », « +3.92 » ; hauteurs en mètres « 2.80 ».
/// </summary>
internal sealed class RoomDataService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Document _doc;
    private readonly PluginData _data;

    public RoomDataService(Document doc, PluginData data)
    {
        _doc = doc;
        _data = data;
    }

    private string? Text(Room r, string key) => RevitUtil.ParamText(r, _data.Profile.Parameter(key)) is { Length: > 0 } s ? s.Trim() : null;

    public List<RoomDataProposal> Propose()
    {
        var rooms = RevitUtil.Rooms(_doc).Where(RevitUtil.IsEnclosed).ToList();
        var levelOf = rooms.ToDictionary(r => r.Id, r => _doc.GetElement(r.LevelId) as Level);
        var classifier = new RoomClassifier(_data.Catalogue.FamillesLocaux);
        var names = rooms.ToDictionary(r => r.Id, r => TextNorm.Normalize(RevitUtil.RoomName(r)));
        var families = rooms.ToDictionary(r => r.Id, r => classifier.Classify(RevitUtil.RoomName(r))?.FamilyCode);
        var byLevel = rooms.ToLookup(r => r.LevelId);
        var byName = rooms.ToLookup(r => names[r.Id]);
        var values = rooms.ToDictionary(r => r.Id, r => new[] { "piece_niv", "piece_hsp", "piece_hsd" }.ToDictionary(k => k, k => Text(r, k)));
        var ceilings = new Lazy<List<Element>>(() => new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Ceilings).WhereElementIsNotElementType().ToList());
        var floors = new Lazy<List<Floor>>(() => new FilteredElementCollector(_doc).OfClass(typeof(Floor)).Cast<Floor>().ToList());
        var reference = rooms.Select(r => levelOf[r.Id]).Where(l => l != null).GroupBy(l => l!.Id)
            .Select(g => g.First()!).OrderBy(l => LevelNaming.Designation(l.Name) == "REZ-DE-CHAUSSEE" ? 0 : 1).ThenBy(l => Math.Abs(l.Elevation)).FirstOrDefault();
        var list = new List<RoomDataProposal>();

        string? Common(IEnumerable<Room> pool, string key) =>
            pool.Select(x => values[x.Id][key]).Where(v => v != null).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        foreach (var r in rooms)
        {
            var level = levelOf[r.Id];
            var name = names[r.Id];
            var sameLevel = byLevel[r.LevelId].Where(x => x.Id != r.Id).ToList();
            var sameName = byName[name].Where(x => x.Id != r.Id).ToList();
            var family = families[r.Id];

            void Add(string key, string label, string? value, DecisionSource src, string why)
            {
                if (value == null || RevitUtil.Param(r, _data.Profile.Parameter(key)) is not { IsReadOnly: false, StorageType: StorageType.String }) return;
                list.Add(new RoomDataProposal { Room = r, Key = key, Label = label, Value = value, Source = src, Justification = why });
            }

            if (Text(r, "piece_niv") == null && level != null)
            {
                var v = Common(sameLevel, "piece_niv");
                if (v != null) Add("piece_niv", "Niv", v, DecisionSource.Projet, $"valeur des autres pièces du niveau {level.Name}");
                else if (reference != null)
                    Add("piece_niv", "Niv", LevelNaming.FormatNiv(Units.ToMm(level.Elevation - reference.Elevation) / 1000), DecisionSource.Regle,
                        $"altitude du niveau {level.Name} par rapport à {reference.Name} (±0.00)");
            }
            if (Text(r, "piece_hsp") == null)
            {
                var v = Common(sameName.Where(x => x.LevelId == r.LevelId), "piece_hsp");
                var why = "même local sur le niveau";
                if (v == null) { v = Common(sameName, "piece_hsp"); why = "même local sur un autre niveau"; }
                if (v == null && family != null)
                {
                    v = Common(sameLevel.Where(x => families[x.Id] == family), "piece_hsp");
                    why = "valeur usuelle des locaux de la même famille sur le niveau";
                }
                if (v != null) Add("piece_hsp", "HSP", v, DecisionSource.Projet, why);
                else if (CeilingHeight(r, level, ceilings.Value) is { } h) Add("piece_hsp", "HSP", h.ToString("0.00", Inv), DecisionSource.Regle, "hauteur du faux plafond modélisé");
            }
            if (Text(r, "piece_hsd") == null)
            {
                var v = Common(sameLevel, "piece_hsd");
                if (v != null) Add("piece_hsd", "HSD", v, DecisionSource.Projet, $"valeur usuelle du niveau {level?.Name}");
                else if (SlabHeight(r, level, floors.Value) is { } h) Add("piece_hsd", "HSD", h.ToString("0.00", Inv), DecisionSource.Regle, "sous-face du plancher haut modélisé");
            }
        }
        return list;
    }

    private XYZ? Center(Room r) => (r.Location as LocationPoint)?.Point;

    private double? CeilingHeight(Room r, Level? level, IEnumerable<Element> ceilings)
    {
        if (level == null || Center(r) is not { } c) return null;
        foreach (var ce in ceilings)
        {
            if (ce.LevelId != level.Id) continue;
            var bb = ce.get_BoundingBox(null);
            if (bb == null || c.X < bb.Min.X || c.X > bb.Max.X || c.Y < bb.Min.Y || c.Y > bb.Max.Y) continue;
            var h = ce.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble();
            if (h is > 0) return Math.Round(Units.ToMm(h.Value) / 1000, 2);
        }
        return null;
    }

    private double? SlabHeight(Room r, Level? level, IEnumerable<Floor> floors)
    {
        if (level == null || Center(r) is not { } c) return null;
        double? best = null;
        foreach (var f in floors)
        {
            var bb = f.get_BoundingBox(null);
            if (bb == null || c.X < bb.Min.X || c.X > bb.Max.X || c.Y < bb.Min.Y || c.Y > bb.Max.Y) continue;
            var bottom = bb.Min.Z - level.ProjectElevation;
            var h = Units.ToMm(bottom) / 1000;
            if (h < 2.0 || h > 8.0) continue;
            if (best == null || h < best) best = h;
        }
        return best.HasValue ? Math.Round(best.Value, 2) : null;
    }

    public void Apply(IEnumerable<RoomDataProposal> items, Report report, WarningCollector w, DecisionLog? log)
    {
        using var t = WarningCollector.Start(_doc, "Avion par terre — données de pièces", w);
        foreach (var p in items)
        {
            if (Text(p.Room, p.Key) != null) continue;
            if (RevitUtil.TrySetString(p.Room, _data.Profile.Parameter(p.Key), p.Value))
            {
                report.Updated.Add($"{RevitUtil.RoomLabel(p.Room)} : {p.Label} = {p.Value} ({p.Justification})");
                log?.Add("Données de pièces", $"{RevitUtil.RoomLabel(p.Room)} / {p.Label}", p.Value, p.Source, p.Justification, id: p.Room.Id.Value);
            }
        }
        t.Commit();
    }
}
