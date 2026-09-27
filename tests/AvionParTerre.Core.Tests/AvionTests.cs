using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Joinery;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;
using Xunit;

namespace AvionParTerre.Core.Tests;

public class ResourceNameTests
{
    [Theory]
    [InlineData("Etiquette de porte 100è:Nombre", "Etiquette de porte 100e:Nombre")]
    [InlineData("Calepin  Baies_Vue en plan", "calepin baies_vue en plan")]
    public void Noms_compares_sans_accents_ni_casse(string a, string b) => Assert.True(TextNorm.SameResource(a, b));

    [Theory]
    [InlineData("Cartouche A3 Horizontale1", "Cartouche A3 Horizontale")]
    [InlineData("Cartouche APD-DCE (A0-A1)1", "Cartouche APD-DCE (A0-A1)")]
    [InlineData("Titre de vue 2", "Titre de vue")]
    [InlineData("Cartouche APD-DCE (A3) (1)", "Cartouche APD-DCE (A3)")]
    public void Famille_rechargee_reconnue(string loaded, string expected) =>
        Assert.Equal(TextNorm.FamilyStem(expected), TextNorm.FamilyStem(loaded));

    [Fact]
    public void Marges_du_cartouche_recharge()
    {
        var rules = new LayoutRules { ZonesCartouche = { ["Cartouche A3 Horizontale"] = new Margins { Droite = 28 } } };
        Assert.Equal(28, rules.MarginsFor("Cartouche A3 Horizontale1").Droite);
    }
}

public class LevelNamingTests
{
    [Theory]
    [InlineData("N00 _ RDC", "PLAN DU REZ-DE-CHAUSSEE")]
    [InlineData("N01 _ 1er Etage", "PLAN DU 1ER ETAGE")]
    [InlineData("N02_N02", "PLAN DU 2EME ETAGE")]
    [InlineData("N03_Toiture", "PLAN DE TOITURE")]
    [InlineData("R+2", "PLAN DU 2EME ETAGE")]
    [InlineData("SS1 _ Sous Sol1", "PLAN DU SOUS-SOL")]
    [InlineData("Zorglub", "PLAN ZORGLUB")]
    public void Titres_de_plans_a_la_maniere_de_l_agence(string level, string title) =>
        Assert.Equal(title, LevelNaming.PlanTitle(level));

    [Theory]
    [InlineData(0, "+/-0.00")]
    [InlineData(3.92, "+3.92")]
    [InlineData(-3.3, "-3.30")]
    public void Niv_au_format_agence(double m, string expected) => Assert.Equal(expected, LevelNaming.FormatNiv(m));
}

public class JoineryLooseTests
{
    private static readonly JoineryClassifier C = new(new JoineryRules
    {
        Lots = { new JoineryLot { Code = "CAL", Prefixes = { "CAV", "PAV" } }, new JoineryLot { Code = "CB", Prefixes = { "PB" } } },
    });

    [Fact]
    public void Prefixe_approche_signale()
    {
        var c = C.Classify("PBTV1D1");
        Assert.NotNull(c);
        Assert.Equal("CB", c!.Lot);
        Assert.False(c.Exact);
        Assert.True(C.Classify("CAV3")!.Exact);
    }
}

public class AdvisorTests
{
    private static readonly FinishCatalogue Cat = FinishCatalogue.Load(Path.Combine(AppContext.BaseDirectory, "data", "catalogue.json"));

    private sealed class Canned : ILlmClient
    {
        private readonly string _answer;
        public LlmRequest? Last;
        public Canned(string answer) => _answer = answer;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(new LlmResponse { Content = _answer, PromptTokens = 100, CompletionTokens = 50, Cost = 0.01 });
        }
    }

    [Fact]
    public void Json_extrait_d_un_bloc_markdown()
    {
        var n = JsonExtract.FirstObject("Voici :\n```json\n{\"a\": \"x { y }\", \"b\": [1]}\n```\nFin");
        Assert.Equal("x { y }", JsonExtract.Str(n, "a"));
    }

    [Fact]
    public async Task Decisions_IA_validees_contre_le_referentiel()
    {
        var sol = Cat.Finitions.First(f => f.Support == "Sol");
        var answer = "{\"pieces\":[{\"id\":\"r1\",\"famille\":\"inconnue\",\"profil\":\"PLA-05\",\"sol\":\"" + sol.Designation.ToUpperInvariant() +
                     "\",\"mur\":\"Enduit à la chaux teinté (hors référentiel)\",\"plafond\":\"ne pas écrire\",\"justification\":\"test\",\"sources\":[]}]}";
        var llm = new Canned(answer);
        var log = new DecisionLog();
        var adv = new AutopilotAdvisor(llm, Cat, new JoineryRules(), new AiOptions { WebSearch = true }, log);
        var rooms = new List<RoomInput>
        {
            new() { Key = "r1", Name = "Zorglub", Level = "RDC", Known = { ["Plafond"] = "Dalle béton brute" } },
            new() { Key = "r2", Name = "zorglub", Level = "RDC", Known = { ["Plafond"] = "Dalle béton brute" } },
        };
        var res = await adv.AdviseRoomsAsync(rooms, null, new ProjectContext { Document = "test" });

        Assert.True(llm.Last!.WebSearch); // local non classé → recherche internet
        Assert.Equal(2, res.Count);       // une seule question pour deux locaux de même libellé
        var a = res.First(r => r.Key == "r2");
        Assert.Equal("PLA-05", a.ProfileId);
        Assert.Null(a.FamilyCode);                        // famille inconnue rejetée
        Assert.Equal(sol.Code, a.Finishes["Sol"].Code);   // désignation rattachée au référentiel
        Assert.True(a.Finishes["Mur"].HorsReferentiel);
        Assert.False(a.Finishes.ContainsKey("Plafond"));  // déjà décidé : jamais modifié
        Assert.Contains(adv.Errors, e => e.Contains("inconnue"));
        Assert.Equal(0.01, log.CoutUsd);
    }

    [Fact]
    public async Task Lot_et_prescriptions_des_menuiseries()
    {
        var llm = new Canned("{\"menuiseries\":[{\"repere\":\"XX1\",\"lot\":\"CB\",\"prescriptions\":\"- Bois dur\\n- Quincaillerie inox\",\"justification\":\"porte bois\"},{\"repere\":\"YY1\",\"lot\":\"ZZ\"}]}");
        var rules = new JoineryRules { Lots = { new JoineryLot { Code = "CB", Libelle = "Bois" } } };
        var adv = new AutopilotAdvisor(llm, Cat, rules, new AiOptions(), new DecisionLog());
        var res = await adv.AdviseJoineryAsync(new List<JoineryInput> { new() { Mark = "XX1" }, new() { Mark = "YY1" } }, new ProjectContext());
        Assert.Equal("CB", res.First(r => r.Mark == "XX1").Lot);
        Assert.Equal("— Bois dur\n— Quincaillerie inox", res.First(r => r.Mark == "XX1").Prescriptions);
        Assert.Null(res.First(r => r.Mark == "YY1").Lot);
    }

    [Fact]
    public void Journal_des_decisions_en_csv()
    {
        var log = new DecisionLog();
        log.Add("Finitions", "101 Bureau", "Sol : Grès", DecisionSource.Ia, "proche d'un bureau", new[] { "https://a" }, "m/x");
        var csv = log.ToCsv();
        Assert.Contains("IA (m/x)", csv);
        Assert.Equal("IA : 1", log.Summary());
    }
}
