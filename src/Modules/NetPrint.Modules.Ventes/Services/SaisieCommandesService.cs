using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Saisie de l'en-tête de commande : règles de DOS505 (écrans DOS50501 et DOS50512), messages repris de PRTMSG.
// Écarts volontaires (rétro-doc DOS505, rapport d'écriture) :
//   - pas de n° provisoire : le n° est réservé à l'enregistrement ;
//   - l'encours (DOS901) bloque comme le RPG, avec un message (DOS901 n'en donnait pas dans DOS505) ;
//   - la qualité client est recontrôlée à l'enregistrement ;
//   - modification : refus si l'en-tête a changé depuis sa lecture (le RPG réécrivait tout, solde compris).
// =====================================================================
internal sealed class SaisieCommandesService(
    ISourceSaisieCommandes donnees, ISourceCommandes commandes, IUtilisateurCourant utilisateur) : ISaisieCommandes
{
    public async Task<ContexteSaisieCommande> LireContexteAsync(CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var p = await donnees.ParametresAsync(u.Societe, ct);
        var listes = await donnees.ListesAsync(u.Societe, ct);
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        return new ContexteSaisieCommande(p.Enseignes, p.SousTraitance, listes.Enseignes, listes.ModesReglement,
            listes.CodesVente, droits);
    }

    // ------------------------------------------------------------------
    // Écran 1 : client, type, enseigne, date → en-tête proposé
    // ------------------------------------------------------------------
    public async Task<EnteteSaisie> PreparerAsync(DebutCommande debut, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        await ExigerDroitAsync(u, d => d.Creer, "Vous n'êtes pas autorisé à créer des commandes.", ct);
        var p = await donnees.ParametresAsync(u.Societe, ct);
        var erreurs = new List<ErreurSaisie>();
        if (debut.CompteGeneral is null or 0) erreurs.Add(new(nameof(DebutCommande.CompteGeneral), Messages.Obligatoire));
        if (debut.CompteClient is null or 0) erreurs.Add(new(nameof(DebutCommande.CompteClient), Messages.Obligatoire));
        if (debut.TypeCommande is not (TypesCommande.Negoce or TypesCommande.ProduitFini))
            erreurs.Add(new(nameof(DebutCommande.TypeCommande), Messages.Type34));
        Lever(erreurs);

        var client = await donnees.ClientAsync(u.Societe, debut.CompteGeneral!.Value, debut.CompteClient!.Value, ct);
        await ControlerClientAsync(u, p, debut, client, ct);

        var categorie = debut.TypeCommande == TypesCommande.Negoce ? "$1" : "$2";
        var defauts = await donnees.DefautsAsync(u.Societe, u.Profil, client!, debut.CompteGeneral.Value,
            debut.CompteClient.Value, categorie, ct);
        if (!defauts.CategorieExiste || !defauts.SousCategorieExiste)
            throw new RegleMetierException($"La catégorie {categorie} (ou sa sous-catégorie) n'existe pas pour cette société.");

        var proposition = Proposer(debut, client!, defauts, p, categorie);
        // Représentant limité (B000F.ILIMIT) : la commande est à lui
        if (await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct) is { } impose) proposition.Representant = impose;
        return proposition;
    }

    // Droits PRT000P1 du programme DOS500C, vérifiés côté serveur (l'écran ne fait que griser les boutons)
    private async Task ExigerDroitAsync(UtilisateurConnecte u, Func<DroitsCommandes, bool> droit, string message, CancellationToken ct)
    {
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        if (!droit(droits)) throw new RegleMetierException(message);
    }

    // Représentant limité : il ne peut ni créer ni déplacer une commande vers un autre représentant
    private async Task ImposerRepresentantAsync(UtilisateurConnecte u, EnteteSaisie s, CancellationToken ct)
    {
        if (await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct) is { } impose) s.Representant = impose;
    }

    private async Task ControlerClientAsync(UtilisateurConnecte u, ParametresSaisie p, DebutCommande debut, ClientSaisie? client,
        CancellationToken ct)
    {
        var erreurs = new List<ErreurSaisie>();
        if (client is null) Lever([new(nameof(DebutCommande.CompteClient), Messages.ClientInexistant)]);
        if (client!.DateFermeture > 0) erreurs.Add(new(nameof(DebutCommande.CompteClient), Messages.ClientCloture));
        var qualite = ControleQualite(client.Qualite, debut.ForcerQualite);
        if (qualite is { } q && !q.Forcable) erreurs.Add(new(nameof(DebutCommande.CompteClient), q.Message));

        if (p.Enseignes)
        {
            if (string.IsNullOrWhiteSpace(debut.Enseigne)) erreurs.Add(new(nameof(DebutCommande.Enseigne), Messages.Obligatoire));
            else if (await donnees.CompteGeneralEnseigneAsync(u.Societe, debut.Enseigne.Trim().ToUpperInvariant(), ct) is not { } genEnseigne)
                erreurs.Add(new(nameof(DebutCommande.Enseigne), Messages.EnseigneInexistante));
            else if (genEnseigne != 0 && genEnseigne != debut.CompteGeneral)
                erreurs.Add(new(nameof(DebutCommande.Enseigne), Messages.EnseigneCompteGeneral));
        }
        if (Encours(client) is { } depassement)
            erreurs.Add(new(nameof(DebutCommande.CompteClient),
                $"Cette commande fait dépasser l'en-cours du client ({depassement:N2} au-delà du découvert autorisé)."));
        Lever(erreurs);

        // Qualité 1 ou 5 : l'utilisateur doit confirmer (F19 du RPG)
        if (qualite is { Forcable: true } avertissement)
            throw new SaisieInvalideException([new(nameof(DebutCommande.CompteClient), avertissement.Message)]) { Forcable = true };
    }

    // Qualité client (CQUACL, réponse de David) : 1 alerte à la commande, 2-3-4 blocage commande, 5 blocage livraisons.
    // DOS505 : 1 et 5 se forcent par F19, 2-3-4 jamais. Null si rien à signaler (ou déjà forcé).
    internal static (string Message, bool Forcable)? ControleQualite(string qualite, bool forcee) => qualite switch
    {
        "1" when !forcee => (Messages.Qualite1, true),
        "5" when !forcee => (Messages.Qualite5, true),
        "2" or "3" or "4" => (Messages.Qualite234, false),
        _ => null,
    };

    // DOS901 option C : en-cours comptable + commercial − découvert maison (k€) − découvert SFAC (k€, si CGSFAC = 'O').
    // Client non contrôlé si CGSFAC = 'N' sans découvert. Renvoie le dépassement, ou null.
    internal static decimal? Encours(ClientSaisie c)
    {
        if (c.ControleEncours == "N" && c.DecouvertMaison == 0 && c.DecouvertSfac == 0) return null;
        var depassement = c.EncoursComptable + c.EncoursCommercial - c.DecouvertMaison * 1000
            - (c.ControleEncours == "O" ? c.DecouvertSfac * 1000 : 0);
        return depassement > 0 ? depassement : null;
    }

    // Valeurs par défaut (DOS505 l. 880-956, 1018-1033, 1550-1609)
    internal static EnteteSaisie Proposer(DebutCommande debut, ClientSaisie client, DefautsSaisie defauts, ParametresSaisie p,
        string categorie)
    {
        var aLivrer = client.ModeTransport != "E";
        var e = new EnteteSaisie
        {
            CompteGeneral = debut.CompteGeneral ?? 0,
            CompteClient = debut.CompteClient ?? 0,
            NomClient = client.Nom,
            Enseigne = p.Enseignes ? (debut.Enseigne ?? "").Trim().ToUpperInvariant() : "",
            DateDossier = debut.Date,
            TypeCommande = debut.TypeCommande,
            Categorie = categorie,
            SousCategorie = categorie,
            TypeFacturation = "1",
            Monnaie = client.Monnaie,
            CodeVente = client.CodeVente,
            Representant = client.Representant == 0 ? null : client.Representant,
            // Taux du représentant, remplacé par celui du client s'il en a un
            TauxCommission = client.TauxCommission > 0 ? client.TauxCommission : defauts.TauxRepresentant is > 0 ? defauts.TauxRepresentant : null,
            Revendeur = client.Revendeur == "O",
            EnlevementOuLivraison = client.ModeTransport is "E" or "A" ? client.ModeTransport : "A",
            Port = !aLivrer ? "" : client.TypePort != "" ? client.TypePort : "F",
            NbLieux = aLivrer ? 1 : null,
            AdresseFacturation = aLivrer ? "F" : "",
            // Client de regroupement = client facturé
            CompteGeneralFacture = client.CompteGeneralRegroupement != 0 ? client.CompteGeneralRegroupement : null,
            CompteClientFacture = client.CompteGeneralRegroupement != 0 ? client.CompteClientRegroupement : null,
            Fabricant = p.FabricantDefaut is "2" or "3" ? defauts.InitialesProfil : client.Fabricant,
            Interlocuteur = defauts.Interlocuteur,
            AdresseArc = defauts.AdresseArc == 0 ? null : defauts.AdresseArc,
            EditionEtiquettes = true,
            SousTraitee = false,
        };
        if (debut.ForcerQualite && client.Qualite is "1" or "5")
            e.Avertissements.Add(client.Qualite == "1" ? Messages.Qualite1 : Messages.Qualite5);
        return e;
    }

    // ------------------------------------------------------------------
    // Écran 2 : contrôles de l'en-tête (ECR02 de DOS505, dans le même ordre)
    // ------------------------------------------------------------------
    internal static List<ErreurSaisie> Controler(EnteteSaisie s, ReferencesEntete r, bool sousTraitanceSaisissable,
        bool interlocuteurObligatoire)
    {
        var e = new List<ErreurSaisie>();
        void Si(bool condition, string zone, string message)
        {
            if (condition && e.All(x => x.Zone != zone)) e.Add(new(zone, message));
        }

        Si(s.TypeCommande is not (TypesCommande.Negoce or TypesCommande.ProduitFini), nameof(s.TypeCommande), Messages.Type34);
        Si(s.TypeFacturation is not ("1" or "2" or "3"), nameof(s.TypeFacturation), Messages.Facturation123);
        Si(string.IsNullOrWhiteSpace(s.Designation1), nameof(s.Designation1), Messages.Obligatoire);
        Si(s.DateLivraison is null && s.SemaineLivraison is not > 0, nameof(s.DateLivraison), Messages.LivraisonObligatoire);
        if (s.DateLivraison is null && s.SemaineLivraison is > 0)
        {
            Si(s.AnneeLivraison is not > 0, nameof(s.AnneeLivraison), Messages.Obligatoire);
            Si(!r.SemaineExiste, nameof(s.SemaineLivraison), Messages.SemaineInexistante);
        }
        Si(s.DateLivraison is not null && r.SemaineDeLaDate is null, nameof(s.DateLivraison), Messages.DateIncorrecte);
        Si(s.AdresseArc is > 0 && !r.AdresseArcExiste, nameof(s.AdresseArc), Messages.AdresseInexistante);
        Si(interlocuteurObligatoire && string.IsNullOrWhiteSpace(s.Interlocuteur), nameof(s.Interlocuteur), Messages.Obligatoire);
        Si(s.CompteClientFacture is > 0 && s.CompteGeneralFacture is not > 0, nameof(s.CompteGeneralFacture), Messages.Obligatoire);
        Si(s.CompteClientFacture is > 0 && !r.ClientFactureExiste, nameof(s.CompteClientFacture), Messages.ClientInexistant);
        Si(!r.RepresentantExiste, nameof(s.Representant), Messages.RepresentantInexistant);
        Si(s.TauxCommission is < 0 or >= 100, nameof(s.TauxCommission), "Le taux de commission doit être compris entre 0 et 99,99.");
        Si(!r.FabricantExiste, nameof(s.Fabricant), Messages.FabricantInexistant);
        Si(s.CodeVente != "" && r.TypeCodeVente is null, nameof(s.CodeVente), Messages.CodeLigneInexistant);
        Si(s.CodeVente != "" && r.TypeCodeVente is not (null or "V"), nameof(s.CodeVente), Messages.CodeLigneVente);
        Si(s.SousTraitee && !sousTraitanceSaisissable, nameof(s.SousTraitee), "La sous-traitance n'est pas utilisée dans cette société.");
        Si(s.EnlevementOuLivraison is not ("E" or "A"), nameof(s.EnlevementOuLivraison), Messages.EnlevementOuLivraison);
        if (s.EnlevementOuLivraison == "A")
        {
            Si(s.Port is not ("D" or "A" or "F"), nameof(s.Port), Messages.Port);
            Si(s.NbLieux is not > 0, nameof(s.NbLieux), Messages.Obligatoire);
            Si(s.AdresseFacturation is not ("F" or "L" or "R" or "C"), nameof(s.AdresseFacturation), Messages.AdresseFacturation);
        }
        Si(s.ModeReglement != "" && !r.ModeReglementExiste, nameof(s.ModeReglement), Messages.ModeReglement);
        Si(s.ModeReglement == "" && (s.Le is > 0 || s.Echeance is > 0), nameof(s.ModeReglement), Messages.Obligatoire);
        Si(!ValeursEntete.Echeances.Contains(s.Echeance ?? 0), nameof(s.Echeance), Messages.Echeance);
        // ÉCART : le RPG affichait ici le message du client facturé (CPT0001) ; on donne le bon message
        Si(s.Le is < 0 or > 31, nameof(s.Le), Messages.Jour31);
        Si(s.Arrete is < 0 or > 31, nameof(s.Arrete), Messages.Jour31);
        Si(s.Releve != "" && !ValeursEntete.Releves.Contains(s.Releve), nameof(s.Releve), Messages.Releve);
        Si(s.Escompte is < 0 or >= 10, nameof(s.Escompte), "L'escompte doit être compris entre 0 et 9,9 %.");
        var lignesTexte = LignesTexte(s.TexteComplementaire);
        Si(lignesTexte.Count > LignesTexteMax, nameof(s.TexteComplementaire),
            $"Le texte complémentaire est limité à {LignesTexteMax} lignes de {LargeurTexte} caractères ({lignesTexte.Count} lignes).");
        var longue = lignesTexte.FindIndex(l => l.Length > LargeurTexte);
        Si(longue >= 0, nameof(s.TexteComplementaire),
            $"La ligne {longue + 1} du texte complémentaire dépasse {LargeurTexte} caractères ({(longue >= 0 ? lignesTexte[longue].Length : 0)}).");
        return e;
    }

    // Texte complémentaire (AB0011.CCTXT1-8) : lignes de 40 caractères au plus (une ligne plus longue est refusée, pas
    // coupée au milieu d'un mot), blancs de fin de ligne et lignes vides de fin retirés, blancs de début conservés
    internal const int LignesTexteMax = 8;
    internal const int LargeurTexte = 40;

    internal static List<string> LignesTexte(string texte)
    {
        var lignes = (texte ?? "").Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lignes.Count > 0 && lignes[^1] == "") lignes.RemoveAt(lignes.Count - 1);
        return lignes;
    }

    // Semaine écrite dans AB001 : celle saisie, sinon celle de la date de livraison (D300F)
    internal static (int Annee, int Semaine) Semaine(EnteteSaisie s, ReferencesEntete r) =>
        s.DateLivraison is null ? (s.SemaineLivraison is > 0 ? s.AnneeLivraison ?? 0 : 0, s.SemaineLivraison ?? 0)
            : r.SemaineDeLaDate ?? (0, 0);

    public async Task<CleCommande> CreerAsync(EnteteSaisie saisie, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        await ExigerDroitAsync(u, d => d.Creer, "Vous n'êtes pas autorisé à créer des commandes.", ct);
        var s = Normaliser(saisie);
        await ImposerRepresentantAsync(u, s, ct);
        var p = await donnees.ParametresAsync(u.Societe, ct);

        // Client recontrôlé à l'enregistrement (clôture, qualité, enseigne, encours)
        var debut = new DebutCommande
        {
            CompteGeneral = s.CompteGeneral, CompteClient = s.CompteClient, TypeCommande = s.TypeCommande, Enseigne = s.Enseigne,
            Date = s.DateDossier, ForcerQualite = s.Avertissements.Count > 0,
        };
        var client = await donnees.ClientAsync(u.Societe, s.CompteGeneral, s.CompteClient, ct);
        await ControlerClientAsync(u, p, debut, client, ct);

        var r = await donnees.VerifierAsync(u.Societe, s, ct);
        Lever(Controler(s, r, p.SousTraitance, client!.InterlocuteurObligatoire));
        return await donnees.CreerAsync(u.Societe, u.Profil, s, client, p, Semaine(s, r), ct);
    }

    public async Task<EnteteSaisie?> LireAsync(CleCommande cle, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var impose = await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var e = await donnees.LireAsync(u.Societe, cle, impose, ct);
        if (e is not null) e.Empreinte = Empreinte(e);
        return e;
    }

    public async Task ModifierAsync(CleCommande cle, EnteteSaisie saisie, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        await ExigerDroitAsync(u, d => d.Modifier, "Vous n'êtes pas autorisé à modifier les commandes.", ct);
        var actuel = await LireAsync(cle, ct) ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        if (actuel.Soldee) throw new RegleMetierException("Option impossible, dossier soldé.");
        if (actuel.Bloquee) throw new RegleMetierException("Commande sous-traitée. Modification impossible.");

        var s = Normaliser(saisie);
        // Zones figées en modification (DOS505 : client, date, enseigne et type protégés ; facturation figée avec des lignes)
        s.CompteGeneral = actuel.CompteGeneral;
        s.CompteClient = actuel.CompteClient;
        s.DateDossier = actuel.DateDossier;
        s.Enseigne = actuel.Enseigne;
        s.TypeCommande = actuel.TypeCommande;
        if (actuel.AvecLignes) s.TypeFacturation = actuel.TypeFacturation;
        await ImposerRepresentantAsync(u, s, ct);

        var p = await donnees.ParametresAsync(u.Societe, ct);
        var client = await donnees.ClientAsync(u.Societe, s.CompteGeneral, s.CompteClient, ct);
        var r = await donnees.VerifierAsync(u.Societe, s, ct);
        Lever(Controler(s, r, p.SousTraitance, client?.InterlocuteurObligatoire ?? false));
        if (!await donnees.ModifierAsync(u.Societe, u.Profil, cle, s, Semaine(s, r), Empreinte, ct))
            throw new RegleMetierException("L'en-tête a été modifié par un autre utilisateur depuis son ouverture : rechargez la commande.");
    }

    // Zones de l'en-tête comparées pour détecter une modification concurrente (solde compris)
    internal static string Empreinte(EnteteSaisie e) => string.Join("|",
        e.TypeFacturation, e.Designation1, e.Designation2, e.DateLivraison, e.SemaineLivraison, e.AnneeLivraison, e.AdresseArc,
        e.Interlocuteur, e.ReferenceCommandeClient, e.DateCommandeClient, e.CompteGeneralFacture, e.CompteClientFacture,
        e.Revendeur, e.Representant, e.TauxCommission, e.Fabricant, e.CodeVente, e.SousTraitee, e.EnlevementOuLivraison, e.Port,
        e.NbLieux, e.AdresseFacturation, e.ModeReglement, e.Echeance, e.Le, e.Arrete, e.Escompte, e.Releve, e.NomHonoraires,
        e.MontantHonoraires, e.TauxHonoraires, e.CommandesGroupees, e.TexteCommandesGroupees, e.EditionEtiquettes, e.Soldee,
        e.Bloquee, e.AvecLignes, e.TexteComplementaire, e.TexteSurArc, e.TexteSurBl, e.TexteSurFacture);

    // Codes en majuscules, textes sans blancs autour ; longueurs des zones AB001
    internal static EnteteSaisie Normaliser(EnteteSaisie saisie)
    {
        var s = saisie.Copier();
        static string Code(string? v, int n) => Coupe((v ?? "").Trim().ToUpperInvariant(), n);
        static string Texte(string? v, int n) => Coupe((v ?? "").Trim(), n);
        static string Coupe(string v, int n) => v.Length > n ? v[..n] : v;
        s.Enseigne = Code(s.Enseigne, 1);
        s.TypeCommande = Code(s.TypeCommande, 1);
        s.TypeFacturation = Code(s.TypeFacturation, 1);
        s.Designation1 = Texte(s.Designation1, 40);
        s.Designation2 = Texte(s.Designation2, 40);
        s.Interlocuteur = Texte(s.Interlocuteur, 39);
        s.ReferenceCommandeClient = Texte(s.ReferenceCommandeClient, 15);
        s.Fabricant = Code(s.Fabricant, 3);
        s.CodeVente = Code(s.CodeVente, 1);
        s.EnlevementOuLivraison = Code(s.EnlevementOuLivraison, 1);
        s.Port = Code(s.Port, 1);
        s.AdresseFacturation = Code(s.AdresseFacturation, 1);
        s.ModeReglement = Code(s.ModeReglement, 2);
        s.Releve = Code(s.Releve, 1);
        s.NomHonoraires = Texte(s.NomHonoraires, 12);
        s.TexteCommandesGroupees = Texte(s.TexteCommandesGroupees, 20);
        s.TexteComplementaire = string.Join("\n", LignesTexte(s.TexteComplementaire));
        if (s.EnlevementOuLivraison == "E")
        {
            s.Port = "";
            s.NbLieux = null;
            s.AdresseFacturation = "";
        }
        if (s.DateLivraison is not null)
        {
            s.SemaineLivraison = null;
            s.AnneeLivraison = null;
        }
        return s;
    }

    private static void Lever(IReadOnlyList<ErreurSaisie> erreurs)
    {
        if (erreurs.Count > 0) throw new SaisieInvalideException(erreurs);
    }
}

// Numérotation des commandes (GETDOSSIER, DOS005 POPT02 / POPT05, DOS505 R10-R11)
internal static class NumerotationCommande
{
    // Retour de GETDOSSIER : n° sur 5 positions, blanc, lettre-clé ; « -1 » cadré à droite en cas d'erreur
    public static (int Numero, string Cle) Decouper(string retour)
    {
        var r = retour.PadRight(7);
        if (r.Trim() == "-1" || !int.TryParse(r[..5], out var numero) || numero <= 0)
            throw new RegleMetierException($"Attribution du n° de dossier impossible (GETDOSSIER a répondu « {retour.Trim()} »).");
        return (numero, r[6..7].Trim());
    }

    // POPT05 « Forcer la clef de cde à 9 » ; POPT02 = 'N' : clé de GETDOSSIER ; sinon CDECLEF (programme spécifique, absent
    // des sources) dont le RPG retient '9' en cas d'échec : on retient '9'.
    public static string Cle(bool garderNumero, bool forcerCle9, string cleGetDossier) =>
        forcerCle9 || garderNumero || cleGetDossier == "" ? "9" : cleGetDossier;

    // Clé déjà prise pour ce n° : on essaie 1 à 9 puis 0 (comme le RPG) ; null si les dix sont prises
    public static string? CleLibre(string cle, IReadOnlySet<string> prises)
    {
        if (!prises.Contains(cle)) return cle;
        return "1234567890".Select(c => c.ToString()).FirstOrDefault(c => !prises.Contains(c));
    }

    // Facturation automatique du client (CDOPT2 de 1 à 8)
    public static bool FacturationAutomatique(string cdopt2) => cdopt2 is "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8";
}

// Messages de PRTMSG (NP11P) repris tels quels, sans l'identifiant en tête
internal static class Messages
{
    public const string Obligatoire = "Cette zone doit être renseignée.";                                        // GEN0001
    public const string ClientInexistant = "Ce compte client n'existe pas.";                                     // CPT0001
    public const string ClientCloture = "Ce client est clôturé.";                                                 // CLI0004
    public const string Qualite1 = "Attention : vous ne devez pas faire de dossier pour un client de qualité 1.";  // CPT0018
    public const string Qualite5 = "Attention : ce client est bloqué en livraison.";                              // CLI0005
    public const string Qualite234 = "Il est interdit de faire un dossier pour un client de qualité 2, 3 ou 4.";  // CPT0020
    public const string EnseigneInexistante = "Cette enseigne n'existe pas.";                                    // ENS0001
    public const string EnseigneCompteGeneral = "Le compte général est différent de celui de l'enseigne.";       // ENS0003
    public const string Type34 = "Cette zone doit être égale à 3 ou 4.";                                         // GEN0056
    public const string Facturation123 = "Cette zone doit être égale à 1, 2 ou 3.";                              // GEN0045
    public const string LivraisonObligatoire = "La date ou la semaine de livraison prévue est obligatoire.";     // DOS0014
    public const string SemaineInexistante = "Cette semaine n'existe pas.";                                      // DAT0010
    public const string DateIncorrecte = "Cette date est incorrecte.";                                           // DAT0001
    public const string AdresseInexistante = "Cette adresse n'existe pas.";                                      // APL0002
    public const string RepresentantInexistant = "Ce représentant n'existe pas.";                                // REP0001
    public const string FabricantInexistant = "Fabricant inexistant.";                                           // TAB0014
    public const string CodeLigneInexistant = "Ce code ligne n'existe pas.";                                     // FAC0045
    public const string CodeLigneVente = "Le code ligne doit être de type « Vente ».";                           // FAC0058
    public const string EnlevementOuLivraison = "Cette zone doit être égale à E ou A.";                          // DEV0006
    public const string Port = "Cette zone doit être égale à D, A ou F.";                                        // DEV0008
    public const string AdresseFacturation = "Le code adresse doit être égal à C, F, L ou R.";                   // DEV0032
    public const string ModeReglement = "Ce mode de règlement n'existe pas.";                                    // CPT0004
    public const string Echeance = "Le nombre de jours d'échéance est incorrect.";                               // CPT0009
    public const string Jour31 = "Le jour doit être inférieur ou égal à 31.";                                    // CPT0010
    public const string Releve = "Ce type de relevé n'existe pas.";                                              // CPT0003
}
