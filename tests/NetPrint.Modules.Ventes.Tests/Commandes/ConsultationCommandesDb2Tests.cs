using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Intégration DB2, lecture seule : les requêtes de la liste des commandes s'exécutent sur NP11F de test
public class ConsultationCommandesDb2Tests(ITestOutputHelper sortie)
{
    private static readonly ConsultationCommandesDb2 Donnees = new(BaseDeTest.Configuration);

    private static CriteresCommandes Client(bool avecSoldees = true) => new()
    {
        CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, AvecSoldees = avecSoldees,
    };

    [FactDb2]
    public async Task Commandes_du_client_d_essai()
    {
        var lignes = await Donnees.RechercherAsync(BaseDeTest.Societe, Client(), default);
        Assert.All(lignes, l =>
        {
            Assert.Contains(l.Cctypc, new[] { "3", "4" });
            Assert.Equal(BaseDeTest.CompteGeneral, l.Rgen);
            Assert.Equal(BaseDeTest.CompteClient, l.Rcli);
        });
        var commandes = ConsultationCommandesService.Regrouper(lignes);
        sortie.WriteLine($"{commandes.Count} commandes, {lignes.Count} lignes lues ; états : " +
            string.Join(", ", commandes.GroupBy(c => c.Etat).Select(g => $"{g.Key} {g.Count()}")));
        foreach (var c in commandes.Take(5))
            sortie.WriteLine($"{c.Numero} {c.TypeCommande} {c.DateOuverture} {c.Designation} : {c.NbLignes} lignes, valeur {c.Valeur}, livré {c.TauxLivraison:P0}");
        Assert.Equal(commandes.Count, lignes.Select(l => (l.Rdoss, l.Ccdcle)).Distinct().Count());
    }

    [FactDb2]
    public async Task Sans_les_soldees_aucune_commande_soldee()
    {
        var lignes = await Donnees.RechercherAsync(BaseDeTest.Societe, Client(avecSoldees: false), default);
        Assert.All(lignes, l => Assert.Equal("", l.Cctags));
    }

    [FactDb2]
    public async Task Une_commande_retrouvee_par_son_numero()
    {
        var toutes = await Donnees.RechercherAsync(BaseDeTest.Societe, Client(), default);
        if (toutes.Count == 0) return;   // client sans commande : rien à comparer
        var premiere = toutes[0];
        var lignes = await Donnees.RechercherAsync(BaseDeTest.Societe,
            new CriteresCommandes { Numero = premiere.Rdoss, Cle = premiere.Ccdcle, AvecSoldees = true }, default);
        Assert.NotEmpty(lignes);
        Assert.All(lignes, l => Assert.Equal((premiere.Rdoss, premiere.Ccdcle), (l.Rdoss, l.Ccdcle)));
    }

    // Tous les critères à la fois : vérifie la syntaxe et les noms de colonnes de chaque condition (résultat sans importance)
    [FactDb2]
    public async Task Tous_les_criteres_ensemble_s_executent()
    {
        var c = new CriteresCommandes
        {
            Numero = 1, Cle = "A", CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient,
            Representant = 1, Devis = 1, Produit = 1, Article = "*A*", Indice = "01", ReferenceImprime = "R_%",
            ReferenceCommandeClient = "P", NumeroAppel = "A", Fabricant = "X", BonLivraison = 1, Facture = 1,
            Categorie = "$1", SousCategorie = "01", Designation = "étiquette", DateDu = new(2020, 1, 1), DateAu = new(2026, 12, 31),
            LivraisonDu = new(2020, 1, 1), LivraisonAu = new(2026, 12, 31), TypeCommande = "3", Enseigne = "A",
            FiniLargeur = 10, FiniHauteur = 10, FiniTroisiemeCote = 1, AvecSoldees = true,
        };
        var lignes = await Donnees.RechercherAsync(BaseDeTest.Societe, c, default);
        Assert.NotNull(lignes);
    }

    [FactDb2]
    public async Task Jokers_article_en_debut_et_en_fin()
    {
        foreach (var article in new[] { "A*", "*A", "*A*", "ABC" })
            Assert.NotNull(await Donnees.RechercherAsync(BaseDeTest.Societe, new CriteresCommandes { Article = article, TypeCommande = "4" }, default));
    }

    // Fiche : la plus récente commande soldée du client (avec BL et factures) et une commande en cours
    [FactDb2]
    public async Task Fiches_du_client_d_essai()
    {
        var commandes = ConsultationCommandesService.Regrouper(await Donnees.RechercherAsync(BaseDeTest.Societe, Client(), default));
        foreach (var c in new[] { commandes.FirstOrDefault(c => c.Soldee), commandes.FirstOrDefault(c => !c.Soldee) }.OfType<CommandeListe>())
        {
            var brut = await Donnees.LireFicheAsync(BaseDeTest.Societe, c.Cle, default);
            Assert.NotNull(brut);
            var fiche = ConsultationCommandesService.VersFiche(brut, DroitsCommandes.Tous);
            Assert.Equal(c.Cle, fiche.Entete.Cle);
            Assert.True(fiche.Lignes.Count >= c.NbLignes);   // la fiche montre aussi les lignes « à la demande » non mouvementées
            sortie.WriteLine($"{c.Numero} {fiche.Entete.Etat} : {fiche.Lignes.Count} lignes, {fiche.Livraisons.Count} BL " +
                $"({fiche.Livraisons.Sum(l => l.Lignes.Count)} lignes livrées), {fiche.Factures.Count} factures, " +
                $"{fiche.Journal.Count} mouvements, {fiche.Historique.Count} historiques ; règlement « {fiche.Entete.Reglement} », " +
                $"facturation « {fiche.Entete.TypeFacturation} », {fiche.Entete.Livraison}, {fiche.Entete.Port}");
        }
    }

    [FactDb2]
    public async Task Fiche_inexistante() =>
        Assert.Null(await Donnees.LireFicheAsync(BaseDeTest.Societe, new CleCommande(99999, "Z"), default));

    [FactDb2]
    public async Task Representant_et_droits_lisibles()
    {
        _ = await Donnees.RepresentantImposeAsync(BaseDeTest.Societe, "QSECOFR", default);
        var options = await Donnees.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, "QSECOFR", default);
        if (options is not null) Assert.Equal(8, options.Count);
    }
}
