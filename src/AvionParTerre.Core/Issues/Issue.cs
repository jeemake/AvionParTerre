namespace AvionParTerre.Core.Issues;

/// <summary>Gravité des contrôles (§5.3 du plan).</summary>
public enum Severity
{
    /// <summary>Bloquant pour la génération ciblée.</summary>
    Bloquant,
    /// <summary>Génération préparatoire possible, émission bloquée.</summary>
    EmissionBloquee,
    /// <summary>À revoir visuellement.</summary>
    ARevoir,
    Information,
}

public sealed record Issue(
    Severity Gravite,
    string Regle,
    string Objet,
    string Message,
    string Action,
    long? ElementId = null,
    string? Vue = null)
{
    public string GraviteLibelle => Gravite switch
    {
        Severity.Bloquant => "Bloquant",
        Severity.EmissionBloquee => "Émission bloquée",
        Severity.ARevoir => "À revoir",
        _ => "Information",
    };
}
