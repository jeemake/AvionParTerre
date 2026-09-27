using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Profiles;

namespace AvionParTerre.Revit.Services;

/// <summary>Élément à disposer sur une feuille : vue (viewport), nomenclature ou note textuelle.</summary>
internal sealed class SheetItem
{
    public string Key { get; init; } = "";
    public View? View { get; init; }
    public ViewSchedule? Schedule { get; init; }
    public TextNote? Text { get; init; }
    public bool StartNewPage { get; init; }
}

internal sealed class CompositionResult
{
    public int PageCount { get; set; }
    public List<string> Oversize { get; } = new();
    public Dictionary<string, int> PageOfItem { get; } = new();
}

/// <summary>
/// Dispose des vues sur une ou plusieurs feuilles : mesure après placement (vue + titre),
/// composition par étagères dans la zone de dessin du cartouche, pages supplémentaires si nécessaire.
/// </summary>
internal sealed class SheetComposer
{
    private readonly Document _doc;
    private readonly LayoutRules _rules;

    private readonly ElementId _viewportType;

    public SheetComposer(Document doc, LayoutRules rules, ElementId? viewportType = null)
    {
        _doc = doc;
        _rules = rules;
        _viewportType = viewportType ?? ElementId.InvalidElementId;
    }

    /// <summary>Zone de dessin de la feuille en mm papier (coordonnées de la feuille).</summary>
    public RectMm DrawingArea(ViewSheet sheet)
    {
        var tb = RevitUtil.TitleBlockOf(_doc, sheet);
        var bb = tb?.get_BoundingBox(sheet);
        if (tb == null || bb == null) return new RectMm(0, 0, 400, 270);
        var m = _rules.MarginsFor(tb.Symbol.FamilyName);
        double x0 = Units.ToMm(bb.Min.X), y0 = Units.ToMm(bb.Min.Y), x1 = Units.ToMm(bb.Max.X), y1 = Units.ToMm(bb.Max.Y);
        return new RectMm(x0 + m.Gauche, y0 + m.Bas, (x1 - x0) - m.Gauche - m.Droite, (y1 - y0) - m.Bas - m.Haut);
    }

    /// <summary>
    /// Place les éléments. <paramref name="pageProvider"/> renvoie la feuille d'index donné (création si besoin).
    /// Les éléments déjà placés sur une feuille ne sont pas déplacés (retouches conservées).
    /// </summary>
    public CompositionResult Compose(IReadOnlyList<SheetItem> items, Func<int, ViewSheet> pageProvider, bool tryCompact, RectMm? areaOverride = null)
    {
        var result = new CompositionResult();
        var first = pageProvider(0);
        var area = areaOverride ?? DrawingArea(first);

        // 1. Placement provisoire au centre pour mesurer
        var measured = new List<(SheetItem Item, Element Placed, double W, double H, XYZ Offset)>();
        var center = new XYZ(Units.Mm(area.CenterX), Units.Mm(area.CenterY), 0);
        foreach (var it in items)
        {
            var placed = PlaceAt(first, it, center);
            if (placed == null) continue;
            _doc.Regenerate();
            var (min, max) = Extents(first, placed);
            var anchor = Anchor(placed, first);
            // Décalage entre le point d'ancrage (centre de vue ou coin) et le coin bas-gauche de l'emprise
            measured.Add((it, placed, Units.ToMm(max.X - min.X), Units.ToMm(max.Y - min.Y), anchor - min));
        }

        // 2. Composition : compacte si possible, sinon en respectant les sauts de page demandés
        var spacing = _rules.EspacementVues;
        PackResult pack = null!;
        if (tryCompact)
        {
            pack = SheetPacker.Pack(measured.Select(m => new PackItem(m.Item.Key, m.W, m.H)).ToList(), area, spacing);
            if (pack.PageCount > 1) pack = null!;
        }
        pack ??= SheetPacker.Pack(measured.Select(m => new PackItem(m.Item.Key, m.W, m.H, m.Item.StartNewPage)).ToList(), area, spacing);
        result.PageCount = pack.PageCount;
        result.Oversize.AddRange(pack.Oversize);

        // 3. Positionnement final
        foreach (var p in pack.Placed)
        {
            var m = measured.First(x => x.Item.Key == p.Key);
            var sheet = pageProvider(p.Page);
            var placed = m.Placed;
            var target = new XYZ(Units.Mm(p.Rect.X), Units.Mm(p.Rect.Y), 0) + m.Offset;
            if (p.Page != 0 && m.Item.Text != null)
            {
                // Une note textuelle reste sur la première page : signalée pour reprise manuelle.
                result.Oversize.Add(p.Key);
                result.PageOfItem[p.Key] = 0;
                continue;
            }
            if (p.Page != 0)
            {
                // Une vue ne peut figurer que sur une feuille : on la retire de la page 0 puis on la replace.
                _doc.Delete(placed.Id);
                placed = PlaceAt(sheet, m.Item, target)!;
                _doc.Regenerate();
                var (min2, _) = Extents(sheet, placed);
                target = new XYZ(Units.Mm(p.Rect.X), Units.Mm(p.Rect.Y), 0) + (Anchor(placed, sheet) - min2);
            }
            MoveTo(placed, sheet, target);
            result.PageOfItem[p.Key] = p.Page;
        }
        return result;
    }

    /// <summary>Vérifie qu'un viewport existant reste dans la zone de dessin.</summary>
    public bool FitsInArea(ViewSheet sheet, Viewport vp)
    {
        var area = DrawingArea(sheet);
        var (min, max) = Extents(sheet, vp);
        return Units.ToMm(min.X) >= area.X - 1 && Units.ToMm(max.X) <= area.Right + 1
            && Units.ToMm(min.Y) >= area.Y - 1 && Units.ToMm(max.Y) <= area.Top + 1;
    }

    private Element? PlaceAt(ViewSheet sheet, SheetItem it, XYZ point)
    {
        if (it.View != null)
        {
            if (!Viewport.CanAddViewToSheet(_doc, sheet.Id, it.View.Id)) return null;
            var vp = Viewport.Create(_doc, sheet.Id, it.View.Id, point);
            if (_viewportType != ElementId.InvalidElementId) vp.ChangeTypeId(_viewportType);
            return vp;
        }
        if (it.Schedule != null)
            return ScheduleSheetInstance.Create(_doc, sheet.Id, it.Schedule.Id, point);
        if (it.Text != null)
        {
            // Les notes textuelles sont créées directement sur la feuille par l'appelant
            return it.Text;
        }
        return null;
    }

    private static XYZ Anchor(Element e, View sheet) => e switch
    {
        Viewport vp => vp.GetBoxCenter(),
        ScheduleSheetInstance si => si.Point,
        TextNote tn => tn.Coord,
        _ => e.get_BoundingBox(sheet).Min,
    };

    private void MoveTo(Element e, View sheet, XYZ anchor)
    {
        switch (e)
        {
            case Viewport vp:
                vp.SetBoxCenter(anchor);
                break;
            case ScheduleSheetInstance si:
                si.Point = anchor;
                break;
            case TextNote tn:
                ElementTransformUtils.MoveElement(_doc, tn.Id, anchor - tn.Coord);
                break;
        }
    }

    /// <summary>Emprise imprimée : cadre de la vue + titre de la vue.</summary>
    private static (XYZ Min, XYZ Max) Extents(View sheet, Element e)
    {
        if (e is Viewport vp)
        {
            var box = vp.GetBoxOutline();
            var min = box.MinimumPoint;
            var max = box.MaximumPoint;
            try
            {
                var label = vp.GetLabelOutline();
                if (label != null && !label.IsEmpty)
                {
                    min = new XYZ(Math.Min(min.X, label.MinimumPoint.X), Math.Min(min.Y, label.MinimumPoint.Y), 0);
                    max = new XYZ(Math.Max(max.X, label.MaximumPoint.X), Math.Max(max.Y, label.MaximumPoint.Y), 0);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return (min, max);
        }
        var bb = e.get_BoundingBox(sheet);
        return bb == null ? (XYZ.Zero, XYZ.Zero) : (bb.Min, bb.Max);
    }
}
