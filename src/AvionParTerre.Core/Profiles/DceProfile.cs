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
}

public sealed class JoineryRules
{
    public List<JoineryLot> Lots { get; set; } = new();
    /// <summary>Nombre de répétitions déclaré du bâtiment type (mode « bâtiment type répété »). 1 = occurrences réelles.</summary>
    public int RepetitionsBatiment { get; set; } = 1;
}
