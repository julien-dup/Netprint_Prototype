using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Règles de la saisie de l'en-tête de commande (DOS505)
public class SaisieCommandesServiceTests
{
    // ---------- Qualité client et encours ----------
    [Theory]
    [InlineData("", false, null, false)]
    [InlineData("1", false, "qualité 1", true)]
    [InlineData("1", true, null, false)]
    [InlineData("5", false, "bloqué en livraison", true)]
    [InlineData("2", true, "qualité 2, 3 ou 4", false)]
    [InlineData("3", false, "qualité 2, 3 ou 4", false)]
    [InlineData("4", true, "qualité 2, 3 ou 4", false)]
    public void Qualite_client(string qualite, bool forcee, string? extrait, bool forcable)
    {
        var r = SaisieCommandesService.ControleQualite(qualite, forcee);
        if (extrait is null) { Assert.Null(r); return; }
        Assert.NotNull(r);
        Assert.Contains(extrait, r.Value.Message);
        Assert.Equal(forcable, r.Value.Forcable);
    }

    [Fact]
    public void Client_non_controle_en_encours() =>
        Assert.Null(SaisieCommandesService.Encours(Client() with { ControleEncours = "N", EncoursComptable = 1_000_000 }));

    [Theory]
    [InlineData("O", 10, 5, 12000, 2000, null)]        // 14 000 ≤ 10 k + 5 k
    [InlineData("O", 10, 5, 12000, 4000, 1000)]        // 16 000 − 15 000
    [InlineData("", 10, 5, 12000, 4000, 6000)]          // découvert SFAC ignoré si CGSFAC ≠ 'O'
    [InlineData("N", 10, 0, 9000, 2000, 1000)]          // contrôlé car découvert maison > 0
    public void Encours(string controle, int maison, int sfac, int comptable, int commercial, int? depassement)
    {
        var c = Client() with
        {
            ControleEncours = controle, DecouvertMaison = maison, DecouvertSfac = sfac,
            EncoursComptable = comptable, EncoursCommercial = commercial,
        };
        Assert.Equal(depassement, SaisieCommandesService.Encours(c) is { } d ? (int)d : null);
    }

    // ---------- Valeurs par défaut ----------
    [Fact]
    public void Valeurs_par_defaut_du_client()
    {
        var e = SaisieCommandesService.Proposer(Debut(), Client(), Defauts(), Parametres(), "$2");
        Assert.Equal("$2", e.Categorie);
        Assert.Equal("$2", e.SousCategorie);
        Assert.Equal("1", e.TypeFacturation);
        Assert.Equal(28, e.Representant);
        Assert.Equal(5m, e.TauxCommission);                // taux du représentant (le client n'en a pas)
        Assert.Equal("LE", e.Fabricant);                   // SOP13 = 1 : fabricant du client
        Assert.Equal("A", e.EnlevementOuLivraison);
        Assert.Equal("F", e.Port);
        Assert.Equal(1, e.NbLieux);
        Assert.Equal("F", e.AdresseFacturation);
        Assert.Equal("2", e.CodeVente);
        Assert.Equal("E", e.Monnaie);
        Assert.Equal("MME DUPONT", e.Interlocuteur);
        Assert.Equal(3, e.AdresseArc);
        Assert.Null(e.CompteClientFacture);
        Assert.True(e.EditionEtiquettes);
        Assert.Equal("1", e.Enseigne);
    }

    [Fact]
    public void Taux_du_client_prioritaire_et_client_de_regroupement_facture()
    {
        var e = SaisieCommandesService.Proposer(Debut(), Client() with
        {
            TauxCommission = 7.5m, CompteGeneralRegroupement = 411000, CompteClientRegroupement = 99,
        }, Defauts(), Parametres(), "$2");
        Assert.Equal(7.5m, e.TauxCommission);
        Assert.Equal(411000, e.CompteGeneralFacture);
        Assert.Equal(99, e.CompteClientFacture);
    }

    [Fact]
    public void Enlevement_sans_port_ni_lieux()
    {
        var e = SaisieCommandesService.Proposer(Debut(), Client() with { ModeTransport = "E", TypePort = "D" }, Defauts(), Parametres(), "$2");
        Assert.Equal("E", e.EnlevementOuLivraison);
        Assert.Equal("", e.Port);
        Assert.Null(e.NbLieux);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    public void Fabricant_du_profil_selon_SOP13(string sop13) =>
        Assert.Equal("DLP", SaisieCommandesService.Proposer(Debut(), Client(), Defauts(), Parametres() with { FabricantDefaut = sop13 }, "$2").Fabricant);

    // ---------- Contrôles de l'écran 2 ----------
    [Fact]
    public void En_tete_complet_accepte() => Assert.Empty(Controler(Valide()));

    public static TheoryData<string, string> EnTetesRefuses() => new()
    {
        { "Designation1", "Cette zone doit être renseignée." },
        { "DateLivraison", "La date ou la semaine de livraison prévue est obligatoire." },
        { "TypeFacturation", "Cette zone doit être égale à 1, 2 ou 3." },
        { "Port", "Cette zone doit être égale à D, A ou F." },
        { "NbLieux", "Cette zone doit être renseignée." },
        { "AdresseFacturation", "Le code adresse doit être égal à C, F, L ou R." },
        { "EnlevementOuLivraison", "Cette zone doit être égale à E ou A." },
        { "Echeance", "Le nombre de jours d'échéance est incorrect." },
        { "Le", "Le jour doit être inférieur ou égal à 31." },
        { "Arrete", "Le jour doit être inférieur ou égal à 31." },
        { "Releve", "Ce type de relevé n'existe pas." },
        { "ModeReglement", "Cette zone doit être renseignée." },
    };

    [Theory]
    [MemberData(nameof(EnTetesRefuses))]
    public void En_tete_refuse(string zone, string message)
    {
        var s = Valide();
        switch (zone)
        {
            case "Designation1": s.Designation1 = " "; break;
            case "DateLivraison": s.DateLivraison = null; s.SemaineLivraison = null; break;
            case "TypeFacturation": s.TypeFacturation = "4"; break;
            case "Port": s.Port = "X"; break;
            case "NbLieux": s.NbLieux = 0; break;
            case "AdresseFacturation": s.AdresseFacturation = "Z"; break;
            case "EnlevementOuLivraison": s.EnlevementOuLivraison = "B"; break;
            case "Echeance": s.ModeReglement = "VI"; s.Echeance = 35; break;
            case "Le": s.ModeReglement = "VI"; s.Le = 32; break;
            case "Arrete": s.Arrete = 40; break;
            case "Releve": s.Releve = "X"; break;
            case "ModeReglement": s.Echeance = 30; break;
        }
        var erreur = Assert.Single(Controler(s), e => e.Zone == zone);
        Assert.Equal(message, erreur.Message);
    }

    [Fact]
    public void References_inexistantes()
    {
        var s = Valide();
        s.CodeVente = "Z";
        s.ModeReglement = "XX";
        s.AdresseArc = 9;
        s.CompteGeneralFacture = 411000;
        s.CompteClientFacture = 1;
        var r = References() with
        {
            RepresentantExiste = false, FabricantExiste = false, TypeCodeVente = null, ModeReglementExiste = false,
            AdresseArcExiste = false, ClientFactureExiste = false,
        };
        var zones = SaisieCommandesService.Controler(s, r, true, false).Select(e => e.Zone).ToList();
        Assert.Equal(["AdresseArc", "CompteClientFacture", "Representant", "Fabricant", "CodeVente", "ModeReglement"], zones);
    }

    [Fact]
    public void Code_ligne_qui_n_est_pas_de_vente() =>
        Assert.Contains(Controler(Valide(), References() with { TypeCodeVente = "R" }),
            e => e is { Zone: "CodeVente", Message: "Le code ligne doit être de type « Vente »." });

    [Fact]
    public void Semaine_inexistante() =>
        Assert.Contains(Controler(Semaine(45), References() with { SemaineExiste = false }),
            e => e is { Zone: "SemaineLivraison", Message: "Cette semaine n'existe pas." });

    [Fact]
    public void Enlevement_sans_controle_de_port() =>
        Assert.Empty(Controler(Valide().Avec(s => { s.EnlevementOuLivraison = "E"; s.Port = ""; s.NbLieux = null; s.AdresseFacturation = ""; })));

    [Fact]
    public void Interlocuteur_obligatoire_pour_certains_clients() =>
        Assert.Contains(SaisieCommandesService.Controler(Valide().Avec(s => s.Interlocuteur = ""), References(), true, true),
            e => e.Zone == "Interlocuteur");

    [Fact]
    public void Sous_traitance_refusee_si_non_geree() =>
        Assert.Contains(SaisieCommandesService.Controler(Valide().Avec(s => s.SousTraitee = true), References(), false, false),
            e => e.Zone == "SousTraitee");

    [Fact]
    public void Semaine_de_la_date_ou_semaine_saisie()
    {
        Assert.Equal((2026, 46), SaisieCommandesService.Semaine(Valide(), References()));
        Assert.Equal((2026, 45), SaisieCommandesService.Semaine(Semaine(45), References()));
    }

    [Fact]
    public void Normalisation_des_zones()
    {
        var s = SaisieCommandesService.Normaliser(Valide().Avec(x =>
        {
            x.Fabricant = " le "; x.ModeReglement = "vi"; x.Designation1 = "  Étiquettes  " + new string('x', 50);
            x.EnlevementOuLivraison = "e"; x.Port = "F"; x.NbLieux = 2; x.SemaineLivraison = 3;
        }));
        Assert.Equal("LE", s.Fabricant);
        Assert.Equal("VI", s.ModeReglement);
        Assert.Equal(40, s.Designation1.Length);
        Assert.StartsWith("Étiquettes", s.Designation1);
        Assert.Equal("", s.Port);
        Assert.Null(s.NbLieux);
        Assert.Null(s.SemaineLivraison);     // la date prime sur la semaine
    }

    // ---------- Numérotation ----------
    [Theory]
    [InlineData("00003 D", 3, "D")]
    [InlineData("68916 9", 68916, "9")]
    public void Retour_de_GETDOSSIER(string retour, int numero, string cle) =>
        Assert.Equal((numero, cle), NumerotationCommande.Decouper(retour));

    [Theory]
    [InlineData("     -1")]
    [InlineData("")]
    [InlineData("00000 A")]
    public void GETDOSSIER_en_erreur(string retour) =>
        Assert.Throws<RegleMetierException>(() => NumerotationCommande.Decouper(retour));

    [Theory]
    [InlineData(false, false, "D", "D")]   // POPT02 = 'N' : clé de GETDOSSIER
    [InlineData(false, true, "D", "9")]    // POPT05 : clé forcée à 9
    [InlineData(true, true, "D", "9")]
    [InlineData(true, false, "D", "9")]    // CDECLEF (spécifique, absent) : 9 comme le RPG en erreur
    public void Cle_de_commande(bool garder, bool forcer9, string cleGetDossier, string attendu) =>
        Assert.Equal(attendu, NumerotationCommande.Cle(garder, forcer9, cleGetDossier));

    [Fact]
    public void Cle_deja_prise_remplacee_par_un_chiffre()
    {
        Assert.Equal("9", NumerotationCommande.CleLibre("9", new HashSet<string> { "A" }));
        Assert.Equal("1", NumerotationCommande.CleLibre("9", new HashSet<string> { "9" }));
        Assert.Equal("3", NumerotationCommande.CleLibre("9", new HashSet<string> { "9", "1", "2" }));
        Assert.Null(NumerotationCommande.CleLibre("9", "1234567890".Select(c => c.ToString()).ToHashSet()));
    }

    // ---------- Service (sources factices) ----------
    [Fact]
    public async Task Preparation_qualite_1_a_confirmer_puis_forcee()
    {
        var source = new SourceSaisieFactice { Client = Client() with { Qualite = "1" } };
        var service = Service(source);
        var ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => service.PreparerAsync(Debut()));
        Assert.True(ex.Forcable);
        var debut = Debut();
        debut.ForcerQualite = true;
        var e = await service.PreparerAsync(debut);
        Assert.Single(e.Avertissements);
    }

    [Fact]
    public async Task Preparation_client_cloture_ou_inexistant_refusee()
    {
        var cloture = await Assert.ThrowsAsync<SaisieInvalideException>(() =>
            Service(new SourceSaisieFactice { Client = Client() with { DateFermeture = 20250101 } }).PreparerAsync(Debut()));
        Assert.Contains(cloture.Erreurs, e => e.Message == "Ce client est clôturé.");
        var absent = await Assert.ThrowsAsync<SaisieInvalideException>(() =>
            Service(new SourceSaisieFactice { Client = null }).PreparerAsync(Debut()));
        Assert.Contains(absent.Erreurs, e => e.Message == "Ce compte client n'existe pas.");
    }

    [Fact]
    public async Task Preparation_enseigne_obligatoire_et_coherente()
    {
        var debut = Debut();
        debut.Enseigne = "";
        var ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => Service(new SourceSaisieFactice()).PreparerAsync(debut));
        Assert.Contains(ex.Erreurs, e => e.Zone == "Enseigne");
        var autre = await Assert.ThrowsAsync<SaisieInvalideException>(() =>
            Service(new SourceSaisieFactice { CompteGeneralEnseigne = 401000 }).PreparerAsync(Debut()));
        Assert.Contains(autre.Erreurs, e => e.Message == "Le compte général est différent de celui de l'enseigne.");
    }

    [Fact]
    public async Task Creation_controlee_puis_ecrite()
    {
        var source = new SourceSaisieFactice();
        var cle = await Service(source).CreerAsync(Valide());
        Assert.Equal(new CleCommande(12345, "9"), cle);
        Assert.Equal((2026, 46), source.SemaineEcrite);
        Assert.Equal("DLP", source.ProfilEcrit);
    }

    [Fact]
    public async Task Creation_refusee_sans_ecriture()
    {
        var source = new SourceSaisieFactice();
        await Assert.ThrowsAsync<SaisieInvalideException>(() => Service(source).CreerAsync(Valide().Avec(s => s.Designation1 = "")));
        Assert.Null(source.ProfilEcrit);
    }

    [Fact]
    public async Task Modification_d_une_commande_soldee_refusee()
    {
        var source = new SourceSaisieFactice { Lue = Valide().Avec(s => s.Soldee = true) };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Service(source).ModifierAsync(new CleCommande(1, "9"), Valide()));
        Assert.Equal("Option impossible, dossier soldé.", ex.Message);
    }

    [Fact]
    public async Task Modification_zones_figees_et_concurrence()
    {
        var lue = Valide().Avec(s => { s.AvecLignes = true; s.TypeFacturation = "2"; });
        var source = new SourceSaisieFactice { Lue = lue };
        var service = Service(source);
        var ecran = (await service.LireAsync(new CleCommande(1, "9")))!;
        ecran.CompteClient = 999;          // non modifiable
        ecran.TypeFacturation = "3";       // figé : des lignes existent
        ecran.Designation1 = "Nouvelle désignation";
        await service.ModifierAsync(new CleCommande(1, "9"), ecran);
        Assert.Equal(376, source.Modifiee!.CompteClient);
        Assert.Equal("2", source.Modifiee.TypeFacturation);
        Assert.Equal("Nouvelle désignation", source.Modifiee.Designation1);

        source.Lue = lue.Avec(s => s.Designation2 = "changée ailleurs");
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => service.ModifierAsync(new CleCommande(1, "9"), ecran));
        Assert.Contains("modifié par un autre utilisateur", ex.Message);
    }

    [Fact]
    public async Task Creation_et_modification_refusees_sans_droit()
    {
        var sansDroits = new ConsultationCommandesServiceTests.SourceFactice { Options = ["", "**", "**", "", "", "", "", ""] };
        var service = new SaisieCommandesService(new SourceSaisieFactice { Lue = Valide() }, sansDroits,
            new ConsultationCommandesServiceTests.UtilisateurFactice());
        Assert.Equal("Vous n'êtes pas autorisé à créer des commandes.",
            (await Assert.ThrowsAsync<RegleMetierException>(() => service.CreerAsync(Valide()))).Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => service.PreparerAsync(Debut()));
        Assert.Equal("Vous n'êtes pas autorisé à modifier les commandes.",
            (await Assert.ThrowsAsync<RegleMetierException>(() => service.ModifierAsync(new CleCommande(1, "9"), Valide()))).Message);
    }

    [Fact]
    public async Task Representant_impose_a_la_creation()
    {
        var source = new SourceSaisieFactice();
        var limite = new ConsultationCommandesServiceTests.SourceFactice { RepresentantImpose = 12 };
        var service = new SaisieCommandesService(source, limite, new ConsultationCommandesServiceTests.UtilisateurFactice());
        Assert.Equal(12, (await service.PreparerAsync(Debut())).Representant);
        await service.CreerAsync(Valide().Avec(s => s.Representant = 34));
        Assert.Equal(12, source.Creee!.Representant);
    }

    // ---------- Jeux d'essai ----------
    private static List<ErreurSaisie> Controler(EnteteSaisie s, ReferencesEntete? r = null) =>
        SaisieCommandesService.Controler(s, r ?? References(), true, false);

    private static DebutCommande Debut() => new()
    {
        CompteGeneral = 411100, CompteClient = 376, TypeCommande = "4", Enseigne = "1", Date = new(2026, 10, 30),
    };

    private static ClientSaisie Client() => new(
        Nom: "LABORATOIRES CLARINS", Qualite: "", DateFermeture: 0, Representant: 28, TauxCommission: 0, ModeTransport: "A",
        TypePort: "", CodeVente: "2", Revendeur: "N", CompteGeneralRegroupement: 0, CompteClientRegroupement: 0, Fabricant: "LE",
        Monnaie: "E", InterlocuteurObligatoire: false, FacturationAuto: "4", AdresseFactureAuto: 0, TypePieceFacture: "",
        ControleEncours: "N", DecouvertMaison: 0, DecouvertSfac: 0, EncoursComptable: 0, EncoursCommercial: 0);

    private static DefautsSaisie Defauts() => new(5m, "DLP", "MME DUPONT", 3, true, true);

    private static ParametresSaisie Parametres() => new(true, "1", true, true, true, true, "O");

    private static ReferencesEntete References() => new((2026, 46), true, true, true, true, true, "V", true);

    internal static EnteteSaisie Valide() => new()
    {
        CompteGeneral = 411100, CompteClient = 376, NomClient = "LABORATOIRES CLARINS", Enseigne = "1",
        DateDossier = new(2026, 10, 30), TypeCommande = "4", Categorie = "$2", SousCategorie = "$2", TypeFacturation = "1",
        Designation1 = "Étiquettes test", DateLivraison = new(2026, 11, 12), Representant = 28, Fabricant = "LE",
        CodeVente = "2", Monnaie = "E", EnlevementOuLivraison = "A", Port = "F", NbLieux = 1, AdresseFacturation = "F",
    };

    private static EnteteSaisie Semaine(int semaine) => Valide().Avec(s =>
    {
        s.DateLivraison = null;
        s.SemaineLivraison = semaine;
        s.AnneeLivraison = 2026;
    });

    private static SaisieCommandesService Service(SourceSaisieFactice source) =>
        new(source, new ConsultationCommandesServiceTests.SourceFactice(), new ConsultationCommandesServiceTests.UtilisateurFactice());

    internal sealed class SourceSaisieFactice : ISourceSaisieCommandes
    {
        public ClientSaisie? Client { get; init; } = SaisieCommandesServiceTests.Client();
        public int? CompteGeneralEnseigne { get; init; } = 0;
        public EnteteSaisie? Lue { get; set; }
        public (int, int)? SemaineEcrite { get; private set; }
        public string? ProfilEcrit { get; private set; }
        public EnteteSaisie? Modifiee { get; private set; }
        public EnteteSaisie? Creee { get; private set; }

        public Task<ParametresSaisie> ParametresAsync(int societe, CancellationToken ct) => Task.FromResult(Parametres());
        public Task<ListesSaisie> ListesAsync(int societe, CancellationToken ct) => Task.FromResult(new ListesSaisie([], [], []));
        public Task<ClientSaisie?> ClientAsync(int societe, int g, int c, CancellationToken ct) => Task.FromResult(Client);
        public Task<int?> CompteGeneralEnseigneAsync(int societe, string enseigne, CancellationToken ct) => Task.FromResult(CompteGeneralEnseigne);
        public Task<DefautsSaisie> DefautsAsync(int societe, string profil, ClientSaisie client, int g, int c, string categorie,
            CancellationToken ct) => Task.FromResult(Defauts());
        public Task<ReferencesEntete> VerifierAsync(int societe, EnteteSaisie s, CancellationToken ct) => Task.FromResult(References());

        public Task<CleCommande> CreerAsync(int societe, string profil, EnteteSaisie s, ClientSaisie client, ParametresSaisie p,
            (int Annee, int Semaine) semaine, CancellationToken ct)
        {
            SemaineEcrite = semaine;
            ProfilEcrit = profil;
            Creee = s;
            return Task.FromResult(new CleCommande(12345, "9"));
        }

        public Task<EnteteSaisie?> LireAsync(int societe, CleCommande cle, int? representant, CancellationToken ct) =>
            Task.FromResult(Lue?.Copier());

        public Task<bool> ModifierAsync(int societe, string profil, CleCommande cle, EnteteSaisie s, (int Annee, int Semaine) semaine,
            Func<EnteteSaisie, string> empreinte, CancellationToken ct)
        {
            if (Lue is null || empreinte(Lue) != s.Empreinte) return Task.FromResult(false);
            Modifiee = s;
            return Task.FromResult(true);
        }
    }
}

internal static class EnteteSaisieTests
{
    public static EnteteSaisie Avec(this EnteteSaisie e, Action<EnteteSaisie> modifier)
    {
        var c = e.Copier();
        modifier(c);
        return c;
    }
}
