using AvionParTerre.Core.Layout;
using AvionParTerre.Core.Profiles;
using Xunit;

namespace AvionParTerre.Core.Tests;

public class AnnotationTests
{
    [Fact]
    public void Chaine_de_cotes_triee_et_references_confondues_fusionnees()
    {
        // mur, gauche de baie, droite de baie, mur ; la droite de baie confondue avec le mur de fin
        var kept = Annotation.Chain(new[] { 4.10, 0.0, 1.05, 1.18, 4.105 }, 0.01);
        Assert.Equal(new[] { 1, 2, 3, 0 }, kept);
    }

    [Fact]
    public void Annotations_empilees_sans_chevauchement_dans_l_ordre_des_cibles()
    {
        // poignée (1050) et serrure (940) trop proches : la serrure descend ; ordre vertical conservé
        var y = Annotation.Stack(new double[] { 2150, 1050, 940, 250 }, 150, 0, 2400);
        Assert.Equal(2150, y[0]);
        Assert.Equal(1050, y[1]);
        Assert.Equal(900, y[2]);
        Assert.Equal(250, y[3]);
    }

    [Fact]
    public void Pile_d_annotations_remontee_si_elle_depasse_le_bas()
    {
        var y = Annotation.Stack(new double[] { 100, 90, 80 }, 50, 0, 1000);
        Assert.Equal(new double[] { 100, 50, 0 }, y);
    }

    [Theory]
    [InlineData(0, -1, "FACADE SUD")]
    [InlineData(0, 1, "FACADE NORD")]
    [InlineData(1, 0.2, "FACADE EST")]
    [InlineData(-1, -0.3, "FACADE OUEST")]
    public void Nom_de_facade_d_apres_le_cote_vu(double x, double y, string name) => Assert.Equal(name, Annotation.FacadeName(x, y));

    [Fact]
    public void Axe_principal_du_batiment_pondere_par_la_longueur_des_murs()
    {
        var a = 20 * Math.PI / 180;
        var angle = Annotation.DominantAngle(new[] { (a, 30.0), (a + Math.PI / 2, 12.0), (a + Math.PI, 30.0), (0.0, 3.0) });
        Assert.Equal(20, angle * 180 / Math.PI, 3);
    }

    [Fact]
    public void Annotations_de_menuiseries_du_profil()
    {
        var p = PluginProfile.Load(Path.Combine(AppContext.BaseDirectory, "data", "profile.json"));
        var porteBois = p.Menuiseries.AnnotationsFor("CB", isDoor: true, "PB2")!;
        Assert.Contains(porteBois.Notes, n => n.Texte == "Poignées inox" && n.Cible == "poignee");
        Assert.Contains(porteBois.Notes, n => n.Texte == "Serrure à cylindre");
        Assert.Contains(porteBois.Notes, n => n.Texte == "Charnière réversible à billes");
        // Préfixe explicite (volet jalousie) prioritaire sur la règle générale des fenêtres aluminium
        Assert.Contains(p.Menuiseries.AnnotationsFor("CAL", isDoor: false, "EnsVJ7")!.Notes, n => n.Cible == "lames");
        Assert.DoesNotContain(p.Menuiseries.AnnotationsFor("CAL", isDoor: false, "CAF3")!.Notes, n => n.Cible == "lames");
        Assert.Null(p.Menuiseries.AnnotationsFor(null, true, "XX"));
        Assert.Equal("CALEPIN BOIS", p.Menuiseries.Lots.First(l => l.Code == "CB").SousDossier);
        Assert.Equal("DCE", p.Rangement.Dossier);
        Assert.Equal("cm", p.Cotation.UniteCarnets);
        Assert.Equal(1050, p.Cotation.HauteurPoigneeMm);
    }
}
