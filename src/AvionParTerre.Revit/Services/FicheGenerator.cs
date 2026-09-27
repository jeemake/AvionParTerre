using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Joinery;
using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

/// <summary>Type de menuiserie présent dans le modèle, avec son repère, son lot et ses occurrences.</summary>
internal sealed class JoineryType
{
    public FamilySymbol Symbol { get; init; } = null!;
    public string Mark { get; init; } = "";
    public string? Lot { get; set; }
    /// <summary>Origine du lot : préfixe, préfixe approché, IA.</summary>
    public string LotSource { get; set; } = "";
    public List<FamilyInstance> Instances { get; init; } = new();
    public View? Drafting { get; init; }
    public string? DraftingSheet { get; init; }
    public bool Ambiguous { get; init; }
    public ViewSheet? Fiche { get; init; }
    /// <summary>Fiche existante de l'agence (feuille nommée d'après le repère, non gérée par le plugin).</summary>
    public ViewSheet? AgencySheet { get; init; }
    /// <summary>Prescriptions décidées (IA, à valider) ; null = « N/A ».</summary>
    public string? Prescriptions { get; set; }

    public double? WidthMm => Dim(BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.GENERIC_WIDTH);
    public double? HeightMm => Dim(BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.GENERIC_HEIGHT);

    private double? Dim(params BuiltInParameter[] bips)
    {
        foreach (var b in bips)
        {
            var p = Symbol.get_Parameter(b) ?? Instances.FirstOrDefault()?.get_Parameter(b);
            if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 1e-6) return Units.ToMm(p.AsDouble());
        }
        return null;
    }
}

/// <summary>
/// Fiches menuiseries A3 (§9) selon les normes du projet quand elles existent : numéro « AL-nn » / « CB-nn », nom de feuille = repère,
/// titre en haut à gauche, nomenclature « Tableau quantitatif_repère » au format de l'agence. Contenu : vue de dessin K&amp;D du même nom
/// si elle existe, sinon élévation et plan générés (gabarits « Calepin Baies »), puis bloc descriptif en Century Gothic.
/// </summary>
internal sealed class FicheGenerator
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly KdResources _res;
    private readonly JoineryClassifier _classifier;
    private readonly ProjectNorms _norms;
    private readonly Dimensioning _dims;

    public FicheGenerator(Document doc, PluginData data, ProjectNorms? norms = null)
    {
        _doc = doc;
        _data = data;
        _res = new KdResources(doc, data);
        _classifier = new JoineryClassifier(data.Profile.Menuiseries);
        _norms = norms ?? ProjectNorms.Detect(doc, data);
        _dims = new Dimensioning(doc, data);
    }

    public ProjectNorms Norms => _norms;

    public static string Key(FamilySymbol s) => $"FICHE|{s.UniqueId}";

    public List<JoineryType> Collect()
    {
        var idx = Identity.Index(_doc);
        var instances = new FilteredElementCollector(_doc)
            .WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Doors), new ElementCategoryFilter(BuiltInCategory.OST_Windows)))
            .WhereElementIsNotElementType().OfType<FamilyInstance>()
            .Where(fi => fi.SuperComponent == null)
            .GroupBy(fi => fi.Symbol.Id).ToDictionary(g => g.Key, g => g.ToList());
        var drafting = new FilteredElementCollector(_doc).OfClass(typeof(ViewDrafting)).Cast<View>()
            .Where(v => !v.IsTemplate).GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var symbols = instances.Keys.Select(id => (FamilySymbol)_doc.GetElement(id)).ToList();
        var dupMarks = symbols.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(s => s.FamilyName).Distinct().Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return symbols.Select(s =>
        {
            var mark = s.Name.Trim();
            drafting.TryGetValue(mark, out var dv);
            var cls = _classifier.Classify(mark);
            var fiche = idx.TryGetValue(Key(s), out var e) ? e as ViewSheet : null;
            return new JoineryType
            {
                Symbol = s,
                Mark = mark,
                Lot = cls?.Lot,
                LotSource = cls == null ? "" : cls.Exact ? "préfixe" : $"préfixe approché « {cls.Prefix} »",
                Instances = instances[s.Id],
                Drafting = dv,
                DraftingSheet = dv != null && RevitUtil.IsOnSheet(dv) ? dv.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER).AsString() : null,
                Ambiguous = dupMarks.Contains(mark),
                Fiche = fiche,
                AgencySheet = fiche == null && _norms.FicheSheetByMark.TryGetValue(mark, out var a) ? a : null,
            };
        }).OrderBy(t => t.Lot ?? "~").ThenBy(t => t.Mark, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Fiche déjà présente (plugin, agence ou vue de dessin placée) : pas de nouvelle feuille.</summary>
    public static string? ExistingLabel(JoineryType t) =>
        t.Fiche != null ? $"{t.Fiche.SheetNumber} (plugin)"
        : t.AgencySheet != null ? $"{t.AgencySheet.SheetNumber} (agence)"
        : t.DraftingSheet != null ? $"{t.DraftingSheet} (vue de dessin placée)"
        : null;

    public void Run(IReadOnlyList<JoineryType> types, Report report, WarningCollector warnings, Action<JoineryType, int>? progress = null, Func<bool>? cancelled = null)
    {
        var defaultTb = _res.TitleBlockFor("cartouche_fiche", "A3");
        if (defaultTb == ElementId.InvalidElementId && _norms.Fiches.Count == 0)
        {
            report.Issue(Severity.Bloquant, "ressource_kd", "Cartouche fiche", "Aucun cartouche disponible pour les fiches.",
                "Charger un cartouche A3 K&D ou en choisir un dans Paramètres > Ressources.");
            report.Issues.AddRange(_res.Missing);
            return;
        }
        _res.PrepareViewTitles(report.Created, warnings);
        var nextIndex = Identity.All(_doc).Where(x => x.Data.Role == "fiche")
            .Select(x => x.Data.Payload.Split('|')).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Max(p => int.TryParse(p[1], out var i) ? i : 0) + 1);

        int processed = 0;
        foreach (var jt in types)
        {
            if (cancelled?.Invoke() == true) break;
            progress?.Invoke(jt, ++processed);
            if (cancelled?.Invoke() == true) break;
            if (jt.Lot == null)
            {
                report.Issue(Severity.ARevoir, "lot_menuiserie", jt.Mark, "Repère sans lot reconnu (préfixe inconnu).",
                    "Compléter les préfixes du profil, renommer le type selon la convention K&D ou lancer le mode avion par terre.", jt.Symbol.Id.Value);
                continue;
            }
            if (jt.Ambiguous)
                report.Issue(Severity.EmissionBloquee, "repere_ambigu", jt.Mark,
                    $"Le repère {jt.Mark} est porté par plusieurs familles.", "Attribuer un repère unique par type d'ouvrage.", jt.Symbol.Id.Value);
            if (jt.Fiche == null && jt.AgencySheet != null)
            {
                report.Kept.Add($"{jt.Mark} : fiche de l'agence {jt.AgencySheet.SheetNumber} existante — conservée, non dupliquée");
                continue;
            }
            if (jt.Fiche == null && jt.DraftingSheet != null)
            {
                report.Kept.Add($"{jt.Mark} : fiche existante hors plugin (vue de dessin sur la feuille {jt.DraftingSheet}) — non dupliquée");
                continue;
            }
            using var t = WarningCollector.Start(_doc, $"Avion par terre — fiche {jt.Mark}", warnings);
            try
            {
                if (jt.Fiche != null) UpdateFiche(jt, report);
                else
                {
                    _norms.Fiches.TryGetValue(jt.Lot, out var norm);
                    var idx = norm != null ? Math.Max(norm.NextIndex, nextIndex.TryGetValue(jt.Lot, out var ni) ? ni : 0)
                        : nextIndex.TryGetValue(jt.Lot, out var i) ? i : 1;
                    var tb = norm?.TitleBlock is { } ntb && _doc.GetElement(ntb) is FamilySymbol ? ntb : defaultTb;
                    CreateFiche(jt, tb, idx, norm, report);
                    nextIndex[jt.Lot] = idx + 1;
                    if (norm != null) norm.NextIndex = idx + 1;
                }
                t.Commit();
            }
            catch (Exception ex)
            {
                t.RollBack();
                report.Issue(Severity.Bloquant, "erreur", jt.Mark, ex.Message, "Corriger puis relancer.", jt.Symbol.Id.Value);
            }
        }
        report.Issues.AddRange(_res.Missing);
        if (_dims.Failed > 0)
            report.Issue(Severity.Information, "cotation", "Fiches menuiseries", $"{_dims.Failed} cote(s) non créée(s) (plans de référence de famille introuvables).",
                "Compléter les cotes à la main si nécessaire.");
    }

    private string SheetNumber(JoineryType jt, int index, FicheNorm? norm)
    {
        if (norm != null) return norm.Number(index);
        var n = _data.Profile.Numerotation;
        return n.PrefixeBatiment + Numbering.Format(n.Fiche, ("lot", jt.Lot!), ("index", index));
    }

    private void CreateFiche(JoineryType jt, ElementId tb, int index, FicheNorm? norm, Report report)
    {
        var sheet = ViewSheet.Create(_doc, tb);
        var number = SheetNumber(jt, index, norm);
        var numbers = RevitUtil.SheetNumbers(_doc, sheet);
        var wanted = number;
        for (int i = index + 1; numbers.Contains(number); i++) number = SheetNumber(jt, i, norm);
        if (number != wanted)
            report.Issue(Severity.Information, "numerotation", wanted, $"Numéro déjà utilisé : {number} attribué.", "Aucune action nécessaire.");
        sheet.SheetNumber = number;
        // Convention de l'agence : la feuille porte le nom du repère
        sheet.Name = RevitUtil.CleanName(jt.Mark);
        var lotLabel = _data.Profile.Menuiseries.Lots.FirstOrDefault(l => l.Code == jt.Lot)?.Libelle;
        // Phase « DCE » (cartouche des calepins de l'agence) ; le calepin du lot devient un sous-dossier de DCE (BrowserFolders)
        PlanGenerator.FillSheetParams(sheet, _data.Profile, "DCE", norm != null ? norm.Lot2 : TextNorm.UpperTitle(lotLabel));

        var composer = new SheetComposer(_doc, _data.Profile.MiseEnPage, _res.ViewportTypeWithTitle());
        var area = composer.DrawingArea(sheet);

        // Titre : repère en haut à gauche (position de la convention de l'agence si relevée)
        var titleType = _res.CenturyText(TitleSize(norm), bold: true, report.Created);
        var tbBox = RevitUtil.TitleBlockOf(_doc, sheet)?.get_BoundingBox(sheet);
        var titlePt = norm?.TitleOffsetMm is { } off && tbBox != null
            ? new XYZ(tbBox.Min.X + Units.Mm(off.X), tbBox.Min.Y + Units.Mm(off.Y), 0)
            : new XYZ(Units.Mm(area.X), Units.Mm(area.Top), 0);
        var title = TextNote.Create(_doc, sheet.Id, titlePt, jt.Mark,
            new TextNoteOptions(titleType) { HorizontalAlignment = HorizontalTextAlignment.Left, VerticalAlignment = VerticalTextAlignment.Top });
        _doc.Regenerate();
        var tbb = title.get_BoundingBox(sheet);
        if (tbb != null)
        {
            var below = Units.ToMm(tbb.Min.Y) - 4;
            if (below > area.Y + 50 && below < area.Top) area = new RectMm(area.X, area.Y, area.Width, below - area.Y);
        }

        var items = new List<SheetItem>();
        var text = DescriptionText(jt);
        var tt = _res.CenturyText(2.5, bold: false, report.Created);
        var note = TextNote.Create(_doc, sheet.Id, new XYZ(Units.Mm(area.X), Units.Mm(area.Top), 0), Units.Mm(110), text,
            new TextNoteOptions(tt) { HorizontalAlignment = HorizontalTextAlignment.Left, VerticalAlignment = VerticalTextAlignment.Top });
        items.Add(new SheetItem { Key = "texte", Text = note });

        if (jt.Drafting != null)
        {
            items.Add(new SheetItem { Key = "dessin", View = jt.Drafting });
            report.Created.Add($"{jt.Mark} : vue de dessin K&D « {jt.Drafting.Name} » réutilisée");
        }
        Func<List<ElementId>>? annotate = null;
        if (jt.Drafting == null)
        {
            var rep = Representative(jt);
            if (rep != null)
            {
                var (elev, plan) = GeneratedViews(jt, rep, area);
                items.Add(new SheetItem { Key = "elevation", View = elev });
                if (plan != null) items.Add(new SheetItem { Key = "plan", View = plan });
                annotate = () => Annotate(jt, rep, elev, plan, report);
                report.Created.Add($"{jt.Mark} : élévation et plan générés sur l'exemplaire {rep.Id.Value} (gabarits Calepin Baies), cotés et annotés");
                report.Issue(Severity.ARevoir, "fiche_generee", jt.Mark, "Aucune vue de dessin K&D nommée d'après le repère : vues de modèle générées, cotées et annotées.",
                    "Contrôler les vues, les cotes et les annotations (côté des charnières et de la poignée), compléter coupes/détails selon l'ouvrage.", jt.Symbol.Id.Value);
            }
        }
        var (sched, agencyFormat) = LocationSchedule(jt, report);
        items.Add(new SheetItem { Key = "localisation", Schedule = sched });

        // Une fiche = une page : si le contenu déborde, nomenclature regroupée puis vues à l'échelle suivante.
        var scalable = items.Where(i => i.View is ViewSection or ViewPlan).Select(i => i.View!).ToList();
        int[] steps = { 20, 25, 50, 100 };
        // Cotes et annotations avant composition (l'emprise des vues en tient compte), refaites à chaque changement d'échelle
        var annotations = annotate?.Invoke() ?? new List<ElementId>();
        CompositionResult res;
        for (int attempt = 0; ; attempt++)
        {
            _doc.Regenerate();
            res = composer.Compose(items, k => sheet, tryCompact: true, area);
            if (res.PageCount <= 1 || attempt >= 4) break;
            foreach (var id in new FilteredElementCollector(_doc, sheet.Id).OfClass(typeof(Viewport)).ToElementIds().ToList()) _doc.Delete(id);
            foreach (var id in new FilteredElementCollector(_doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                         .Where(x => !x.IsTitleblockRevisionSchedule).Select(x => x.Id).ToList()) _doc.Delete(id);
            if (attempt == 0 && sched.Definition.IsItemized && Identity.Get(sched) != null)
            {
                sched.Definition.IsItemized = false;
                report.Issue(Severity.Information, "fiche_nomenclature", number, "Nomenclature trop longue pour la fiche : occurrences regroupées par localisation.",
                    "Aucune action nécessaire.", sheet.Id.Value);
                continue;
            }
            if (scalable.Count == 0) break;
            foreach (var id in annotations.Where(id => _doc.GetElement(id) != null)) _doc.Delete(id);
            foreach (var v in scalable)
            {
                var next = steps.FirstOrDefault(x => x > v.Scale);
                v.Scale = next == 0 ? v.Scale * 2 : next;
            }
            annotations = annotate?.Invoke() ?? new List<ElementId>();
        }
        if (res.PageCount > 1)
            report.Issue(Severity.ARevoir, "debordement", number, "Contenu trop grand pour une fiche A3 : éléments superposés.",
                "Recomposer la fiche (ou la répartir sur deux fiches).", sheet.Id.Value);
        foreach (var o in res.Oversize)
            report.Issue(Severity.ARevoir, "debordement", $"{number} / {o}", "Élément hors zone de dessin.", "Recomposer la fiche manuellement.", sheet.Id.Value);

        var props = new ManagedProps();
        props.Values["texte"] = NormText(note.Text);
        Identity.Set(sheet, new IdentityData { Key = Key(jt.Symbol), Role = "fiche", Payload = $"{jt.Lot}|{index}", Generated = props.Serialize() });
        Identity.Set(note, new IdentityData { Key = Key(jt.Symbol) + "|TEXTE", Role = "fiche_texte" });
        Identity.Set(title, new IdentityData { Key = Key(jt.Symbol) + "|TITRE", Role = "fiche_titre" });
        report.Created.Add($"Feuille {sheet.SheetNumber} {sheet.Name} — {jt.Instances.Count} u" + (agencyFormat ? " (nomenclature au format de l'agence)" : ""));
    }

    private double TitleSize(FicheNorm? norm) =>
        norm?.TitleTextType is { } id && _doc.GetElement(id) is TextNoteType t && KdResources.SizeMm(t) is > 1 and var s ? Math.Round(s * 2) / 2 : 6;

    private void UpdateFiche(JoineryType jt, Report report)
    {
        var sheet = jt.Fiche!;
        CompleteAnnotations(jt, report);
        var id = Identity.Get(sheet)!;
        var props = ManagedProps.Parse(id.Generated);
        var note = new FilteredElementCollector(_doc, sheet.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
            .FirstOrDefault(t => Identity.Get(t)?.Role == "fiche_texte");
        if (note == null)
        {
            report.Kept.Add($"{sheet.SheetNumber} : bloc descriptif supprimé manuellement — non recréé");
            return;
        }
        var desired = DescriptionText(jt);
        switch (props.Decide("texte", NormText(note.Text), NormText(desired)))
        {
            case ManagedProps.Decision.Unchanged:
                report.Unchanged.Add($"Fiche {sheet.SheetNumber} {jt.Mark}");
                break;
            case ManagedProps.Decision.Update:
                note.Text = desired;
                note.ChangeTypeId(_res.CenturyText(2.5, bold: false, report.Created));
                _doc.Regenerate();
                props.Values["texte"] = NormText(note.Text);
                id.Generated = props.Serialize();
                Identity.Set(sheet, id);
                report.Updated.Add($"Fiche {sheet.SheetNumber} {jt.Mark} : bloc descriptif et quantités mis à jour");
                break;
            default:
                report.Kept.Add($"Fiche {sheet.SheetNumber} {jt.Mark} : bloc descriptif retouché conservé (quantité actuelle : {jt.Instances.Count} u)");
                break;
        }
    }

    /// <summary>Cotes et annotations des vues générées d'une fiche (élévation, plan), avec la règle d'annotation du lot.</summary>
    private List<ElementId> Annotate(JoineryType jt, FamilyInstance rep, ViewSection elev, ViewPlan? plan, Report report)
    {
        var ids = new List<ElementId>();
        if (!_data.Profile.Cotation.Fiches) return ids;
        _doc.Regenerate();
        var isDoor = rep.Category?.Id.Value == (long)BuiltInCategory.OST_Doors;
        var rule = _data.Profile.Menuiseries.AnnotationsFor(jt.Lot, isDoor, jt.Mark);
        _dims.JoineryElevation(elev, rep, rule, report.Created, () => _res.CenturyText(_data.Profile.Cotation.TexteMm, bold: false, report.Created));
        ids.AddRange(_dims.Last);
        MarkAnnotated(elev);
        if (plan != null)
        {
            _dims.JoineryPlan(plan, rep, report.Created);
            ids.AddRange(_dims.Last);
            MarkAnnotated(plan);
        }
        return ids;
    }

    private static bool IsAnnotated(View v) => Identity.Get(v) is { } d && ManagedProps.Parse(d.Generated).Values.ContainsKey("cotes");

    private static void MarkAnnotated(View v)
    {
        var d = Identity.Get(v);
        if (d == null) return;
        var props = ManagedProps.Parse(d.Generated);
        props.Values["cotes"] = "1";
        d.Generated = props.Serialize();
        Identity.Set(v, d);
    }

    /// <summary>Fiches des versions précédentes : cotes et annotations ajoutées aux vues générées qui n'en ont pas.</summary>
    private void CompleteAnnotations(JoineryType jt, Report report)
    {
        var idx = Identity.Index(_doc);
        if (!idx.TryGetValue(Key(jt.Symbol) + "|ELEV", out var e) || e is not ViewSection elev || IsAnnotated(elev)) return;
        var rep = Representative(jt);
        if (rep == null) return;
        var plan = idx.TryGetValue(Key(jt.Symbol) + "|PLAN", out var p) ? p as ViewPlan : null;
        Annotate(jt, rep, elev, plan != null && !IsAnnotated(plan) ? plan : null, report);
        report.Updated.Add($"Fiche {jt.Fiche?.SheetNumber} {jt.Mark} : cotes et annotations ajoutées aux vues générées");
    }

    private static string NormText(string s) => s.Replace("\r\n", "\r").Replace("\n", "\r").TrimEnd('\r', ' ');

    internal string DescriptionText(JoineryType jt)
    {
        var lot = _data.Profile.Menuiseries.Lots.FirstOrDefault(l => l.Code == jt.Lot);
        var sb = new StringBuilder();
        sb.AppendLine($"{jt.Mark} — {jt.Symbol.FamilyName}");
        sb.AppendLine($"Lot : {jt.Lot} {lot?.Libelle}".TrimEnd());
        sb.AppendLine($"Dimensions : L {F(jt.WidthMm)} × H {F(jt.HeightMm)} mm");
        sb.AppendLine($"Quantité : {jt.Instances.Count} u");
        var rep = _data.Profile.Menuiseries.RepetitionsBatiment;
        if (rep > 1)
            sb.AppendLine($"Total opération (bâtiment type × {rep}) : {jt.Instances.Count * rep} u");
        sb.AppendLine("Localisations :");
        foreach (var g in jt.Instances.GroupBy(i => (_doc.GetElement(i.LevelId) as Level)?.Name ?? "—").OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key} : {g.Count()} u");
        if (string.IsNullOrWhiteSpace(jt.Prescriptions)) sb.Append("Prescriptions : N/A");
        else
        {
            sb.AppendLine("Prescriptions (proposition IA à valider) :");
            sb.Append(jt.Prescriptions.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        }
        return sb.ToString();
    }

    private static string F(double? v) => v.HasValue ? Math.Round(v.Value).ToString("#,0", CultureInfo.GetCultureInfo("fr-FR")) : "N/A";

    private static FamilyInstance? Representative(JoineryType jt) =>
        jt.Instances.Where(i => i.Location is LocationPoint).OrderBy(i => (i.Document.GetElement(i.LevelId) as Level)?.Elevation ?? 0)
            .ThenBy(i => i.Id.Value).FirstOrDefault();

    private (ViewSection Elev, ViewPlan? Plan) GeneratedViews(JoineryType jt, FamilyInstance fi, RectMm area)
    {
        var p = ((LocationPoint)fi.Location).Point;
        var facing = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
        facing = facing.IsZeroLength() ? XYZ.BasisY : facing.Normalize();
        var look = facing.Negate();
        var along = XYZ.BasisZ.CrossProduct(look);
        var bb = fi.get_BoundingBox(null);
        var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) };
        var halfW = corners.Max(c => Math.Abs((c - p).DotProduct(along)));
        var z0 = bb.Min.Z;
        var h = bb.Max.Z - bb.Min.Z;
        var thick = (fi.Host as Wall)?.Width ?? Units.Mm(200);
        double m = Units.Mm(300);

        var tr = Transform.Identity;
        tr.Origin = new XYZ(p.X, p.Y, z0);
        tr.BasisZ = look;
        tr.BasisY = XYZ.BasisZ;
        tr.BasisX = along;
        var box = new BoundingBoxXYZ
        {
            Transform = tr,
            Min = new XYZ(-halfW - m, -Units.Mm(200), -(thick / 2 + m)),
            Max = new XYZ(halfW + m, h + Units.Mm(300), thick / 2 + m),
        };
        var elev = ViewSection.CreateSection(_doc, _res.ViewFamilyType("type_vue_coupe", ViewFamily.Section), box);
        Apply(elev, _res.ViewTemplate("gabarit_calepin_coupe"));
        elev.Scale = PickScale(2 * (halfW + m), h + Units.Mm(500), area);
        var coarser = elev.get_Parameter(BuiltInParameter.SECTION_COARSER_SCALE_PULLDOWN_METRIC);
        if (coarser != null && !coarser.IsReadOnly && coarser.StorageType == StorageType.Integer) coarser.Set(_data.Profile.Carnets.MasquerReperesAuDelaDe);
        elev.Name = RevitUtil.UniqueViewName(_doc, $"FICHE {jt.Mark} - Élévation");
        RevitUtil.SetTitleOnSheet(elev, $"{jt.Mark} - ELEVATION");
        RevitUtil.CropAnnotations(elev);
        Identity.Set(elev, new IdentityData { Key = Key(jt.Symbol) + "|ELEV", Role = "fiche_vue" });

        ViewPlan? plan = null;
        if (fi.LevelId != ElementId.InvalidElementId)
        {
            plan = ViewPlan.Create(_doc, _res.ViewFamilyType("type_vue_plan", ViewFamily.FloorPlan), fi.LevelId);
            Apply(plan, _res.ViewTemplate("gabarit_calepin_plan"));
            var pm = Units.Mm(500);
            var cb = plan.CropBox;
            cb.Min = new XYZ(bb.Min.X - pm, bb.Min.Y - pm, cb.Min.Z);
            cb.Max = new XYZ(bb.Max.X + pm, bb.Max.Y + pm, cb.Max.Z);
            plan.CropBox = cb;
            plan.CropBoxActive = true;
            plan.CropBoxVisible = false;
            plan.Scale = elev.Scale;
            plan.Name = RevitUtil.UniqueViewName(_doc, $"FICHE {jt.Mark} - Plan");
            RevitUtil.SetTitleOnSheet(plan, $"{jt.Mark} - PLAN");
            RevitUtil.CropAnnotations(plan);
            Identity.Set(plan, new IdentityData { Key = Key(jt.Symbol) + "|PLAN", Role = "fiche_vue" });
        }
        return (elev, plan);
    }

    private static int PickScale(double wFt, double hFt, RectMm area)
    {
        foreach (var s in new[] { 20, 25, 50, 100 })
            if (Units.ToMm(wFt) / s <= area.Width * 0.6 && Units.ToMm(hFt) / s <= area.Height * 0.7) return s;
        return 100;
    }

    private static void Apply(View v, ElementId tpl)
    {
        if (tpl != ElementId.InvalidElementId && v.IsValidViewTemplate(tpl))
            v.ApplyViewTemplateParameters((View)v.Document.GetElement(tpl));
    }

    /// <summary>
    /// Nomenclature de localisation : « Tableau quantitatif_repère » existante, sinon copie du modèle de l'agence
    /// (même catégorie) filtrée sur le type, sinon nomenclature générée.
    /// </summary>
    private (ViewSchedule Schedule, bool AgencyFormat) LocationSchedule(JoineryType jt, Report report)
    {
        var name = ProjectNorms.QuantityScheduleName(jt.Mark);
        var existing = new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .FirstOrDefault(v => !v.IsTemplate && TextNorm.SameResource(v.Name, name));
        if (existing != null)
        {
            report.Created.Add($"{jt.Mark} : nomenclature de l'agence « {existing.Name} » réutilisée");
            return (existing, true);
        }
        if (_norms.QuantitySchedule.TryGetValue(jt.Symbol.Category.Id, out var model))
        {
            var dupId = model.Duplicate(ViewDuplicateOption.Duplicate);
            var dup = (ViewSchedule)_doc.GetElement(dupId);
            var def = dup.Definition;
            var filters = def.GetFilters();
            for (int i = 0; i < filters.Count; i++)
                if (filters[i].IsElementIdValue)
                    def.SetFilter(i, new ScheduleFilter(filters[i].FieldId, filters[i].FilterType, jt.Symbol.Id));
            dup.Name = RevitUtil.UniqueViewName(_doc, name);
            Identity.Set(dup, new IdentityData { Key = Key(jt.Symbol) + "|LOC", Role = "fiche_localisation" });
            return (dup, true);
        }
        return (GeneratedSchedule(jt, name), false);
    }

    private ViewSchedule GeneratedSchedule(JoineryType jt, string name)
    {
        var vs = ViewSchedule.CreateSchedule(_doc, jt.Symbol.Category.Id);
        vs.Name = RevitUtil.UniqueViewName(_doc, name);
        var def = vs.Definition;
        var level = ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                    ?? ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.FAMILY_LEVEL_PARAM);
        var toRoom = ScheduleUtil.AddRoomField(vs, ScheduleFieldType.ToRoom, BuiltInParameter.ROOM_NAME);
        ScheduleUtil.AddRoomField(vs, ScheduleFieldType.FromRoom, BuiltInParameter.ROOM_NAME);
        ScheduleUtil.AddCount(vs);
        var type = ScheduleUtil.AddBuiltIn(vs, BuiltInParameter.ELEM_TYPE_PARAM);
        if (type != null)
        {
            type.IsHidden = true;
            def.AddFilter(new ScheduleFilter(type.FieldId, ScheduleFilterType.Equal, jt.Symbol.Id));
        }
        if (level != null) def.AddSortGroupField(new ScheduleSortGroupField(level.FieldId));
        if (toRoom != null) def.AddSortGroupField(new ScheduleSortGroupField(toRoom.FieldId));
        def.IsItemized = false;
        def.ShowGrandTotal = true;
        def.ShowGrandTotalCount = true;
        def.ShowGrandTotalTitle = true;
        Identity.Set(vs, new IdentityData { Key = Key(jt.Symbol) + "|LOC", Role = "fiche_localisation" });
        return vs;
    }

    /// <summary>Entrées pour l'IA (mode avion par terre).</summary>
    public static JoineryInput Input(JoineryType t) => new()
    {
        Mark = t.Mark,
        Family = t.Symbol.FamilyName,
        Category = t.Symbol.Category?.Name ?? "",
        WidthMm = t.WidthMm,
        HeightMm = t.HeightMm,
        Count = t.Instances.Count,
        Lot = t.Lot,
    };
}
