using AvionParTerre.Core.Text;

namespace AvionParTerre.Core.Catalogue;

public sealed record ProfileSuggestion(ReferenceProfile Profile, double Score, string Raison);

public sealed record ObservationMatch(Observation Observation, double Score);

/// <summary>
/// Propose des profils de référence pour un local (ordre de décision §10.3 du référentiel, étape 3).
/// Une suggestion n'est jamais une prescription : elle doit être validée par l'utilisateur.
/// </summary>
public sealed class FinishMatcher
{
    private readonly FinishCatalogue _catalogue;
    private readonly RoomClassifier _classifier;

    public FinishMatcher(FinishCatalogue catalogue)
    {
        _catalogue = catalogue;
        _classifier = new RoomClassifier(catalogue.FamillesLocaux);
    }

    public Classification? Classify(string? roomName) => _classifier.Classify(roomName);

    /// <param name="libraryCode">Bibliothèque retenue pour le projet ; null = toutes.</param>
    public IReadOnlyList<ProfileSuggestion> Suggest(string? roomName, string? levelName, string? libraryCode, int max = 5)
    {
        var cls = _classifier.Classify(roomName);
        var candidates = _catalogue.Profils.Where(p => libraryCode is null || p.Bibliotheque == libraryCode);
        var list = new List<ProfileSuggestion>();
        foreach (var p in candidates)
        {
            double score = 0;
            var reasons = new List<string>();
            if (cls is not null && p.FamilleLocal == cls.FamilyCode)
            {
                score += 1.0;
                reasons.Add($"famille « {_catalogue.Family(cls.FamilyCode)?.Libelle ?? cls.FamilyCode} »");
            }
            var best = p.LocauxSource.Select(l => (l, s: TextNorm.Jaccard(roomName, l))).OrderByDescending(x => x.s).FirstOrDefault();
            if (best.s > 0)
            {
                score += best.s;
                reasons.Add($"libellé proche de « {best.l} »");
            }
            // Le niveau seul ne justifie jamais une suggestion : il départage des profils déjà pertinents.
            if (score == 0) continue;
            if (!string.IsNullOrEmpty(p.Zone) && !string.IsNullOrEmpty(levelName) && TextNorm.Jaccard(levelName, p.Zone) > 0)
            {
                score += 0.2;
                reasons.Add($"niveau/zone « {p.Zone} »");
            }
            if (score > 0) list.Add(new ProfileSuggestion(p, score, string.Join(", ", reasons)));
        }
        return list.OrderByDescending(s => s.Score).ThenBy(s => s.Profile.Id, StringComparer.Ordinal).Take(max).ToList();
    }

    public IReadOnlyList<ObservationMatch> SimilarObservations(string? roomName, string? libraryCode, int max = 3) =>
        _catalogue.Observations
            .Where(o => libraryCode is null || o.Bibliotheque == libraryCode)
            .Select(o => new ObservationMatch(o, TextNorm.Jaccard(roomName, o.LibelleSource)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .Take(max)
            .ToList();
}
