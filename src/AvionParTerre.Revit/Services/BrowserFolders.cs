using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Joinery;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

/// <summary>
/// Rangement du navigateur de projet. L'API Revit ne modifie pas l'organisation du navigateur : le plugin écrit la valeur des
/// paramètres qui la pilotent. Le niveau de regroupement qui porte le dossier « DCE » (relevé sur les éléments de l'agence,
/// sinon le premier) reçoit « DCE » ; le niveau suivant reçoit le sous-dossier (PLANS GENERAUX, DETAILS DE PIECES, CALEPIN BOIS…).
/// Les feuilles de l'agence rangées dans un dossier « CALEPIN … » de premier niveau passent dans « DCE », dossier conservé en sous-dossier.
/// </summary>
internal sealed class BrowserFolders
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly FolderRules _rules;
    private readonly JoineryClassifier _classifier;

    public BrowserFolders(Document doc, PluginData data)
    {
        _doc = doc;
        _data = data;
        _rules = data.Profile.Rangement;
        _classifier = new JoineryClassifier(data.Profile.Menuiseries);
    }

    private sealed class Section
    {
        public string Label = "";
        public BrowserOrganization? Org;
        /// <summary>Niveau de regroupement du dossier DCE (-1 : aucun dossier DCE, section laissée en place).</summary>
        public int Level = -1;
    }

    private static List<FolderItemInfo> Path(Section s, Element e)
    {
        if (s.Org == null) return new List<FolderItemInfo>();
        try { return s.Org.GetFolderItems(e.Id).ToList(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return new List<FolderItemInfo>(); }
    }

    private bool IsDce(string? name) => TextNorm.SameResource(name ?? "", _rules.Dossier);

    private bool ToMove(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _rules.ARangerDansDossier.Any(p => TextNorm.Normalize(name!).StartsWith(TextNorm.Normalize(p), StringComparison.Ordinal));

    /// <summary>Niveau portant le dossier DCE, relevé sur des éléments existants de l'agence.</summary>
    private int DceLevel(Section s, IEnumerable<Element> sample)
    {
        foreach (var e in sample.Take(400))
        {
            var path = Path(s, e);
            var i = path.FindIndex(f => IsDce(f.Name));
            if (i >= 0) return i;
        }
        return -1;
    }

    private Parameter? FolderParam(Element e, ElementId paramId)
    {
        if (paramId.Value < 0)
            return Enum.IsDefined(typeof(BuiltInParameter), (int)paramId.Value) ? e.get_Parameter((BuiltInParameter)(int)paramId.Value) : null;
        foreach (Parameter p in e.Parameters)
            if (p.Id == paramId) return p;
        return null;
    }

    private string ParamName(ElementId id) =>
        id.Value < 0 && Enum.IsDefined(typeof(BuiltInParameter), (int)id.Value)
            ? LabelUtils.GetLabelFor((BuiltInParameter)(int)id.Value)
            : _doc.GetElement(id)?.Name ?? id.Value.ToString();

    /// <summary>Écrit la valeur d'un niveau de dossier ; faux si le paramètre n'est pas un texte modifiable.</summary>
    private bool Set(Element e, FolderItemInfo f, string value, out bool changed)
    {
        changed = false;
        var p = FolderParam(e, f.ElementId);
        if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
        if (p.AsString() == value) return true;
        changed = p.Set(value);
        return changed;
    }

    public void Arrange(Report report, WarningCollector w)
    {
        if (!_rules.Actif) return;
        var managed = Identity.All(_doc).Where(x => x.Element is View v && !v.IsTemplate).ToList();
        var managedIds = managed.Select(x => x.Element.Id).ToHashSet();
        var sheets = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
        var agencySheets = sheets.Where(s => !managedIds.Contains(s.Id)).ToList();

        var sheetSection = new Section { Label = "Feuilles", Org = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(_doc) };
        var viewSection = new Section { Label = "Vues", Org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(_doc) };
        var schedSection = new Section { Label = "Nomenclatures", Org = BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(_doc) };
        sheetSection.Level = DceLevel(sheetSection, agencySheets);
        if (sheetSection.Level < 0) sheetSection.Level = 0;
        var views = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && !managedIds.Contains(v.Id)).ToList();
        viewSection.Level = DceLevel(viewSection, views.Where(v => v is not ViewSheet && v is not ViewSchedule));
        schedSection.Level = DceLevel(schedSection, views.OfType<ViewSchedule>());

        // Sous-dossiers relevés chez l'agence : plans rangés dans DCE, calepins par lot
        var plansSub = MostCommon(agencySheets.Where(ProjectPlanSheet).Select(s => SubOf(sheetSection, s))) ?? _rules.SousDossierPlans;
        var lotSub = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in agencySheets.Select(s => (Sheet: s, Lot: _classifier.Classify(s.Name.Trim())?.Lot)).Where(x => x.Lot != null).GroupBy(x => x.Lot!))
        {
            var sub = MostCommon(g.Select(x => CalepinOf(sheetSection, x.Sheet)));
            if (sub != null) lotSub[g.Key] = sub;
        }

        var tasks = new List<(Element E, Section S, string Sub, bool Agency)>();
        foreach (var (e, d) in managed)
        {
            var sub = SubFolder(d, plansSub, lotSub);
            if (sub == null) continue;
            var section = e is ViewSheet ? sheetSection : e is ViewSchedule ? schedSection : viewSection;
            tasks.Add((e, section, sub, false));
        }
        foreach (var s in agencySheets)
        {
            var path = Path(sheetSection, s);
            var l = sheetSection.Level;
            if (path.Count > l + 1 && ToMove(path[l].Name)) tasks.Add((s, sheetSection, path[l].Name, true));
        }
        if (tasks.Count == 0) return;

        using var t = WarningCollector.Start(_doc, "Avion par terre — rangement du navigateur", w);
        var notes = new HashSet<string>();
        int moved = 0, agency = 0;
        foreach (var (e, section, sub, isAgency) in tasks)
        {
            if (section.Level < 0)
            {
                notes.Add($"{section.Label} : aucun dossier « {_rules.Dossier} » dans l'organisation du navigateur — éléments laissés en place.");
                continue;
            }
            var path = Path(section, e);
            var l = section.Level;
            if (path.Count <= l)
            {
                notes.Add($"{section.Label} : organisation du navigateur sans regroupement par paramètre — rangement impossible par l'API.");
                continue;
            }
            if (!Set(e, path[l], _rules.Dossier, out var c1))
            {
                notes.Add($"{section.Label} : le dossier de niveau {l + 1} dépend de « {ParamName(path[l].ElementId)} », non modifiable.");
                continue;
            }
            var c2 = false;
            if (path.Count > l + 1)
            {
                if (!Set(e, path[l + 1], sub, out c2))
                    notes.Add($"{section.Label} : le sous-dossier dépend de « {ParamName(path[l + 1].ElementId)} », non modifiable.");
            }
            else notes.Add($"{section.Label} : un seul niveau de dossier (« {ParamName(path[l].ElementId)} ») — ajouter un second regroupement dans l'organisation du navigateur pour les sous-dossiers.");
            if (!c1 && !c2) continue;
            if (isAgency)
            {
                agency++;
                report.Updated.Add($"Feuille de l'agence {((ViewSheet)e).SheetNumber} {e.Name} : rangée dans « {_rules.Dossier} / {sub} »");
            }
            else moved++;
        }
        t.Commit();
        if (moved > 0) report.Updated.Add($"{moved} élément(s) généré(s) rangé(s) dans le dossier « {_rules.Dossier} » du navigateur");
        foreach (var n in notes)
            report.Issue(Severity.Information, "rangement", "Navigateur de projet", n,
                "Vue > Interface utilisateur > Organisation du navigateur : regrouper par le paramètre du dossier, puis par celui du sous-dossier.");
    }

    private string? SubOf(Section s, Element e)
    {
        var path = Path(s, e);
        var l = s.Level;
        return path.Count > l + 1 && IsDce(path[l].Name) ? path[l + 1].Name : null;
    }

    /// <summary>Dossier de calepin d'une fiche de l'agence : « CALEPIN BOIS » au premier niveau, ou déjà rangé sous DCE.</summary>
    private string? CalepinOf(Section s, Element e)
    {
        var path = Path(s, e);
        var l = s.Level;
        if (path.Count > l && ToMove(path[l].Name)) return path[l].Name;
        return SubOf(s, e);
    }

    private static bool ProjectPlanSheet(ViewSheet s)
    {
        var n = TextNorm.Normalize(s.Name);
        return n.StartsWith("plan", StringComparison.Ordinal);
    }

    private static string? MostCommon(IEnumerable<string?> items) =>
        items.Where(i => !string.IsNullOrWhiteSpace(i)).GroupBy(i => i!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    private string? SubFolder(IdentityData d, string plansSub, Dictionary<string, string> lotSub)
    {
        var role = d.Role;
        if (role.StartsWith("plan_general", StringComparison.Ordinal)) return plansSub;
        if (role is "coupes_facades" or "coupe_vue" or "facade_vue") return _rules.SousDossierCoupesFacades;
        if (role.StartsWith("carnet", StringComparison.Ordinal)) return _rules.SousDossierCarnets;
        if (role.StartsWith("fiche", StringComparison.Ordinal))
        {
            var parts = d.Key.Split('|');
            var mark = parts.Length > 1 && _doc.GetElement(parts[1]) is FamilySymbol fs ? fs.Name.Trim() : null;
            var lot = mark != null ? _classifier.Classify(mark)?.Lot : d.Payload.Split('|').FirstOrDefault();
            if (lot != null && lotSub.TryGetValue(lot, out var agency)) return agency;
            return _data.Profile.Menuiseries.Lots.FirstOrDefault(l => l.Code == lot)?.SousDossier ?? _rules.SousDossierFiches;
        }
        return null;
    }
}
