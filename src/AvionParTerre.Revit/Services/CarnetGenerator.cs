using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

/// <summary>
/// Carnets de pièces (module central, §8) : pour chaque pièce, plan agrandi, vue 3D découpée,
/// tableau de pièce et quatre élévations intérieures, composés sur des pages A3 Dn.00, Dn.01…
/// </summary>
internal sealed class CarnetGenerator
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly DceProfile _dce;
    private readonly KdResources _res;
    private readonly CarnetRules _rules;

    public CarnetGenerator(Document doc, PluginData data, DceProfile dce)
    {
        _doc = doc;
        _data = data;
        _dce = dce;
        _rules = data.Profile.Carnets;
        _res = new KdResources(doc, data);
    }

    public static string BaseKey(Room r) => $"CARNET|{r.UniqueId}";
    private static string PageKey(Room r, int page) => $"{BaseKey(r)}|PAGE|{page}";

    /// <summary>Groupe existant d'une pièce, lu sur sa page .00.</summary>
    public static int? ExistingGroup(Dictionary<string, Element> idx, Room r)
    {
        if (!idx.TryGetValue(PageKey(r, 0), out var e)) return null;
        var d = Identity.Get(e);
        return int.TryParse(d?.Payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : null;
    }

    private static int NextGroup(Document doc) =>
        Identity.All(doc).Where(x => x.Data.Role == "carnet_page")
            .Select(x => int.TryParse(x.Data.Payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0)
            .DefaultIfEmpty(0).Max() + 1;

    public string Preview(IEnumerable<Room> rooms)
    {
        var idx = Identity.Index(_doc);
        var next = NextGroup(_doc);
        var lines = new List<string>();
        foreach (var r in rooms)
        {
            var g = ExistingGroup(idx, r);
            if (!RevitUtil.IsEnclosed(r)) { lines.Add($"{RevitUtil.RoomLabel(r)} : pièce non placée ou non fermée — ignorée"); continue; }
            lines.Add(g.HasValue
                ? $"{RevitUtil.RoomLabel(r)} : carnet D{g} existant — vues manquantes recréées, retouches conservées"
                : $"{RevitUtil.RoomLabel(r)} : nouveau carnet D{next++} (plan, 3D, tableau, 4 élévations) sur A3");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public void Run(IReadOnlyList<Room> rooms, Report report, WarningCollector warnings)
    {
        var tb = _res.TitleBlock(_dce.FormatCarnets);
        if (tb == ElementId.InvalidElementId)
        {
            report.Issue(Severity.Bloquant, "ressource_kd", $"Cartouche {_dce.FormatCarnets}", "Aucun cartouche disponible pour les carnets.", "Charger un cartouche A3 K&D ou en choisir un dans Paramètres > Ressources.");
            report.Issues.AddRange(_res.Missing);
            return;
        }
        _res.PrepareViewTitles(report.Created, warnings);
        var next = NextGroup(_doc);
        foreach (var room in rooms)
        {
            if (!RevitUtil.IsEnclosed(room))
            {
                report.Issue(Severity.Bloquant, "piece_non_fermee", RevitUtil.RoomLabel(room),
                    "Pièce non placée ou non fermée : carnet impossible.", "Placer et fermer la pièce.", room.Id.Value);
                continue;
            }
            using var t = WarningCollector.Start(_doc, $"Avion par terre — carnet {RevitUtil.RoomLabel(room)}", warnings);
            try
            {
                var idx = Identity.Index(_doc);
                var group = ExistingGroup(idx, room) ?? next++;
                BuildGroup(room, group, tb, idx, report);
                CheckRoomData(room, group, report);
                t.Commit();
            }
            catch (Exception ex)
            {
                t.RollBack();
                report.Issue(Severity.Bloquant, "erreur", RevitUtil.RoomLabel(room), ex.Message,
                    "Corriger puis relancer : le carnet de cette pièce n'a pas été conservé.", room.Id.Value);
            }
        }
        report.Issues.AddRange(_res.Missing);
    }

    private sealed class RoomFrame
    {
        public XYZ U = XYZ.BasisX, V = XYZ.BasisY, Center = XYZ.Zero;
        public double W, D, H, Z0;
        public BoundingBoxXYZ WorldBox = new();
    }

    private static RoomFrame Frame(Room room)
    {
        var pts = RevitUtil.BoundaryPoints(room) ?? new List<XYZ>();
        var u = RevitUtil.MainDirection(room);
        var v = XYZ.BasisZ.CrossProduct(u);
        var bb = room.get_BoundingBox(null);
        var z0 = bb?.Min.Z ?? 0;
        if (pts.Count == 0 && bb != null) pts = new List<XYZ> { bb.Min, bb.Max, new(bb.Min.X, bb.Max.Y, 0), new(bb.Max.X, bb.Min.Y, 0) };
        double minU = pts.Min(p => p.DotProduct(u)), maxU = pts.Max(p => p.DotProduct(u));
        double minV = pts.Min(p => p.DotProduct(v)), maxV = pts.Max(p => p.DotProduct(v));
        var c = u * ((minU + maxU) / 2) + v * ((minV + maxV) / 2);
        return new RoomFrame
        {
            U = u, V = v,
            Center = new XYZ(c.X, c.Y, z0),
            W = maxU - minU, D = maxV - minV, H = RevitUtil.RoomHeight(room), Z0 = z0,
            WorldBox = new BoundingBoxXYZ
            {
                Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), z0),
                Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), z0 + RevitUtil.RoomHeight(room)),
            },
        };
    }

    private void BuildGroup(Room room, int group, ElementId tb, Dictionary<string, Element> idx, Report report)
    {
        var f = Frame(room);
        var roomName = RevitUtil.RoomName(room);
        var label = $"D{group} - {roomName}";
        var n = _data.Profile.Numerotation;
        var composer = new SheetComposer(_doc, _data.Profile.MiseEnPage, _res.ViewportTypeWithTitle());

        // Pages : existantes réutilisées, nouvelles créées à la demande
        int existingPages = 0;
        while (idx.ContainsKey(PageKey(room, existingPages))) existingPages++;
        bool isNew = existingPages == 0;

        ViewSheet Page(int k)
        {
            if (idx.TryGetValue(PageKey(room, k), out var e) && e is ViewSheet s0) return s0;
            var s = ViewSheet.Create(_doc, tb);
            var number = n.PrefixeBatiment + Numbering.Format(n.CarnetPage, ("groupe", group), ("page", k));
            var numbers = RevitUtil.SheetNumbers(_doc, s);
            if (numbers.Contains(number))
            {
                var wanted = number;
                for (int i = 1; numbers.Contains(number); i++) number = wanted + "-" + i;
                report.Issue(Severity.ARevoir, "numerotation", wanted, $"Numéro déjà utilisé : {number} attribué.", "Vérifier la numérotation des carnets.");
            }
            s.SheetNumber = number;
            s.Name = RevitUtil.CleanName(TextNorm.UpperTitle($"CARNET D{group} - {roomName}") + (k > 0 ? " - ELEVATIONS" : ""));
            PlanGenerator.FillSheetParams(s, _data.Profile);
            Identity.Set(s, new IdentityData { Key = PageKey(room, k), Role = "carnet_page", Payload = group.ToString(CultureInfo.InvariantCulture) });
            idx[PageKey(room, k)] = s;
            report.Created.Add($"Feuille {s.SheetNumber} {s.Name}");
            return s;
        }

        // Zone utile pour le choix d'échelle (mesurée sur la page .00)
        var area = composer.DrawingArea(Page(0));
        double labelMm = 15, sp = _data.Profile.MiseEnPage.EspacementVues;
        var scales = _dce.EchellesCarnets.OrderBy(x => x).ToList();
        int? Fit(double wFt, double hFt, double maxW, double maxH, IEnumerable<int> candidates) =>
            candidates.Cast<int?>().FirstOrDefault(s => Units.ToMm(wFt) / s!.Value <= maxW && Units.ToMm(hFt) / s.Value + labelMm <= maxH);

        double off = Units.Mm(_rules.DecalageCadrageMm);
        var planW = f.WorldBox.Max.X - f.WorldBox.Min.X + 2 * off;
        var planH = f.WorldBox.Max.Y - f.WorldBox.Min.Y + 2 * off;
        // Plan : laisser si possible la moitié droite à la 3D et au tableau (recette AT « plan + tableau + 3D »)
        var planScale = Fit(planW, planH, area.Width * 0.55, area.Height, scales)
                        ?? Fit(planW, planH, area.Width, area.Height, scales) ?? scales.Max();
        // Élévations : 2 × 2 sur une page, sinon 2 par page, sinon une par page
        var elevW = Math.Max(f.W, f.D) + 2 * Units.Mm(300);
        var elevH = f.H + Units.Mm(400);
        var elevScale = Fit(elevW, elevH, (area.Width - sp) / 2, (area.Height - sp) / 2, scales)
                        ?? Fit(elevW, elevH, area.Width, (area.Height - sp) / 2, scales)
                        ?? Fit(elevW, elevH, area.Width, area.Height, scales) ?? scales.Max();
        if (Units.ToMm(planW) / planScale > area.Width || Units.ToMm(elevW) / elevScale > area.Width)
            report.Issue(Severity.ARevoir, "echelle", label, "Pièce trop grande pour l'A3 aux échelles autorisées.",
                "Découper la pièce en zones ou autoriser une échelle supplémentaire dans le profil.", room.Id.Value);
        var planWidthMm = Units.ToMm(planW) / planScale;

        var items = new List<SheetItem>();
        View? Existing(string key) => idx.TryGetValue(key, out var e) ? e as View : null;

        // Plan agrandi
        var planKey = $"{BaseKey(room)}|PLAN";
        var plan = Existing(planKey) as ViewPlan;
        if (plan == null)
        {
            plan = ViewPlan.Create(_doc, _res.ViewFamilyType("type_vue_plan", ViewFamily.FloorPlan), room.LevelId);
            ApplyTemplate(plan, _res.ViewTemplate("gabarit_plan_piece"));
            plan.Scale = planScale;
            var cb = plan.CropBox;
            cb.Min = new XYZ(f.WorldBox.Min.X - off, f.WorldBox.Min.Y - off, cb.Min.Z);
            cb.Max = new XYZ(f.WorldBox.Max.X + off, f.WorldBox.Max.Y + off, cb.Max.Z);
            plan.CropBox = cb;
            plan.CropBoxActive = true;
            plan.CropBoxVisible = false;
            plan.Name = RevitUtil.UniqueViewName(_doc, $"{label} - Plan");
            RevitUtil.SetTitleOnSheet(plan, $"D{group} - PLAN");
            RevitUtil.CropAnnotations(plan);
            Identity.Set(plan, new IdentityData { Key = planKey, Role = "carnet_plan" });
            _doc.Regenerate();
            TagPlan(plan, room);
            report.Created.Add($"Vue « {plan.Name} » (1:{plan.Scale})");
            items.Add(new SheetItem { Key = "plan", View = plan });
        }
        else report.Unchanged.Add($"Vue « {plan.Name} »");

        // Vue 3D découpée
        var key3d = $"{BaseKey(room)}|3D";
        if (Existing(key3d) is not View3D v3)
        {
            v3 = View3D.CreateIsometric(_doc, _res.ViewFamilyType("type_vue_3d", ViewFamily.ThreeDimensional));
            ApplyTemplate(v3, _res.ViewTemplate("gabarit_3d"));
            v3.Scale = planScale;
            var m = Units.Mm(300);
            var tr = Transform.Identity;
            tr.Origin = f.Center;
            tr.BasisX = f.U;
            tr.BasisY = f.V;
            tr.BasisZ = XYZ.BasisZ;
            // Coupe juste sous le plafond pour montrer l'intérieur
            v3.SetSectionBox(new BoundingBoxXYZ
            {
                Transform = tr,
                Min = new XYZ(-f.W / 2 - m, -f.D / 2 - m, -Units.Mm(100)),
                Max = new XYZ(f.W / 2 + m, f.D / 2 + m, f.H - Units.Mm(50)),
            });
            var eye = f.Center + (-f.U - f.V + XYZ.BasisZ * 1.2).Multiply(Units.Mm(30000));
            var fwd = (f.Center + XYZ.BasisZ * (f.H / 3) - eye).Normalize();
            var up = (XYZ.BasisZ - fwd.Multiply(XYZ.BasisZ.DotProduct(fwd))).Normalize();
            v3.SetOrientation(new ViewOrientation3D(eye, up, fwd));
            FitCropToSectionBox(v3);
            // Échelle de la 3D : tenir à côté du plan, sous le tableau (la 3D n'affiche pas d'échelle conventionnelle)
            var cb3 = v3.CropBox;
            double w3 = cb3.Max.X - cb3.Min.X, h3 = cb3.Max.Y - cb3.Min.Y;
            var s3Candidates = scales.Concat(new[] { 60, 75, 100, 150 }).Where(x => x >= planScale).Distinct().OrderBy(x => x);
            v3.Scale = Fit(w3, h3, (area.Width - planWidthMm * 1.15 - sp) * 0.9, area.Height * 0.7, s3Candidates)
                       ?? Fit(w3, h3, area.Width, area.Height, s3Candidates) ?? 100;
            v3.Name = RevitUtil.UniqueViewName(_doc, $"{label} - 3D");
            RevitUtil.SetTitleOnSheet(v3, $"D{group} - VUE 3D");
            Identity.Set(v3, new IdentityData { Key = key3d, Role = "carnet_3d" });
            report.Created.Add($"Vue « {v3.Name} »");
            items.Add(new SheetItem { Key = "3d", View = v3 });
        }
        else report.Unchanged.Add($"Vue « {v3.Name} »");

        // Tableau de pièce
        var tabKey = $"{BaseKey(room)}|TAB";
        if (Existing(tabKey) is not ViewSchedule tab)
        {
            tab = RoomSchedule(room, $"{label} - Tableau");
            Identity.Set(tab, new IdentityData { Key = tabKey, Role = "carnet_tableau" });
            report.Created.Add($"Nomenclature « {tab.Name} »");
            items.Add(new SheetItem { Key = "tab", Schedule = tab });
        }
        else report.Unchanged.Add($"Nomenclature « {tab.Name} »");

        // Élévations intérieures a, b, c, d (sens horaire depuis +V)
        var looks = new[] { f.V, f.U, f.V.Negate(), f.U.Negate() };
        var widths = new[] { f.W, f.D, f.W, f.D };
        var depths = new[] { f.D, f.W, f.D, f.W };
        for (int i = 0; i < 4; i++)
        {
            var letter = Numbering.Letter(i);
            var ekey = $"{BaseKey(room)}|E|{letter}";
            if (Existing(ekey) is ViewSection existingElev) { report.Unchanged.Add($"Vue « {existingElev.Name} »"); continue; }
            var elevName = Numbering.Format(n.CarnetElevation, ("groupe", group), ("lettre", letter));
            var ev = InteriorElevation(f, looks[i], widths[i], depths[i]);
            ApplyTemplate(ev, _res.ViewTemplate("gabarit_elevation_piece"));
            ev.Scale = elevScale;
            var coarser = ev.get_Parameter(BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_METRIC);
            if (coarser != null && !coarser.IsReadOnly && coarser.StorageType == StorageType.Integer) coarser.Set(_rules.MasquerReperesAuDelaDe);
            ev.Name = RevitUtil.UniqueViewName(_doc, $"D{group} - {elevName}");
            RevitUtil.SetTitleOnSheet(ev, elevName);
            RevitUtil.CropAnnotations(ev);
            Identity.Set(ev, new IdentityData { Key = ekey, Role = "carnet_elevation" });
            report.Created.Add($"Vue « {ev.Name} » (1:{ev.Scale})");
            items.Add(new SheetItem { Key = "E" + letter, View = ev, StartNewPage = i == 0 });
        }

        if (items.Count == 0) return;
        // Ordre de composition : plan, tableau, 3D, puis élévations (saut de page avant « a »)
        string[] order = { "plan", "tab", "3d" };
        items = items.OrderBy(i => { var k = Array.IndexOf(order, i.Key); return k >= 0 ? k : order.Length; })
            .ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        _doc.Regenerate();
        // Nouveau carnet : composition complète. Relance : vues recréées placées sur une page supplémentaire.
        int offset = isNew ? 0 : existingPages;
        var res = composer.Compose(items, k => Page(k + offset), tryCompact: isNew);
        // Titre des pages selon leur contenu réel
        foreach (var g in res.PageOfItem.GroupBy(kv => kv.Value))
        {
            if (g.Key + offset == 0) continue;
            var hasElev = g.Any(kv => kv.Key.StartsWith("E", StringComparison.Ordinal));
            var sheetK = Page(g.Key + offset);
            sheetK.Name = RevitUtil.CleanName(TextNorm.UpperTitle($"CARNET D{group} - {roomName}") + (hasElev ? " - ELEVATIONS" : " - SUITE"));
        }
        var recipe = !isNew ? "complément" : res.PageCount == 1 ? "compact" : res.PageCount == 2 ? "standard" : "étendu";
        report.Updated.Add($"Carnet D{group} {roomName} : {res.PageCount} page(s) ajoutée(s), composition {recipe}");
        if (!isNew)
            report.Issue(Severity.ARevoir, "recomposition", label, "Vues recréées placées sur une page complémentaire.",
                "Vérifier la composition du carnet et déplacer les vues si nécessaire.", room.Id.Value);
        foreach (var o in res.Oversize)
            report.Issue(Severity.ARevoir, "debordement", $"{label} / {o}", "Vue plus grande que la zone de dessin A3.",
                "Recadrer la vue ou changer d'échelle parmi celles autorisées.", room.Id.Value);
    }

    /// <summary>Coupe d'élévation intérieure depuis le centre de la pièce, regardant vers <paramref name="look"/>.</summary>
    private ViewSection InteriorElevation(RoomFrame f, XYZ look, double width, double depth)
    {
        var tr = Transform.Identity;
        tr.Origin = f.Center;
        tr.BasisZ = look;
        tr.BasisY = XYZ.BasisZ;
        tr.BasisX = XYZ.BasisZ.CrossProduct(look);
        double m = Units.Mm(300);
        var box = new BoundingBoxXYZ
        {
            Transform = tr,
            Min = new XYZ(-width / 2 - m, -Units.Mm(200), 0),
            Max = new XYZ(width / 2 + m, f.H + Units.Mm(200), depth / 2 + Units.Mm(_rules.ProfondeurElevationMargeMm)),
        };
        return ViewSection.CreateSection(_doc, _res.ViewFamilyType("type_vue_coupe", ViewFamily.Section), box);
    }

    /// <summary>Cadre la vue 3D sur la projection de sa zone de coupe (Revit ne le fait pas via l'API).</summary>
    private void FitCropToSectionBox(View3D v)
    {
        _doc.Regenerate();
        var sbox = v.GetSectionBox();
        var cb = v.CropBox;
        var toView = cb.Transform.Inverse;
        var corners = new List<XYZ>();
        foreach (var x in new[] { sbox.Min.X, sbox.Max.X })
            foreach (var y in new[] { sbox.Min.Y, sbox.Max.Y })
                foreach (var z in new[] { sbox.Min.Z, sbox.Max.Z })
                    corners.Add(toView.OfPoint(sbox.Transform.OfPoint(new XYZ(x, y, z))));
        var m = Units.Mm(200);
        cb.Min = new XYZ(corners.Min(p => p.X) - m, corners.Min(p => p.Y) - m, cb.Min.Z);
        cb.Max = new XYZ(corners.Max(p => p.X) + m, corners.Max(p => p.Y) + m, cb.Max.Z);
        v.CropBox = cb;
        v.CropBoxActive = true;
        v.CropBoxVisible = false;
    }

    private static void ApplyTemplate(View v, ElementId tpl)
    {
        // Application ponctuelle des réglages du gabarit K&D : l'échelle et le cadrage restent pilotables par vue.
        if (tpl != ElementId.InvalidElementId && v.IsValidViewTemplate(tpl))
            v.ApplyViewTemplateParameters((View)v.Document.GetElement(tpl));
    }

    private void TagPlan(ViewPlan plan, Room room)
    {
        var pt = ((LocationPoint)room.Location).Point;
        var tag = _doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(pt.X, pt.Y), plan.Id);
        var tt = _res.RoomTagDetail();
        if (tag != null && tt != ElementId.InvalidElementId) tag.ChangeTypeId(tt);
        var pg = new PlanGenerator(_doc, _data, _dce);
        var level = (Level)_doc.GetElement(room.LevelId);
        pg.TagCategory(plan, level, BuiltInCategory.OST_Doors, _res.DoorTagForScale(50));
        pg.TagCategory(plan, level, BuiltInCategory.OST_Windows, _res.WindowTagForScale(50));
    }

    private ViewSchedule RoomSchedule(Room room, string name)
    {
        var vs = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Rooms));
        vs.Name = RevitUtil.UniqueViewName(_doc, name);
        var p = _data.Profile;
        var num = ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_NUMBER);
        ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_NAME);
        ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_AREA);
        ScheduleUtil.AddNamed(_doc, vs, p.Parameter("piece_niv"));
        ScheduleUtil.AddNamed(_doc, vs, p.Parameter("piece_hsp"));
        ScheduleUtil.AddNamed(_doc, vs, p.Parameter("piece_hsd"));
        ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_FINISH_FLOOR);
        ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_FINISH_WALL);
        ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_FINISH_CEILING);
        var lvl = ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ROOM_LEVEL_ID);
        // Largeurs de colonnes (mm papier) : tableau lisible sur A3
        var def = vs.Definition;
        for (int i = 0; i < def.GetFieldCount(); i++)
        {
            var fld = def.GetField(i);
            var pid = fld.ParameterId.Value;
            double mm = pid == (long)BuiltInParameter.ROOM_NUMBER ? 13
                : pid == (long)BuiltInParameter.ROOM_NAME ? 24
                : pid == (long)BuiltInParameter.ROOM_AREA ? 15
                : pid is (long)BuiltInParameter.ROOM_FINISH_FLOOR or (long)BuiltInParameter.ROOM_FINISH_WALL or (long)BuiltInParameter.ROOM_FINISH_CEILING ? 30
                : 10;
            fld.GridColumnWidth = Units.Mm(mm);
        }
        if (num != null) vs.Definition.AddFilter(new ScheduleFilter(num.FieldId, ScheduleFilterType.Equal, room.Number));
        if (lvl != null)
        {
            lvl.IsHidden = true;
            vs.Definition.AddFilter(new ScheduleFilter(lvl.FieldId, ScheduleFilterType.Equal, room.LevelId));
        }
        return vs;
    }

    private void CheckRoomData(Room room, int group, Report report)
    {
        var p = _data.Profile;
        var missing = new List<string>();
        foreach (var (label, key) in new[] { ("Niv", "piece_niv"), ("HSP", "piece_hsp"), ("HSD", "piece_hsd") })
            if (string.IsNullOrWhiteSpace(RevitUtil.ParamText(room, p.Parameter(key)))) missing.Add(label);
        foreach (var (label, bip) in new[] { ("Sol", BuiltInParameter.ROOM_FINISH_FLOOR), ("Mur", BuiltInParameter.ROOM_FINISH_WALL), ("Plfd", BuiltInParameter.ROOM_FINISH_CEILING) })
            if (string.IsNullOrWhiteSpace(room.get_Parameter(bip)?.AsString())) missing.Add(label);
        if (missing.Count > 0)
            report.Issue(Severity.EmissionBloquee, "donnees_piece", $"D{group} {RevitUtil.RoomLabel(room)}",
                $"Données de pièce manquantes : {string.Join(", ", missing)}.",
                "Compléter (Finitions pour Sol/Mur/Plfd ; Niv, HSP, HSD validés) — aucune valeur n'est inventée.", room.Id.Value);
    }
}
