using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Consultation des BL : conditions SQL (sans base), puis lecture sur NP11F de test
public class ConsultationBlTests(ITestOutputHelper sortie)
{
    [Fact]
    public void Sans_critere_tous_les_types_et_pas_de_representant()
    {
        var (sql, valeurs) = ConsultationBlDb2.Filtre(new CriteresBl(), null);
        Assert.Equal("", sql);
        Assert.Empty(valeurs);
    }

    [Fact]
    public void Criteres_traduits_en_conditions_parametrees()
    {
        var (sql, valeurs) = ConsultationBlDb2.Filtre(new CriteresBl
        {
            NumeroCommande = 68916, CompteGeneral = 411100, CompteClient = 376, Du = new(2026, 1, 1), Au = new(2026, 1, 31),
            Facturation = FiltreFacturation.NonFactures, Divers = false, SousTraitances = false, ReferenceCommande = "po_1",
        }, representant: 28);
        Assert.Contains("e.HNODOS = ?", sql);
        Assert.Contains("h.HCLIE = ?", sql);
        Assert.Contains("e.HNOFAC = 0", sql);
        Assert.Contains("e.HETABL IN (?, ?, ?)", sql);
        Assert.Contains("e.HREP = ?", sql);
        Assert.DoesNotContain("68916", sql);   // valeurs jamais concaténées
        Assert.Equal<object?>([68916, 376, 411100, @"PO\_1%", 20260101, 20260131, "C", "V", "R", 28], valeurs);
    }

    [Fact]
    public void Compte_general_seul_filtre()
    {
        var c = new CriteresBl { CompteGeneral = 411100 };
        Assert.False(c.AucunCritere);
        Assert.Contains("h.HGENE = ?", ConsultationBlDb2.Filtre(c, null).Sql);
    }

    [Theory]
    [InlineData("ABC*", "UPPER(x.DART) LIKE", "ABC%")]
    [InlineData("*ABC", "TRIM(UPPER(x.DART)) LIKE", "%ABC")]
    [InlineData("*ABC*", "UPPER(x.DART) LIKE", "%ABC%")]
    [InlineData("ABC", "x.DART = ?", "ABC")]
    [InlineData("A%C*", "UPPER(x.DART) LIKE", @"A\%C%")]
    public void Article_avec_jokers(string saisie, string condition, string valeur)
    {
        var (c, v) = ConsultationBlDb2.Joker("x.DART", saisie);
        Assert.StartsWith(condition, c);
        Assert.Equal(valeur, v);
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static ConsultationBlService Service() => new(new ConsultationBlDb2(BaseDeTest.Configuration), new Utilisateur());

    [Fact]
    public async Task Aucun_type_choisi_refuse()
    {
        await Assert.ThrowsAsync<RegleMetierException>(() => Service().RechercherAsync(new CriteresBl
        {
            Clients = false, Divers = false, SousTraitances = false, Retours = false,
        }));
    }

    [FactDb2]
    public async Task BL_du_client_d_essai_puis_fiche()
    {
        var service = Service();
        var r = await service.RechercherAsync(new CriteresBl { CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient });
        Assert.NotEmpty(r.Lignes);
        Assert.All(r.Lignes, e => Assert.Equal(BaseDeTest.CompteClient, e.CompteClient));
        Assert.True(r.Lignes.Zip(r.Lignes.Skip(1)).All(p => p.First.NumeroBl >= p.Second.NumeroBl));
        var e = r.Lignes.First(x => x.Etat == EtatsBl.Client && x.Quantite > 0);
        sortie.WriteLine($"BL {e.Libelle} du {e.DateExpedition:dd/MM/yyyy} : commande {e.Dossier}/{e.CleDossier}, {e.Quantite} ex., {e.Ville}");

        var fiche = await service.LireAsync(e.NumeroBl);
        Assert.NotNull(fiche);
        Assert.Equal(e.NumeroBl, fiche.Entete.NumeroBl);
        var element = Assert.Single(fiche.Elements, x => x.Element == e.Element);
        Assert.Equal(e.Quantite, element.Lignes.Sum(l => l.Quantite));
        sortie.WriteLine($"{fiche.Elements.Count} élément(s), {element.Lignes.Count(l => !l.Texte)} ligne(s) article, " +
            $"{element.Lignes.Sum(l => l.Lots.Count)} lot(s)");

        // Recherche par article exact (zone complétée de blancs) et par « finit par »
        if (fiche.Elements.First(x => x.Element == e.Element).Lignes.FirstOrDefault(l => !l.Texte) is { } ligne)
        {
            Assert.Contains((await service.RechercherAsync(new CriteresBl { NumeroBl = e.NumeroBl, Article = ligne.Article })).Lignes,
                x => x.Element == e.Element);
            Assert.Contains((await service.RechercherAsync(new CriteresBl { NumeroBl = e.NumeroBl, Article = "*" + ligne.Article[^3..] })).Lignes,
                x => x.Element == e.Element);
        }

        // Recherche par n° de BL et par commande
        Assert.Contains((await service.RechercherAsync(new CriteresBl { NumeroBl = e.NumeroBl })).Lignes, x => x.Element == e.Element);
        Assert.All((await service.RechercherAsync(new CriteresBl { NumeroCommande = e.Dossier })).Lignes, x => Assert.Equal(e.Dossier, x.Dossier));
        Assert.Null(await service.LireAsync(999999));
    }
}
