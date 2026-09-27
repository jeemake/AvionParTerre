using AvionParTerre.Core.Text;

namespace AvionParTerre.Core.Catalogue;

public sealed record Classification(string FamilyCode, string Keyword);

/// <summary>
/// Classe un local dans une famille normalisée à partir de son nom.
/// Le mot-clé le plus long l'emporte (« hall ascenseur » avant « hall », « atelier éducatif » avant « atelier »).
/// Retourne null si aucun mot-clé ne correspond : le local reste « à classer ».
/// </summary>
public sealed class RoomClassifier
{
    private readonly List<(string Family, string Keyword)> _keywords;

    public RoomClassifier(IEnumerable<LocalFamily> families)
    {
        _keywords = families
            .SelectMany(f => f.MotsCles.Select(k => (f.Code, TextNorm.Normalize(k))))
            .Where(k => k.Item2.Length > 0)
            .OrderByDescending(k => k.Item2.Length)
            .ToList();
    }

    public Classification? Classify(string? roomName)
    {
        var n = TextNorm.Normalize(roomName);
        if (n.Length == 0) return null;
        foreach (var (family, kw) in _keywords)
            if (TextNorm.ContainsPhrase(n, kw))
                return new Classification(family, kw);
        return null;
    }
}
