using System.Text;

namespace AvionParTerre.Core.Documents;

public enum PreparationState { Prepare, ACompleter, PretPourRevue }

/// <summary>Ligne du registre documentaire (document-register.json).</summary>
public sealed class RegisterEntry
{
    /// <summary>Identité interne stable (indépendante du numéro affiché).</summary>
    public string Cle { get; set; } = "";
    public string Famille { get; set; } = "";
    public string Numero { get; set; } = "";
    public string Titre { get; set; } = "";
    public string? Format { get; set; }
    public string? Echelles { get; set; }
    public string? Batiment { get; set; }
    public List<string> Vues { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public PreparationState Etat { get; set; } = PreparationState.Prepare;
    public List<string> Anomalies { get; set; } = new();
    public string? RevitUniqueId { get; set; }
}

public sealed class DocumentRegister
{
    public string Schema { get; set; } = "avion-par-terre/document-register";
    public int SchemaVersion { get; set; } = 1;
    public string Projet { get; set; } = "";
    public string DocumentRevit { get; set; } = "";
    public string Profil { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public List<RegisterEntry> Documents { get; set; } = new();

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Famille;Numéro;Titre;Format;Échelles;État;Vues;Anomalies;Clé");
        foreach (var d in Documents.OrderBy(d => d.Famille).ThenBy(d => d.Numero, StringComparer.Ordinal))
            sb.AppendLine(string.Join(';', new[]
            {
                d.Famille, d.Numero, d.Titre, d.Format ?? "", d.Echelles ?? "", EtatLabel(d.Etat),
                string.Join(" | ", d.Vues), string.Join(" | ", d.Anomalies), d.Cle,
            }.Select(Csv)));
        return sb.ToString();
    }

    public static string EtatLabel(PreparationState s) => s switch
    {
        PreparationState.Prepare => "préparé",
        PreparationState.ACompleter => "à compléter",
        PreparationState.PretPourRevue => "prêt pour revue d'émission",
        _ => s.ToString(),
    };

    public static string Csv(string v) =>
        v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
