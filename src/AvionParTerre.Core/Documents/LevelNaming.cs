using System.Text.RegularExpressions;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Core.Documents;

/// <summary>
/// Titres de plans à la manière de l'agence (« PLAN DU REZ-DE-CHAUSSEE », « PLAN DU 1ER ETAGE », « PLAN DE TOITURE »)
/// à partir des noms de niveaux Revit (« N00 _ RDC », « N01 _ 1er Etage », « N02_N02 », « N03_Toiture »).
/// </summary>
public static class LevelNaming
{
    /// <summary>Désignation du niveau en capitales sans accents (« REZ-DE-CHAUSSEE », « 2EME ETAGE »), ou null si non reconnue.</summary>
    public static string? Designation(string? levelName)
    {
        var n = TextNorm.Normalize(levelName);
        if (n.Length == 0) return null;
        var t = " " + n + " ";
        if (Regex.IsMatch(t, @" (toiture|terrasse|couverture) ")) return "TOITURE";
        if (Regex.IsMatch(t, @" (rdc|rez de chaussee|rez) ")) return "REZ-DE-CHAUSSEE";
        if (Regex.IsMatch(t, @" (mezzanine) ")) return "MEZZANINE";
        var ss = Regex.Match(t, @" (ss|sous sol)\s*(\d+)? ");
        if (ss.Success) return ss.Groups[2].Success && ss.Groups[2].Value != "1" ? $"{ss.Groups[2].Value}EME SOUS-SOL" : "SOUS-SOL";
        var et = Regex.Match(t, @" (\d+)\s*(er|e|eme|ieme)? etage ");
        if (et.Success) return Floor(int.Parse(et.Groups[1].Value));
        var rp = Regex.Match(t, @" r\s*(\d+) ");
        if (rp.Success) return Floor(int.Parse(rp.Groups[1].Value));
        // Code agence « N01 », « N02_N02 » : le premier code de niveau donne l'étage
        var code = Regex.Match(t, @" n(\d{2}) ");
        if (code.Success) return Floor(int.Parse(code.Groups[1].Value));
        return null;
    }

    private static string Floor(int i) => i switch
    {
        0 => "REZ-DE-CHAUSSEE",
        1 => "1ER ETAGE",
        _ => $"{i}EME ETAGE",
    };

    /// <summary>Altitude relative au format de l'agence (paramètre « Niv ») : « +/-0.00 », « +3.92 », « -3.30 ».</summary>
    public static string FormatNiv(double meters)
    {
        var r = Math.Round(meters, 2);
        return Math.Abs(r) < 0.005 ? "+/-0.00" : (r > 0 ? "+" : "-") + Math.Abs(r).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Titre de feuille de plan général.</summary>
    public static string PlanTitle(string? levelName)
    {
        var d = Designation(levelName);
        if (d == null) return TextNorm.UpperTitle($"PLAN {levelName}").Trim();
        return d == "TOITURE" ? "PLAN DE TOITURE" : $"PLAN DU {d}";
    }
}
