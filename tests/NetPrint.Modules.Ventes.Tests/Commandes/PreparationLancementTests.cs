using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;
using P = NetPrint.Modules.Ventes.Services.PreparationLancement;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Valeurs proposées par la fenêtre de lancement et F10, contrôles côté commande (NEG515 § 2.7, 3.2, 3.4 ; NEG510 F10), sans base
public class PreparationLancementTests
{
    private static readonly DateOnly Jour = new(2026, 10, 30);
    private static readonly DateOnly Livraison = new(2026, 11, 20);
    private static readonly NumeroOf EnCours = new(500, "1");
    private static readonly NumeroOf Dernier = new(400, "2");

    private static readonly P.Commande Cde = new(new CleCommande(24, "9"), 411100, 376, "1", "REF", "M. DUPONT", "ETIQUETTES NOEL", "",
        new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30), Livraison, false);

    private static P.Article Stocke(string code) => new(code, "", true, "02", "R", $"ARTICLE {code}", "N1", null, null, null, 0, null, null);

    private static P.Donnees Donnees(bool sop22, params P.Article[] articles) => new(Cde, sop22, articles,
        new Dictionary<DevisOrigine, P.Devis>(), new Dictionary<NumeroOf, P.Dossier>());

    private static Dictionary<ReferenceArticle, int> Restes(params (string, int)[] r) => r.ToDictionary(x => new ReferenceArticle(x.Item1, ""), x => x.Item2);

    [Fact]
    public void Reste_a_affecter_propose_et_valeurs_de_la_commande()
    {
        var d = P.Proposer(Donnees(false, Stocke("A1")), Restes(("A1", 1200)), Jour);
        var b = Assert.Single(d.Articles);
        Assert.Equal((1200, 1200, Livraison), (b.AFabriquer, b.AAffecter, b.DateFinFabrication));
        // Quantité lancée laissée vide : la production prend le total à fabriquer
        Assert.Equal((Livraison, Jour, 0, "1", "M. DUPONT"), (d.DateMiseADisposition, d.DateDossier, d.QuantiteLancee, d.Enseigne, d.Interlocuteur));
        Assert.Equal(("02", "R", "ARTICLE A1", "N1"), (d.Categorie, d.SousCategorie, d.Designation1, d.Norme));
        // Reste négatif (affecté au-delà du commandé) : rien à fabriquer proposé
        Assert.Equal(0, P.Proposer(Donnees(false, Stocke("A1")), Restes(("A1", -50)), Jour).Articles[0].AFabriquer);
    }

    [Fact]
    public void Dossier_precedent_fabrication_en_cours_puis_dernier_dossier_et_son_modele()
    {
        var a = Stocke("A1") with { FabricationEnCours = EnCours, DernierDossier = Dernier };
        var donnees = Donnees(false, a) with { Dossiers = new Dictionary<NumeroOf, P.Dossier> { [EnCours] = new(321, "B") } };
        var d = P.Proposer(donnees, Restes(("A1", 10)), Jour);
        Assert.Equal((EnCours, 321, "B"), (d.DossierPrecedent, d.Modele, d.IndiceComposition));
        Assert.Equal(Dernier, P.Proposer(Donnees(false, a with { FabricationEnCours = null }), Restes(("A1", 10)), Jour).DossierPrecedent);
    }

    [Fact]
    public void Modele_par_article_SOP22_designation_et_dernier_dossier_du_modele()
    {
        var a = Stocke("A1") with { FabricationEnCours = EnCours, Modele = 777, DesignationModele = ("MODELE 777", "SUITE"), DernierDossierModele = Dernier };
        var d = P.Proposer(Donnees(true, a), Restes(("A1", 10)), Jour);
        Assert.Equal((777, "MODELE 777", "SUITE", Dernier), (d.Modele, d.Designation1, d.Designation2, d.DossierPrecedent));
    }

    [Fact]
    public void Devis_designation_dossier_precedent_et_chemin_faconnage()
    {
        var devis = new DevisOrigine(15017, 1, 2);
        var donnees = Donnees(false, Stocke("A1") with { Devis = devis }) with
        {
            Devis = new Dictionary<DevisOrigine, P.Devis> { [devis] = new("DEVIS 15017", "", Dernier, "CF01") },
        };
        var d = P.Proposer(donnees, Restes(("A1", 10)), Jour);
        Assert.Equal((devis, "DEVIS 15017", Dernier, "CF01"), (d.Devis, d.Designation1, d.DossierPrecedent, d.CheminFaconnage));
    }

    [Fact]
    public void Groupage_sans_designation_d_article_et_article_non_stocke_ignore()
    {
        var nonStocke = Stocke("B1") with { Stocke = false, Famille = "09", Designation = "NON STOCKE" };
        var d = P.Proposer(Donnees(false, nonStocke, Stocke("A1")), Restes(("A1", 10), ("B1", 5)), Jour);
        Assert.Equal(("02", "", 15), (d.Categorie, d.Designation1, d.Articles.Sum(b => b.AFabriquer)));
        Assert.Equal(2, d.Articles.Count);
    }

    [Fact]
    public void F10_tout_le_commande_designation_de_la_commande_et_categorie_de_secours()
    {
        var nonStocke = Stocke("B1") with { Stocke = false, Famille = "09", SousFamille = "Z" };
        var d = P.Automatique(Donnees(false, nonStocke, Stocke("A1")), Restes(("A1", 1000), ("B1", 300)), Jour);
        Assert.Equal(("ETIQUETTES NOEL", "02", new DateOnly(2026, 10, 1), Livraison), (d.Designation1, d.Categorie, d.DateDossier, d.DateMiseADisposition));
        Assert.Equal(1000, d.QuantiteLancee);   // articles stockés seulement
        Assert.All(d.Articles, b => Assert.Equal(b.AFabriquer, b.AAffecter));
        // Sans article stocké : catégorie du premier article (NEG510 la laissait vide)
        Assert.Equal(("09", "Z"), (P.Automatique(Donnees(false, nonStocke), Restes(("B1", 300)), Jour) is var x ? (x.Categorie, x.SousCategorie) : default));
    }

    // ---------- Contrôles ----------
    private static readonly NumeroOf Of = new(24, "1");

    private static ArticleALancer Article(int commande, int affecte, params SourceArticle[] sources) => new("A1", "", "", "", true, commande, affecte, sources);

    private static SourceArticle Source(NatureSource nature, int affecteCommande, int disponible, bool stock = false) =>
        new(nature, Of, affecteCommande, 0, 0, 0, 0, 0, 0, disponible, 0, 0, stock, false, null, affecteCommande > 0);

    private static DemandeOf Demande(int aFabriquer, int aAffecter) =>
        P.Proposer(Donnees(false, Stocke("A1")), Restes(("A1", 0)), Jour) with { Articles = [new("A1", "", aFabriquer, aAffecter, Livraison)] };

    [Fact]
    public void Lancement_affecte_au_dela_du_commande_refuse()
    {
        Assert.Empty(P.ControlerAffectations(Demande(600, 600), [Article(1000, 400)], null, []));
        var e = Assert.Single(P.ControlerAffectations(Demande(700, 700), [Article(1000, 400)], null, []));
        Assert.Equal(("Articles[0].AAffecter", P.Messages.AffecteSuperieurCommande), (e.Zone, e.Message));
        Assert.Equal(P.Messages.ArticleHorsCommande,
            Assert.Single(P.ControlerAffectations(Demande(1, 1) with { Articles = [new("ZZ", "", 1, 1, Livraison)] }, [Article(1000, 0)], null, [])).Message);
    }

    [Fact]
    public void Modification_hors_ce_que_l_OF_affectait_deja_et_borne_au_disponible()
    {
        // Commandé 1000, affecté 1000 dont 800 par cet OF (800 à fabriquer) : passer à 1000 dépasse le commandé
        var article = Article(1000, 1000, Source(NatureSource.OfAOuvrir, 800, 0));
        BesoinArticle[] anciens = [new("A1", "", 800, 800, Livraison)];
        Assert.Empty(P.ControlerAffectations(Demande(800, 800), [article], Of, anciens));
        Assert.Equal(P.Messages.AffecteSuperieurCommande, Assert.Single(P.ControlerAffectations(Demande(1000, 1000), [article], Of, anciens)).Message);

        // Commandé 2000, affecté 800 par cet OF, disponible 0 : affecter 900 demande 100 de plus à fabriquer
        var ouvert = Article(2000, 800, Source(NatureSource.OfAOuvrir, 800, 0));
        Assert.Equal(P.Messages.AffecteSuperieurDisponible, Assert.Single(P.ControlerAffectations(Demande(800, 900), [ouvert], Of, anciens)).Message);
        Assert.Empty(P.ControlerAffectations(Demande(900, 900), [ouvert], Of, anciens));
    }

    [Fact]
    public void Affectation_de_stock()
    {
        var lot = Source(NatureSource.Lot, 0, 500);
        var a = Article(1000, 200, lot);
        Assert.Empty(P.ControlerAffectationStock(a, lot, 500));
        Assert.Equal(P.Messages.AffecteSuperieurDisponible, P.ControlerAffectationStock(a, lot, 501).Single().Message);
        Assert.Equal(P.Messages.AffecteSuperieurCommande, P.ControlerAffectationStock(Article(600, 200, lot), lot, 500).Single().Message);
        Assert.Equal(P.Messages.Obligatoire, P.ControlerAffectationStock(a, lot, 0).Single().Message);
        // OF à ouvrir, ou sans disponible ni affectation de stock : impossible
        Assert.Equal(P.Messages.AffectationImpossible, P.ControlerAffectationStock(a, Source(NatureSource.OfAOuvrir, 0, 500), 10).Single().Message);
        Assert.Equal(P.Messages.AffectationImpossible, P.ControlerAffectationStock(a, Source(NatureSource.OfOuvert, 0, 0), 10).Single().Message);
        // Affectation existante modifiable même sans disponible : 300 déjà affectés, ramenés à 250
        var affecte = Source(NatureSource.OfOuvert, 300, 0, stock: true);
        Assert.Empty(P.ControlerAffectationStock(Article(1000, 300, affecte), affecte, 250));
    }
}
