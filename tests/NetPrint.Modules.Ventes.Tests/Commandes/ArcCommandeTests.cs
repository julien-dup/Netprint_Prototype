using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Règles de l'ARC (ARC105P) sans base : canevas, lignes imprimées, totaux, libellés
public class ArcCommandeTests
{
    private static readonly Dictionary<string, string> V = new()
    {
        ["CRS"] = "LABORATOIRES CLARINS", ["EDOS"] = "68537/9", ["EIA"] = "LE", ["GDT1"] = "30/10/26", ["ERFC"] = "PO-12", ["CCP"] = "95300",
        ["CVIL"] = "PONTOISE",
    };

    // ---------- Canevas ----------
    [Theory]
    [InlineData("Commande N° $EDOS  $EIA fin", "Commande N° 68537/9  LEfin")]      // le code lu fait 4 caractères : « EIA » absorbe le blanc qui suit
    [InlineData("V/cde : £ERFC du", "V/cde : PO-12 du")]                             // £ = $ selon le CCSID
    [InlineData("$CCP  $CVIL", "95300 PONTOISE")]
    [InlineData("Réf $XXXX suite", "Réf  suite")]                                    // code inconnu : supprimé

    public void Decodage_des_variables(string canevas, string attendu) => Assert.Equal(attendu, ArcCommande.Decoder(canevas, V));

    [Fact]
    public void Le_paragraphe_reprend_a_sa_colonne()
    {
        // « § » en colonne 19 : la suite s'écrit en colonne 19, que la valeur avant soit courte ou longue
        Assert.Equal("Cde 68537/9" + new string(' ', 8) + "Ascoux", ArcCommande.Decoder("Cde $EDOS          §Ascoux", V));
        Assert.Equal("LABORATOIRES CLARINAscoux", ArcCommande.Decoder("$CRS               §Ascoux", V));
    }

    [Fact]
    public void Une_ligne_de_canevas_ne_depasse_pas_74_colonnes() =>
        Assert.Equal(74, ArcCommande.Decoder(new string('x', 70) + " $CRS", V).Length);

    [Fact]
    public void Chapitre_sans_lignes_vides_en_trop()
    {
        var canevas = new[] { "", "", "Bonjour", "", "", "Merci", "", "" }
            .Select((t, i) => new LigneCanevas("", "", "", "2", "", i + 1, t, i == 2)).ToList();
        var lignes = ArcCommande.Chapitre(canevas, "2", V);
        Assert.Equal(["Bonjour", "", "Merci"], lignes.Select(x => x.Texte));
        Assert.True(lignes[0].Gras);
    }

    [Fact]
    public void Recherche_du_canevas_elargie_puis_en_francais()
    {
        List<LigneCanevas> tous =
        [
            new("6", "$2", "", "2", "", 1, "type 6 catégorie $2", false),
            new("", "", "", "2", "", 1, "général", false),
            new("", "", "", "2", "1", 1, "general english", false),
            new("", "", "", "5", "", 1, "pied", false),
        ];
        Assert.Equal("type 6 catégorie $2", ArcCommande.Canevas(tous, "6", "$2", "XX", "", "2").Single().Texte);
        Assert.Equal("général", ArcCommande.Canevas(tous, "5", "$1", "", "", "2").Single().Texte);
        Assert.Equal("general english", ArcCommande.Canevas(tous, "5", "$1", "", "1", "2").Single().Texte);
        // ÉCART : langue sans canevas → français (le RPG imprimait un ARC sans pied)
        Assert.Equal("pied", ArcCommande.Canevas(tous, "5", "$1", "", "1", "5").Single().Texte);
        Assert.Empty(ArcCommande.Canevas(tous, "5", "$1", "", "", "1"));
    }

    // ---------- Lignes ----------
    [Fact]
    public void Lignes_imprimees_selon_l_option_lignes_soldees()
    {
        List<LigneArc> lignes =
        [
            Ligne(1) with { QuantiteCommandee = 5000, QuantiteLivree = 2000 },
            Ligne(2) with { Soldee = true, QuantiteCommandee = 1000, QuantiteLivree = 1000 },
            Ligne(3) with { TypeLigne = "R", Montant = -50 },                     // remise : jamais imprimée
            Ligne(4) with { TypeLigne = "P", QuantiteCommandee = -1, Montant = 20, Tnum = 0 },   // port : quantité ramenée à 0
            Ligne(5) with { QuantiteCommandee = 3000, QuantiteLivree = 3000 },    // tout livré, non soldée
        ];
        var avec = ArcCommande.Groupes(lignes, avecSoldees: true, dateButoir: false, Libelles.Francais).SelectMany(g => g.Lignes).ToList();
        Assert.Equal([5000m, 1000m, 0m, 3000m], avec.Select(x => x.Quantite!.Value));
        var sans = ArcCommande.Groupes(lignes, avecSoldees: false, dateButoir: false, Libelles.Francais).SelectMany(g => g.Lignes).ToList();
        Assert.Equal([3000m, 0m], sans.Select(x => x.Quantite!.Value));   // reste à livrer ; soldée et entièrement livrée exclues
    }

    [Fact]
    public void Regroupement_par_livraison_dans_l_ordre_des_lignes()
    {
        var a = new AdresseArc("", "QUAI A", "1 rue", "", "75001", "PARIS");
        var b = new AdresseArc("", "QUAI B", "2 rue", "", "69001", "LYON");
        List<LigneArc> lignes =
        [
            Ligne(1) with { AdresseLivraison = a },
            Ligne(2) with { AdresseLivraison = b },
            Ligne(3) with { AdresseLivraison = a },
            Ligne(4) with { AdresseLivraison = a, ALaDemande = true, DateLivraison = new(2026, 12, 31) },
        ];
        var groupes = ArcCommande.Groupes(lignes, true, dateButoir: true, Libelles.Francais);
        Assert.Equal(3, groupes.Count);
        Assert.Equal(2, groupes[0].Lignes.Count);
        Assert.Equal("Pour le 12/11/2026", groupes[0].Titre);
        Assert.Contains("QUAI B", groupes[1].Livraison);
        Assert.Equal("À la demande · date butoir 31/12/2026", groupes[2].Titre);
    }

    [Fact]
    public void Prix_montant_et_complements_d_une_ligne()
    {
        var x = Ligne(1) with
        {
            PrixBrut = 50, Remise = 2, PrixNet = 49, Textes = ["texte 1", ""], NumeroAppel = "455873", DateAppel = new(2026, 10, 1),
            ReferenceClient = "0C300012", Marche = "M-1", ClientFactureLigne = "SA AUTRE (411100 12)", MentionsNorme = ["Norme ISO"],
        };
        var l = ArcCommande.Ligne(x, 2500, Libelles.Francais);
        Assert.Equal(("393228 A", 49m, 122.50m, "Le Mille"), (l.Article, l.Prix!.Value, l.Montant!.Value, l.Unite));
        Assert.Equal("(50,00  - 2 %)", l.PrixAvantRemise);
        Assert.Equal(["texte 1", "Norme ISO", "N° appel 455873 du 01/10/2026", "V/Réf : 0C300012 - M-1", "Facturation : SA AUTRE (411100 12)"],
            l.Complements);
    }

    [Fact]
    public void Le_tout_reprend_le_montant_de_la_ligne() =>
        Assert.Equal(780m, ArcCommande.Ligne(Ligne(1) with { Tnum = MoteurPrixCommande.TnumLeTout, Montant = 780 }, 2500, Libelles.Francais).Montant);

    [Fact]
    public void Vente_par_conditionnement()
    {
        var l = ArcCommande.Ligne(Ligne(1) with { ParConditionnement = true, Emballage = 500, TypeEmballage = "Carton(s)", PrixNet = 0.1m, Tnum = 1 },
            2500, Libelles.Francais);
        Assert.Equal((5m, 50m, "Unité", "Carton(s) de 500 ex."), (l.Quantite!.Value, l.Prix!.Value, l.Unite, l.Designation));
        Assert.Equal("ETIQUETTE", l.Complements[0]);
        Assert.Equal(250m, l.Montant);
    }

    [Fact]
    public void Prix_du_conditionnement_avec_un_prix_au_mille()
    {
        // 12 € le mille, cartons de 500 : 6 € le carton, 5 cartons = 30 €
        var l = ArcCommande.Ligne(Ligne(1) with { ParConditionnement = true, Emballage = 500, PrixNet = 12, PrixBrut = 12, Tnum = 1000 },
            2500, Libelles.Francais);
        Assert.Equal((5m, 6m, 30m), (l.Quantite!.Value, l.Prix!.Value, l.Montant!.Value));
    }

    [Fact]
    public void Une_ligne_texte_reste_avec_la_ligne_qu_elle_suit()
    {
        var b = new AdresseArc("", "QUAI B", "", "", "69001", "LYON");
        List<LigneArc> lignes =
        [
            Ligne(1) with { AdresseLivraison = b },
            Ligne(2) with { TypeLigne = "T", Designation = "Livrer avant 10 h", DateLivraison = null },
            Ligne(3),
        ];
        var groupes = ArcCommande.Groupes(lignes, true, false, Libelles.Francais);
        Assert.Equal(2, groupes.Count);
        Assert.Equal(["393228 A", ""], groupes[0].Lignes.Select(x => x.Article));
    }

    [Fact]
    public void Ligne_gratuite_sans_unite() => Assert.Equal("", ArcCommande.Ligne(Ligne(1) with { Gratuite = true }, 10, Libelles.Francais).Unite);

    [Fact]
    public void Totaux_avec_remise_de_pied()
    {
        var groupes = new[] { new GroupeArc("", "", [Imprimee(1000), Imprimee(450)]) };
        Assert.Equal((1450m, 1450m), ArcCommande.Totaux(groupes, 0));
        Assert.Equal((1450m, 1377.50m), ArcCommande.Totaux(groupes, 5));
    }

    // ---------- En-tête ----------
    [Theory]
    [InlineData(45, 31, 0, "VIREMENT 45 jours le 31")]
    [InlineData(60, 10, 30, "Traite 60 jours fin de mois le 10")]
    [InlineData(0, 0, 0, "Chèque au comptant")]
    public void Libelle_du_reglement(int jours, int le, int arrete, string attendu)
    {
        var mode = attendu.Split(' ')[0];
        Assert.Equal(attendu, ArcCommande.Reglement(Entete() with { ModeReglement = mode, JoursEcheance = jours, JourEcheance = le, Arrete = arrete },
            Libelles.Francais));
    }

    [Fact]
    public void Client_facture_par_defaut_le_client()
    {
        // ÉCART : sans client facturé, $CRSF reprend le client (le RPG laissait ces variables vides)
        var v = ArcCommande.Variables(Entete(), Libelles.Francais, new(2026, 10, 30));
        Assert.Equal("LABORATOIRES CLARINS", v["CRSF"]);
        Assert.Equal("30 octobre 2026", v["GDT2"]);
        Assert.Equal("00024/1", v["EDOS"]);
    }

    [Fact]
    public void Libelles_traduits_par_PRT008()
    {
        var t = "Quantity|Description|Price €|Amount|To be paid €|Unit|Shipping:|Our Ref|Date|Your Ref:|Delivery Call|To be continued|Billing to|of|On demand|Cont.|Order|days|At the Order|The|end of month|January|February|March|April|May|June|July|August|September|October|November|December"
            .Split('|');
        var l = Libelles.Pour(t);
        Assert.Equal(("Our Ref", "Quantity", "October", "Your Ref"), (l.Article, l.Quantite, l.Mois[9], l.VotreCommande));
        Assert.Same(Libelles.Francais, Libelles.Pour([]));
        Assert.Equal("Order acknowledgement", l.Titre);
        var allemand = t.ToArray();
        allemand[16] = "Auftrag";
        Assert.Equal(Libelles.Francais.Titre, Libelles.Pour(allemand).Titre);   // pas de titre traduit dans PRT008
    }

    [Fact]
    public void L_entete_complet_du_canevas_remplace_les_blocs()
    {
        var d = Donnees([new("", "", "", "2", "", 1, "                     $CRS", false)]);
        Assert.True(ArcCommande.Construire(d, true, new(2026, 10, 30)).EnteteParCanevas);
        Assert.False(ArcCommande.Construire(Donnees([new("", "", "", "2", "", 1, "Madame, Monsieur", false)]), true, new(2026, 10, 30)).EnteteParCanevas);
        // $CRSF (client facturé) n'est pas l'adresse du destinataire
        Assert.False(ArcCommande.Construire(Donnees([new("", "", "", "2", "", 1, "Facturé à : $CRSF", false)]), true, new(2026, 10, 30)).EnteteParCanevas);
    }

    // ---------- Message d'envoi ----------
    [Fact]
    public void Objet_et_texte_du_message()
    {
        var aujourdhui = new DateOnly(2026, 10, 30);
        var chapitreA = new List<LigneCanevas> { new("", "", "", "A", "", 1, "Veuillez trouver l'ARC de la commande $EDOS.", false) };
        var une = Donnees(chapitreA);
        Assert.Equal(("Accusé de réception de la commande 00024/1", "Veuillez trouver l'ARC de la commande 00024/1."),
            ArcCommande.Message([une], aujourdhui));

        var avecReference = une with { Entete = une.Entete with { ReferenceCommande = "PO-12", DateCommandeClient = new(2026, 10, 2) } };
        Assert.Equal("Accusé de réception de votre commande PO-12 du 02/10/2026", ArcCommande.Message([avecReference], aujourdhui).Objet);

        var deux = ArcCommande.Message([une, une with { Entete = une.Entete with { Cle = new CleCommande(25, "1") } }], aujourdhui);
        Assert.Equal("ARC du client LABORATOIRES CLARINS", deux.Objet);

        Assert.Equal(ArcCommande.TexteParDefaut, ArcCommande.Message([Donnees([])], aujourdhui).Texte);   // sans chapitre A
    }

    [Fact]
    public void Noms_des_logos_sans_ambiguite_d_abord()
    {
        Assert.Equal(["8-1", "8", "LogoArc81", "Logo81", "LogoArc8", "Logo8", "LogoArc"],
            NetPrint.Noyau.Societes.LogosSociete.Noms(8, "1", "Arc"));
        Assert.Equal(["8", "Logo8"], NetPrint.Noyau.Societes.LogosSociete.Noms(8, "", ""));
    }

    // ---------- Jeux d'essai ----------
    private static LigneArc Ligne(int n) => new(
        Numero: n, TypeLigne: "V", ALaDemande: false, Article: "393228", Indice: "A", Designation: "ETIQUETTE", QuantiteCommandee: 2500,
        QuantiteLivree: 0, Soldee: false, PrixNet: 49, PrixBrut: 49, Remise: 0, Montant: 122.5m, Tnum: 1000, LibelleUnite: "Le Mille",
        Gratuite: false, ParConditionnement: false, Emballage: 0, TypeEmballage: "", DateLivraison: new(2026, 11, 12), AdresseLivraison: null,
        NumeroAppel: "", DateAppel: null, Textes: [], MentionsNorme: [], ReferenceClient: "", Marche: "", ClientFactureLigne: "");

    private static LigneImprimee Imprimee(decimal montant) => new("A", 1, "X", 1, "U", montant, null, []);

    private static EnteteArc Entete() => new(
        Cle: new CleCommande(24, "1"), CompteGeneral: 411100, CompteClient: 376, Langue: "", Enseigne: "1", TypeProduit: "6", Categorie: "$2",
        SousCategorie: "", Client: new AdresseArc("", "LABORATOIRES CLARINS", "5 Rue Ampère", "", "95300", "PONTOISE"), ClientFacture: null,
        CompteClientFacture: 0, Telephone: "", Fax: "", ReferenceFournisseur: "", ModeReglement: "VIREMENT", JoursEcheance: 45, JourEcheance: 31,
        Arrete: 0, ReferenceCommande: "", DateCommandeClient: null, DateDossier: null, Interlocuteur: "SANDRA", Representant: 28,
        NomRepresentant: "ELIOT Gilles", NomRepresentantEtendu: "M Gilles ELLIOT", InitialesFabricant: "", NomFabricant: "", Profil: "",
        NomProfil: "", Designation1: "", Designation2: "", ReferenceImprime: "", Enlevement: "A", Port: "F", DateLivraison: null,
        SemaineLivraison: "", TypeFacturation: "1", Unite: "LT", Monnaie: "E", TauxRemisePied: 0, Email: "");

    private static DonneesArc Donnees(List<LigneCanevas> canevas) =>
        new(Entete(), [Ligne(1)], canevas, [], "B", true, false, true, []);
}
