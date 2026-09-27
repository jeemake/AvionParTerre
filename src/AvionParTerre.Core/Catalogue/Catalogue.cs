namespace AvionParTerre.Core.Catalogue;

/// <summary>Base de données des finitions K&amp;D (data/catalogue.json, générée par tools/build_catalogue.py).</summary>
public sealed class FinishCatalogue
{
    public string Schema { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public string? Date { get; set; }
    public string? Source { get; set; }
    public string? Avertissement { get; set; }
    public List<string> Supports { get; set; } = new();
    public List<LocalFamily> FamillesLocaux { get; set; } = new();
    public List<Finish> Finitions { get; set; } = new();
    public List<Library> Bibliotheques { get; set; } = new();
    public List<ReferenceProfile> Profils { get; set; } = new();
    public List<Observation> Observations { get; set; } = new();

    private Dictionary<string, Finish>? _byCode;

    public Finish? Finish(string? code)
    {
        if (code is null) return null;
        _byCode ??= Finitions.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);
        return _byCode.TryGetValue(code, out var f) ? f : null;
    }

    public Library? LibraryOf(string code) => Bibliotheques.FirstOrDefault(b => b.Code == code);

    public LocalFamily? Family(string? code) => FamillesLocaux.FirstOrDefault(f => f.Code == code);

    public ReferenceProfile? Profile(string? id) => Profils.FirstOrDefault(p => p.Id == id);

    public static FinishCatalogue Load(string path)
    {
        var c = Json.Load<FinishCatalogue>(path);
        if (c.Schema != "avion-par-terre/catalogue")
            throw new InvalidDataException($"{path} n'est pas un catalogue Avion par terre (schema = « {c.Schema} »).");
        if (c.SchemaVersion != 1)
            throw new InvalidDataException($"Version de schéma de catalogue non prise en charge : {c.SchemaVersion}.");
        return c;
    }
}

public sealed class LocalFamily
{
    public string Code { get; set; } = "";
    public string Libelle { get; set; } = "";
    public List<string> MotsCles { get; set; } = new();
}

public sealed class Finish
{
    public string Code { get; set; } = "";
    public string Support { get; set; } = "";
    public string Famille { get; set; } = "";
    public string Designation { get; set; } = "";
    public List<string> AliasSources { get; set; } = new();
    public string? Remarque { get; set; }
}

public sealed class Library
{
    public string Code { get; set; } = "";
    public string ProjetSource { get; set; } = "";
    public string? Gamme { get; set; }
    public string GammeLibelle { get; set; } = "";
    public string Programme { get; set; } = "";
    public string ProgrammeLibelle { get; set; } = "";
    public string? Phase { get; set; }
    public string? Date { get; set; }
    public string? Affaire { get; set; }
    public string Fichier { get; set; } = "";
    public string? ReferenceCitation { get; set; }
    public string? Alerte { get; set; }
    public string? Sha256 { get; set; }
    public List<HeaderColumn> ColonnesEntete { get; set; } = new();

    public string Display => $"{ProjetSource} — {GammeLibelle} — {ProgrammeLibelle}" + (Phase is null ? "" : $" ({Phase})");
}

public sealed class HeaderColumn
{
    public string Finition { get; set; } = "";
    public string LibelleSource { get; set; } = "";
    public string Etat { get; set; } = "";
}

public sealed class FinishApplication
{
    public string Support { get; set; } = "";
    /// <summary>Code de finition ; null = affectation non indiquée dans la source.</summary>
    public string? Finition { get; set; }
    public string? Etat { get; set; }
    public string? Note { get; set; }
    public string? LibelleSource { get; set; }
    public string? Cellule { get; set; }
}

public sealed class ReferenceProfile
{
    public string Id { get; set; } = "";
    public string Bibliotheque { get; set; } = "";
    public string Libelle { get; set; } = "";
    public string FamilleLocal { get; set; } = "";
    public string? Zone { get; set; }
    public List<string> LocauxSource { get; set; } = new();
    public List<FinishApplication> Applications { get; set; } = new();
    public string? Source { get; set; }
    public string? Remarque { get; set; }
    public string Statut { get; set; } = "";
    public string ValidationPourNouveauProjet { get; set; } = "";

    public string Display => $"{Id} · {Libelle}";
}

public sealed class Observation
{
    public string Id { get; set; } = "";
    public string Bibliotheque { get; set; } = "";
    public string LibelleSource { get; set; } = "";
    public string? Rubrique { get; set; }
    public string Reference { get; set; } = "";
    public List<FinishApplication> Applications { get; set; } = new();
}
