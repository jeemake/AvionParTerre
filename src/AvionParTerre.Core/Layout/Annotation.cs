namespace AvionParTerre.Core.Layout;

/// <summary>Règles géométriques des cotes et annotations, indépendantes de Revit (testables).</summary>
public static class Annotation
{
    /// <summary>
    /// Chaîne de cotes : références triées le long de la ligne de cote, les références confondues (à <paramref name="tolerance"/> près)
    /// réduites à une seule. Renvoie les indices des références retenues, dans l'ordre.
    /// </summary>
    public static List<int> Chain(IReadOnlyList<double> positions, double tolerance)
    {
        var order = Enumerable.Range(0, positions.Count).OrderBy(i => positions[i]).ToList();
        var kept = new List<int>();
        foreach (var i in order)
            if (kept.Count == 0 || positions[i] - positions[kept[^1]] > tolerance) kept.Add(i);
        return kept;
    }

    /// <summary>
    /// Position des textes d'annotation empilés à droite d'un dessin : chaque texte au plus près de la hauteur de son point visé,
    /// dans l'ordre des hauteurs, sans chevauchement (<paramref name="spacing"/> minimal) et entre <paramref name="bottom"/> et <paramref name="top"/>.
    /// Renvoie les hauteurs dans l'ordre des cibles fournies.
    /// </summary>
    public static double[] Stack(IReadOnlyList<double> targets, double spacing, double bottom, double top)
    {
        var n = targets.Count;
        var result = new double[n];
        if (n == 0) return result;
        var order = Enumerable.Range(0, n).OrderByDescending(i => targets[i]).ToList();
        // Descente : pas plus haut que le précédent moins l'espacement
        var y = new double[n];
        for (int k = 0; k < n; k++)
        {
            var wanted = Math.Min(targets[order[k]], top);
            y[k] = k == 0 ? wanted : Math.Min(wanted, y[k - 1] - spacing);
        }
        // Remontée si la pile dépasse le bas
        if (y[n - 1] < bottom)
        {
            y[n - 1] = bottom;
            for (int k = n - 2; k >= 0; k--) y[k] = Math.Max(y[k], y[k + 1] + spacing);
        }
        for (int k = 0; k < n; k++) result[order[k]] = y[k];
        return result;
    }

    /// <summary>
    /// Nom de façade d'après le côté d'où elle est vue (direction du bâtiment vers l'observateur, repère du projet : +Y = nord).
    /// </summary>
    public static string FacadeName(double towardViewerX, double towardViewerY)
    {
        var a = Math.Atan2(towardViewerY, towardViewerX) * 180 / Math.PI;
        if (a < 0) a += 360;
        return a switch
        {
            >= 45 and < 135 => "FACADE NORD",
            >= 135 and < 225 => "FACADE OUEST",
            >= 225 and < 315 => "FACADE SUD",
            _ => "FACADE EST",
        };
    }

    /// <summary>
    /// Direction dominante d'un ensemble de segments (angle en radians, ramené dans [0, π/2[), pondérée par leur longueur :
    /// axes principaux du bâtiment pour les coupes et façades.
    /// </summary>
    public static double DominantAngle(IEnumerable<(double Angle, double Length)> segments, double binDegrees = 1)
    {
        var bins = new Dictionary<int, double>();
        var n = (int)Math.Round(90 / binDegrees);
        foreach (var (angle, length) in segments)
        {
            var d = angle * 180 / Math.PI % 90;
            if (d < 0) d += 90;
            var b = (int)Math.Round(d / binDegrees) % n;
            bins[b] = bins.GetValueOrDefault(b) + length;
        }
        if (bins.Count == 0) return 0;
        var best = bins.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        return best * binDegrees * Math.PI / 180;
    }
}
