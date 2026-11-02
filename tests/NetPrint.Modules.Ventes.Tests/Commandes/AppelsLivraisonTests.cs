using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using A = NetPrint.Modules.Ventes.Contrats.AvertissementsLigne;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Règles des appels de livraison (CalculAppels) et moteur commun (SaisieLignesService.Appels), sans base
public class AppelsLivraisonTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 10, 30);

    // ---------- Ligne d'origine (règle de NEG300NAV, recalculée depuis le contrat) ----------
    [Theory]
    [InlineData(10000, 0, 0, 10000, 10000, false)]      // aucun appel
    [InlineData(10000, 3000, 0, 7000, 7000, false)]     // appel partiel
    [InlineData(10000, 10000, 0, 0, 0, true)]           // tout appelé : soldée
    [InlineData(10000, 12000, 0, 0, 0, true)]           // dépassement accepté : jamais de reste négatif
    [InlineData(10000, 6000, 4000, 0, 0, true)]         // livré directement sur la ligne : plus rien à livrer
    [InlineData(10000, 6000, 1000, 4000, 3000, false)]
    public void Reste_de_la_ligne_d_origine(int contrat, int appelee, int livree, int reste, int resteALivrer, bool soldee) =>
        Assert.Equal((reste, resteALivrer, soldee), CalculAppels.Origine(contrat, appelee, livree));

    [Fact]
    public void Origine_soldee_remise_en_cours_quand_un_appel_est_supprime()
    {
        Assert.True(CalculAppels.Origine(5000, 5000, 0).Soldee);
        Assert.False(CalculAppels.Origine(5000, 2000, 0).Soldee);   // appel de 3000 supprimé : 3000 à appeler de nouveau
    }

    [Theory]
    [InlineData(true, 4000, true)]     // soldée à la main avec 4000 encore à appeler : le solde tient
    [InlineData(true, 0, false)]       // soldée parce que tout était appelé : recalculée
    [InlineData(false, 4000, false)]
    public void Solde_manuel_de_l_origine_conserve(bool soldee, int resteEnregistre, bool conserve) =>
        Assert.Equal(conserve, CalculAppels.SoldeeParLUtilisateur(soldee, resteEnregistre));

    [Fact]
    public void Article_d_une_ligne_a_la_demande_fige_quand_elle_a_des_appels()
    {
        var ancienne = Origine();
        var l = ancienne.Avec(x => x.Article = "AUTRE");
        Assert.Contains(SaisieLignesService.Controler(Contexte(), l, Article(), Verifs() with { AppelsExistent = true }, ancienne, Aujourdhui).Erreurs,
            e => e.Zone == nameof(LigneSaisie.Article));
        Assert.DoesNotContain(SaisieLignesService.Controler(Contexte(), l, Article(), Verifs(), ancienne, Aujourdhui).Erreurs,
            e => e.Zone == nameof(LigneSaisie.Article));
    }

    [Theory]
    [InlineData(3000, 0, 5000, false)]
    [InlineData(6000, 0, 5000, true)]                   // création au-delà du reste
    [InlineData(6000, 3000, 2000, true)]                // modification : +3000 pour 2000 restant
    [InlineData(4000, 3000, 2000, false)]
    [InlineData(1000, 3000, 0, false)]                  // baisse d'un appel sur une origine épuisée
    public void Depassement_du_reste_a_appeler(int quantite, int ancienne, int reste, bool depasse) =>
        Assert.Equal(depasse, CalculAppels.Depasse(quantite, ancienne, reste));

    [Fact]
    public void Maxi_au_prorata_et_solde_du_maxi()
    {
        Assert.Equal(3300, CalculAppels.MaxiProrata(3000, 11000, 10000));
        Assert.Null(CalculAppels.MaxiProrata(3000, 0, 10000));
        Assert.Null(CalculAppels.MaxiProrata(3000, 11000, 0));
        Assert.Equal(4000, CalculAppels.MaxiSolde(11000, 7000));
        Assert.Equal(0, CalculAppels.MaxiSolde(11000, 12000));
    }

    // ---------- Nouvel appel : image de l'origine ----------
    [Fact]
    public void Nouvel_appel_reprend_l_origine_sans_quantite_ni_date()
    {
        var o = Origine();
        var l = SaisieLignesService.NouvelAppel(o);
        Assert.True(l.EstAppel);
        Assert.Equal(0, l.NumeroLigne);
        Assert.Equal(4, l.LigneOrigine);
        Assert.Equal(7000, l.ResteOrigine);
        Assert.Null(l.Quantite);
        Assert.Null(l.DateLivraison);
        Assert.Null(l.MaxiLivrable);
        Assert.Equal((49m, 10m, "LM", 3m), (l.PrixBrut!.Value, l.Remise!.Value, l.Unite, l.TauxCommission!.Value));
        Assert.Equal(44.1m, l.PrixNet);
        Assert.Equal((12, "APP-1", "Quai 3"), (l.NumeroAdresse!.Value, l.NumeroAppel, l.Commentaire1));
        Assert.True(l.PrixSaisi && l.CommissionSaisie);   // le tarif et la commission ne sont pas recalculés
        Assert.Equal(0, l.QuantiteOrigine);
        Assert.Empty(l.Empreinte);
    }

    [Fact]
    public void Le_prix_d_un_appel_ne_vient_jamais_de_l_appelant()
    {
        var l = SaisieLignesService.NouvelAppel(Origine());
        l.PrixBrut = 1;
        l.Article = "AUTRE";
        l.Unite = "ML";
        SaisieLignesService.Imposer(l, Origine());
        Assert.Equal((49m, "393229", "LM"), (l.PrixBrut!.Value, l.Article, l.Unite));
    }

    [Fact]
    public void Le_maxi_par_tranches_ne_s_applique_pas_a_un_appel()
    {
        var c = Contexte() with { CalculMaxi = true, MaxiSociete = [(100000, 10)] };
        var l = SaisieLignesService.NouvelAppel(Origine());
        l.Quantite = 3000;
        SaisieLignesService.RecalculerMaxi(c, l);
        Assert.Null(l.MaxiLivrable);
    }

    // ---------- Contrôles ----------
    [Fact]
    public void Appel_au_dela_du_reste_a_accepter()
    {
        var l = Appel(8000);
        var (erreurs, avertissements) = SaisieLignesService.ControlerAppel(Contexte(), l, Article(), Verifs(), null, Origine(), Aujourdhui);
        Assert.Empty(erreurs);
        var a = Assert.Single(avertissements);
        Assert.Equal(A.DepassementOrigine, a.Zone);
        Assert.Contains("7 000", a.Message.Replace(' ', ' ').Replace(' ', ' '));

        l.AvertissementsAcceptes.Add(A.DepassementOrigine);
        Assert.Empty(SaisieLignesService.ControlerAppel(Contexte(), l, Article(), Verifs(), null, Origine(), Aujourdhui).Avertissements);
    }

    [Fact]
    public void Modification_d_un_appel_compare_l_ecart_au_reste()
    {
        var ancienne = Appel(3000).Avec(x => { x.NumeroLigne = 6; });
        var l = ancienne.Avec(x => x.Quantite = 9000);
        Assert.DoesNotContain(SaisieLignesService.ControlerAppel(Contexte(), l, Article(), Verifs(), ancienne, Origine(), Aujourdhui).Avertissements,
            a => a.Zone == A.DepassementOrigine);   // +6000 pour 7000 restant
        l.Quantite = 11000;
        Assert.Contains(SaisieLignesService.ControlerAppel(Contexte(), l, Article(), Verifs(), ancienne, Origine(), Aujourdhui).Avertissements,
            a => a.Zone == A.DepassementOrigine);
    }

    [Fact]
    public void Le_reste_de_l_origine_compte_dans_le_disponible()
    {
        // Entrée 5000, réservé 6000 (dont les 7000 du reste de l'origine, en partie) : l'appel de 3000 ne manque pas de stock
        var a = Article() with { StockControle = true, Stocke = true };
        var v = Verifs() with { Stock = (5000m, 6000m) };
        Assert.DoesNotContain(SaisieLignesService.ControlerAppel(Contexte(), Appel(3000), a, v, null, Origine(), Aujourdhui).Avertissements,
            x => x.Zone == A.StockInsuffisant);
        // Ligne normale de 3000 dans les mêmes conditions : stock insuffisant
        Assert.Contains(SaisieLignesService.Controler(Contexte(), Appel(3000).Avec(x => x.LigneOrigine = 0), a, v, null, Aujourdhui).Avertissements,
            x => x.Zone == A.StockInsuffisant);
    }

    [Fact]
    public void Les_avertissements_sur_l_article_ne_sont_pas_redemandes()
    {
        var a = Article() with { Remplacement = "O" };
        Assert.Empty(SaisieLignesService.ControlerAppel(Contexte(), Appel(1000), a, Verifs(), null, Origine(), Aujourdhui).Avertissements);
    }

    [Fact]
    public void Un_appel_reste_une_ligne_normale_controlee()
    {
        var l = Appel(0).Avec(x => { x.DateLivraison = null; x.NumeroAdresse = null; });
        var erreurs = SaisieLignesService.ControlerAppel(Contexte(), l, Article(), Verifs() with { ClientEdi = true }, null, Origine(), Aujourdhui).Erreurs;
        Assert.Contains(erreurs, e => e.Zone == nameof(LigneSaisie.Quantite));
        Assert.Contains(erreurs, e => e.Zone == nameof(LigneSaisie.DateLivraison));
        Assert.Contains(erreurs, e => e.Zone == nameof(LigneSaisie.NumeroAdresse));
        Assert.DoesNotContain(erreurs, e => e.Zone == nameof(LigneSaisie.NumeroAppel));   // n° d'appel repris de l'origine
    }

    // ---------- Jeux d'essai ----------
    private static LigneSaisie Origine() => new()
    {
        NumeroLigne = 4, CodeVente = "2", Article = "393229", Statut = StatutsLigne.ALaDemande, Quantite = 7000, QuantiteOrigine = 10000,
        Unite = "LM", PrixBrut = 49m, Remise = 10m, TauxCommission = 3m, Designation = "ETIQUETTE TEST", LigneOrigine = 4,
        MaxiLivrable = 11000, NumeroAdresse = 12, NumeroAppel = "APP-1", Commentaire1 = "Quai 3", Empreinte = "x",
        ArticleComplete = "393229|",
    };

    private static LigneSaisie Appel(int quantite)
    {
        var l = SaisieLignesService.NouvelAppel(Origine());
        l.Quantite = quantite;
        l.DateLivraison = new(2026, 11, 12);
        return l;
    }

    private static ContexteLigne Contexte() => new(
        TypeCommande: "4", TypeFacturation: "1", CompteGeneral: 411100, CompteClient: 376, CodeVente: "2", Enlevement: "A",
        Representant: 28, TauxCommission: 5, SousTraitee: false, NonModifiable: false, Soldee: false, Enseigne: "1",
        DateLivraison: null, DateDossier: new(2026, 10, 30), AdresseParDefaut: 0, SansMaxi: false, PrixCessionObligatoire: false,
        NouvelleApplication: false, LivraisonDeLEnTete: false, Depot: 1,
        TypesStock: new Dictionary<string, string> { ["P1"] = "P1" }, TypeCommission: "A", SeuilsCommission: "", TauxCommissions: "",
        MaxiClient: [], MaxiSociete: [], CalculMaxi: false, PlafondFranco: 0);

    private static ArticleLigne Article() => new(
        Remplacement: "N", Type: "2", TypeFacturation: "1", CompteGeneral: 411100, CompteClient: 376, Stocke: false,
        TarifParEmballage: false, SaisieEnUc: false, StockControle: false, Emballage: 0, FractionnableCommande: true,
        FractionnableLivraison: true, Unite: "LM", Designation: "ETIQUETTE TEST", Norme: "NF1", Emplacement: "A1",
        PrixRevient: 3, PrixCession: 4, Textes: [.. Enumerable.Repeat("", 19)], Nomenclature: false);

    private static VerificationsLigne Verifs() => new(
        TypeCodeVente: "V", TnumUnite: 1000, UniteExiste: true, Stock: null, DateAuCalendrier: true, TypeJour: "T", JourSemaine: 4,
        JoursAutorises: null, AdresseExiste: true, ClientFactureExiste: true, ClientEdi: false, EmplacementExiste: true,
        OprNonLivre: null, AppelsExistent: false, ReferenceFournisseurManquante: false);
}
