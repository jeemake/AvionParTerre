namespace AvionParTerre.Core.Layout;

/// <summary>Rectangle en millimètres papier (origine en bas à gauche).</summary>
public readonly record struct RectMm(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Top => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public bool Contains(RectMm o) =>
        o.X >= X - 1e-6 && o.Y >= Y - 1e-6 && o.Right <= Right + 1e-6 && o.Top <= Top + 1e-6;

    public bool Intersects(RectMm o) =>
        o.X < Right - 1e-6 && o.Right > X + 1e-6 && o.Y < Top - 1e-6 && o.Top > Y + 1e-6;
}

public sealed record PackItem(string Key, double Width, double Height, bool StartNewPage = false);

public sealed record PlacedItem(string Key, int Page, RectMm Rect);

public sealed record PackResult(IReadOnlyList<PlacedItem> Placed, IReadOnlyList<string> Oversize, int PageCount);

/// <summary>
/// Composition des vues sur feuilles (algorithme « MaxRects », placement en haut à gauche) :
/// l'ordre des éléments est conservé, chacun prend l'emplacement libre le plus haut puis le plus à gauche ;
/// une page est ajoutée lorsque plus aucun emplacement ne convient.
/// Une vue plus grande que la zone de dessin est signalée, jamais réduite silencieusement.
/// </summary>
public static class SheetPacker
{
    public static PackResult Pack(IReadOnlyList<PackItem> items, RectMm area, double spacing)
    {
        var placed = new List<PlacedItem>();
        var oversize = new List<string>();
        int page = 0;
        bool pageUsed = false;
        var free = new List<RectMm> { area };

        foreach (var it in items)
        {
            var tooBig = it.Width > area.Width + 1e-6 || it.Height > area.Height + 1e-6;
            if (tooBig) oversize.Add(it.Key);
            if (it.StartNewPage && pageUsed) NewPage();

            var pos = tooBig ? null : Find(free, it.Width, it.Height);
            if (pos == null && pageUsed && !tooBig)
            {
                NewPage();
                pos = Find(free, it.Width, it.Height);
            }
            if (pos == null)
            {
                // Trop grand : seul sur sa page, calé en haut à gauche
                if (pageUsed) NewPage();
                pos = new RectMm(area.X, area.Top - it.Height, it.Width, it.Height);
            }

            var rect = pos.Value;
            placed.Add(new PlacedItem(it.Key, page, rect));
            pageUsed = true;
            if (tooBig) free.Clear(); // Oversize items must remain alone, including wide but short views.
            else Occupy(free, new RectMm(rect.X, rect.Y - spacing, rect.Width + spacing, rect.Height + spacing));
        }

        return new PackResult(Center(placed, area), oversize, pageUsed ? page + 1 : 0);

        void NewPage()
        {
            page++;
            pageUsed = false;
            free.Clear();
            free.Add(area);
        }
    }

    private static RectMm? Find(List<RectMm> free, double w, double h)
    {
        RectMm? best = null;
        foreach (var f in free)
        {
            if (w > f.Width + 1e-6 || h > f.Height + 1e-6) continue;
            var cand = new RectMm(f.X, f.Top - h, w, h);
            if (best == null || cand.Top > best.Value.Top + 1e-6 ||
                (Math.Abs(cand.Top - best.Value.Top) <= 1e-6 && cand.X < best.Value.X))
                best = cand;
        }
        return best;
    }

    /// <summary>Retire <paramref name="used"/> des rectangles libres (découpe MaxRects puis élagage des inclusions).</summary>
    private static void Occupy(List<RectMm> free, RectMm used)
    {
        var next = new List<RectMm>();
        foreach (var f in free)
        {
            if (!f.Intersects(used)) { next.Add(f); continue; }
            if (used.X > f.X) next.Add(new RectMm(f.X, f.Y, used.X - f.X, f.Height));
            if (used.Right < f.Right) next.Add(new RectMm(used.Right, f.Y, f.Right - used.Right, f.Height));
            if (used.Y > f.Y) next.Add(new RectMm(f.X, f.Y, f.Width, used.Y - f.Y));
            if (used.Top < f.Top) next.Add(new RectMm(f.X, used.Top, f.Width, f.Top - used.Top));
        }
        next = next.Where(r => r.Width > 1e-3 && r.Height > 1e-3).ToList();
        free.Clear();
        for (int i = 0; i < next.Count; i++)
        {
            bool contained = false;
            for (int j = 0; j < next.Count && !contained; j++)
                if (i != j && next[j].Contains(next[i]) && (!next[i].Contains(next[j]) || j < i)) contained = true;
            if (!contained) free.Add(next[i]);
        }
    }

    /// <summary>Centre le contenu de chaque page dans la zone de dessin (même décalage pour tous ses éléments).</summary>
    private static IReadOnlyList<PlacedItem> Center(List<PlacedItem> placed, RectMm area)
    {
        var result = new List<PlacedItem>(placed.Count);
        foreach (var g in placed.GroupBy(p => p.Page))
        {
            var minX = g.Min(p => p.Rect.X);
            var maxX = g.Max(p => p.Rect.Right);
            var minY = g.Min(p => p.Rect.Y);
            var maxY = g.Max(p => p.Rect.Top);
            var dx = (area.X + Math.Max(0, (area.Width - (maxX - minX)) / 2)) - minX;
            var dy = (area.Top - Math.Max(0, (area.Height - (maxY - minY)) / 2)) - maxY;
            result.AddRange(g.Select(p => p with { Rect = p.Rect with { X = p.Rect.X + dx, Y = p.Rect.Y + dy } }));
        }
        return result;
    }
}
