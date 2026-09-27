using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AvionParTerre.Core;
using AvionParTerre.Core.Issues;

namespace AvionParTerre.Revit.Services;

/// <summary>Rapport d'opérations d'une génération : créations, mises à jour, retouches conservées, anomalies.</summary>
internal sealed class Report
{
    public string Title { get; }
    /// <summary>Synthèse affichée en tête du rapport.</summary>
    public List<string> Notes { get; } = new();
    public List<string> Created { get; } = new();
    public List<string> Updated { get; } = new();
    public List<string> Unchanged { get; } = new();
    public List<string> Kept { get; } = new();
    public List<Issue> Issues { get; } = new();
    public List<string> RevitWarnings { get; } = new();
    public string? OutputFolder { get; set; }

    public Report(string title) => Title = title;

    public void Issue(Severity s, string rule, string obj, string message, string action, long? id = null) =>
        Issues.Add(new Issue(s, rule, obj, message, action, id));

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title);
        sb.AppendLine(new string('=', Title.Length));
        sb.AppendLine($"{DateTime.Now:dd/MM/yyyy HH:mm}");
        if (Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in Notes) sb.AppendLine(n);
        }
        Section(sb, "Créés", Created);
        Section(sb, "Mis à jour", Updated);
        Section(sb, "Retouches conservées (non écrasées)", Kept);
        Section(sb, "Inchangés", Unchanged);
        if (Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Anomalies ({Issues.Count})");
            foreach (var g in Issues.GroupBy(i => i.Gravite).OrderBy(g => g.Key))
                foreach (var i in g)
                    sb.AppendLine($"  [{i.GraviteLibelle}] {i.Objet} — {i.Message} → {i.Action}");
        }
        if (RevitWarnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Avertissements Revit ({RevitWarnings.Count})");
            foreach (var w in RevitWarnings.Distinct()) sb.AppendLine("  " + w);
        }
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"{title} ({items.Count})");
        foreach (var i in items) sb.AppendLine("  " + i);
    }
}

/// <summary>
/// Propriétés gérées d'un objet généré. Compare dernier état généré / état actuel / état souhaité (§12 du plan) :
/// une propriété retouchée manuellement n'est pas écrasée.
/// </summary>
internal sealed class ManagedProps
{
    public Dictionary<string, string> Values { get; set; } = new();

    public static ManagedProps Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ManagedProps();
        try { return Json.Deserialize<ManagedProps>(json!) ?? new ManagedProps(); }
        catch { return new ManagedProps(); }
    }

    public string Serialize() => Json.Serialize(this);

    public enum Decision { Unchanged, Update, KeepRetouch }

    /// <summary>Décide si <paramref name="current"/> peut être remplacé par <paramref name="desired"/>.</summary>
    public Decision Decide(string key, string current, string desired)
    {
        if (current == desired) return Decision.Unchanged;
        if (!Values.TryGetValue(key, out var last)) return Decision.Update;
        return current == last ? Decision.Update : Decision.KeepRetouch;
    }
}
