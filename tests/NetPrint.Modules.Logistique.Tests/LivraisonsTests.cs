using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Livraisons à effectuer et fiche de préparation (EXP078) : règles sans base, puis lecture sur NP11F de test
public class LivraisonsTests(ITestOutputHelper sortie)
{
    [Fact]
    public void Periodes_semaine_ISO()
    {
        var jeudi = new DateOnly(2026, 10, 29);
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)), new CriteresLivraisons().Bornes(jeudi));
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8)),
            new CriteresLivraisons { Periode = PeriodeLivraison.SemaineSuivante }.Bornes(jeudi));
        var dimanche = new DateOnly(2026, 11, 1);
        Assert.Equal(new DateOnly(2026, 10, 26), new CriteresLivraisons().Bornes(dimanche).Du);   // le dimanche termine la semaine
    }

    [Fact]
    public void Conditionnement_et_montant_a_facturer()
    {
        Assert.Equal((4, 1, 1, 10m), ReglesLivraisons.Conditionnement(3500, 1000, 6, 40, 2.5m, "P"));   // 4 UC, 1 carton, 1 palette
        Assert.Equal((4, 4, 0, 0m), ReglesLivraisons.Conditionnement(3500, 1000, 0, 0, 0, "C"));        // emballage carton : UC = cartons
        Assert.Equal((0, 0, 0, 0m), ReglesLivraisons.Conditionnement(0, 1000, 6, 40, 2.5m, "P"));
        Assert.Equal(150m, ReglesLivraisons.MontantAFacturer(5000, 2000, 50, 1000));                      // 3 000 × 50 ‰
        Assert.Equal(300m, ReglesLivraisons.MontantAFacturer(5000, 2000, 500, 999_999_999));              // « le tout » au prorata
        Assert.Equal(0m, ReglesLivraisons.MontantAFacturer(5000, 5000, 50, 1000));
    }

    private static LigneALivrer Ligne(int dossier, string cle = "9") => new(
        dossier, cle, 1, "", 411100, 20, "DIOR", false, "ART", "", "", "", null, null, "", 100, 0, 100, false,
        new AdresseBl(0, "", "DIOR", "R", "", "45000", "ORLEANS", "45", ""), null, null, 0, 0, 0, 0, 0, 0, 0, 0, "", 0);

    [Fact]
    public void Creer_le_BL_d_un_seul_dossier()
    {
        Assert.NotNull(LivraisonsService.ControlerPourBl([]));
        Assert.Null(LivraisonsService.ControlerPourBl([Ligne(1), Ligne(1)]));
        Assert.Contains("un seul dossier", LivraisonsService.ControlerPourBl([Ligne(1), Ligne(2)]));
    }

    [Fact]
    public void Fiches_par_jour_client_adresse()
    {
        var a = new AdresseBl(0, "", "DIOR", "R", "", "45000", "ORLEANS", "45", "");
        LignePreparation L(DateOnly date, int dossier, AdresseBl adresse) => new(date, 411100, 20, "DIOR", false, adresse, [], "TNT", dossier, "9",
            "", "", null, false, 1, "ART1", "", "", "", "ETUI", 3500, 0, 1000, "P", 6, 40, 2.5m, "", []);
        var j1 = new DateOnly(2023, 11, 2);
        var lignes = new[] { L(j1, 1, a), L(j1, 2, a), L(j1, 3, a with { Numero = 2, Ville = "PARIS" }), L(j1.AddDays(1), 4, a) };
        var fiches = Preparations.Construire(lignes, [new LotPreparation("ART1", "", "12345A", 800, null)], new Dictionary<(string, string), int> { [("ART1", "")] = 500 });
        Assert.Equal(3, fiches.Count);                       // 2 commandes à la même adresse le même jour : une seule fiche
        Assert.Equal(2, fiches[0].Commandes.Count);
        Assert.Equal((20m, 2, 2), (fiches[0].Poids, fiches[0].Colis, fiches[0].Palettes));
        var l = fiches[0].Commandes[0].Lignes[0];
        Assert.Equal(["Lot 12345A : 800"], l.Lots);
        Assert.Equal("En fabrication : 500", l.EnFabrication);
        Assert.Equal("ART1", l.CodeBarre);
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    [FactDb2]
    public async Task Livraisons_de_novembre_2023_et_fiche_de_preparation()
    {
        var service = new LivraisonsService(new LivraisonsDb2(BaseDeTest.Configuration), new Utilisateur());
        var r = await service.RechercherAsync(new CriteresLivraisons
        {
            Periode = PeriodeLivraison.Dates, Du = new DateOnly(2023, 11, 1), Au = new DateOnly(2023, 11, 30), CompteGeneral = 411100, CompteClient = 20,
        });
        sortie.WriteLine($"{r.Lignes.Count} ligne(s)");
        Assert.NotEmpty(r.Lignes);
        Assert.All(r.Lignes, l => Assert.Equal(20, l.CompteClient));
        foreach (var l in r.Lignes.Take(5))
            sortie.WriteLine($"{l.DateLivraison:dd/MM} {l.LibelleCommande} {l.Article} reste {l.Reste} stock {l.Stock} fab {l.EnFabrication} OPR {l.Opr} UC {l.Uc}");
        await Assert.ThrowsAsync<RegleMetierException>(() => service.RechercherAsync(new CriteresLivraisons { Periode = PeriodeLivraison.Dates }));

        var pdf = await new PreparationService(new PreparationDb2(BaseDeTest.Configuration), new Utilisateur())
            .EditerAsync(new CriteresPreparation(new DateOnly(2023, 11, 1), new DateOnly(2023, 11, 30), 411100, 20));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Contenu, 0, 4));
        var fichier = Path.Combine(Path.GetTempPath(), pdf.Nom);
        await File.WriteAllBytesAsync(fichier, pdf.Contenu);
        sortie.WriteLine(fichier);
    }
}
