namespace AvionParTerre.Core.Profiles;

/// <summary>Référentiel documentaire (data/profile.json) : ressources K&amp;D, formats, numérotation, mise en page.</summary>
public sealed class PluginProfile
{
    public string Schema { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public Dictionary<string, List<string>> Ressources { get; set; } = new();
    public Dictionary<string, List<string>> Parametres { get; set; } = new();
    public List<DceProfile> ProfilsDce { get; set; } = new();
    public NumberingRules Numerotation { get; set; } = new();
    public LayoutRules MiseEnPage { get; set; } = new();
    public CarnetRules Carnets { get; set; } = new();
    public JoineryRules Menuiseries { get; set; } = new();
    public DimensionRules Cotation { get; set; } = new();
    public FolderRules Rangement { get; set; } = new();

    public IReadOnlyList<string> Resource(string key) =>
        Ressources.TryGetValue(key, out var v) ? v : Array.Empty<string>();

    public IReadOnlyList<string> Parameter(string key) =>
        Parametres.TryGetValue(key, out var v) ? v : Array.Empty<string>();

    public DceProfile? Dce(string code) => ProfilsDce.FirstOrDefault(p => p.Code == code);

    public static PluginProfile Load(string path)
    {
        var p = Json.Load<PluginProfile>(path);
        if (p.Schema != "avion-par-terre/profile")
            throw new InvalidDataException($"{path} n'est pas un profil Avion par terre (schema = « {p.Schema} »).");
        if (p.SchemaVersion != 1)
            throw new InvalidDataException($"Version de schéma de profil non prise en charge : {p.SchemaVersion}.");
        return p;
    }
}

public sealed class DceProfile
{
    public string Code { get; set; } = "";
    public string Libelle { get; set; } = "";
    public string FormatPlans { get; set; } = "A1";
    public int EchellePlans { get; set; } = 100;
    public string FormatCarnets { get; set; } = "A3";
    public List<int> EchellesCarnets { get; set; } = new() { 20, 25, 30, 35, 50 };
    /// <summary>Cartouche imposé (normes du projet) ; null = cartouche du format.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public long? TitleBlockId { get; set; }

    public override string ToString() => Libelle;
}

public sealed class NumberingRules
{
    public string PrefixeBatiment { get; set; } = "";
    public string Plans { get; set; } = "1.{index:00}";
    public int PlansPas { get; set; } = 10;
    public string CarnetGroupe { get; set; } = "D{groupe}";
    public string CarnetPage { get; set; } = "D{groupe}.{page:00}";
    public string CarnetElevation { get; set; } = "E{groupe}-{lettre}";
    public string Fiche { get; set; } = "{lot}-{index:00}";
}

public sealed class Margins
{
    public double Gauche { get; set; } = 15;
    public double Droite { get; set; } = 15;
    public double Bas { get; set; } = 15;
    public double Haut { get; set; } = 15;
}

public sealed class LayoutRules
{
    public string? Remarque { get; set; }
    public Dictionary<string, Margins> ZonesCartouche { get; set; } = new();
    public Margins ZoneDefaut { get; set; } = new();
    public double EspacementVues { get; set; } = 10;

    /// <summary>Marges du cartouche ; le nom est comparé sans accents ni suffixe de rechargement (« Cartouche A3 Horizontale1 »).</summary>
    public Margins MarginsFor(string titleBlockFamily)
    {
        if (ZonesCartouche.TryGetValue(titleBlockFamily, out var m)) return m;
        var stem = Text.TextNorm.FamilyStem(titleBlockFamily);
        return ZonesCartouche.FirstOrDefault(z => Text.TextNorm.FamilyStem(z.Key) == stem).Value ?? ZoneDefaut;
    }
}

public sealed class CarnetRules
{
    public double DecalageCadrageMm { get; set; } = 600;
    public double ProfondeurElevationMargeMm { get; set; } = 400;
    public int MasquerReperesAuDelaDe { get; set; } = 50;
    public List<string> FamillesProposees { get; set; } = new();
}

public sealed class JoineryLot
{
    public string Code { get; set; } = "";
    public string Libelle { get; set; } = "";
    public List<string> Prefixes { get; set; } = new();
    /// <summary>Sous-dossier des fiches du lot dans le dossier DCE du navigateur (« CALEPIN BOIS »).</summary>
    public string? SousDossier { get; set; }
}

public sealed class JoineryRules
{
    public List<JoineryLot> Lots { get; set; } = new();
    /// <summary>Nombre de répétitions déclaré du bâtiment type (mode « bâtiment type répété »). 1 = occurrences réelles.</summary>
    public int RepetitionsBatiment { get; set; } = 1;
    /// <summary>Annotations des élévations de fiches (quincaillerie, matériaux), par lot, catégorie et préfixe.</summary>
    public List<JoineryAnnotationRule> Annotations { get; set; } = new();

    /// <summary>
    /// Règle d'annotation d'un repère : la plus spécifique l'emporte (préfixe explicite, puis catégorie, puis lot seul).
    /// </summary>
    public JoineryAnnotationRule? AnnotationsFor(string? lot, bool isDoor, string mark)
    {
        if (lot == null) return null;
        var cat = isDoor ? "portes" : "fenetres";
        return Annotations
            .Where(r => r.Lots.Count == 0 || r.Lots.Contains(lot, StringComparer.OrdinalIgnoreCase))
            .Where(r => string.IsNullOrEmpty(r.Categorie) || string.Equals(r.Categorie, cat, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Rule: r, Prefix: r.Prefixes.Where(p => mark.StartsWith(p, StringComparison.OrdinalIgnoreCase)).Select(p => p.Length).DefaultIfEmpty(-1).Max()))
            .Where(x => x.Rule.Prefixes.Count == 0 || x.Prefix > 0)
            .OrderByDescending(x => x.Prefix)
            .ThenByDescending(x => string.IsNullOrEmpty(x.Rule.Categorie) ? 0 : 1)
            .ThenByDescending(x => x.Rule.Lots.Count > 0 ? 1 : 0)
            .Select(x => x.Rule).FirstOrDefault();
    }
}

public sealed class JoineryAnnotationRule
{
    public List<string> Lots { get; set; } = new();
    /// <summary>« portes », « fenetres » ou vide (toutes).</summary>
    public string? Categorie { get; set; }
    /// <summary>Préfixes de repère visés (« VJ », « EnsVJ ») ; vide = tous les repères du lot.</summary>
    public List<string> Prefixes { get; set; } = new();
    public List<JoineryNote> Notes { get; set; } = new();
}

/// <summary>Annotation avec ligne de repère : texte et point visé sur l'élévation (poignee, serrure, charniere_haut…).</summary>
public sealed class JoineryNote
{
    public string Texte { get; set; } = "";
    public string Cible { get; set; } = "";
}

/// <summary>Cotation des carnets (plans et élévations de pièces) et des fiches menuiseries.</summary>
public sealed class DimensionRules
{
    public bool Carnets { get; set; } = true;
    public bool Fiches { get; set; } = true;
    /// <summary>Unité des cotes : « cm » (carnets de l'agence : 410, 328) ou « mm » (fiches : 930, 2400).</summary>
    public string UniteCarnets { get; set; } = "cm";
    public string UniteFiches { get; set; } = "mm";
    public double TexteMm { get; set; } = 2.0;
    /// <summary>Distance papier entre deux lignes de cotes (et entre l'ouvrage et la première ligne).</summary>
    public double DecalageMm { get; set; } = 7;
    /// <summary>Hauteur de la poignée des portes, cotée depuis le bas de la porte (mm).</summary>
    public double HauteurPoigneeMm { get; set; } = 1050;
    /// <summary>Libellé au-dessus de la cote de largeur en plan des fiches.</summary>
    public string? LibelleLargeurPlan { get; set; } = "largeur réservation";
    /// <summary>Largeur papier réservée aux annotations à droite de l'élévation d'une fiche.</summary>
    public double BandeAnnotationsMm { get; set; } = 70;
}

/// <summary>
/// Rangement dans le navigateur de projet : les éléments générés vont dans le dossier « DCE », avec un sous-dossier par famille
/// documentaire ; les dossiers « CALEPIN … » de l'agence sont rangés dans « DCE ».
/// </summary>
public sealed class FolderRules
{
    public bool Actif { get; set; } = true;
    public string Dossier { get; set; } = "DCE";
    public string SousDossierPlans { get; set; } = "PLANS GENERAUX";
    public string SousDossierCoupesFacades { get; set; } = "COUPES ET FACADES";
    public string SousDossierCarnets { get; set; } = "DETAILS DE PIECES";
    public string SousDossierFiches { get; set; } = "CALEPINS MENUISERIES";
    /// <summary>Dossiers de premier niveau de l'agence à ranger dans le dossier DCE (début du nom, sans accents ni casse).</summary>
    public List<string> ARangerDansDossier { get; set; } = new() { "CALEPIN" };
}
