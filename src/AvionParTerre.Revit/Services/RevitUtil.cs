using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace AvionParTerre.Revit.Services;

internal static class RevitUtil
{
    public static IEnumerable<Room> Rooms(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>();

    public static bool IsPlaced(Room r) => r.Location != null;

    public static bool IsEnclosed(Room r) => r.Location != null && r.Area > 1e-6;

    public static string RoomName(Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

    public static string RoomLabel(Room r) => $"{r.Number} {RoomName(r)}".Trim();

    public static IEnumerable<Level> Levels(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation);

    /// <summary>Premier paramètre trouvé parmi une liste de noms (profil).</summary>
    public static Parameter? Param(Element e, IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            var p = e.LookupParameter(n);
            if (p != null) return p;
        }
        return null;
    }

    public static string? ParamText(Element e, IEnumerable<string> names)
    {
        var p = Param(e, names);
        if (p == null) return null;
        return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
    }

    public static bool TrySetString(Element e, IEnumerable<string> names, string value)
    {
        var p = Param(e, names);
        if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
        return p.Set(value);
    }

    public static string UniqueViewName(Document doc, string wanted, View? self = null)
    {
        var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => self == null || v.Id != self.Id).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        var name = CleanName(wanted);
        if (!names.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!names.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>Caractères interdits dans les noms de vues/feuilles Revit.</summary>
    public static string CleanName(string s)
    {
        foreach (var c in "{}[]|;<>?`~\\:") s = s.Replace(c, '-');
        return s.Trim();
    }

    /// <summary>Numéros de feuilles utilisés, hors <paramref name="except"/> (Revit pré-attribue un numéro à toute feuille créée).</summary>
    public static HashSet<string> SheetNumbers(Document doc, ViewSheet? except = null) =>
        new(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => except == null || s.Id != except.Id).Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);

    /// <summary>Compare deux noms de ressources sans tenir compte de la casse, des accents ni des espaces multiples.</summary>
    public static bool SameName(string a, string b) => AvionParTerre.Core.Text.TextNorm.SameResource(a, b);

    public static ViewSheet? SheetOf(Document doc, View v)
    {
        var num = v.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString();
        if (string.IsNullOrEmpty(num)) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .FirstOrDefault(s => s.SheetNumber == num);
    }

    public static bool IsOnSheet(View v) =>
        !string.IsNullOrEmpty(v.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString());

    public static FamilyInstance? TitleBlockOf(Document doc, ViewSheet s) =>
        new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).OfType<FamilyInstance>().FirstOrDefault();

    /// <summary>Contour de la pièce (points du contour de finition), ou null si non fermée.</summary>
    public static List<XYZ>? BoundaryPoints(Room r)
    {
        var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        var loops = r.GetBoundarySegments(opt);
        if (loops == null || loops.Count == 0) return null;
        var pts = new List<XYZ>();
        foreach (var loop in loops)
            foreach (var seg in loop)
                pts.AddRange(seg.GetCurve().Tessellate());
        return pts.Count >= 3 ? pts : null;
    }

    /// <summary>Direction du plus long segment de contour (axes locaux de la pièce).</summary>
    public static XYZ MainDirection(Room r)
    {
        var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        var loops = r.GetBoundarySegments(opt);
        Curve? best = null;
        if (loops != null)
            foreach (var loop in loops)
                foreach (var seg in loop)
                {
                    var c = seg.GetCurve();
                    if (c is Line && (best == null || c.Length > best.Length)) best = c;
                }
        if (best is not Line l) return XYZ.BasisX;
        var d = new XYZ(l.Direction.X, l.Direction.Y, 0).Normalize();
        // Ramène l'angle dans ]-45°, 45°] pour que « a » reste proche du nord du projet
        var angle = Math.Atan2(d.Y, d.X);
        while (angle > Math.PI / 4) angle -= Math.PI / 2;
        while (angle <= -Math.PI / 4) angle += Math.PI / 2;
        return new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
    }

    /// <summary>« Titre sur la feuille » : repère court affiché sous la vue (ex. « E1-a »).</summary>
    public static void SetTitleOnSheet(View v, string title) =>
        v.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.Set(title);

    public static void CropAnnotations(View v)
    {
        var p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
        if (p != null && !p.IsReadOnly) p.Set(1);
    }

    public static double RoomHeight(Room r)
    {
        var h = r.UnboundedHeight;
        if (h > 1e-6) return h;
        var bb = r.get_BoundingBox(null);
        return bb != null ? bb.Max.Z - bb.Min.Z : Units.Mm(2700);
    }
}

/// <summary>Supprime les avertissements Revit pendant la génération et les conserve pour le rapport.</summary>
internal sealed class WarningCollector : IFailuresPreprocessor
{
    public List<string> Warnings { get; } = new();

    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        foreach (var f in a.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning)
            {
                Warnings.Add(f.GetDescriptionText());
                a.DeleteWarning(f);
            }
        }
        return FailureProcessingResult.Continue;
    }

    public static Transaction Start(Document doc, string name, WarningCollector w)
    {
        var t = new Transaction(doc, name);
        var o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(w);
        o.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(o);
        t.Start();
        return t;
    }
}
