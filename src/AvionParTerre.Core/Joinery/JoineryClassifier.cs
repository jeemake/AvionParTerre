using AvionParTerre.Core.Profiles;

namespace AvionParTerre.Core.Joinery;

/// <param name="Exact">Faux si le préfixe est suivi d'autres capitales (« PBD1D1 » classé par « PB ») : classement probable, à confirmer.</param>
public sealed record JoineryClass(string Lot, string Prefix, bool Exact = true);

/// <summary>
/// Classe un type de menuiserie dans un lot (CAL / CB / CS) d'après le préfixe de son repère.
/// Le préfixe n'est qu'une aide : la catégorie Revit, le matériau et le fonctionnement restent des propriétés distinctes.
/// </summary>
public sealed class JoineryClassifier
{
    private readonly List<(string Lot, string Prefix)> _prefixes;

    public JoineryClassifier(JoineryRules rules)
    {
        _prefixes = rules.Lots
            .SelectMany(l => l.Prefixes.Select(p => (l.Code, p)))
            .OrderByDescending(p => p.p.Length)
            .ToList();
    }

    public JoineryClass? Classify(string? mark)
    {
        if (string.IsNullOrWhiteSpace(mark)) return null;
        var m = mark.Trim();
        foreach (var (lot, prefix) in _prefixes)
        {
            if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // Le préfixe doit être suivi d'un chiffre, d'une fin de chaîne ou d'un séparateur : « PB1 » oui, « PBX » non.
            var rest = m[prefix.Length..];
            if (rest.Length == 0 || char.IsDigit(rest[0]) || !char.IsLetter(rest[0]) || char.IsLower(rest[0]))
                return new JoineryClass(lot, prefix);
        }
        // Repère hors convention stricte mais commençant par un préfixe connu et comportant un chiffre (« PBTV1D1 »)
        foreach (var (lot, prefix) in _prefixes)
            if (m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && m[prefix.Length..].Any(char.IsDigit))
                return new JoineryClass(lot, prefix, Exact: false);
        return null;
    }
}
