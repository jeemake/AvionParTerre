using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

internal sealed class PlanRequest
{
    public Level Level { get; init; } = null!;
    public int Index { get; init; }
    /// <summary>Créer un plan même si une feuille de l'agence couvre déjà ce niveau (choix explicite de l'utilisateur).</summary>
    public bool Force { get; init; }
}

/// <summary>Plans généraux DCE : une vue par niveau (gabarit APD-DCE_xxe), étiquettes, feuille A0/A1 K&amp;D.</summary>
internal sealed class PlanGenerator
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly KdResources _res;
    private readonly DceProfile _dce;
    private readonly ProjectNorms _norms;

    public PlanGenerator(Document doc, PluginData data, DceProfile dce, ProjectNorms? norms = null)
    {
        _doc = doc;
        _data = data;
        _dce = dce;
        _res = new KdResources(doc, data);
        _norms = norms ?? ProjectNorms.Detect(doc, data);
    }

    public static string ViewKey(Level l) => $"PLAN|{l.UniqueId}|VUE";
    public static string SheetKey(Level l) => $"PLAN|{l.UniqueId}|FEUILLE";

    /// <summary>Numéro de feuille : série des plans existants du projet si elle existe (1.10, 1.11…), sinon règle du profil.</summary>
    public string SheetNumberFor(int index, ISet<string>? used = null)
    {
        var n = _data.Profile.Numerotation;
        if (_norms.PlanNumberPrefix != null && used != null)
        {
            // Première place libre de la série, après le dernier plan général existant
            var pre = _norms.PlanNumberPrefix;
            var last = _norms.PlanSheetByLevel.Values.Select(s => s.SheetNumber)
                .Select(x => x.StartsWith(pre) && int.TryParse(x.Substring(pre.Length), out var i) ? i : 0).DefaultIfEmpty(0).Max();
            for (int i = Math.Max(last + 1, 1); ; i++)
            {
                var num = pre + i.ToString(new string('0', _norms.PlanNumberDigits));
                if (!used.Contains(num)) return num;
            }
        }
        return n.PrefixeBatiment + Numbering.Format(n.Plans, ("index", index * n.PlansPas));
    }

    public static string SheetTitle(Level l) => LevelNaming.PlanTitle(l.Name);

    public static string ViewName(Level l) => RevitUtil.CleanName($"DCE_{l.Name}");

    private bool Covered(PlanRequest r, Dictionary<string, Element> idx) =>
        !r.Force && !idx.ContainsKey(SheetKey(r.Level)) && _norms.PlanSheetByLevel.ContainsKey(r.Level.Id);

    /// <summary>Simulation : ce qui sera créé ou mis à jour, sans écriture.</summary>
    public string Preview(IEnumerable<PlanRequest> reqs)
    {
        var idx = Identity.Index(_doc);
        var lines = new List<string>();
        var used = RevitUtil.SheetNumbers(_doc);
        foreach (var r in reqs)
        {
            if (Covered(r, idx))
            {
                var agency = _norms.PlanSheetByLevel[r.Level.Id];
                lines.Add($"{r.Level.Name} : déjà couvert par la feuille de l'agence {agency.SheetNumber} {agency.Name} — conservée, non dupliquée");
                continue;
            }
            var v = idx.ContainsKey(ViewKey(r.Level)) ? "vue existante" : $"vue « {ViewName(r.Level)} » à créer";
            string s;
            if (idx.TryGetValue(SheetKey(r.Level), out var se)) s = $"feuille {((ViewSheet)se).SheetNumber} existante";
            else
            {
                var num = SheetNumberFor(r.Index, used);
                used.Add(num);
                s = $"feuille {num} « {SheetTitle(r.Level)} » à créer";
            }
            lines.Add($"{r.Level.Name} : {v}, {s} — {_dce.FormatPlans}, 1:{_dce.EchellePlans}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public void Run(IReadOnlyList<PlanRequest> reqs, Report report, WarningCollector warnings)
    {
        var idx = Identity.Index(_doc);
        var vft = _res.ViewFamilyType("type_vue_plan", ViewFamily.FloorPlan);
        var tpl = _res.PlanTemplateForScale(_dce.EchellePlans);
        var tb = _dce.TitleBlockId is { } tbId && _doc.GetElement(new ElementId(tbId)) is FamilySymbol ? new ElementId(tbId) : _res.TitleBlock(_dce.FormatPlans);
        if (tb == ElementId.InvalidElementId)
        {
            report.Issue(Severity.Bloquant, "ressource_kd", $"Cartouche {_dce.FormatPlans}", "Cartouche introuvable : génération des plans impossible.",
                "Charger un cartouche APD-DCE ou en choisir un dans Paramètres > Ressources.");
            report.Issues.AddRange(_res.Missing);
            return;
        }
        // Plans généraux : fenêtre de vue des plans de l'agence (titre porté par le cartouche)
        var vpType = _norms.PlanViewportType ?? _res.PrepareViewTitles(report.Created, warnings);
        SheetComposer? composer = null;
        var numbers = RevitUtil.SheetNumbers(_doc);

        foreach (var r in reqs)
        {
            if (Covered(r, idx))
            {
                var agency = _norms.PlanSheetByLevel[r.Level.Id];
                report.Kept.Add($"{r.Level.Name} : plan général de l'agence {agency.SheetNumber} {agency.Name} conservé (non dupliqué)");
                continue;
            }
            using var t = WarningCollector.Start(_doc, $"Avion par terre — plan {r.Level.Name}", warnings);
            try
            {
                composer ??= new SheetComposer(_doc, _data.Profile.MiseEnPage, vpType);
                var view = EnsureView(r.Level, idx, vft, tpl, report);
                TagAll(view, r.Level, report);
                var sheet = EnsureSheet(r, idx, tb, numbers, report);
                if (!RevitUtil.IsOnSheet(view))
                {
                    composer.Compose(new[] { new SheetItem { Key = "plan", View = view } }, _ => sheet, tryCompact: true);
                }
                _doc.Regenerate();
                var vp = new FilteredElementCollector(_doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>()
                    .FirstOrDefault(x => x.ViewId == view.Id);
                if (vp != null && !composer.FitsInArea(sheet, vp))
                    report.Issue(Severity.ARevoir, "debordement", $"{sheet.SheetNumber} {sheet.Name}",
                        $"La vue dépasse la zone de dessin du {_dce.FormatPlans} à 1:{view.Scale}.",
                        "Recadrer, découper en zones (vues dépendantes) ou choisir un autre profil ; l'échelle n'a pas été modifiée.", sheet.Id.Value);
                t.Commit();
            }
            catch (Exception ex)
            {
                t.RollBack();
                report.Issue(Severity.Bloquant, "erreur", r.Level.Name, ex.Message, "Corriger puis relancer : aucune feuille partielle n'a été conservée.");
            }
        }
        report.Issues.AddRange(_res.Missing);
    }

    private ViewPlan EnsureView(Level level, Dictionary<string, Element> idx, ElementId vft, ElementId tpl, Report report)
    {
        var desiredName = ViewName(level);
        if (idx.TryGetValue(ViewKey(level), out var e) && e is ViewPlan existing)
        {
            var id = Identity.Get(existing)!;
            var props = ManagedProps.Parse(id.Generated);
            var changed = false;
            var dn = props.Decide("nom", existing.Name, desiredName);
            if (dn == ManagedProps.Decision.Update) { existing.Name = RevitUtil.UniqueViewName(_doc, desiredName, existing); changed = true; }
            else if (dn == ManagedProps.Decision.KeepRetouch) report.Kept.Add($"Vue « {existing.Name} » : nom retouché conservé");
            var dt = props.Decide("gabarit", existing.ViewTemplateId.Value.ToString(), tpl.Value.ToString());
            if (dt == ManagedProps.Decision.Update && tpl != ElementId.InvalidElementId) { existing.ViewTemplateId = tpl; changed = true; }
            else if (dt == ManagedProps.Decision.KeepRetouch) report.Kept.Add($"Vue « {existing.Name} » : gabarit retouché conservé");
            props.Values["nom"] = existing.Name;
            props.Values["gabarit"] = existing.ViewTemplateId.Value.ToString();
            id.Generated = props.Serialize();
            Identity.Set(existing, id);
            (changed ? report.Updated : report.Unchanged).Add($"Vue « {existing.Name} »");
            return existing;
        }

        var v = ViewPlan.Create(_doc, vft, level.Id);
        v.Name = RevitUtil.UniqueViewName(_doc, desiredName);
        RevitUtil.SetTitleOnSheet(v, TextNorm.UpperTitle(level.Name));
        if (tpl != ElementId.InvalidElementId) v.ViewTemplateId = tpl;
        else v.Scale = _dce.EchellePlans;
        CropToLevel(v, level);
        var p = new ManagedProps();
        p.Values["nom"] = v.Name;
        p.Values["gabarit"] = v.ViewTemplateId.Value.ToString();
        Identity.Set(v, new IdentityData { Key = ViewKey(level), Role = "plan_general_vue", Generated = p.Serialize() });
        report.Created.Add($"Vue « {v.Name} » (1:{v.Scale})");
        return v;
    }

    /// <summary>
    /// Cadrage : celui du plan de l'agence pour ce niveau s'il existe (norme du projet), sinon l'emprise des pièces du niveau
    /// et des murs qui les bordent (+2 m). Les éléments isolés loin du bâtiment ne dilatent plus le cadrage.
    /// </summary>
    private void CropToLevel(ViewPlan v, Level level)
    {
        if (_norms.PlanSheetByLevel.TryGetValue(level.Id, out var agency))
        {
            var src = agency.GetAllViewports().Select(id => _doc.GetElement(id) as Viewport)
                .Select(vp => vp == null ? null : _doc.GetElement(vp.ViewId) as ViewPlan)
                .FirstOrDefault(p => p != null && p.CropBoxActive);
            if (src != null)
            {
                var c = v.CropBox;
                var sc = src.CropBox;
                c.Min = new XYZ(sc.Min.X, sc.Min.Y, c.Min.Z);
                c.Max = new XYZ(sc.Max.X, sc.Max.Y, c.Max.Z);
                v.CropBox = c;
                v.CropBoxActive = true;
                v.CropBoxVisible = false;
                return;
            }
        }
        var rooms = RevitUtil.Rooms(_doc).Where(r => r.LevelId == level.Id && RevitUtil.IsEnclosed(r))
            .Select(r => r.get_BoundingBox(null)).Where(b => b != null).ToList();
        if (rooms.Count == 0) return;
        double near = Units.Mm(3000);
        double x0 = rooms.Min(b => b!.Min.X) - near, y0 = rooms.Min(b => b!.Min.Y) - near;
        double x1 = rooms.Max(b => b!.Max.X) + near, y1 = rooms.Max(b => b!.Max.Y) + near;
        var boxes = rooms.Concat(new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Element>()
                .Where(w => w.LevelId == level.Id).Select(w => w.get_BoundingBox(null))
                .Where(b => b != null && b.Max.X > x0 && b.Min.X < x1 && b.Max.Y > y0 && b.Min.Y < y1))
            .ToList();
        double m = Units.Mm(2000);
        var cb = v.CropBox;
        cb.Min = new XYZ(Math.Max(x0, boxes.Min(b => b!.Min.X)) - m, Math.Max(y0, boxes.Min(b => b!.Min.Y)) - m, cb.Min.Z);
        cb.Max = new XYZ(Math.Min(x1, boxes.Max(b => b!.Max.X)) + m, Math.Min(y1, boxes.Max(b => b!.Max.Y)) + m, cb.Max.Z);
        v.CropBox = cb;
        v.CropBoxActive = true;
        v.CropBoxVisible = false;
    }

    private ViewSheet EnsureSheet(PlanRequest r, Dictionary<string, Element> idx, ElementId tb, HashSet<string> numbers, Report report)
    {
        var desiredName = SheetTitle(r.Level);
        if (idx.TryGetValue(SheetKey(r.Level), out var e) && e is ViewSheet existing)
        {
            var id = Identity.Get(existing)!;
            var props = ManagedProps.Parse(id.Generated);
            var d = props.Decide("nom", existing.Name, desiredName);
            if (d == ManagedProps.Decision.Update) { existing.Name = RevitUtil.CleanName(desiredName); report.Updated.Add($"Feuille {existing.SheetNumber} : titre mis à jour"); }
            else if (d == ManagedProps.Decision.KeepRetouch) report.Kept.Add($"Feuille {existing.SheetNumber} : titre retouché conservé");
            else report.Unchanged.Add($"Feuille {existing.SheetNumber} {existing.Name}");
            props.Values["nom"] = existing.Name;
            id.Generated = props.Serialize();
            Identity.Set(existing, id);
            return existing;
        }

        var s = ViewSheet.Create(_doc, tb);
        var number = SheetNumberFor(r.Index, numbers);
        if (numbers.Contains(number))
        {
            var wanted = number;
            for (int i = 1; numbers.Contains(number); i++) number = wanted + "-" + i;
            report.Issue(Severity.ARevoir, "numerotation", wanted, $"Numéro déjà utilisé par une feuille non gérée : {number} attribué.",
                "Vérifier la convention de numérotation du dossier.");
        }
        numbers.Add(number);
        s.SheetNumber = number;
        s.Name = RevitUtil.CleanName(desiredName);
        FillSheetParams(s, _data.Profile, lot: _norms.PlanLot ?? "ARCHITECTURE");
        var p = new ManagedProps();
        p.Values["nom"] = s.Name;
        Identity.Set(s, new IdentityData { Key = SheetKey(r.Level), Role = "plan_general", Generated = p.Serialize(), Payload = r.Level.Name });
        report.Created.Add($"Feuille {s.SheetNumber} {s.Name} ({_dce.FormatPlans})");
        return s;
    }

    /// <summary>Phase « DCE » et lot de la feuille (valeur de la convention du projet si elle existe).</summary>
    public static void FillSheetParams(ViewSheet s, PluginProfile profile, string phase = "DCE", string? lot = "ARCHITECTURE")
    {
        RevitUtil.TrySetString(s, profile.Parameter("feuille_phase"), phase);
        if (!string.IsNullOrEmpty(lot)) RevitUtil.TrySetString(s, profile.Parameter("feuille_lot"), lot);
    }

    /// <summary>Étiquette pièces, portes et fenêtres non encore étiquetées dans la vue.</summary>
    private void TagAll(ViewPlan view, Level level, Report report)
    {
        _doc.Regenerate();
        var scale = view.Scale;
        var roomTag = _res.RoomTagForScale(scale);
        // Toutes les étiquettes propres à la vue, y compris hors cadrage (le collecteur de vue ne les voit pas)
        var tagged = new HashSet<ElementId>(new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_RoomTags)
            .OfType<RoomTag>().Where(t => t.OwnerViewId == view.Id).Select(t => t.TaggedLocalRoomId));
        int n = 0;
        foreach (var room in RevitUtil.Rooms(_doc).Where(r => r.LevelId == level.Id && RevitUtil.IsEnclosed(r)))
        {
            if (tagged.Contains(room.Id)) continue;
            var pt = ((LocationPoint)room.Location).Point;
            var tag = _doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(pt.X, pt.Y), view.Id);
            if (tag != null && roomTag != ElementId.InvalidElementId) tag.ChangeTypeId(roomTag);
            n++;
        }
        n += TagCategory(view, level, BuiltInCategory.OST_Doors, _res.DoorTagForScale(scale));
        n += TagCategory(view, level, BuiltInCategory.OST_Windows, _res.WindowTagForScale(scale));
        if (n > 0) report.Created.Add($"{n} étiquette(s) dans « {view.Name} »");
    }

    internal int TagCategory(View view, Level level, BuiltInCategory cat, ElementId tagType)
    {
        if (tagType == ElementId.InvalidElementId) return 0;
        var already = new HashSet<ElementId>(new FilteredElementCollector(_doc).OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>().Where(t => t.OwnerViewId == view.Id).SelectMany(t => t.GetTaggedLocalElementIds()));
        int n = 0;
        foreach (var fi in new FilteredElementCollector(_doc, view.Id).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
        {
            if (fi.SuperComponent != null || fi.LevelId != level.Id || already.Contains(fi.Id) || fi.Location is not LocationPoint lp) continue;
            IndependentTag.Create(_doc, tagType, view.Id, new Reference(fi), false, TagOrientation.Horizontal, lp.Point);
            n++;
        }
        return n;
    }
}
