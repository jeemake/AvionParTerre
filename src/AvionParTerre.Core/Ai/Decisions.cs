using System.Text;
using AvionParTerre.Core.Documents;

namespace AvionParTerre.Core.Ai;

/// <summary>
/// Origine d'une décision, par ordre de priorité : ce que l'utilisateur ou le projet a déjà décidé n'est jamais remplacé
/// par une règle ou par l'IA.
/// </summary>
public enum DecisionSource
{
    Utilisateur,
    Projet,
    Referentiel,
    Regle,
    Ia,
    NonDecide,
}

public sealed class Decision
{
    public string Domaine { get; set; } = "";
    public string Objet { get; set; } = "";
    public string Choix { get; set; } = "";
    public DecisionSource Source { get; set; }
    public string? Justification { get; set; }
    public List<string> Sources { get; set; } = new();
    public string? Modele { get; set; }
    public long? ElementId { get; set; }

    public string SourceLibelle => Libelle(Source, Modele);

    public static string Libelle(DecisionSource s, string? model = null) => s switch
    {
        DecisionSource.Utilisateur => "Utilisateur",
        DecisionSource.Projet => "Projet (maquette)",
        DecisionSource.Referentiel => "Référentiel K&D",
        DecisionSource.Regle => "Règle automatique",
        DecisionSource.Ia => model == null ? "IA" : $"IA ({model})",
        _ => "Non décidé",
    };
}

/// <summary>Journal des décisions du mode avion par terre (JSON + CSV dans le dossier de sorties).</summary>
public sealed class DecisionLog
{
    public string Schema { get; set; } = "avion-par-terre/decisions";
    public int SchemaVersion { get; set; } = 1;
    public string Document { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public string? Modele { get; set; }
    public double? CoutUsd { get; set; }
    public int JetonsEntree { get; set; }
    public int JetonsSortie { get; set; }
    public List<Decision> Decisions { get; set; } = new();

    public Decision Add(string domaine, string objet, string choix, DecisionSource source, string? justification = null,
        IEnumerable<string>? sources = null, string? model = null, long? id = null)
    {
        var d = new Decision
        {
            Domaine = domaine, Objet = objet, Choix = choix, Source = source, Justification = justification,
            Modele = source == DecisionSource.Ia ? model ?? Modele : null, ElementId = id,
        };
        if (sources != null) d.Sources.AddRange(sources.Distinct());
        Decisions.Add(d);
        return d;
    }

    public void Account(LlmResponse r)
    {
        JetonsEntree += r.PromptTokens;
        JetonsSortie += r.CompletionTokens;
        if (r.Cost.HasValue) CoutUsd = (CoutUsd ?? 0) + r.Cost.Value;
    }

    public string ToCsv()
    {
        var sb = new StringBuilder("Domaine;Objet;Décision;Source;Justification;Sources\r\n");
        foreach (var d in Decisions)
            sb.Append(string.Join(";", new[] { d.Domaine, d.Objet, d.Choix, d.SourceLibelle, d.Justification ?? "", string.Join(" ", d.Sources) }
                .Select(DocumentRegister.Csv))).Append("\r\n");
        return sb.ToString();
    }

    public string Summary() =>
        string.Join(" · ", Decisions.GroupBy(d => d.Source).OrderBy(g => g.Key).Select(g => $"{Decision.Libelle(g.Key)} : {g.Count()}"));
}
