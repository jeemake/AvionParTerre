using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

internal sealed class SectionElevationRequest
{
    public bool Coupes { get; init; }
    public bool Facades { get; init; }
    /// <summary>Générer même si une feuille de l'agence couvre déjà les coupes / façades (choix explicite de l'utilisateur).</summary>
    public bool Force { get; init; }
}

/// <summary>
/// Coupes et façades des plans généraux : deux coupes A-A (longitudinale) et B-B (transversale) par le centre du bâtiment,
/// quatre façades selon ses axes principaux, cadrées sur l'emprise du bâtiment, à l'échelle des plans, sur les grandes feuilles
/// « COUPES » et « FACADES » numérotées à la suite des plans généraux.
/// </summary>
internal sealed class SectionElevationGenerator
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly DceProfile _dce;
    private readonly KdResources _res;
    private readonly ProjectNorms _norms;

    public SectionElevationGenerator(Document doc, PluginData data, DceProfile dce, ProjectNorms norms)
    {
        _doc = doc;
        _data = data;
        _dce = dce;
        _norms = norms;
        _res = new KdResources(doc, data);
    }

    public const string Coupes = "COUPES";
    public const string Facades = "FACADES";
    public static string ViewKey(string kind, string id) => $"{kind}|{id}";
    public static string SheetKey(string kind, int page) => $"{kind}|FEUILLE|{page}";

    private sealed class Building
    {
        public XYZ U = XYZ.BasisX, V = XYZ.BasisY, Center = XYZ.Zero;
        public double MinU, MaxU, MinV, MaxV, Zmin, Zmax;
        public double Lu => MaxU - MinU;
        public double Lv => MaxV - MinV;

        public IEnumerable<XYZ> Corners()
        {
            foreach (var u in new[] { MinU, MaxU })
                foreach (var v in new[] { MinV, MaxV })
                    foreach (var z in new[] { Zmin, Zmax })
                        yield return new XYZ(U.X * u + V.X * v, U.Y * u + V.Y * v, z);
        }
    }

    private bool CoveredSections(SectionElevationRequest r) => !r.Force && _norms.SectionSheet != null;
    private bool CoveredElevations(SectionElevationRequest r) => !r.Force && _norms.ElevationSheet != null;

    public IEnumerable<string> Preview(SectionElevationRequest r)
    {
        var idx = Identity.Index(_doc);
        if (r.Coupes)
            yield return CoveredSections(r) ? $"Coupes : feuille de l'agence {_norms.SectionSheet!.SheetNumber} {_norms.SectionSheet.Name} — conservée, non dupliquée"
                : idx.ContainsKey(SheetKey(Coupes, 0)) ? "Coupes A-A et B-B : feuille existante — vues manquantes recréées"
                : $"Coupes A-A (longitudinale) et B-B (transversale) à créer, feuille « COUPES » — 1:{_dce.EchellePlans}";
        if (r.Facades)
            yield return CoveredElevations(r) ? $"Façades : feuille de l'agence {_norms.ElevationSheet!.SheetNumber} {_norms.ElevationSheet.Name} — conservée, non dupliquée"
                : idx.ContainsKey(SheetKey(Facades, 0)) ? "Façades : feuille existante — vues manquantes recréées"
                : $"4 façades à créer, feuille « FACADES » — 1:{_dce.EchellePlans}";
    }

    public void Run(SectionElevationRequest r, Report report, WarningCollector w)
    {
        if (!r.Coupes && !r.Facades) return;
        if (r.Coupes && CoveredSections(r))
            report.Kept.Add($"Coupes : feuille de l'agence {_norms.SectionSheet!.SheetNumber} {_norms.SectionSheet.Name} conservée (non dupliquée)");
        if (r.Facades && CoveredElevations(r))
            report.Kept.Add($"Façades : feuille de l'agence {_norms.ElevationSheet!.SheetNumber} {_norms.ElevationSheet.Name} conservée (non dupliquée)");
        bool doSections = r.Coupes && !CoveredSections(r), doElevations = r.Facades && !CoveredElevations(r);
        if (!doSections && !doElevations) return;

        var tb = _dce.TitleBlockId is { } tbId && _doc.GetElement(new ElementId(tbId)) is FamilySymbol ? new ElementId(tbId) : _res.TitleBlock(_dce.FormatPlans);
        if (tb == ElementId.InvalidElementId)
        {
            report.Issue(Severity.Bloquant, "ressource_kd", $"Cartouche {_dce.FormatPlans}", "Cartouche introuvable : coupes et façades impossibles.",
                "Charger un cartouche APD-DCE ou en choisir un dans Paramètres > Ressources.");
            return;
        }
        var b = Measure();
        if (b == null)
        {
            report.Issue(Severity.ARevoir, "coupes_facades", "Bâtiment", "Aucun mur ni pièce : emprise du bâtiment inconnue.", "Modéliser les murs puis relancer.");
            return;
        }
        // Titres des vues en texte simple Century Gothic (plusieurs vues par feuille) : préparés hors transaction
        var vpType = _res.PrepareViewTitles(report.Created, w);
        var numbers = RevitUtil.SheetNumbers(_doc);
        var planGen = new PlanGenerator(_doc, _data, _dce, _norms);
        var nextIndex = RevitUtil.Levels(_doc).Count();

        if (doSections)
            Kind(Coupes, "COUPES", report, w, () => SectionViews(b, report), tb, vpType, numbers, planGen, ref nextIndex);
        if (doElevations)
            Kind(Facades, "FACADES", report, w, () => ElevationViews(b, report), tb, vpType, numbers, planGen, ref nextIndex);
        report.Issues.AddRange(_res.Missing);
    }

    private void Kind(string kind, string title, Report report, WarningCollector w, Func<List<View>> create, ElementId tb, ElementId vpType,
        HashSet<string> numbers, PlanGenerator planGen, ref int nextIndex)
    {
        using var t = WarningCollector.Start(_doc, $"Avion par terre — {title.ToLowerInvariant()}", w);
        try
        {
            var views = create();
            var idx = Identity.Index(_doc);
            var composer = new SheetComposer(_doc, _data.Profile.MiseEnPage, vpType);
            var index = nextIndex;
            ViewSheet Page(int k)
            {
                if (idx.TryGetValue(SheetKey(kind, k), out var e) && e is ViewSheet s0) return s0;
                var s = ViewSheet.Create(_doc, tb);
                var number = planGen.SheetNumberFor(index++, numbers);
                if (numbers.Contains(number))
                {
                    var wanted = number;
                    for (int i = 1; numbers.Contains(number); i++) number = wanted + "-" + i;
                }
                numbers.Add(number);
                s.SheetNumber = number;
                s.Name = RevitUtil.CleanName(k == 0 ? title : $"{title} ({k + 1})");
                PlanGenerator.FillSheetParams(s, _data.Profile, lot: _norms.PlanLot ?? "ARCHITECTURE");
                Identity.Set(s, new IdentityData { Key = SheetKey(kind, k), Role = "coupes_facades", Payload = kind });
                idx[SheetKey(kind, k)] = s;
                report.Created.Add($"Feuille {s.SheetNumber} {s.Name} ({_dce.FormatPlans})");
                return s;
            }
            var toPlace = views.Where(v => !RevitUtil.IsOnSheet(v)).ToList();
            if (toPlace.Count > 0)
            {
                // Pages existantes conservées : les vues recréées vont sur une page suivante
                int existing = 0;
                while (idx.ContainsKey(SheetKey(kind, existing))) existing++;
                var offset = existing > 0 ? existing : 0;
                _doc.Regenerate();
                var res = composer.Compose(toPlace.Select(v => new SheetItem { Key = v.Name, View = v }).ToList(), k => Page(k + offset), tryCompact: true);
                foreach (var o in res.Oversize)
                    report.Issue(Severity.ARevoir, "debordement", $"{title} / {o}", $"Vue plus grande que la zone de dessin du {_dce.FormatPlans} à 1:{_dce.EchellePlans}.",
                        "Recadrer la vue ou choisir un autre profil documentaire.");
            }
            nextIndex = index;
            t.Commit();
        }
        catch (Exception ex)
        {
            t.RollBack();
            report.Issue(Severity.Bloquant, "erreur", title, ex.Message, "Corriger puis relancer : aucune vue partielle n'a été conservée.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Emprise du bâtiment
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Emprise : murs proches des pièces (les éléments isolés loin du bâtiment sont ignorés), axes principaux d'après la direction
    /// dominante des murs, altitude des sols aux toitures.
    /// </summary>
    private Building? Measure()
    {
        var rooms = RevitUtil.Rooms(_doc).Where(RevitUtil.IsEnclosed).Select(r => r.get_BoundingBox(null)).Where(x => x != null).ToList();
        var walls = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.get_BoundingBox(null) != null).ToList();
        if (rooms.Count > 0)
        {
            double near = Units.Mm(3000);
            double x0 = rooms.Min(r => r!.Min.X) - near, y0 = rooms.Min(r => r!.Min.Y) - near;
            double x1 = rooms.Max(r => r!.Max.X) + near, y1 = rooms.Max(r => r!.Max.Y) + near;
            walls = walls.Where(w => { var bb = w.get_BoundingBox(null); return bb.Max.X > x0 && bb.Min.X < x1 && bb.Max.Y > y0 && bb.Min.Y < y1; }).ToList();
        }
        if (walls.Count == 0) return null;
        var angle = Annotation.DominantAngle(walls.Select(w => w.Location).OfType<LocationCurve>().Select(lc => lc.Curve).OfType<Line>()
            .Select(l => (Math.Atan2(l.Direction.Y, l.Direction.X), l.Length)));
        var u = new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
        var v = XYZ.BasisZ.CrossProduct(u);
        var boxes = walls.Select(w => w.get_BoundingBox(null)).ToList();
        var extra = new FilteredElementCollector(_doc)
            .WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Roofs), new ElementCategoryFilter(BuiltInCategory.OST_Floors)))
            .WhereElementIsNotElementType().Select(e => e.get_BoundingBox(null)).Where(bb => bb != null).ToList();
        double minX = boxes.Min(x => x.Min.X), maxX = boxes.Max(x => x.Max.X), minY = boxes.Min(x => x.Min.Y), maxY = boxes.Max(x => x.Max.Y);
        // Toitures et planchers dans l'emprise des murs seulement
        boxes.AddRange(extra.Where(bb => bb.Max.X > minX && bb.Min.X < maxX && bb.Max.Y > minY && bb.Min.Y < maxY));
        var pts = boxes.SelectMany(bb => new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) }).ToList();
        var b = new Building
        {
            U = u, V = v,
            MinU = pts.Min(p => p.DotProduct(u)), MaxU = pts.Max(p => p.DotProduct(u)),
            MinV = pts.Min(p => p.DotProduct(v)), MaxV = pts.Max(p => p.DotProduct(v)),
            Zmin = walls.Min(w => w.get_BoundingBox(null).Min.Z), Zmax = pts.Max(p => p.Z),
        };
        var c = u * ((b.MinU + b.MaxU) / 2) + v * ((b.MinV + b.MaxV) / 2);
        b.Center = new XYZ(c.X, c.Y, b.Zmin);
        return b;
    }

    // -----------------------------------------------------------------------------------------
    // Coupes
    // -----------------------------------------------------------------------------------------

    private List<View> SectionViews(Building b, Report report)
    {
        var idx = Identity.Index(_doc);
        var views = new List<View>();
        var vft = _res.ViewFamilyType("type_vue_coupe", ViewFamily.Section);
        var tpl = _res.ViewTemplate("gabarit_coupe", optional: true);
        double m = Units.Mm(2000);
        // A-A : plan de coupe parallèle au grand axe, regard vers +V ; B-B : parallèle au petit axe, regard vers +U
        foreach (var (id, look, right, width, depth) in new[] { ("A", b.V, b.U, b.Lu, b.Lv), ("B", b.U, b.V, b.Lv, b.Lu) })
        {
            var key = ViewKey(Coupes, id);
            if (idx.TryGetValue(key, out var e) && e is View existing)
            {
                views.Add(existing);
                report.Unchanged.Add($"Vue « {existing.Name} »");
                continue;
            }
            // La vue regarde vers -BasisZ ; plan de coupe au centre (Max.Z = 0), profondeur jusqu'au-delà de la façade opposée
            var tr = Transform.Identity;
            tr.Origin = b.Center;
            tr.BasisZ = look.Negate();
            tr.BasisY = XYZ.BasisZ;
            tr.BasisX = tr.BasisY.CrossProduct(tr.BasisZ);
            var box = new BoundingBoxXYZ
            {
                Transform = tr,
                Min = new XYZ(-width / 2 - m, -Units.Mm(1500), -(depth / 2 + m)),
                Max = new XYZ(width / 2 + m, b.Zmax - b.Zmin + Units.Mm(1500), 0),
            };
            var vs = ViewSection.CreateSection(_doc, vft, box);
            ApplyTemplate(vs, tpl);
            var label = $"COUPE {id}-{id}";
            vs.Name = RevitUtil.UniqueViewName(_doc, $"DCE_{label}");
            RevitUtil.SetTitleOnSheet(vs, label);
            Identity.Set(vs, new IdentityData { Key = key, Role = "coupe_vue" });
            report.Created.Add($"Vue « {vs.Name} » (1:{vs.Scale})");
            views.Add(vs);
        }
        return views;
    }

    // -----------------------------------------------------------------------------------------
    // Façades
    // -----------------------------------------------------------------------------------------

    private List<View> ElevationViews(Building b, Report report)
    {
        var idx = Identity.Index(_doc);
        var views = new List<View>();
        var vft = _res.ViewFamilyType("type_vue_elevation", ViewFamily.Elevation);
        var tpl = _res.ViewTemplate("gabarit_facade", optional: true);
        var plans = PlanCandidates(b.Zmin);
        if (vft == ElementId.InvalidElementId || plans.Count == 0)
        {
            report.Issue(Severity.ARevoir, "coupes_facades", "Façades", "Type de vue d'élévation ou plan d'étage introuvable : façades non créées.",
                "Créer un plan d'étage (Plans généraux) et vérifier les types de vues d'élévation du gabarit.");
            return views;
        }
        foreach (var look in new[] { b.V, b.V.Negate(), b.U, b.U.Negate() })
        {
            var name = Annotation.FacadeName(-look.X, -look.Y);
            var key = ViewKey(Facades, name);
            if (idx.TryGetValue(key, out var e) && e is View existing)
            {
                views.Add(existing);
                report.Unchanged.Add($"Vue « {existing.Name} »");
                continue;
            }
            var depthToCenter = (Math.Abs(look.DotProduct(b.U)) > 0.5 ? b.Lu : b.Lv) / 2;
            var origin = b.Center - look * (depthToCenter + Units.Mm(5000));
            var ev = CreateElevation(vft, origin, look, plans);
            if (ev == null)
            {
                report.Issue(Severity.ARevoir, "coupes_facades", name, "Repère d'élévation non créé (non visible dans les plans d'étage).",
                    "Créer la façade à la main ou vérifier la visibilité des repères d'élévation dans les plans.");
                continue;
            }
            ApplyTemplate(ev, tpl);
            _doc.Regenerate();
            // Cadrage sur l'emprise du bâtiment vue depuis ce côté
            var cb = ev.CropBox;
            var inv = cb.Transform.Inverse;
            var local = b.Corners().Select(inv.OfPoint).ToList();
            double m = Units.Mm(2000);
            cb.Min = new XYZ(local.Min(p => p.X) - m, local.Min(p => p.Y) - Units.Mm(1500), cb.Min.Z);
            cb.Max = new XYZ(local.Max(p => p.X) + m, local.Max(p => p.Y) + Units.Mm(1500), cb.Max.Z);
            ev.CropBoxActive = true;
            ev.CropBox = cb;
            ev.CropBoxVisible = false;
            ev.Name = RevitUtil.UniqueViewName(_doc, $"DCE_{name}");
            RevitUtil.SetTitleOnSheet(ev, name);
            Identity.Set(ev, new IdentityData { Key = key, Role = "facade_vue" });
            report.Created.Add($"Vue « {ev.Name} » (1:{ev.Scale})");
            views.Add(ev);
        }
        return views;
    }

    /// <summary>
    /// Repère d'élévation à 4 faces : la vue dont le regard est le plus proche de <paramref name="look"/> est conservée, puis le repère
    /// est tourné pour l'aligner exactement sur l'axe du bâtiment.
    /// </summary>
    private ViewSection? CreateElevation(ElementId vft, XYZ origin, XYZ look, List<ViewPlan> plans)
    {
        foreach (var plan in plans)
        {
            ElevationMarker? marker = null;
            try
            {
                marker = ElevationMarker.CreateElevationMarker(_doc, vft, origin, _dce.EchellePlans);
                var created = new List<ViewSection>();
                for (int i = 0; i < marker.MaximumViewCount; i++)
                    if (marker.IsAvailableIndex(i)) created.Add(marker.CreateElevation(_doc, plan.Id, i));
                if (created.Count == 0)
                {
                    _doc.Delete(marker.Id);
                    continue;
                }
                _doc.Regenerate();
                XYZ Look(View v) => new XYZ(-v.ViewDirection.X, -v.ViewDirection.Y, 0).Normalize();
                var best = created.OrderByDescending(v => Look(v).DotProduct(look)).First();
                foreach (var other in created.Where(v => v.Id != best.Id)) _doc.Delete(other.Id);
                var actual = Look(best);
                var angle = Math.Atan2(actual.CrossProduct(look).Z, actual.DotProduct(look));
                if (Math.Abs(angle) > 1e-3)
                    ElementTransformUtils.RotateElement(_doc, marker.Id, Line.CreateBound(origin, origin + XYZ.BasisZ), angle);
                best.Scale = _dce.EchellePlans;
                return best;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                if (marker != null && marker.IsValidObject) _doc.Delete(marker.Id);
            }
        }
        return null;
    }

    /// <summary>Plans où placer le repère : plans DCE du plugin, puis plans d'étage du niveau le plus bas.</summary>
    private List<ViewPlan> PlanCandidates(double zmin)
    {
        var plans = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(p => !p.IsTemplate && p.ViewType == ViewType.FloorPlan && p.GenLevel != null).ToList();
        var managed = Identity.All(_doc).Where(x => x.Data.Role == "plan_general_vue").Select(x => x.Element.Id).ToHashSet();
        return plans.OrderBy(p => managed.Contains(p.Id) ? 0 : 1)
            .ThenBy(p => Math.Abs(p.GenLevel.Elevation - zmin))
            .ThenBy(p => p.Id.Value).Take(6).ToList();
    }

    private void ApplyTemplate(View v, ElementId tpl)
    {
        // Réglages du gabarit de l'agence appliqués une fois ; l'échelle reste celle des plans généraux
        if (tpl != ElementId.InvalidElementId && v.IsValidViewTemplate(tpl)) v.ApplyViewTemplateParameters((View)_doc.GetElement(tpl));
        v.Scale = _dce.EchellePlans;
        var coarser = v.get_Parameter(BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_METRIC);
        if (coarser != null && !coarser.IsReadOnly && coarser.StorageType == StorageType.Integer) coarser.Set(Math.Max(_dce.EchellePlans, 200));
    }
}
