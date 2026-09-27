using System.Globalization;
using System.Text;

namespace AvionParTerre.Core.Text;

/// <summary>Normalisation des libellés : minuscules, sans accents, ponctuation remplacée par des espaces.</summary>
public static class TextNorm
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Titre entièrement en capitales, sans accents (règle de la charte K&amp;D pour les mots en capitales).
    /// La ponctuation est conservée.
    /// </summary>
    public static string UpperTitle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToUpperInvariant(c));
        return sb.ToString().Normalize(NormalizationForm.FormC).Replace("Œ", "OE").Replace("Æ", "AE");
    }

    /// <summary>Clé de comparaison d'un nom de ressource Revit : sans accents, casse ni espaces multiples (« 100è » = « 100e »).</summary>
    public static string ResourceKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool SameResource(string? a, string? b) => ResourceKey(a) == ResourceKey(b);

    /// <summary>
    /// Radical d'un nom de famille chargée plusieurs fois (« Cartouche A3 Horizontale1 », « … 2 », « … Copie 1 », « … (1) »)
    /// pour retrouver la ressource quand le nom exact n'existe pas.
    /// </summary>
    public static string FamilyStem(string? s)
    {
        var k = ResourceKey(s);
        k = System.Text.RegularExpressions.Regex.Replace(k, @"(\s*\(\d+\)|\s+copie(\s*\d+)?|\s+\d+|(?<=[a-z\)])\d+)$", "");
        return k.Trim();
    }

    public static string[] Tokens(string? s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Vrai si <paramref name="phrase"/> (normalisée) apparaît comme suite de mots entiers dans <paramref name="text"/> (normalisé).</summary>
    public static bool ContainsPhrase(string normalizedText, string normalizedPhrase)
    {
        if (normalizedPhrase.Length == 0) return false;
        var t = " " + normalizedText + " ";
        return t.Contains(" " + normalizedPhrase + " ", StringComparison.Ordinal);
    }

    /// <summary>Similarité de Jaccard sur les mots significatifs (chiffres isolés ignorés).</summary>
    public static double Jaccard(string? a, string? b)
    {
        var ta = Tokens(a).Where(Significant).ToHashSet();
        var tb = Tokens(b).Where(Significant).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Intersect(tb).Count();
        return (double)inter / ta.Union(tb).Count();
    }

    private static readonly HashSet<string> StopWords = new() { "de", "du", "des", "la", "le", "les", "d", "l", "et", "a", "au", "aux", "n", "en" };

    private static bool Significant(string t) => !StopWords.Contains(t) && !t.All(char.IsDigit);
}
