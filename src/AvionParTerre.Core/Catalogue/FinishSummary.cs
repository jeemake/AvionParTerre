namespace AvionParTerre.Core.Catalogue;

/// <summary>Résumé Sol / Mur / Plafond d'une liste d'affectations, destiné aux paramètres de pièce Revit.</summary>
public static class FinishSummary
{
    public const string Sol = "Sol";
    public const string Mur = "Mur";
    public const string Plafond = "Plafond";
    public const string Facade = "Façade";

    public static readonly string[] RoomSupports = { Sol, Mur, Plafond };

    /// <summary>
    /// Texte par support. Un support sans affectation connue renvoie null (« à définir ») :
    /// on n'écrit jamais « non indiqué » comme s'il s'agissait d'une prescription.
    /// </summary>
    public static string? For(FinishCatalogue catalogue, IEnumerable<FinishApplication> apps, string support)
    {
        var parts = apps
            .Where(a => a.Support == support && a.Finition is not null)
            .Select(a =>
            {
                var d = catalogue.Finish(a.Finition)?.Designation ?? a.Finition!;
                return a.Note is not null && a.Note.Contains("teinte", StringComparison.OrdinalIgnoreCase)
                    ? $"{d} ({a.Note})"
                    : d;
            })
            .Distinct()
            .ToList();
        return parts.Count == 0 ? null : string.Join(" + ", parts);
    }

    public static IReadOnlyDictionary<string, string?> ForRoom(FinishCatalogue catalogue, IEnumerable<FinishApplication> apps)
    {
        var list = apps.ToList();
        return RoomSupports.ToDictionary(s => s, s => For(catalogue, list, s));
    }

    /// <summary>Supports explicitement « non indiqués » dans la source (à compléter).</summary>
    public static IEnumerable<string> MissingSupports(IEnumerable<FinishApplication> apps)
    {
        var list = apps.ToList();
        return RoomSupports.Where(s => !list.Any(a => a.Support == s && a.Finition is not null));
    }
}
