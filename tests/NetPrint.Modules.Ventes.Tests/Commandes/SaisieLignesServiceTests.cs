using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using A = NetPrint.Modules.Ventes.Contrats.AvertissementsLigne;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Règles de la saisie d'une ligne de commande (NEG520 / CTL520)
public class SaisieLignesServiceTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 10, 30);

    // ---------- Nouvelle ligne ----------
    [Fact]
    public void Nouvelle_ligne_avec_le_code_vente_et_l_adresse_par_defaut()
    {
        var l = SaisieLignesService.Nouvelle(Contexte() with { AdresseParDefaut = 4 });
        Assert.Equal("2", l.CodeVente);
        Assert.Equal(StatutsLigne.Normal, l.Statut);
        Assert.Equal(4, l.NumeroAdresse);
        Assert.Null(l.DateLivraison);
    }

    [Fact]
    public void Nouvelle_ligne_en_enlevement_et_date_de_l_en_tete()
    {
        var l = SaisieLignesService.Nouvelle(Contexte() with { Enlevement = "E", LivraisonDeLEnTete = true, DateLivraison = new(2026, 11, 20) });
        Assert.Equal("Enlevement client", l.AdresseRaisonSociale);
        Assert.Equal(new DateOnly(2026, 11, 20), l.DateLivraison);
    }

    // ---------- Compléter ----------
    [Fact]
    public void Valeurs_de_l_article_et_prix_du_tarif()
    {
        var l = SaisieLignesService.Completer(Contexte(), Ligne(prix: null), Article(), Tarif(prixFiche: 12.5m), 1000, Aujourdhui);
        Assert.Equal("ETIQUETTE TEST", l.Designation);
        Assert.Equal("LM", l.Unite);
        Assert.Equal("NF1", l.Norme);
        Assert.Equal("A1", l.Emplacement);
        Assert.Equal("ligne 1\nligne 2", l.Texte);
        Assert.Equal(12.5m, l.PrixBrut);
        Assert.Equal("Prix de la fiche article", l.SourcePrix);
        Assert.Equal(12.5m, l.PrixNet);
        Assert.Equal(31.25m, l.Montant);               // 2 500 / 1 000 × 12,50
        Assert.Equal(3m, l.PrixCession);               // prix de revient (SOP15 non actif)
        Assert.Equal(5m, l.TauxCommission);            // pas de table : taux de l'en-tête
    }

    [Fact]
    public void Prix_saisi_conserve()
    {
        var l = SaisieLignesService.Completer(Contexte(), Ligne(prix: 49m) .Avec(x => x.PrixSaisi = true), Article(), Tarif(prixFiche: 12.5m), 1000, Aujourdhui);
        Assert.Equal(49m, l.PrixBrut);
        Assert.Equal(122.5m, l.Montant);
    }

    [Fact]
    public void Remise_et_ligne_gratuite()
    {
        var remise = SaisieLignesService.Completer(Contexte(), Ligne(prix: 10m).Avec(x => { x.PrixSaisi = true; x.Remise = 15; }), Article(), null, 1, Aujourdhui);
        Assert.Equal(8.5m, remise.PrixNet);
        var gratuite = SaisieLignesService.Completer(Contexte(), Ligne(prix: 10m).Avec(x => x.Gratuite = true), Article(), Tarif(12), 1, Aujourdhui);
        Assert.Equal(0, gratuite.PrixBrut);
        Assert.Equal(0, gratuite.Montant);
    }

    [Fact]
    public void Quantite_en_UC()
    {
        var a = Article() with { SaisieEnUc = true, Emballage = 500 };
        var depuisUc = SaisieLignesService.Completer(Contexte(), Ligne().Avec(x => { x.Quantite = null; x.QuantiteUc = 3; }), a, null, 1, Aujourdhui);
        Assert.Equal(1500, depuisUc.Quantite);
        var depuisQte = SaisieLignesService.Completer(Contexte(), Ligne().Avec(x => x.Quantite = 1200), a, null, 1, Aujourdhui);
        Assert.Equal((3, 1500), (depuisQte.QuantiteUc, depuisQte.Quantite));
    }

    [Fact]
    public void Maxi_livrable_calcule_selon_DOS009()
    {
        var c = Contexte() with { CalculMaxi = true, MaxiSociete = [(10000, 10)] };
        Assert.Equal(2750, SaisieLignesService.Completer(c, Ligne(), Article(), null, 1000, Aujourdhui).MaxiLivrable);
        Assert.Null(SaisieLignesService.Completer(c with { SansMaxi = true }, Ligne(), Article(), null, 1000, Aujourdhui).MaxiLivrable);
    }

    // ---------- Contrôles ----------
    [Fact]
    public void Ligne_complete_acceptee() => Assert.Empty(Controler(Ligne()).Erreurs);

    public static TheoryData<string, string> LignesRefusees() => new()
    {
        { "CodeVente", "Cette zone doit être renseignée." },
        { "Article", "Cette zone doit être renseignée." },
        { "Quantite", "Cette zone doit être renseignée." },
        { "PrixBrut", "Cette zone doit être renseignée." },
        { "DateLivraison", "Cette zone doit être renseignée." },
        { "NumeroAdresse", "L'adresse ou son n° doit être renseigné quand le statut est Normal." },
        { "Statut", "Cette zone doit être égale à « Normal » ou « À la demande »." },
        { "Gratuite", "Réponse incorrecte si prix renseigné ou ligne gratuite." },
    };

    [Theory]
    [MemberData(nameof(LignesRefusees))]
    public void Ligne_refusee(string zone, string message)
    {
        var l = Ligne();
        switch (zone)
        {
            case "CodeVente": l.CodeVente = ""; break;
            case "Article": l.Article = ""; break;
            case "Quantite": l.Quantite = 0; break;
            case "PrixBrut": l.PrixBrut = 0; break;
            case "DateLivraison": l.DateLivraison = null; break;
            case "NumeroAdresse": l.NumeroAdresse = null; break;
            case "Statut": l.Statut = "2"; break;
            case "Gratuite": l.Gratuite = true; l.PrixADefinir = true; break;
        }
        Assert.Contains(Controler(l).Erreurs, e => e.Zone == zone && e.Message == message);
    }

    [Fact]
    public void Article_cloture_type_et_facturation()
    {
        Assert.Contains(Controler(Ligne(), Article() with { Remplacement = "C" }).Erreurs, e => e.Message == "Cet article est clôturé.");
        Assert.Contains(Controler(Ligne(), Article() with { Type = "1" }).Erreurs, e => e.Message == "Type de l'article non conforme au type de la commande.");
        Assert.Contains(Controler(Ligne(), Article() with { TypeFacturation = "2" }).Erreurs, e => e.Message.StartsWith("Type de facturation de l'article"));
        Assert.Contains(SaisieLignesService.Controler(Contexte(), Ligne(), null, Verifs(), null, Aujourdhui).Erreurs,
            e => e.Message == "Cet article n'existe pas.");
    }

    [Fact]
    public void Codes_vente_refuses()
    {
        Assert.Contains(Controler(Ligne(), v: Verifs() with { TypeCodeVente = null }).Erreurs, e => e.Message == "Ce code ligne n'existe pas.");
        Assert.Contains(Controler(Ligne(), v: Verifs() with { TypeCodeVente = "T" }).Erreurs, e => e.Zone == "CodeVente");
        Assert.Contains(Controler(Ligne(), v: Verifs() with { TypeCodeVente = "R" }).Erreurs, e => e.Message == "Type de ligne non autorisé.");
    }

    [Fact]
    public void Avertissements_a_accepter()
    {
        var v = Verifs() with { Stock = (1000, 0), DateAuCalendrier = true, TypeJour = "F" };
        var l = Ligne().Avec(x => x.DateLivraison = new(2026, 10, 1));
        var codes = Controler(l, v: v).Avertissements.Select(a => a.Zone).ToList();
        Assert.Equal([A.StockInsuffisant, A.DatePassee, A.JourNonTravaille], codes);

        l.AvertissementsAcceptes = [A.StockInsuffisant, A.DatePassee, A.JourNonTravaille];
        Assert.Empty(Controler(l, v: v).Avertissements);
    }

    [Fact]
    public void Jour_de_livraison_non_autorise_par_le_client()
    {
        var v = Verifs() with { JoursAutorises = "OONOOO", JourSemaine = 3 };
        Assert.Contains(Controler(Ligne(), v: v).Avertissements, a => a.Zone == A.JourNonAutorise);
        Assert.DoesNotContain(Controler(Ligne(), v: v with { JourSemaine = 2 }).Avertissements, a => a.Zone == A.JourNonAutorise);
    }

    [Fact]
    public void Arrondi_d_emballage_propose()
    {
        var a = Article() with { FractionnableCommande = false, Emballage = 1000 };
        var w = Assert.Single(Controler(Ligne(), a).Avertissements, x => x.Zone == A.QuantiteArrondie);
        Assert.Contains(3000.ToString("N0"), w.Message);
    }

    [Fact]
    public void Ligne_d_un_OPR_non_livre_non_modifiable()
    {
        var ancienne = Ligne().Avec(x => x.NumeroLigne = 2);
        var l = ancienne.Avec(x => x.Quantite = 3000);
        Assert.Contains(SaisieLignesService.Controler(Contexte(), l, Article(), Verifs() with { OprNonLivre = 77 }, ancienne, Aujourdhui).Erreurs,
            e => e.Message == "La ligne fait partie de l'OPR n° 77.");
    }

    [Fact]
    public void Passage_a_la_demande_vers_normal_interdit_avec_des_appels()
    {
        var ancienne = Ligne().Avec(x => { x.NumeroLigne = 2; x.Statut = StatutsLigne.ALaDemande; });
        var l = ancienne.Avec(x => x.Statut = StatutsLigne.Normal);
        Assert.Contains(SaisieLignesService.Controler(Contexte(), l, Article(), Verifs() with { AppelsExistent = true }, ancienne, Aujourdhui).Erreurs,
            e => e.Message == "Modification impossible, il existe des appels de livraison.");
    }

    [Fact]
    public void Numero_d_appel_obligatoire_pour_un_client_EDI() =>
        Assert.Contains(Controler(Ligne(), v: Verifs() with { ClientEdi = true }).Erreurs, e => e.Zone == "NumeroAppel");

    [Theory]
    [InlineData(StatutsLigne.ALaDemande, true, 0, 0, null, "appels")]
    [InlineData(StatutsLigne.Normal, false, 10, 0, null, "livrée ou facturée")]
    [InlineData(StatutsLigne.Normal, false, 0, 0, 12, "OPR n° 12")]
    [InlineData(StatutsLigne.Normal, false, 0, 0, null, null)]
    public void Refus_de_suppression(string statut, bool appels, int livree, int facturee, int? opr, string? extrait)
    {
        var l = Ligne().Avec(x => { x.Statut = statut; x.QuantiteLivree = livree; x.QuantiteFacturee = facturee; });
        var refus = SaisieLignesService.RefusSuppression(l, Verifs() with { AppelsExistent = appels, OprNonLivre = opr });
        if (extrait is null) Assert.Null(refus);
        else Assert.Contains(extrait, refus);
    }

    [Fact]
    public void Texte_libre_en_19_lignes_de_40()
    {
        var lignes = LignesCommandeDb2.TexteEnLignes("court\n" + new string('x', 90));
        Assert.Equal(19, lignes.Count);
        Assert.Equal(["court", new string('x', 40), new string('x', 40), new string('x', 10)], lignes.Take(4));
    }

    [Theory]
    [InlineData("1", "1", "NG")]
    [InlineData("2", "1", "P1")]
    [InlineData("2", "2", "P2")]
    [InlineData("2", "3", "P3")]
    public void Type_de_stock(string type, string facturation, string attendu) =>
        Assert.Equal(attendu, LignesCommandeDb2.TypeStock(Article() with { Stocke = true, Type = type, TypeFacturation = facturation },
            new Dictionary<string, string> { ["NE"] = "NG", ["P1"] = "P1", ["P2"] = "P2", ["P3"] = "P3", ["SF"] = "" }));

    [Fact]
    public void Article_non_stocke_sans_type_de_stock() =>
        Assert.Null(LignesCommandeDb2.TypeStock(Article() with { Stocke = false }, new Dictionary<string, string> { ["P1"] = "P1" }));

    [Fact]
    public void Colonnes_ecrites_dates_et_montants_en_francs()
    {
        var l = Ligne().Avec(x => { x.PrixBrut = 10; x.DateConfirmee = new(2026, 11, 20); });
        var colonnes = LignesCommandeDb2.ColonnesLigne(Contexte(), l, 1, null).ToDictionary(c => c.Colonne, c => c.Valeur);
        Assert.Equal(20261112, colonnes["DNDATB"]);     // date saisie
        Assert.Equal(20261120, colonnes["DNDLIV"]);     // date confirmée
        Assert.Equal(65.60m, colonnes["DNPUSA"]);       // 10 € en francs
        Assert.Equal(2500, colonnes["DNQTER"]);
        Assert.Equal("O", colonnes["DNEDTB"]);
        Assert.Equal(0, colonnes["DNLORI"]);
    }

    [Fact]
    public void Ligne_a_la_demande_origine_sur_elle_meme()
    {
        var l = Ligne().Avec(x => { x.NumeroLigne = 4; x.Statut = StatutsLigne.ALaDemande; });
        var colonnes = LignesCommandeDb2.ColonnesLigne(Contexte(), l, 1, null).ToDictionary(c => c.Colonne, c => c.Valeur);
        Assert.Equal(4, colonnes["DNLORI"]);
        Assert.Equal(2500, colonnes["DNQTEO"]);
    }

    // ---------- Jeux d'essai ----------
    private static (List<ErreurSaisie> Erreurs, List<ErreurSaisie> Avertissements) Controler(LigneSaisie l, ArticleLigne? a = null,
        VerificationsLigne? v = null) =>
        SaisieLignesService.Controler(Contexte(), l, a ?? (l.Article == "" ? null : Article()), v ?? Verifs(), null, Aujourdhui);

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
        PrixRevient: 3, PrixCession: 4, Textes: ["ligne 1", "ligne 2", .. Enumerable.Repeat("", 17)], Nomenclature: false);

    private static VerificationsLigne Verifs() => new(
        TypeCodeVente: "V", TnumUnite: 1000, UniteExiste: true, Stock: null, DateAuCalendrier: true, TypeJour: "T", JourSemaine: 4,
        JoursAutorises: null, AdresseExiste: true, ClientFactureExiste: true, ClientEdi: false, EmplacementExiste: true,
        OprNonLivre: null, AppelsExistent: false, ReferenceFournisseurManquante: false);

    private static LigneSaisie Ligne(decimal? prix = 49m) => new()
    {
        CodeVente = "2", Article = "393229", Statut = StatutsLigne.Normal, Quantite = 2500, Unite = "LM", PrixBrut = prix,
        DateLivraison = new(2026, 11, 12), NumeroAdresse = 1, Designation = "",
    };

    private static MoteurPrixCommande.DonneesTarif Tarif(decimal prixFiche) => new(
        new MoteurPrixCommande.Article(false, 0, prixFiche, 3, 4, "LM", 0, false, 0, 0), "", 411100, 376, [],
        new Dictionary<string, long>(), [], [], [], false, 0, 0, false);
}

internal static class LigneSaisieTests
{
    public static LigneSaisie Avec(this LigneSaisie l, Action<LigneSaisie> modifier)
    {
        var c = l.Copier();
        modifier(c);
        return c;
    }
}
