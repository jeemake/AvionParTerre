using System.Text.RegularExpressions;

namespace AvionParTerre.Core.Documents;

/// <summary>
/// Formatage des numéros affichés à partir de gabarits comme « D{groupe}.{page:00} ».
/// Les numéros sont toujours des chaînes (jamais des décimaux) : « 1.10 » reste « 1.10 ».
/// </summary>
public static partial class Numbering
{
    [GeneratedRegex(@"\{(?<name>[a-z_]+)(:(?<fmt>[^}]+))?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public static string Format(string pattern, IReadOnlyDictionary<string, object> values) =>
        Placeholder().Replace(pattern, m =>
        {
            var name = m.Groups["name"].Value;
            if (!values.TryGetValue(name, out var v))
                throw new KeyNotFoundException($"Valeur « {name} » absente pour le gabarit de numérotation « {pattern} ».");
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return v switch
            {
                int i when fmt is not null => i.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f when fmt is not null => f.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
        });

    public static string Format(string pattern, params (string Key, object Value)[] values) =>
        Format(pattern, values.ToDictionary(v => v.Key, v => v.Value));

    /// <summary>Lettre d'élévation : 0 → a, 1 → b… 25 → z, 26 → aa.</summary>
    public static string Letter(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            index--;
            s = (char)('a' + index % 26) + s;
            index /= 26;
        }
        return s;
    }
}
