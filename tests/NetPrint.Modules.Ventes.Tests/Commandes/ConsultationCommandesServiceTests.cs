using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Règles de la consultation des commandes : contrôles des critères, droits, valeur d'une ligne, état, regroupement
public class ConsultationCommandesServiceTests
{
    // ---------- Contrôles (Controle de FEN_Historiquedossiers) ----------
    public static TheoryData<CriteresCommandes, string> CriteresRefuses() => new()
    {
        { new CriteresCommandes(), "Vous devez renseigner au moins un critère." },
        { new CriteresCommandes { CompteGeneral = 411000, TypeCommande = "3", AvecSoldees = true, Enseigne = "A", FiniLargeur = 10 },
          "Vous devez renseigner au moins un critère." },
        { new CriteresCommandes { Produit = 12 }, "Le client doit être renseigné." },
        { new CriteresCommandes { Numero = 1, SousCategorie = "01" }, "La catégorie doit être renseignée." },
        { new CriteresCommandes { Numero = 1, Indice = "01" }, "L'article doit être renseigné." },
        { new CriteresCommandes { Cle = "A", Representant = 3 }, "Le n° de commande doit être renseigné." },
        { new CriteresCommandes { DateDu = new(2026, 2, 1), DateAu = new(2026, 1, 1) },
          "La date de début doit être inférieure ou égale à la date de fin." },
        { new CriteresCommandes { LivraisonDu = new(2026, 2, 1), LivraisonAu = new(2026, 1, 1) },
          "La date de livraison de début doit être inférieure ou égale à celle de fin." },
    };

    [Theory]
    [MemberData(nameof(CriteresRefuses))]
    public void Criteres_refuses(CriteresCommandes c, string message) =>
        Assert.Equal(message, Assert.Throws<RegleMetierException>(() => ConsultationCommandesService.Controler(c)).Message);

    public static TheoryData<CriteresCommandes> CriteresAcceptes() => new()
    {
        new CriteresCommandes { Numero = 12345 },
        new CriteresCommandes { CompteGeneral = 411000, CompteClient = 1 },
        new CriteresCommandes { CompteGeneral = 411000, CompteClient = 1, Produit = 42 },
        new CriteresCommandes { Representant = 5 },
        new CriteresCommandes { Article = "ABC*", Indice = "01" },
        new CriteresCommandes { Categorie = "$1", SousCategorie = "01" },
        new CriteresCommandes { DateDu = new(2026, 1, 1) },
        new CriteresCommandes { LivraisonAu = new(2026, 1, 1) },
        new CriteresCommandes { Designation = "étiquette" },
        new CriteresCommandes { NumeroAppel = "A1" },
        new CriteresCommandes { BonLivraison = 9 },
        new CriteresCommandes { Facture = 9 },
    };

    [Theory]
    [MemberData(nameof(CriteresAcceptes))]
    public void Criteres_acceptes(CriteresCommandes c) => ConsultationCommandesService.Controler(c);

    // ---------- Droits PRT000P1 (DOS500C) ----------
    [Fact]
    public void Sans_parametrage_tout_est_permis() =>
        Assert.Equal(DroitsCommandes.Tous, ConsultationCommandesService.Droits(null));

    [Fact]
    public void Options_interdites_par_double_etoile()
    {
        //                              Total  Création Modif  Suppr  Visu  Calcul Édition MAJ
        var d = ConsultationCommandesService.Droits(["", "**", "", "**", "", "", "**", ""]);
        Assert.Equal(new DroitsCommandes(Creer: false, Modifier: true, Supprimer: false, Editer: false), d);
    }

    // ---------- Valeur d'une ligne ----------
    [Theory]
    [InlineData(12.5, 1000, 1000, 12.5)]          // prix au mille
    [InlineData(2, 150, 1, 300)]                   // prix unitaire
    [InlineData(450, 5000, 999999999, 450)]        // « Le tout » : prix forfaitaire
    [InlineData(450, 5000, 0, 450)]                // unité sans diviseur
    [InlineData(10, 333, 1000, 3.33)]              // arrondi au centime
    [InlineData(10, 335, 1000, 3.35)]
    public void Valeur_d_une_ligne(decimal prix, int quantite, long tnum, decimal attendu) =>
        Assert.Equal(attendu, ConsultationCommandesService.Valeur(prix, quantite, tnum));

    // ---------- État d'une commande ----------
    [Fact]
    public void Commande_soldee_quelles_que_soient_les_lignes() =>
        Assert.Equal(EtatCommande.Soldee, ConsultationCommandesService.Etat(true, [Ligne(quantite: 10, livree: 0)]));

    [Fact]
    public void Commande_sans_ligne_en_cours() =>
        Assert.Equal(EtatCommande.EnCours, ConsultationCommandesService.Etat(false, []));

    [Fact]
    public void Toutes_les_lignes_fermes_livrees() =>
        Assert.Equal(EtatCommande.Livree, ConsultationCommandesService.Etat(false,
            [Ligne(quantite: 10, livree: 10), Ligne(quantite: 5, livree: 6), Ligne(quantite: 100, livree: 0, aLaDemande: true)]));

    [Fact]
    public void Une_partie_livree() =>
        Assert.Equal(EtatCommande.PartiellementLivree, ConsultationCommandesService.Etat(false,
            [Ligne(quantite: 10, livree: 10), Ligne(quantite: 5, livree: 0)]));

    [Fact]
    public void Lignes_soldees_ignorees() =>
        Assert.Equal(EtatCommande.Livree, ConsultationCommandesService.Etat(false,
            [Ligne(quantite: 10, livree: 10), Ligne(quantite: 5, livree: 0, soldee: true)]));

    [Fact]
    public void Rien_de_livre() =>
        Assert.Equal(EtatCommande.EnCours, ConsultationCommandesService.Etat(false, [Ligne(quantite: 10, livree: 0)]));

    // ---------- Regroupement ----------
    [Fact]
    public void Lignes_regroupees_par_commande_dans_l_ordre_de_lecture()
    {
        var commandes = ConsultationCommandesService.Regrouper(
        [
            Brut(200, "A", 1), Brut(200, "A", 2), Brut(150, "B", 1),
        ]);
        Assert.Equal(["00200/A", "00150/B"], commandes.Select(c => c.Numero));
        Assert.Equal(2, commandes[0].NbLignes);
        Assert.Equal(1, commandes[1].NbLignes);
    }

    [Fact]
    public void Commande_sans_ligne_lue_sans_ligne()
    {
        var c = Assert.Single(ConsultationCommandesService.Regrouper([Brut(300, "C", 0) with { Dnstat = "", Dnart = "" }]));
        Assert.Equal(0, c.NbLignes);
        Assert.Equal(0m, c.Valeur);
        Assert.Null(c.TauxLivraison);
    }

    [Fact]
    public void Mise_en_forme_de_la_commande()
    {
        var l1 = Brut(200, "A", 1) with { Dndliv = 20261120, Dnpus = 12.5m, Dnqtec = 2000, Dnqtel = 1000, Tnum = 1000 };
        var l2 = Brut(200, "A", 2) with { Dndliv = 20261105, Dnpus = 100m, Dnqtec = 1, Tnum = 999999999, Dnopt2 = "" };
        var l3 = Brut(200, "A", 3) with { Dndliv = 20261101, Dnsold = "O", Dnqtec = 5, Dnpus = 1, Tnum = 1 };
        var c = Assert.Single(ConsultationCommandesService.Regrouper([l1, l2, l3]));

        Assert.Equal("411000 1234", c.Client);
        Assert.Equal("Étiquettes Ligne 2", c.Designation);
        Assert.Equal("12 MARTIN", c.Representant);
        Assert.Equal(new DateOnly(2026, 11, 5), c.DateLivraison);   // première date des lignes non soldées
        Assert.Equal(25m + 100m + 5m, c.Valeur);
        Assert.Equal(EtatCommande.PartiellementLivree, c.Etat);
        Assert.Null(c.DateSolde);
    }

    [Fact]
    public void Ligne_gratuite_sans_valeur_et_libelles_de_prix()
    {
        var c = Assert.Single(ConsultationCommandesService.Regrouper(
        [
            Brut(1, "A", 1) with { Dnopt2 = "O", Dnpus = 50 },
            Brut(1, "A", 2) with { Dnopt5 = "O", Dnpus = 0 },
        ]));
        Assert.Equal(0m, c.Lignes[0].Valeur);
        Assert.Equal("Gratuit", c.Lignes[0].Prix);
        Assert.Equal("Prix à définir", c.Lignes[1].Prix);
    }

    [Fact]
    public void Ligne_a_la_demande_et_appel_de_livraison()
    {
        var c = Assert.Single(ConsultationCommandesService.Regrouper(
        [
            Brut(1, "A", 1) with { Dnstat = "3" },
            Brut(1, "A", 2) with { Dnstat = "1", Dnlori = 1, Dnappe = "APP-7" },
        ]));
        Assert.True(c.Lignes[0].ALaDemande);
        Assert.False(c.Lignes[0].EstAppel);
        Assert.True(c.Lignes[1].EstAppel);
    }

    [Fact]
    public void Commande_soldee_avec_sa_date_de_solde()
    {
        var c = Assert.Single(ConsultationCommandesService.Regrouper([Brut(1, "A", 1) with { Cctags = "D", DateSolde = new(2026, 9, 30) }]));
        Assert.True(c.Soldee);
        Assert.Equal(new DateOnly(2026, 9, 30), c.DateSolde);
    }

    [Fact]
    public void Taux_de_livraison_des_lignes_fermes()
    {
        var c = Assert.Single(ConsultationCommandesService.Regrouper(
        [
            Brut(1, "A", 1) with { Dnqtec = 100, Dnqtel = 100 },
            Brut(1, "A", 2) with { Dnqtec = 300, Dnqtel = 500 },   // sur-livraison plafonnée à la quantité
            Brut(1, "A", 3) with { Dnqtec = 600, Dnqtel = 0 },
            Brut(1, "A", 4) with { Dnstat = "3", Dnqtec = 1000, Dnqtel = 0 },   // à la demande : ignorée
        ]));
        Assert.Equal(0.4m, c.TauxLivraison);
    }

    // ---------- Recherche (source de données factice) ----------
    [Fact]
    public async Task Le_representant_impose_remplace_la_saisie()
    {
        var source = new SourceFactice { RepresentantImpose = 12 };
        var service = new ConsultationCommandesService(source, new UtilisateurFactice());
        await service.RechercherAsync(new CriteresCommandes { Representant = 99 });
        Assert.Equal(12, source.DerniersCriteres!.Representant);
    }

    [Fact]
    public async Task Le_representant_impose_suffit_comme_critere()
    {
        var source = new SourceFactice { RepresentantImpose = 12 };
        var service = new ConsultationCommandesService(source, new UtilisateurFactice());
        await service.RechercherAsync(new CriteresCommandes());
        Assert.Equal(55, source.DerniereSociete);
    }

    [Fact]
    public async Task Compte_general_de_la_session_si_seul_l_auxiliaire_est_saisi()
    {
        var source = new SourceFactice();
        var service = new ConsultationCommandesService(source, new UtilisateurFactice());
        await service.RechercherAsync(new CriteresCommandes { CompteClient = 1234 });
        Assert.Equal(411000, source.DerniersCriteres!.CompteGeneral);
    }

    [Fact]
    public async Task Les_criteres_de_l_ecran_ne_sont_pas_modifies()
    {
        var source = new SourceFactice { RepresentantImpose = 12 };
        var service = new ConsultationCommandesService(source, new UtilisateurFactice());
        var criteres = new CriteresCommandes { Numero = 1, Representant = 99 };
        await service.RechercherAsync(criteres);
        Assert.Equal(99, criteres.Representant);
    }

    [Fact]
    public async Task Liste_coupee_au_maximum()
    {
        var source = new SourceFactice
        {
            Lignes = Enumerable.Range(1, LimitesRecherche.Maximum + 1).Select(i => Brut(i, "A", 1)).ToList(),
        };
        var service = new ConsultationCommandesService(source, new UtilisateurFactice());
        var r = await service.RechercherAsync(new CriteresCommandes { Representant = 1 });
        Assert.True(r.Tronque);
        Assert.Equal(LimitesRecherche.Maximum, r.Lignes.Count());
    }

    [Fact]
    public async Task Controle_avant_toute_lecture()
    {
        var source = new SourceFactice();
        var service = new ConsultationCommandesService(source, new UtilisateurFactice());
        await Assert.ThrowsAsync<RegleMetierException>(() => service.RechercherAsync(new CriteresCommandes()));
        Assert.Null(source.DerniersCriteres);
    }

    [Fact]
    public async Task Contexte_representant_et_droits_du_programme_DOS500C()
    {
        var source = new SourceFactice { RepresentantImpose = 7, Options = ["", "", "**", "", "", "", "", ""] };
        var contexte = await new ConsultationCommandesService(source, new UtilisateurFactice()).LireContexteAsync();
        Assert.Equal(7, contexte.RepresentantImpose);
        Assert.False(contexte.Droits.Modifier);
        Assert.True(contexte.Droits.Creer);
        Assert.Equal("DOS500C", source.DernierProgramme);
    }

    [Fact]
    public async Task Sans_session_refuse()
    {
        var service = new ConsultationCommandesService(new SourceFactice(), new UtilisateurFactice(UtilisateurConnecte.Anonyme));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RechercherAsync(new CriteresCommandes { Numero = 1 }));
    }

    // ---------- Jeux d'essai ----------
    private static LigneCommandeListe Ligne(int quantite, int livree, bool aLaDemande = false, bool soldee = false) =>
        new(new CleCommande(1, "A"), 1, "ART", "", "", "", "", aLaDemande, false, "", null, quantite, livree, 0,
            quantite - livree, 0, "", false, false, 0, "", 0, "", "", soldee, 0);

    internal static LigneCommandeBrute Brut(int dossier, string cle, int ligne) => new(
        Rdoss: dossier, Ccdcle: cle, Cctypc: "3", Rgen: 411000, Rcli: 1234, Cnom: "CLIENT TEST", NomFacture: "",
        Ccrefc: "PO-1", Ccdes1: "Étiquettes", Ccdes2: "Ligne 2", DateOuverture: new(2026, 10, 1), DateCommandeClient: null,
        DateLivraisonEntete: null, DateSolde: null, Cctags: "", Ccrep: 12, NomRepresentant: "MARTIN", Ccinat: "JD",
        Ccense: "", Dnnlig: ligne, Dnart: "ART" + ligne, Dnind: "", Arfimp: "", Dndesa: "Désignation " + ligne, Dnstat: "1",
        Dnlori: 0, Dnappe: "", Dndliv: 0, Dnqtec: 10, Dnqtel: 0, Dnqtef: 0, Dnqter: 10, Dnpus: 0, Dnuv: "U", Dnopt2: "",
        Dnopt5: "", Tnum: 1, Dnnorm: "", Amodel: 0, Afam: "", Asfam: "", Dnsold: "", Dnccom: 0, Texte: "", NbDocuments: 0);

    private sealed class UtilisateurFactice(UtilisateurConnecte? u = null) : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(u ?? new UtilisateurConnecte("DLP", 55, "TEST", 411000));
    }

    private sealed class SourceFactice : ISourceCommandes
    {
        public int? RepresentantImpose { get; init; }
        public IReadOnlyList<string>? Options { get; init; }
        public List<LigneCommandeBrute> Lignes { get; init; } = [];
        public CriteresCommandes? DerniersCriteres { get; private set; }
        public int DerniereSociete { get; private set; }
        public string? DernierProgramme { get; private set; }

        public Task<int?> RepresentantImposeAsync(int societe, string profil, CancellationToken ct) => Task.FromResult(RepresentantImpose);

        public Task<IReadOnlyList<string>?> OptionsProgrammeAsync(string programme, string profil, CancellationToken ct)
        {
            DernierProgramme = programme;
            return Task.FromResult(Options);
        }

        public Task<IReadOnlyList<LigneCommandeBrute>> RechercherAsync(int societe, CriteresCommandes criteres, CancellationToken ct)
        {
            DerniereSociete = societe;
            DerniersCriteres = criteres;
            return Task.FromResult<IReadOnlyList<LigneCommandeBrute>>(Lignes);
        }
    }
}
