using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Joinery;
using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Profiles;
using Xunit;

namespace AvionParTerre.Core.Tests;

public class CatalogueTests
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly FinishCatalogue Cat = FinishCatalogue.Load(Path.Combine(DataDir, "catalogue.json"));

    [Fact]
    public void Catalogue_contient_les_quatre_bibliotheques_et_leurs_profils()
    {
        Assert.Equal(4, Cat.Bibliotheques.Count);
        Assert.Contains(Cat.Profils, p => p.Id == "PLA-20");
        Assert.All(Cat.Profils, p => Assert.Equal("observation_source", p.Statut));
        // Toute affectation référence une finition connue
        Assert.All(Cat.Profils.SelectMany(p => p.Applications).Where(a => a.Finition is not null),
            a => Assert.NotNull(Cat.Finish(a.Finition)));
        Assert.All(Cat.Observations.SelectMany(o => o.Applications), a => Assert.NotNull(Cat.Finish(a.Finition)));
    }

    [Theory]
    [InlineData("Hall ascenseur", "circulation")]
    [InlineData("Grand hall", "accueil")]
    [InlineData("Toilettes PMR", "sanitaire")]
    [InlineData("Salle d'eau 2", "sanitaire")]
    [InlineData("Ateliers Educatif", "reunion_spectacle")]
    [InlineData("Atelier Potier N°3", "production")]
    [InlineData("Bureau Chef de gare", "administration")]
    [InlineData("Local technique électricité + serveur", "technique")]
    [InlineData("Chambre principale", "habitation")]
    public void Classement_des_locaux(string name, string family)
    {
        var c = new RoomClassifier(Cat.FamillesLocaux).Classify(name);
        Assert.NotNull(c);
        Assert.Equal(family, c!.FamilyCode);
    }

    [Fact]
    public void Local_inconnu_reste_a_classer()
    {
        Assert.Null(new RoomClassifier(Cat.FamillesLocaux).Classify("Zorglub"));
    }

    [Fact]
    public void Suggestion_sanitaire_dans_la_bibliotheque_G2()
    {
        var m = new FinishMatcher(Cat);
        var s = m.Suggest("Toilettes H", "N01", "KD_G2_APROMAC_TERTIAIRE");
        Assert.Equal("APO-03", s[0].Profile.Id);
    }

    [Fact]
    public void Local_non_classe_sans_libelle_proche_na_pas_de_suggestion()
    {
        var s = new FinishMatcher(Cat).Suggest("Boutique 1", "N00 _ RDC", null);
        Assert.Empty(s);
    }

    [Fact]
    public void Resume_ne_transforme_pas_un_vide_en_prescription()
    {
        var p = Cat.Profile("VLG-03")!;
        var r = FinishSummary.ForRoom(Cat, p.Applications);
        Assert.Null(r["Plafond"]);
        Assert.Equal("Peinture Pantex 800 avec enduit repassé + Faïence GC 30 × 60", r["Mur"]);
    }

    [Fact]
    public void Resume_conserve_la_teinte_noire_localisee()
    {
        var r = FinishSummary.ForRoom(Cat, Cat.Profile("PLA-20")!.Applications);
        Assert.Contains("Teinte noire", r["Mur"]);
        var sas = FinishSummary.ForRoom(Cat, Cat.Profile("PLA-18")!.Applications);
        Assert.DoesNotContain("noire", sas["Mur"]);
    }
}

public class NumberingTests
{
    [Fact]
    public void Numeros_sont_des_chaines()
    {
        Assert.Equal("1.10", Numbering.Format("1.{index:00}", ("index", 10)));
        Assert.Equal("D13.02", Numbering.Format("D{groupe}.{page:00}", ("groupe", 13), ("page", 2)));
        Assert.Equal("E1-c", Numbering.Format("E{groupe}-{lettre}", ("groupe", 1), ("lettre", Numbering.Letter(2))));
        Assert.Equal("aa", Numbering.Letter(26));
    }
}

public class PackerTests
{
    [Fact]
    public void Quatre_elevations_sur_deux_pages_si_trop_hautes()
    {
        var area = new RectMm(0, 0, 380, 270);
        var items = new[]
        {
            new PackItem("plan", 200, 200), new PackItem("3d", 150, 150),
            new PackItem("a", 180, 120, StartNewPage: true), new PackItem("b", 180, 120),
            new PackItem("c", 180, 120), new PackItem("d", 180, 120),
        };
        var r = SheetPacker.Pack(items, area, 10);
        Assert.Equal(2, r.PageCount);
        Assert.All(r.Placed.Where(p => p.Key.Length == 1), p => Assert.Equal(1, p.Page));
        Assert.Empty(r.Oversize);
        Assert.All(r.Placed, p => Assert.True(p.Rect.X >= 0 && p.Rect.Right <= 380 && p.Rect.Y >= 0 && p.Rect.Top <= 270));
    }

    [Fact]
    public void Plan_tableau_et_3D_tiennent_sur_une_page_sans_chevauchement()
    {
        var area = new RectMm(0, 0, 382, 277);
        var r = SheetPacker.Pack(new[] { new PackItem("plan", 160, 248), new PackItem("tab", 205, 40), new PackItem("3d", 200, 180) }, area, 10);
        Assert.Equal(1, r.PageCount);
        AssertNoOverlap(r);
    }

    [Fact]
    public void Quatre_elevations_en_grille_sur_une_page()
    {
        var area = new RectMm(0, 0, 382, 277);
        var items = Enumerable.Range(0, 4).Select(i => new PackItem("E" + i, 170, 110)).ToList();
        var r = SheetPacker.Pack(items, area, 10);
        Assert.Equal(1, r.PageCount);
        AssertNoOverlap(r);
    }

    [Fact]
    public void Oversize_view_keeps_its_own_page()
    {
        var r = SheetPacker.Pack(new[] { new PackItem("wide", 500, 40), new PackItem("small", 30, 30) }, new RectMm(0, 0, 380, 270), 10);
        Assert.Equal(2, r.PageCount);
        Assert.Equal(0, r.Placed[0].Page);
        Assert.Equal(1, r.Placed[1].Page);
    }

    private static void AssertNoOverlap(PackResult r)
    {
        foreach (var a in r.Placed)
            foreach (var b in r.Placed)
                if (a.Key != b.Key && a.Page == b.Page)
                    Assert.False(a.Rect.Intersects(b.Rect), $"{a.Key} chevauche {b.Key}");
    }

    [Fact]
    public void Vue_trop_grande_signalee()
    {
        var r = SheetPacker.Pack(new[] { new PackItem("x", 500, 100) }, new RectMm(0, 0, 380, 270), 10);
        Assert.Contains("x", r.Oversize);
    }
}

public class JoineryTests
{
    private static readonly JoineryClassifier C = new(new JoineryRules
    {
        Lots =
        {
            new JoineryLot { Code = "CAL", Prefixes = { "CAFa", "CAF", "CAP", "ENSA" } },
            new JoineryLot { Code = "CB", Prefixes = { "PBB", "PB", "PLR" } },
            new JoineryLot { Code = "CS", Prefixes = { "PM" } },
        },
    });

    [Theory]
    [InlineData("PB1A1", "CB", "PB")]
    [InlineData("PBB1A1", "CB", "PBB")]
    [InlineData("CAFa1", "CAL", "CAFa")]
    [InlineData("CAP2A1", "CAL", "CAP")]
    [InlineData("PM1A1", "CS", "PM")]
    public void Classement_par_prefixe(string mark, string lot, string prefix)
    {
        var c = C.Classify(mark);
        Assert.Equal(new JoineryClass(lot, prefix), c);
    }

    [Fact]
    public void Repere_inconnu_non_classe() => Assert.Null(C.Classify("Ouvrant à la française"));
}
