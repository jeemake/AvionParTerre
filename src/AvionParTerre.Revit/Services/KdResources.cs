using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

internal enum ResourceKind { Symbol, ViewTemplate, TextType }

/// <summary>Rôle de ressource K&amp;D (clé de profile.json) et catégorie Revit correspondante.</summary>
internal sealed record ResourceRole(string Key, string Label, ResourceKind Kind, BuiltInCategory Category = BuiltInCategory.INVALID);

/// <summary>
/// Résolution des ressources K&amp;D (cartouches, gabarits, étiquettes, titres, textes). Ordre :
/// 1. choix de l'utilisateur pour ce projet (Paramètres) ; 2. noms du profil, sans tenir compte des accents ni de la casse ;
/// 3. même famille chargée sous un autre nom (« Cartouche A3 Horizontale1 ») ; 4. ressource disponible équivalente
/// (cartouche du même format, étiquette « DCE » d'échelle proche…). Un remplacement est signalé, jamais silencieux.
/// </summary>
internal sealed class KdResources
{
    public const string TitleFamilyName = "Avion par terre - Titre de vue";
    public const string Font = "Century Gothic";

    public static readonly ResourceRole[] Roles =
    {
        new("cartouche_A0", "Cartouche A0 (plans)", ResourceKind.Symbol, BuiltInCategory.OST_TitleBlocks),
        new("cartouche_A1", "Cartouche A1 (plans)", ResourceKind.Symbol, BuiltInCategory.OST_TitleBlocks),
        new("cartouche_A3", "Cartouche A3 (carnets de pièces)", ResourceKind.Symbol, BuiltInCategory.OST_TitleBlocks),
        new("cartouche_fiche", "Cartouche des fiches menuiseries", ResourceKind.Symbol, BuiltInCategory.OST_TitleBlocks),
        new("gabarit_plan_1_50", "Gabarit plan 1:50", ResourceKind.ViewTemplate),
        new("gabarit_plan_1_100", "Gabarit plan 1:100", ResourceKind.ViewTemplate),
        new("gabarit_plan_1_200", "Gabarit plan 1:200", ResourceKind.ViewTemplate),
        new("gabarit_plan_piece", "Gabarit plan de pièce", ResourceKind.ViewTemplate),
        new("gabarit_elevation_piece", "Gabarit élévation de pièce", ResourceKind.ViewTemplate),
        new("gabarit_3d", "Gabarit vue 3D", ResourceKind.ViewTemplate),
        new("gabarit_calepin_plan", "Gabarit calepin (plan)", ResourceKind.ViewTemplate),
        new("gabarit_calepin_coupe", "Gabarit calepin (coupe)", ResourceKind.ViewTemplate),
        new("gabarit_coupe", "Gabarit des coupes (plans généraux)", ResourceKind.ViewTemplate),
        new("gabarit_facade", "Gabarit des façades (plans généraux)", ResourceKind.ViewTemplate),
        new("etiquette_piece_1_50", "Étiquette de pièce 1:50", ResourceKind.Symbol, BuiltInCategory.OST_RoomTags),
        new("etiquette_piece_1_100", "Étiquette de pièce 1:100", ResourceKind.Symbol, BuiltInCategory.OST_RoomTags),
        new("etiquette_piece_detail", "Étiquette de pièce (carnets)", ResourceKind.Symbol, BuiltInCategory.OST_RoomTags),
        new("etiquette_porte_1_50", "Étiquette de porte 1:50", ResourceKind.Symbol, BuiltInCategory.OST_DoorTags),
        new("etiquette_porte_1_100", "Étiquette de porte 1:100", ResourceKind.Symbol, BuiltInCategory.OST_DoorTags),
        new("etiquette_fenetre_1_50", "Étiquette de fenêtre 1:50", ResourceKind.Symbol, BuiltInCategory.OST_WindowTags),
        new("etiquette_fenetre_1_100", "Étiquette de fenêtre 1:100", ResourceKind.Symbol, BuiltInCategory.OST_WindowTags),
        new("titre_vue_famille", "Famille de titre de vue (texte simple)", ResourceKind.Symbol, BuiltInCategory.OST_ViewportLabel),
        new("texte_fiche", "Texte des fiches", ResourceKind.TextType),
        new("texte_fiche_titre", "Titre des fiches", ResourceKind.TextType),
    };

    private readonly Document _doc;
    private readonly PluginProfile _profile;
    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly Dictionary<string, ElementId> _cache = new();
    public List<Issue> Missing { get; } = new();

    public KdResources(Document doc, PluginProfile profile, ProjectChoices? choices = null)
    {
        _doc = doc;
        _profile = profile;
        _overrides = choices?.Ressources ?? new Dictionary<string, string>();
    }

    public KdResources(Document doc, PluginData data) : this(doc, data.Profile, data.Choices) { }

    // -----------------------------------------------------------------------------------------
    // Familles
    // -----------------------------------------------------------------------------------------

    public static string SymbolName(FamilySymbol s) => $"{s.FamilyName}:{s.Name}";

    private IEnumerable<FamilySymbol> Symbols(BuiltInCategory cat) =>
        new FilteredElementCollector(_doc).OfCategory(cat).WhereElementIsElementType().OfType<FamilySymbol>();

    private static FamilySymbol? FindSymbol(IEnumerable<FamilySymbol> symbols, string name, bool stem)
    {
        var parts = name.Split(new[] { ':' }, 2);
        Func<string?, string?, bool> famEq = stem
            ? (a, b) => TextNorm.FamilyStem(a) == TextNorm.FamilyStem(b)
            : TextNorm.SameResource;
        return symbols.FirstOrDefault(s => parts.Length == 2
            ? famEq(s.FamilyName, parts[0]) && TextNorm.SameResource(s.Name, parts[1])
            : TextNorm.SameResource(s.Name, name) || famEq(s.FamilyName, name));
    }

    /// <summary>Type de famille « Famille:Type » d'une catégorie donnée.</summary>
    public ElementId Symbol(string key, BuiltInCategory cat, Func<IEnumerable<FamilySymbol>, (FamilySymbol? Symbol, string Why)>? substitute = null)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var all = Symbols(cat).ToList();
        var names = _profile.Resource(key);
        FamilySymbol? found = null;
        if (_overrides.TryGetValue(key, out var ov)) found = FindSymbol(all, ov, stem: false);
        // Pour chaque nom, dans l'ordre du profil : nom exact, puis même famille rechargée sous un autre nom
        var stemmed = false;
        if (found == null)
            foreach (var n in names)
            {
                found = FindSymbol(all, n, stem: false);
                if (found == null) { found = FindSymbol(all, n, stem: true); stemmed = found != null; }
                if (found != null) break;
            }
        if (stemmed && found != null)
            Missing.Add(new Issue(Severity.Information, "ressource_kd", key,
                $"Nom exact introuvable : « {SymbolName(found)} » (même famille, autre nom) utilisé.",
                "Aucune action nécessaire ; sinon choisir la ressource dans Paramètres."));
        if (found == null && substitute != null)
        {
            var (sub, why) = substitute(all);
            found = sub;
            if (sub != null)
                Missing.Add(new Issue(Severity.Information, "ressource_remplacement", key,
                    $"« {string.Join(" / ", names)} » introuvable : « {SymbolName(sub)} » utilisé ({why}).",
                    "Vérifier le rendu ; imposer une autre ressource dans Paramètres si besoin."));
        }
        if (found == null)
            Missing.Add(new Issue(Severity.ARevoir, "ressource_kd", key,
                $"Ressource K&D introuvable : {string.Join(" / ", names)}.", "Charger la ressource du gabarit agence ou la choisir dans Paramètres."));
        var id = found?.Id ?? ElementId.InvalidElementId;
        _cache[key] = id;
        return id;
    }

    // -----------------------------------------------------------------------------------------
    // Cartouches
    // -----------------------------------------------------------------------------------------

    private static readonly Dictionary<string, (double W, double H)> Paper = new()
    {
        ["A0"] = (1189, 841), ["A1"] = (841, 594), ["A2"] = (594, 420), ["A3"] = (420, 297), ["A4"] = (297, 210),
    };

    /// <summary>Format papier d'un cartouche : mesuré sur les feuilles existantes, sinon déduit du nom (« (A3) », « A3-Paysage »).</summary>
    public string? PaperFormat(FamilySymbol s)
    {
        var inst = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
            .OfType<FamilyInstance>().FirstOrDefault(i => i.Symbol.Id == s.Id);
        if (inst != null && _doc.GetElement(inst.OwnerViewId) is View sheet && inst.get_BoundingBox(sheet) is { } bb)
        {
            double w = Units.ToMm(bb.Max.X - bb.Min.X), h = Units.ToMm(bb.Max.Y - bb.Min.Y);
            var (big, small) = (Math.Max(w, h), Math.Min(w, h));
            var f = Paper.Where(p => Math.Abs(p.Value.W - big) < 25 && Math.Abs(p.Value.H - small) < 25).Select(p => p.Key).FirstOrDefault();
            if (f != null) return f;
        }
        var fam = Regex.Match(s.FamilyName, @"\((A\d)\)", RegexOptions.IgnoreCase);
        if (fam.Success) return fam.Groups[1].Value.ToUpperInvariant();
        var type = Regex.Match(s.Name, @"\b(A\d)\b", RegexOptions.IgnoreCase);
        return type.Success ? type.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static bool IsCoverSheet(FamilySymbol s) =>
        Regex.IsMatch(TextNorm.ResourceKey(SymbolName(s)), "garde|couverture|vide|cover");

    private static int DceRank(FamilySymbol s)
    {
        var k = TextNorm.ResourceKey(s.FamilyName);
        return k.Contains("apd-dce") ? 0 : k.Contains("dce") ? 1 : k.Contains("horizontale") ? 2 : 3;
    }

    /// <summary>Cartouche d'un format ; à défaut, un cartouche disponible du même format, sinon du format le plus proche.</summary>
    public ElementId TitleBlock(string format) => TitleBlockFor($"cartouche_{format}", format);

    public ElementId TitleBlockFor(string key, string format) => Symbol(key, BuiltInCategory.OST_TitleBlocks, all =>
    {
        var usable = all.Where(s => !IsCoverSheet(s)).Select(s => (S: s, F: PaperFormat(s))).ToList();
        var same = usable.Where(x => x.F == format).OrderBy(x => DceRank(x.S)).Select(x => x.S).FirstOrDefault();
        if (same != null) return (same, $"cartouche {format} disponible");
        // Format le plus proche (plans : un A0 remplace un A1 manquant)
        var order = Paper.Keys.ToList();
        var target = order.IndexOf(format);
        var near = usable.Where(x => x.F != null).OrderBy(x => Math.Abs(order.IndexOf(x.F!) - target)).ThenBy(x => order.IndexOf(x.F!))
            .ThenBy(x => DceRank(x.S)).FirstOrDefault();
        if (near.S != null) return (near.S, $"aucun cartouche {format} : format {near.F} le plus proche");
        var any = usable.OrderBy(x => DceRank(x.S)).Select(x => x.S).FirstOrDefault();
        return (any, "seul cartouche disponible");
    });

    // -----------------------------------------------------------------------------------------
    // Étiquettes
    // -----------------------------------------------------------------------------------------

    /// <summary>Échelle portée par le nom de famille (« Etiquette de pièce 50è » → 50).</summary>
    private static int? ScaleIn(FamilySymbol s)
    {
        var m = Regex.Match(s.FamilyName, @"(\d{2,4})\s*(e|è|ème|eme)\b", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static Func<IEnumerable<FamilySymbol>, (FamilySymbol?, string)> NearestTag(int scale, string? keyword) => all =>
    {
        var list = all.ToList();
        var pool = keyword == null ? list : list.Where(s => TextNorm.ResourceKey(SymbolName(s)).Contains(keyword)).ToList();
        if (pool.Count == 0) pool = list;
        var best = pool.OrderBy(s => ScaleIn(s) is { } k ? Math.Abs(Math.Log((double)k / scale)) : 10).FirstOrDefault();
        return (best, keyword != null && pool != list ? $"type « {keyword.ToUpperInvariant()} » d'échelle la plus proche" : "échelle la plus proche");
    };

    public ElementId RoomTagForScale(int scale) =>
        Symbol(scale <= 50 ? "etiquette_piece_1_50" : "etiquette_piece_1_100", BuiltInCategory.OST_RoomTags, NearestTag(scale, "dce"));

    public ElementId RoomTagDetail() =>
        Symbol("etiquette_piece_detail", BuiltInCategory.OST_RoomTags, NearestTag(20, "dce"));

    public ElementId DoorTagForScale(int scale) =>
        Symbol(scale <= 50 ? "etiquette_porte_1_50" : "etiquette_porte_1_100", BuiltInCategory.OST_DoorTags, NearestTag(scale, null));

    public ElementId WindowTagForScale(int scale) =>
        Symbol(scale <= 50 ? "etiquette_fenetre_1_50" : "etiquette_fenetre_1_100", BuiltInCategory.OST_WindowTags, NearestTag(scale, null));

    // -----------------------------------------------------------------------------------------
    // Vues, textes
    // -----------------------------------------------------------------------------------------

    /// <param name="optional">Ressource facultative : son absence est une simple information.</param>
    public ElementId ViewTemplate(string key, bool optional = false) => Resolve(key, name =>
        new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && TextNorm.SameResource(v.Name, name))?.Id, optional: optional);

    public ElementId ViewFamilyType(string key, ViewFamily family) => Resolve(key, name =>
        new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family && TextNorm.SameResource(t.Name, name))?.Id,
        fallback: () => new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)?.Id);

    public ElementId TextType(string key) => Resolve(key, name =>
        new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType))
            .FirstOrDefault(t => TextNorm.SameResource(t.Name, name))?.Id,
        fallback: () => _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType));

    public static string FontOf(TextNoteType t) => t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString() ?? "";

    public static double SizeMm(TextNoteType t) => Units.ToMm(t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0);

    /// <summary>
    /// Type de texte Century Gothic (charte K&amp;D) de la taille demandée : un type existant du modèle si possible,
    /// sinon un type « Avion par terre - Century Gothic n mm » dérivé du type par défaut. À appeler dans une transaction.
    /// </summary>
    public ElementId CenturyText(double sizeMm, bool bold, List<string>? created)
    {
        var key = $"cg_{sizeMm:0.##}_{bold}";
        if (_cache.TryGetValue(key, out var c)) return c;
        bool Bold(TextNoteType t) => t.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.AsInteger() == 1;
        bool Plain(TextNoteType t) => t.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC)?.AsInteger() != 1
                                      && t.get_Parameter(BuiltInParameter.TEXT_STYLE_UNDERLINE)?.AsInteger() != 1;
        bool Black(TextNoteType t) => (t.get_Parameter(BuiltInParameter.LINE_COLOR)?.AsInteger() ?? 0) == 0;
        var types = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        var name = $"Avion par terre - {Font} {sizeMm:0.##} mm" + (bold ? " gras" : "");
        var found = types.FirstOrDefault(t => t.Name == name)
                    ?? types.Where(t => TextNorm.SameResource(FontOf(t), Font) && Math.Abs(SizeMm(t) - sizeMm) < 0.05 && Bold(t) == bold && Plain(t) && Black(t))
                        .OrderBy(t => t.Name.Length).FirstOrDefault();
        if (found == null)
        {
            var model = (TextNoteType)_doc.GetElement(_doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType));
            found = (TextNoteType)model.Duplicate(name);
            found.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(Font);
            found.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(Units.Mm(sizeMm));
            found.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)?.Set(bold ? 1 : 0);
            found.get_Parameter(BuiltInParameter.TEXT_STYLE_ITALIC)?.Set(0);
            found.get_Parameter(BuiltInParameter.TEXT_STYLE_UNDERLINE)?.Set(0);
            found.get_Parameter(BuiltInParameter.LINE_COLOR)?.Set(0);
            found.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);
            created?.Add($"Type de texte « {name} »");
        }
        _cache[key] = found.Id;
        return found.Id;
    }

    // -----------------------------------------------------------------------------------------
    // Titres de vues : texte simple en Century Gothic, sans pictogramme
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Prépare le type de fenêtre de vue « Avion par terre - Titre de vue » : titre affiché, famille de titre en texte simple
    /// (« NOM - PLAN / Ech : 1 : 20 ») convertie en Century Gothic. La famille de l'agence n'est pas modifiée : une copie
    /// « Avion par terre - Titre de vue » est chargée. À appeler hors transaction (édition de famille).
    /// </summary>
    public ElementId PrepareViewTitles(List<string>? created, WarningCollector? w = null)
    {
        if (_cache.TryGetValue("titre_vue", out var cached)) return cached;
        var typeName = _profile.Resource("titre_vue").FirstOrDefault() ?? TitleFamilyName;
        var vpTypes = new FilteredElementCollector(_doc).OfClass(typeof(ElementType)).Cast<ElementType>()
            .Where(t => t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL) != null).ToList();
        var existing = vpTypes.FirstOrDefault(t => TextNorm.SameResource(t.Name, typeName));
        if (existing != null) return _cache["titre_vue"] = existing.Id;

        var label = CenturyTitleFamily(created, w);
        var model = vpTypes.FirstOrDefault(t => _profile.Resource("titre_vue_modele").Any(n => TextNorm.SameResource(t.Name, n))) ?? vpTypes.FirstOrDefault();
        if (model == null) return _cache["titre_vue"] = ElementId.InvalidElementId;
        using var t = w != null ? WarningCollector.Start(_doc, "Avion par terre — type de titre de vue", w) : StartPlain("Avion par terre — type de titre de vue");
        var vt = model.Duplicate(typeName);
        vt.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.Set(1);
        if (label != ElementId.InvalidElementId) vt.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_LABEL_TAG)?.Set(label);
        t.Commit();
        created?.Add($"Type de fenêtre de vue « {vt.Name} » (titre affiché en texte simple {Font}, dérivé de « {model.Name} »)");
        return _cache["titre_vue"] = vt.Id;
    }

    /// <summary>Type de fenêtre de vue préparé par <see cref="PrepareViewTitles"/> (invalide si non préparé).</summary>
    public ElementId ViewportTypeWithTitle() => _cache.TryGetValue("titre_vue", out var id) ? id : ElementId.InvalidElementId;

    private Transaction StartPlain(string name)
    {
        var t = new Transaction(_doc, name);
        t.Start();
        return t;
    }

    private ElementId CenturyTitleFamily(List<string>? created, WarningCollector? w)
    {
        var labels = Symbols(BuiltInCategory.OST_ViewportLabel).ToList();
        var mine = labels.FirstOrDefault(s => TextNorm.SameResource(s.FamilyName, TitleFamilyName));
        if (mine != null) return mine.Id;
        var srcId = Symbol("titre_vue_famille", BuiltInCategory.OST_ViewportLabel,
            all => (all.FirstOrDefault(s => !Regex.IsMatch(TextNorm.ResourceKey(s.FamilyName), "k&d|kd")), "famille de titre disponible"));
        if (srcId == ElementId.InvalidElementId || _doc.GetElement(srcId) is not FamilySymbol src) return ElementId.InvalidElementId;
        if (_doc.IsModifiable) return src.Id; // appel dans une transaction : pas d'édition de famille possible
        try
        {
            var fdoc = _doc.EditFamily(src.Family);
            var dir = Path.Combine(Path.GetTempPath(), "AvionParTerre");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, TitleFamilyName + ".rfa");
            try
            {
                using (var ft = new Transaction(fdoc, "Century Gothic"))
                {
                    ft.Start();
                    foreach (var tt in new FilteredElementCollector(fdoc).OfClass(typeof(TextElementType)).Cast<TextElementType>())
                        tt.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(Font);
                    ft.Commit();
                }
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                fdoc.Close(false);
            }
            Family? fam;
            using (var t = w != null ? WarningCollector.Start(_doc, "Avion par terre — famille de titre", w) : StartPlain("Avion par terre — famille de titre"))
            {
                if (!_doc.LoadFamily(path, new OverwriteFamilyOptions(), out fam) || fam == null)
                {
                    t.RollBack();
                    return src.Id;
                }
                t.Commit();
            }
            try { File.Delete(path); } catch (IOException) { }
            created?.Add($"Famille de titre de vue « {TitleFamilyName} » (copie de « {src.FamilyName} » en {Font})");
            var id = fam.GetFamilySymbolIds().FirstOrDefault();
            return id ?? src.Id;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            Missing.Add(new Issue(Severity.Information, "titre_vue", src.FamilyName,
                $"Copie en {Font} impossible ({ex.Message}) : famille d'origine utilisée.", "Aucune action nécessaire."));
            return src.Id;
        }
    }

    private sealed class OverwriteFamilyOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>Gabarit de plan correspondant à l'échelle (APD-DCE_50e, _100e, _200e).</summary>
    public ElementId PlanTemplateForScale(int scale) => ViewTemplate($"gabarit_plan_1_{scale}");

    private ElementId Resolve(string key, Func<string, ElementId?> find, Func<ElementId?>? fallback = null, bool optional = false)
    {
        if (_cache.TryGetValue(key, out var id)) return id;
        var names = _profile.Resource(key).ToList();
        if (_overrides.TryGetValue(key, out var ov)) names.Insert(0, ov);
        foreach (var n in names)
        {
            var found = find(n);
            if (found != null && found != ElementId.InvalidElementId)
            {
                _cache[key] = found;
                return found;
            }
        }
        var fb = fallback?.Invoke();
        var sev = fb != null || optional ? Severity.Information : Severity.ARevoir;
        Missing.Add(new Issue(sev, "ressource_kd", key,
            names.Count == 0
                ? $"Aucun nom de ressource défini pour « {key} » dans profile.json."
                : $"Ressource K&D introuvable : {string.Join(" / ", names)}" + (fb != null ? " — ressource par défaut utilisée." : "."),
            "Charger la ressource du gabarit agence, ou la choisir dans Paramètres."));
        var result = fb ?? ElementId.InvalidElementId;
        _cache[key] = result;
        return result;
    }

    /// <summary>Résolution de tous les rôles (audit, onglet Ressources des paramètres).</summary>
    public IEnumerable<(ResourceRole Role, ElementId Id)> ResolveAll()
    {
        foreach (var r in Roles)
        {
            var id = r.Key switch
            {
                "cartouche_A0" => TitleBlock("A0"),
                "cartouche_A1" => TitleBlock("A1"),
                "cartouche_A3" => TitleBlock("A3"),
                "cartouche_fiche" => TitleBlockFor("cartouche_fiche", "A3"),
                "etiquette_piece_1_50" => RoomTagForScale(50),
                "etiquette_piece_1_100" => RoomTagForScale(100),
                "etiquette_piece_detail" => RoomTagDetail(),
                "etiquette_porte_1_50" => DoorTagForScale(50),
                "etiquette_porte_1_100" => DoorTagForScale(100),
                "etiquette_fenetre_1_50" => WindowTagForScale(50),
                "etiquette_fenetre_1_100" => WindowTagForScale(100),
                _ => r.Kind switch
                {
                    ResourceKind.ViewTemplate => ViewTemplate(r.Key),
                    ResourceKind.TextType => TextType(r.Key),
                    _ => Symbol(r.Key, r.Category),
                },
            };
            yield return (r, id);
        }
    }

    /// <summary>Candidats du modèle pour un rôle (liste déroulante des paramètres).</summary>
    public IEnumerable<string> Candidates(ResourceRole r) => r.Kind switch
    {
        ResourceKind.Symbol => Symbols(r.Category).Select(SymbolName).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase),
        ResourceKind.ViewTemplate => new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase),
        _ => new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Select(t => t.Name).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase),
    };

    public string NameOf(ElementId id) => _doc.GetElement(id) switch
    {
        FamilySymbol s => SymbolName(s),
        Element e => e.Name,
        _ => "",
    };
}
