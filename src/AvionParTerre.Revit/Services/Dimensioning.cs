using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Profiles;

namespace AvionParTerre.Revit.Services;

/// <summary>Côté de pièce : segments de contour colinéaires consécutifs (un mur coupé par une cloison reste un seul côté).</summary>
internal sealed class RoomSide
{
    public XYZ Start { get; set; } = XYZ.Zero;
    public XYZ End { get; set; } = XYZ.Zero;
    public bool IsLine { get; init; } = true;
    public XYZ Inward { get; set; } = XYZ.Zero;
    public List<ElementId> Elements { get; } = new();
    public XYZ Dir => (End - Start).Normalize();
    public double Length => Start.DistanceTo(End);
    public XYZ Mid => (Start + End) / 2;
}

/// <summary>
/// Cotation automatique à la manière des planches de l'agence : dans les carnets, largeurs des côtés de la pièce, chaînes de cotes
/// des baies (plan) et largeur, baies, allèges et hauteur sous plafond (élévations) ; dans les fiches, largeur réservation, hauteur,
/// hauteur de poignée et annotations de quincaillerie avec lignes de repère. Les cotes s'accrochent aux faces des murs, des sols et
/// des plafonds et aux plans de référence des familles (gauche, droite, haut, bas) : elles suivent la maquette.
/// </summary>
internal sealed class Dimensioning
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly DimensionRules _rules;
    private Dictionary<ElementId, List<FamilyInstance>>? _openings;
    private readonly Dictionary<string, ElementId> _types = new();

    public int Created { get; private set; }
    public int Failed { get; private set; }
    /// <summary>Éléments créés par le dernier appel (cotes, notes, repères de poignée) : supprimés si la vue change d'échelle.</summary>
    public List<ElementId> Last { get; } = new();

    public Dimensioning(Document doc, PluginData data)
    {
        _doc = doc;
        _data = data;
        _rules = data.Profile.Cotation;
    }

    // -----------------------------------------------------------------------------------------
    // Types de cotes : type K&D du profil, sinon « Avion par terre - Cote cm / mm » en Century Gothic
    // -----------------------------------------------------------------------------------------

    public ElementId DimensionType(string key, string unit, List<string>? created)
    {
        if (_types.TryGetValue(key, out var cached)) return cached;
        var types = new FilteredElementCollector(_doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
            .Where(t => t.StyleType == DimensionStyleType.Linear).ToList();
        var found = _data.Profile.Resource(key).Select(n => types.FirstOrDefault(t => RevitUtil.SameName(t.Name, n))).FirstOrDefault(t => t != null);
        if (found == null && _data.Choices.Ressources.TryGetValue(key, out var ov))
            found = types.FirstOrDefault(t => RevitUtil.SameName(t.Name, ov));
        if (found == null)
        {
            var name = $"Avion par terre - Cote {unit}";
            found = types.FirstOrDefault(t => t.Name == name);
            if (found == null && _doc.GetElement(_doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType)) is DimensionType model)
            {
                found = (DimensionType)model.Duplicate(name);
                found.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(KdResources.Font);
                found.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(Units.Mm(_rules.TexteMm));
                try
                {
                    var fo = new FormatOptions(unit == "mm" ? UnitTypeId.Millimeters : UnitTypeId.Centimeters) { UseDefault = false, Accuracy = 1 };
                    found.SetUnitsFormatOptions(fo);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Unités du projet conservées
                }
                created?.Add($"Type de cote « {name} » ({KdResources.Font} {_rules.TexteMm:0.#} mm, en {unit})");
            }
        }
        var id = found?.Id ?? ElementId.InvalidElementId;
        _types[key] = id;
        return id;
    }

    /// <summary>Une cote impossible (géométrie inattendue) ne doit jamais faire échouer la feuille : elle est comptée et signalée.</summary>
    private void Safe(Action a)
    {
        Last.Clear();
        try { a(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { Failed++; }
    }

    // -----------------------------------------------------------------------------------------
    // Pièces
    // -----------------------------------------------------------------------------------------

    /// <summary>Côtés de la pièce (contour extérieur, nu fini), avec leur normale vers l'intérieur.</summary>
    public static List<RoomSide> Sides(Room room)
    {
        var sides = new List<RoomSide>();
        var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
        if (loops == null || loops.Count == 0) return sides;
        var loop = loops.OrderByDescending(l => l.Sum(s => s.GetCurve().Length)).First();
        foreach (var seg in loop)
        {
            var c = seg.GetCurve();
            if (c.Length < Units.Mm(1)) continue;
            XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
            var last = sides.Count > 0 ? sides[^1] : null;
            if (c is Line && last != null && Collinear(last, a, b))
            {
                last.End = b;
                if (!last.Elements.Contains(seg.ElementId)) last.Elements.Add(seg.ElementId);
                continue;
            }
            var s = new RoomSide { Start = a, End = b, IsLine = c is Line };
            s.Elements.Add(seg.ElementId);
            sides.Add(s);
        }
        if (sides.Count > 3 && Collinear(sides[^1], sides[0].Start, sides[0].End))
        {
            sides[^1].End = sides[0].End;
            sides[^1].Elements.AddRange(sides[0].Elements.Where(e => !sides[^1].Elements.Contains(e)));
            sides.RemoveAt(0);
        }
        var z = (room.get_BoundingBox(null)?.Min.Z ?? 0) + Units.Mm(1000);
        foreach (var s in sides)
        {
            var n = XYZ.BasisZ.CrossProduct(s.Dir);
            var probe = s.Mid + n * Units.Mm(150);
            s.Inward = room.IsPointInRoom(new XYZ(probe.X, probe.Y, z)) ? n : n.Negate();
        }
        return sides;
    }

    private static bool Collinear(RoomSide s, XYZ a, XYZ b)
    {
        if (!s.IsLine || (a - s.End).GetLength() > Units.Mm(5) || (b - a).GetLength() < 1e-9) return false;
        var d = (b - a).Normalize();
        return d.CrossProduct(s.Dir).GetLength() < 1e-3 && (b - s.Start).CrossProduct(s.Dir).GetLength() < Units.Mm(5);
    }

    /// <summary>Plan de pièce : largeur de chaque axe principal et chaînes de cotes des côtés percés de baies.</summary>
    public void RoomPlan(ViewPlan plan, Room room, List<string>? created) => Safe(() => RoomPlanCore(plan, room, created));

    private void RoomPlanCore(ViewPlan plan, Room room, List<string>? created)
    {
        if (!_rules.Carnets) return;
        var sides = Sides(room);
        if (sides.Count < 3) return;
        var type = DimensionType("type_cote_piece", _rules.UniteCarnets, created);
        var (z0, zTop) = RoomRange(room);
        var off = Units.Mm(_rules.DecalageMm) * plan.Scale;
        var u = RevitUtil.MainDirection(room);
        var v = XYZ.BasisZ.CrossProduct(u);
        RoomSide? Longest(XYZ axis) => sides.Where(s => s.IsLine && Math.Abs(s.Dir.DotProduct(axis)) > 0.99).OrderByDescending(s => s.Length).FirstOrDefault();
        var overall = new[] { Longest(u), Longest(v) }.Where(s => s != null).ToHashSet();
        for (int i = 0; i < sides.Count; i++)
        {
            var s = sides[i];
            if (!s.IsLine || s.Length < Units.Mm(300)) continue;
            var (a, b) = Ends(sides, i, s.Dir);
            var openings = OpeningsOn(s, z0, zTop).ToList();
            Line At(int k) => Line.CreateBound(s.Start + s.Inward * (off * k), s.End + s.Inward * (off * k));
            int row = 1;
            if (openings.Count > 0)
            {
                var refs = new List<(Reference, double)>();
                if (a != null) refs.Add(a.Value);
                if (b != null) refs.Add(b.Value);
                foreach (var fi in openings) refs.AddRange(WidthRefs(fi, s.Dir));
                if (Create(plan, At(row), refs, type) != null) row++;
            }
            if (overall.Contains(s) && a != null && b != null)
                Create(plan, At(row), new List<(Reference, double)> { a.Value, b.Value }, type);
        }
    }

    /// <summary>
    /// Élévation intérieure : largeur du mur vu (chaîne des baies puis largeur totale, sous la vue), hauteurs (allèges et hauteurs
    /// de baies, puis hauteur sous plafond, à gauche de la vue).
    /// </summary>
    public void RoomElevation(ViewSection ev, Room room, List<string>? created) => Safe(() => RoomElevationCore(ev, room, created));

    private void RoomElevationCore(ViewSection ev, Room room, List<string>? created)
    {
        if (!_rules.Carnets) return;
        var sides = Sides(room);
        var look = new XYZ(-ev.ViewDirection.X, -ev.ViewDirection.Y, 0);
        if (sides.Count < 3 || look.IsZeroLength()) return;
        look = look.Normalize();
        var bb = room.get_BoundingBox(null);
        if (bb == null) return;
        var center = (bb.Min + bb.Max) / 2;
        // Mur vu : normale intérieure opposée à la direction du regard, situé devant le centre de la pièce
        var s = sides.Where(x => x.IsLine && x.Inward.DotProduct(look) < -0.95 && (x.Mid - center).DotProduct(look) > 0)
            .OrderByDescending(x => x.Length).FirstOrDefault();
        if (s == null) return;
        var idx = sides.IndexOf(s);
        var type = DimensionType("type_cote_piece", _rules.UniteCarnets, created);
        var (z0, zTop) = RoomRange(room);
        var cb = ev.CropBox;
        var t = cb.Transform;
        var off = Units.Mm(_rules.DecalageMm) * ev.Scale;
        var openings = OpeningsOn(s, z0, zTop).ToList();

        // Horizontal, sous la vue
        var hDir = t.BasisX;
        Line H(int k) => Line.CreateBound(t.OfPoint(new XYZ(cb.Min.X, cb.Min.Y - off * k, 0)), t.OfPoint(new XYZ(cb.Max.X, cb.Min.Y - off * k, 0)));
        var (a, b) = Ends(sides, idx, hDir);
        int row = 1;
        if (openings.Count > 0)
        {
            var refs = new List<(Reference, double)>();
            if (a != null) refs.Add(a.Value);
            if (b != null) refs.Add(b.Value);
            foreach (var fi in openings) refs.AddRange(WidthRefs(fi, hDir));
            if (Create(ev, H(row), refs, type) != null) row++;
        }
        if (a != null && b != null) Create(ev, H(row), new List<(Reference, double)> { a.Value, b.Value }, type);

        // Vertical, à gauche de la vue : sol fini, baies, plafond
        var vDir = t.BasisY;
        Line V(int k) => Line.CreateBound(t.OfPoint(new XYZ(cb.Min.X - off * k, cb.Min.Y, 0)), t.OfPoint(new XYZ(cb.Min.X - off * k, cb.Max.Y, 0)));
        var p = (room.Location as LocationPoint)?.Point ?? center;
        var floor = HostFace(p, z0, BuiltInCategory.OST_Floors, top: true, z0 - Units.Mm(300), z0 + Units.Mm(300));
        var ceiling = HostFace(p, z0, BuiltInCategory.OST_Ceilings, top: false, z0 + Units.Mm(1500), zTop + Units.Mm(300))
                      ?? HostFace(p, z0, BuiltInCategory.OST_Floors, top: false, z0 + Units.Mm(1500), zTop + Units.Mm(600));
        double Pos(double z) => new XYZ(p.X, p.Y, z).DotProduct(vDir);
        row = 1;
        if (floor != null)
        {
            var heights = openings.SelectMany(fi => HeightRefs(fi)).Where(h => h.Z > floor.Value.Z + Units.Mm(30)).ToList();
            if (heights.Count > 0)
            {
                var refs = new List<(Reference, double)> { (floor.Value.Ref, Pos(floor.Value.Z)) };
                refs.AddRange(heights.Select(h => (h.Ref, Pos(h.Z))));
                if (ceiling != null) refs.Add((ceiling.Value.Ref, Pos(ceiling.Value.Z)));
                if (Create(ev, V(row), refs, type) != null) row++;
            }
            if (ceiling != null)
                Create(ev, V(row), new List<(Reference, double)> { (floor.Value.Ref, Pos(floor.Value.Z)), (ceiling.Value.Ref, Pos(ceiling.Value.Z)) }, type);
        }
        EnsureAnnotationCrop(ev, left: 2 * _rules.DecalageMm + 8, bottom: 2 * _rules.DecalageMm + 8);
    }

    private static (double Z0, double ZTop) RoomRange(Room room)
    {
        var z0 = room.get_BoundingBox(null)?.Min.Z ?? 0;
        return (z0, z0 + RevitUtil.RoomHeight(room));
    }

    /// <summary>Références des extrémités d'un côté : faces des côtés voisins, perpendiculaires à la direction de cote.</summary>
    private ((Reference Ref, double Pos)? A, (Reference Ref, double Pos)? B) Ends(List<RoomSide> sides, int i, XYZ dimDir)
    {
        var n = sides.Count;
        var s = sides[i];
        var a = WallFace(sides[(i - 1 + n) % n], s.Start, dimDir);
        var b = WallFace(sides[(i + 1) % n], s.End, dimDir);
        return (a == null ? null : (a, s.Start.DotProduct(dimDir)), b == null ? null : (b, s.End.DotProduct(dimDir)));
    }

    /// <summary>Face latérale d'un mur du côté, passant par <paramref name="p"/> et perpendiculaire à <paramref name="dimDir"/>.</summary>
    private Reference? WallFace(RoomSide side, XYZ p, XYZ dimDir)
    {
        Reference? best = null;
        var dist = Units.Mm(30);
        foreach (var id in side.Elements)
        {
            if (_doc.GetElement(id) is not Wall w) continue;
            foreach (var shell in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(w, shell); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                foreach (var r in refs)
                {
                    if (w.GetGeometryObjectFromReference(r) is not PlanarFace f || Math.Abs(f.FaceNormal.DotProduct(dimDir)) < 0.99) continue;
                    var d = Math.Abs((p - f.Origin).DotProduct(f.FaceNormal));
                    if (d < dist) { dist = d; best = r; }
                }
            }
        }
        return best;
    }

    /// <summary>Face supérieure (sol) ou inférieure (plafond, dalle) au droit de <paramref name="p"/>, dans la plage d'altitudes donnée.</summary>
    private (Reference Ref, double Z)? HostFace(XYZ p, double z0, BuiltInCategory cat, bool top, double zMin, double zMax)
    {
        var box = new Outline(new XYZ(p.X - 0.5, p.Y - 0.5, zMin), new XYZ(p.X + 0.5, p.Y + 0.5, zMax));
        (Reference, double)? best = null;
        var bestD = double.MaxValue;
        foreach (var host in new FilteredElementCollector(_doc).OfCategory(cat).WhereElementIsNotElementType()
                     .WherePasses(new BoundingBoxIntersectsFilter(box)).OfType<HostObject>())
        {
            IList<Reference> refs;
            try { refs = top ? HostObjectUtils.GetTopFaces(host) : HostObjectUtils.GetBottomFaces(host); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            foreach (var r in refs)
            {
                if (host.GetGeometryObjectFromReference(r) is not PlanarFace f || Math.Abs(f.FaceNormal.Z) < 0.99) continue;
                var proj = f.Project(new XYZ(p.X, p.Y, f.Origin.Z));
                if (proj == null) continue;
                var z = f.Origin.Z;
                if (z < zMin || z > zMax) continue;
                // Sol : le plus proche du niveau de la pièce ; plafond : le plus bas
                var d = top ? Math.Abs(z - z0) : z;
                if (d < bestD) { bestD = d; best = (r, z); }
            }
        }
        return best;
    }

    private IEnumerable<FamilyInstance> OpeningsOn(RoomSide s, double z0, double zTop)
    {
        _openings ??= new FilteredElementCollector(_doc)
            .WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Doors), new ElementCategoryFilter(BuiltInCategory.OST_Windows)))
            .WhereElementIsNotElementType().OfType<FamilyInstance>()
            .Where(fi => fi.SuperComponent == null && fi.Host != null && fi.Location is LocationPoint)
            .GroupBy(fi => fi.Host.Id).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var id in s.Elements)
        {
            if (!_openings.TryGetValue(id, out var list)) continue;
            foreach (var fi in list)
            {
                var p = ((LocationPoint)fi.Location).Point;
                var t = (p - s.Start).DotProduct(s.Dir);
                var bb = fi.get_BoundingBox(null);
                if (t < -Units.Mm(10) || t > s.Length + Units.Mm(10) || bb == null || bb.Min.Z > zTop || bb.Max.Z < z0) continue;
                yield return fi;
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Références des menuiseries
    // -----------------------------------------------------------------------------------------

    public static double? OpeningWidth(FamilyInstance fi) =>
        Dim(fi, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.GENERIC_WIDTH);

    public static double? OpeningHeight(FamilyInstance fi) =>
        Dim(fi, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.GENERIC_HEIGHT);

    private static double? Dim(FamilyInstance fi, params BuiltInParameter[] bips)
    {
        foreach (var b in bips)
        {
            var p = fi.get_Parameter(b) ?? fi.Symbol.get_Parameter(b);
            if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 1e-6) return p.AsDouble();
        }
        return null;
    }

    /// <summary>Plans de référence gauche / droite de la famille, avec leur position estimée le long de <paramref name="dir"/>.</summary>
    private static IEnumerable<(Reference, double)> WidthRefs(FamilyInstance fi, XYZ dir)
    {
        var tr = fi.GetTransform();
        var w = OpeningWidth(fi);
        if (w == null) yield break;
        foreach (var (kind, k) in new[] { (FamilyInstanceReferenceType.Left, -1), (FamilyInstanceReferenceType.Right, 1) })
        {
            var r = fi.GetReferences(kind).FirstOrDefault();
            if (r != null) yield return (r, (tr.Origin + tr.BasisX * (k * w.Value / 2)).DotProduct(dir));
        }
    }

    /// <summary>Plans de référence bas / haut de la famille (allège, hauteur de baie).</summary>
    private static IEnumerable<(Reference Ref, double Z)> HeightRefs(FamilyInstance fi)
    {
        var bb = fi.get_BoundingBox(null);
        if (bb == null) yield break;
        var bottom = fi.GetReferences(FamilyInstanceReferenceType.Bottom).FirstOrDefault();
        var top = fi.GetReferences(FamilyInstanceReferenceType.Top).FirstOrDefault();
        if (bottom != null) yield return (bottom, bb.Min.Z);
        if (top != null) yield return (top, bb.Max.Z);
    }

    // -----------------------------------------------------------------------------------------
    // Fiches menuiseries
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Élévation d'une fiche : largeur (sous la vue), hauteur et hauteur de poignée (à gauche), annotations de la règle du lot
    /// avec lignes de repère (à droite). Les éléments créés sont listés dans <see cref="Last"/>.
    /// </summary>
    public void JoineryElevation(ViewSection ev, FamilyInstance fi, JoineryAnnotationRule? rule, List<string>? created, Func<ElementId> noteType) => Safe(() => JoineryElevationCore(ev, fi, rule, created, noteType));

    private void JoineryElevationCore(ViewSection ev, FamilyInstance fi, JoineryAnnotationRule? rule, List<string>? created, Func<ElementId> noteType)
    {
        if (!_rules.Fiches) return;
        var type = DimensionType("type_cote_menuiserie", _rules.UniteFiches, created);
        var cb = ev.CropBox;
        var t = cb.Transform;
        var inv = t.Inverse;
        var s = ev.Scale;
        var off = Units.Mm(_rules.DecalageMm) * s;
        var bb = fi.get_BoundingBox(null);
        if (bb == null || fi.Location is not LocationPoint lp) return;
        var hDir = t.BasisX;
        var local = inv.OfPoint(new XYZ(lp.Point.X, lp.Point.Y, bb.Min.Z));
        var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z) };
        var w = OpeningWidth(fi) ?? corners.Max(c => c.DotProduct(hDir)) - corners.Min(c => c.DotProduct(hDir));
        var h = OpeningHeight(fi) ?? bb.Max.Z - bb.Min.Z;
        double cx = local.X, by = local.Y;
        XYZ P(double x, double y) => t.OfPoint(new XYZ(x, y, 0));
        bool isDoor = fi.Category?.Id.Value == (long)BuiltInCategory.OST_Doors;

        // Largeur : plans gauche / droite, sinon repères dessinés
        var wRefs = WidthRefs(fi, hDir).ToList();
        if (wRefs.Count < 2)
            wRefs = new List<(Reference, double)>
            {
                (Tick(ev, P(cx - w / 2, cb.Min.Y - off), vertical: true, s), P(cx - w / 2, 0).DotProduct(hDir)),
                (Tick(ev, P(cx + w / 2, cb.Min.Y - off), vertical: true, s), P(cx + w / 2, 0).DotProduct(hDir)),
            };
        Create(ev, Line.CreateBound(P(cb.Min.X, cb.Min.Y - off), P(cb.Max.X, cb.Min.Y - off)), wRefs, type);

        // Hauteurs : bas / haut de la famille, sinon repères dessinés
        var vDir = t.BasisY;
        var bottom = fi.GetReferences(FamilyInstanceReferenceType.Bottom).FirstOrDefault() ?? Tick(ev, P(cb.Min.X - off, by), vertical: false, s);
        var top = fi.GetReferences(FamilyInstanceReferenceType.Top).FirstOrDefault() ?? Tick(ev, P(cb.Min.X - off, by + h), vertical: false, s);
        double Y(double y) => P(0, y).DotProduct(vDir);
        Create(ev, Line.CreateBound(P(cb.Min.X - off, cb.Min.Y), P(cb.Min.X - off, cb.Max.Y)),
            new List<(Reference, double)> { (bottom, Y(by)), (top, Y(by + h)) }, type);
        // Hauteur de poignée : repère sur la poignée, cote juste à côté (comme sur les calepins de l'agence)
        var handleY = by + Units.Mm(_rules.HauteurPoigneeMm);
        if (isDoor && _rules.HauteurPoigneeMm > 0 && Units.Mm(_rules.HauteurPoigneeMm) < h)
        {
            var hx = cx + w / 2 - Units.Mm(90);
            var mark = Tick(ev, P(hx, handleY), vertical: false, s);
            var lx = hx - Units.Mm(4) * s;
            Create(ev, Line.CreateBound(P(lx, cb.Min.Y), P(lx, cb.Max.Y)),
                new List<(Reference, double)> { (bottom, Y(by)), (mark, Y(handleY)) }, type);
        }

        // Annotations à droite, reliées à leur cible
        if (rule != null && rule.Notes.Count > 0)
        {
            var targets = rule.Notes.Select(n => Target(n.Cible, cx, by, w, h, handleY)).ToList();
            var spacing = Units.Mm(_rules.TexteMm * 2.6) * s;
            var ys = Annotation.Stack(targets.Select(p => p.Y).ToList(), spacing, cb.Min.Y, cb.Max.Y);
            var tx = cb.Max.X + Units.Mm(8) * s;
            var tt = noteType();
            for (int i = 0; i < rule.Notes.Count; i++)
            {
                var note = TextNote.Create(_doc, ev.Id, P(tx, ys[i]), rule.Notes[i].Texte,
                    new TextNoteOptions(tt) { HorizontalAlignment = HorizontalTextAlignment.Left, VerticalAlignment = VerticalTextAlignment.Middle });
                try
                {
                    var leader = note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                    leader.End = P(targets[i].X, targets[i].Y);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Note conservée sans ligne de repère
                }
                Last.Add(note.Id);
            }
            EnsureAnnotationCrop(ev, right: _rules.BandeAnnotationsMm);
        }
        EnsureAnnotationCrop(ev, left: 2 * _rules.DecalageMm + 8, bottom: _rules.DecalageMm + 8);
    }

    /// <summary>Point visé par une annotation, dans le repère de la vue (x vers la droite, y vers le haut).</summary>
    private static XYZ Target(string cible, double cx, double by, double w, double h, double handleY)
    {
        double L = cx - w / 2 + Units.Mm(15), R = cx + w / 2 - Units.Mm(20);
        return cible switch
        {
            "charniere_haut" => new XYZ(L, by + h - Units.Mm(250), 0),
            "charniere_milieu" => new XYZ(L, by + h * 0.55, 0),
            "charniere_bas" => new XYZ(L, by + Units.Mm(250), 0),
            "dormant" => new XYZ(R, by + h * 0.8, 0),
            "ouvrant" => new XYZ(cx + w * 0.1, by + h * 0.68, 0),
            "poignee" => new XYZ(cx + w / 2 - Units.Mm(90), handleY, 0),
            "serrure" => new XYZ(cx + w / 2 - Units.Mm(90), handleY - Units.Mm(110), 0),
            "poignee_fenetre" => new XYZ(cx + w / 2 - Units.Mm(60), by + h * 0.5, 0),
            "vitrage" => new XYZ(cx, by + h * 0.6, 0),
            "lames" => new XYZ(cx - w * 0.15, by + h * 0.4, 0),
            "joint" => new XYZ(cx + w / 2 - Units.Mm(10), by + h * 0.9, 0),
            "seuil" => new XYZ(cx, by + Units.Mm(15), 0),
            _ => new XYZ(cx, by + h / 2, 0),
        };
    }

    /// <summary>Plan d'une fiche : largeur réservation (plans gauche / droite) côté opposé au débattement.</summary>
    public void JoineryPlan(ViewPlan plan, FamilyInstance fi, List<string>? created) => Safe(() => JoineryPlanCore(plan, fi, created));

    private void JoineryPlanCore(ViewPlan plan, FamilyInstance fi, List<string>? created)
    {
        if (!_rules.Fiches || fi.Location is not LocationPoint lp) return;
        var bb = fi.get_BoundingBox(null);
        if (bb == null) return;
        var facing = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
        facing = facing.IsZeroLength() ? XYZ.BasisY : facing.Normalize();
        var along = XYZ.BasisZ.CrossProduct(facing);
        var p = new XYZ(lp.Point.X, lp.Point.Y, bb.Min.Z);
        var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) };
        double Ext(XYZ d) => corners.Max(c => (new XYZ(c.X, c.Y, p.Z) - p).DotProduct(d));
        var side = Ext(facing) <= Ext(facing.Negate()) ? facing : facing.Negate();
        var dist = Ext(side) + Units.Mm(_rules.DecalageMm) * plan.Scale;
        var w = OpeningWidth(fi) ?? Ext(along) * 2;
        var o = p + side * dist;
        var refs = WidthRefs(fi, along).ToList();
        if (refs.Count < 2) return;
        var type = DimensionType("type_cote_menuiserie", _rules.UniteFiches, created);
        var d = Create(plan, Line.CreateBound(o - along * (w / 2 + Units.Mm(300)), o + along * (w / 2 + Units.Mm(300))), refs, type);
        if (d != null && !string.IsNullOrWhiteSpace(_rules.LibelleLargeurPlan))
            try { d.Above = _rules.LibelleLargeurPlan; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        EnsureAnnotationCrop(plan, left: 2 * _rules.DecalageMm + 8, right: 2 * _rules.DecalageMm + 8, bottom: 2 * _rules.DecalageMm + 8, top: 2 * _rules.DecalageMm + 8);
    }

    // -----------------------------------------------------------------------------------------
    // Outils
    // -----------------------------------------------------------------------------------------

    /// <summary>Petit trait de repère (ligne de détail) servant de référence de cote quand la famille n'en fournit pas.</summary>
    private Reference Tick(View v, XYZ at, bool vertical, int scale)
    {
        var t = v.CropBox.Transform;
        var dir = vertical ? t.BasisY : t.BasisX;
        var half = Units.Mm(1.5) * scale;
        var dc = _doc.Create.NewDetailCurve(v, Line.CreateBound(at - dir * half, at + dir * half));
        Last.Add(dc.Id);
        return dc.GeometryCurve.Reference;
    }

    /// <summary>
    /// Crée une cote (simple ou chaîne) : références triées le long de la ligne, confondues fusionnées ; une chaîne dont la somme
    /// des segments ne correspond pas à la longueur totale (références mal ordonnées) est supprimée.
    /// </summary>
    private Dimension? Create(View v, Line line, IList<(Reference Ref, double Pos)> refs, ElementId type)
    {
        var keep = Annotation.Chain(refs.Select(r => r.Pos).ToList(), Units.Mm(8));
        if (keep.Count < 2) return null;
        var ra = new ReferenceArray();
        foreach (var i in keep) ra.Append(refs[i].Ref);
        Dimension? d;
        try
        {
            d = _doc.GetElement(type) is DimensionType dt ? _doc.Create.NewDimension(v, line, ra, dt) : _doc.Create.NewDimension(v, line, ra);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            Failed++;
            return null;
        }
        if (d == null) { Failed++; return null; }
        if (keep.Count > 2)
        {
            _doc.Regenerate();
            double sum = 0;
            foreach (DimensionSegment seg in d.Segments) sum += seg.Value ?? 0;
            var total = refs[keep[^1]].Pos - refs[keep[0]].Pos;
            if (Math.Abs(sum - total) > Units.Mm(30))
            {
                _doc.Delete(d.Id);
                Failed++;
                return null;
            }
        }
        Created++;
        Last.Add(d.Id);
        return d;
    }

    /// <summary>Élargit le cadre des annotations (mm papier) pour que les cotes et notes placées hors du cadrage restent visibles.</summary>
    private static void EnsureAnnotationCrop(View v, double left = 0, double right = 0, double bottom = 0, double top = 0)
    {
        try
        {
            var m = v.GetCropRegionShapeManager();
            if (left > 0) m.LeftAnnotationCropOffset = Math.Max(m.LeftAnnotationCropOffset, Units.Mm(left));
            if (right > 0) m.RightAnnotationCropOffset = Math.Max(m.RightAnnotationCropOffset, Units.Mm(right));
            if (bottom > 0) m.BottomAnnotationCropOffset = Math.Max(m.BottomAnnotationCropOffset, Units.Mm(bottom));
            if (top > 0) m.TopAnnotationCropOffset = Math.Max(m.TopAnnotationCropOffset, Units.Mm(top));
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Vue sans cadre d'annotations
        }
    }
}
