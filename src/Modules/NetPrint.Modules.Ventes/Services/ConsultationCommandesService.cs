using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Consultation des commandes : règles de DOS500S et de HistoriqueDossiers (Controle, représentant imposé, droits,
// valeur d'une ligne, état de la commande). Le SQL est dans Donnees/RequeteListeCommandes, l'affichage dans Ecrans/Commandes.
// =====================================================================
internal sealed class ConsultationCommandesService(ISourceCommandes donnees, IUtilisateurCourant utilisateur) : IConsultationCommandes
{
    // Programme dont les options PRT000P1 donnent les droits de la gestion des dossiers (menu PRTMEN → DOS500C)
    public const string ProgrammeDroits = "DOS500C";
    private const long TnumLeTout = 999999999;   // unité de vente « Le tout » (prix forfaitaire)

    public async Task<ContexteCommandes> LireContexteAsync(CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var impose = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var options = await donnees.OptionsProgrammeAsync(ProgrammeDroits, u.Profil, ct);
        return new(impose, Droits(options));
    }

    // PAUTO1 à 8 = AOPT01 à 08 : Total, Création, Modification, Suppression, Visualisation, Calcul, Édition, MAJ ; « ** » = interdit.
    // DOS500S : création et solde/réactivation = PAUTO2, modification = PAUTO3, suppression = PAUTO4, édition = PAUTO7.
    internal static DroitsCommandes Droits(IReadOnlyList<string>? options)
    {
        if (options is null) return DroitsCommandes.Tous;
        bool Permis(int n) => n >= options.Count || options[n] != "**";
        return new DroitsCommandes(Creer: Permis(1), Modifier: Permis(2), Supprimer: Permis(3), Editer: Permis(6));
    }

    // ------------------------------------------------------------------
    // Recherche
    // ------------------------------------------------------------------
    public async Task<ResultatRechercheCommandes> RechercherAsync(CriteresCommandes criteres, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var c = criteres.Copier();

        // Restriction commerciale : le représentant imposé remplace toujours la saisie (le WinDev grisait la zone)
        var impose = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        if (impose is not null) c.Representant = impose;
        if (c.CompteClient is > 0 && c.CompteGeneral is null or 0) c.CompteGeneral = u.CompteGeneral;

        Controler(c);

        var brut = await donnees.RechercherAsync(u.Societe, c, ct);
        var tronque = brut.Count > LimitesRecherche.Maximum;
        return new(Regrouper(tronque ? brut.Take(LimitesRecherche.Maximum) : brut), tronque);
    }

    // Procédure Controle de FEN_Historiquedossiers (messages repris, ponctuation harmonisée avec la consultation des devis)
    internal static void Controler(CriteresCommandes c)
    {
        // Ne comptent pas seuls : compte général, type, soldées, enseigne, format (comme le WinDev)
        var aucunCritere = c.Numero is null or 0 && c.CompteClient is null or 0 && c.Representant is null or 0
            && c.Devis is null or 0 && c.Produit is null or 0 && Vide(c.Article) && Vide(c.Indice)
            && Vide(c.ReferenceImprime) && Vide(c.ReferenceCommandeClient) && Vide(c.NumeroAppel) && Vide(c.Fabricant)
            && c.BonLivraison is null or 0 && c.Facture is null or 0 && Vide(c.Categorie) && Vide(c.SousCategorie)
            && Vide(c.Designation) && c.DateDu is null && c.DateAu is null && c.LivraisonDu is null && c.LivraisonAu is null;
        if (aucunCritere) throw new RegleMetierException("Vous devez renseigner au moins un critère.");
        if (c.Produit is > 0 && c.CompteClient is null or 0)
            throw new RegleMetierException("Le client doit être renseigné.");
        if (!Vide(c.SousCategorie) && Vide(c.Categorie))
            throw new RegleMetierException("La catégorie doit être renseignée.");
        if (!Vide(c.Indice) && Vide(c.Article))
            throw new RegleMetierException("L'article doit être renseigné.");
        if (!Vide(c.Cle) && c.Numero is null or 0)
            throw new RegleMetierException("Le n° de commande doit être renseigné.");
        if (c.DateDu is { } du && c.DateAu is { } au && du > au)
            throw new RegleMetierException("La date de début doit être inférieure ou égale à la date de fin.");
        if (c.LivraisonDu is { } ldu && c.LivraisonAu is { } lau && ldu > lau)
            throw new RegleMetierException("La date de livraison de début doit être inférieure ou égale à celle de fin.");
    }

    // ------------------------------------------------------------------
    // Fiche
    // ------------------------------------------------------------------
    public async Task<FicheCommande?> LireAsync(CleCommande cle, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        // Représentant limité : une commande d'un autre représentant n'est pas visible (DOS500S R6, FEN_commandes) ;
        // le filtre est dans la requête, rien d'autre n'est lu pour une commande interdite
        var impose = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var brut = await donnees.LireFicheAsync(u.Societe, cle with { Cle = cle.Cle.Trim().ToUpperInvariant() }, impose, ct);
        if (brut is null) return null;

        var droits = Droits(await donnees.OptionsProgrammeAsync(ProgrammeDroits, u.Profil, ct));
        return VersFiche(brut, droits);
    }

    internal static FicheCommande VersFiche(FicheBrute brut, DroitsCommandes droits)
    {
        var e = brut.Entete;
        var c = e.Commune;
        var cle = new CleCommande(c.Rdoss, c.Ccdcle);
        var lignes = brut.Lignes.Select(l => VersLigneFiche(cle, l.Ligne, l.Complement)).ToList();
        var soldee = c.Cctags != "";
        var factureAutre = e.Cclif != 0 && (e.Cgenf != c.Rgen || e.Cclif != c.Rcli);

        var entete = new EnteteCommande(
            Cle: cle,
            TypeCommande: c.Cctypc,
            TypeFacturation: TypesFacturation.Libelle(e.Ccfact),
            Client: $"{c.Rgen} {c.Rcli}",
            NomClient: c.Cnom,
            ClientFacture: factureAutre ? $"{e.Cgenf} {e.Cclif} {e.NomFactureEntete}".Trim() : "",
            Enseigne: c.Ccense,
            Designation1: c.Ccdes1,
            Designation2: c.Ccdes2,
            ReferenceCommandeClient: c.Ccrefc,
            Interlocuteur: e.Ccnint,
            DateOuverture: c.DateOuverture,
            DateCommandeClient: c.DateCommandeClient,
            DateLivraison: c.DateLivraisonEntete,
            SemaineLivraison: e.Ccdtss != 0 ? $"S{e.Ccdtss:00}/{e.Ccdtsa}" : "",
            Representant: c.Ccrep != 0 ? $"{c.Ccrep} {c.NomRepresentant}".Trim() : "",
            TauxCommission: e.Cctxco,
            Fabricant: c.Ccinat,
            Categorie: e.Ccat,
            SousCategorie: e.Ccscat,
            Profil: e.Ccprof,
            Livraison: e.Ccecal switch { "E" => "Enlèvement par le client", "A" => "À livrer", _ => e.Ccecal },
            Port: e.Ccport switch { "D" => "Port dû", "A" => "Port avancé", "F" => "Franco", _ => e.Ccport },
            Reglement: Reglement(e),
            Devise: e.Cccdev,
            CodeVente: e.Cctypv,
            TauxRemise: e.Ctxrm,
            Revendeur: e.Ccrcvd == "O",
            AdresseArc: e.Ccnaea,
            NonModifiable: e.Ccop11 == "O",
            SousTraitee: e.Ccop10 == "O",
            DateSolde: soldee ? c.DateSolde : null,
            Etat: Etat(soldee, lignes.Select(l => l.Ligne).ToList()),
            Valeur: lignes.Sum(l => l.Ligne.Valeur));

        return new FicheCommande(entete, lignes, brut.Livraisons, brut.Factures, brut.Journal, brut.Historique,
            c.NbDocuments, droits, brut.JournalTronque, brut.HistoriqueTronque);
    }

    // Mode de règlement (table RG) et échéance : « 30 jours le 10, arrêté au 31 »
    internal static string Reglement(ComplementEnteteBrut e)
    {
        var mode = e.LibReglement != "" ? e.LibReglement : e.Ccmod;
        var echeance = new List<string>();
        if (e.Ccech > 0) echeance.Add($"{e.Ccech} jours");
        if (e.Ccle > 0) echeance.Add($"le {e.Ccle}");
        if (e.Ccart > 0) echeance.Add($"arrêté au {e.Ccart}");
        return echeance.Count == 0 ? mode : $"{mode} · {string.Join(", ", echeance)}".Trim(' ', '·');
    }

    private static LigneFiche VersLigneFiche(CleCommande cle, LigneCommandeBrute l, ComplementLigneBrut x)
    {
        var adresse = string.Join(" ", new[] { x.Dnrs, x.Dnpost, x.Dnvill }.Where(s => s != ""));
        if (adresse == "" && x.Dnliga != 0) adresse = $"Adresse n° {x.Dnliga}";
        return new LigneFiche(
            Ligne: VersLigne(cle, l),
            AdresseLivraison: adresse,
            DateConfirmee: x.Dndatb != 0 ? BaseDb2.DateAAAAMMJJ(x.Dndatb) : null,
            Commentaires: string.Join("\n", new[] { x.Dncomm, x.Dncom2, x.Dncom3 }.Where(s => s != "")),
            TauxCommission: x.Dntxco,
            TauxRemise: x.Dntxre,
            LigneOrigine: l.Dnlori,
            QuantiteAppelee: x.Dnqtea,
            MaxiLivrable: x.Dnqmax,
            PrixCession: x.Dnpxce,
            Devis: x.Dndev != 0 ? $"{x.Dndev:00000}/{x.Dnvers}/{x.Dnhypo}" : "",
            ClientFacture: x.Dnclif != 0 ? $"{x.Dngenf} {x.Dnclif} {x.NomFactureLigne}".Trim() : "",
            Emplacement: x.Demp);
    }

    // ------------------------------------------------------------------
    // Mise en forme : lignes regroupées par commande, dans l'ordre de lecture
    // ------------------------------------------------------------------
    internal static List<CommandeListe> Regrouper(IEnumerable<LigneCommandeBrute> brut)
    {
        var commandes = new List<CommandeListe>();
        foreach (var groupe in brut.GroupBy(l => (l.Rdoss, l.Ccdcle)))
        {
            var e = groupe.First();
            var cle = new CleCommande(e.Rdoss, e.Ccdcle);
            var lignes = groupe.Where(l => l.Dnstat != "").Select(l => VersLigne(cle, l)).ToList();
            var soldee = e.Cctags != "";
            var actives = lignes.Where(l => !l.Soldee).ToList();
            var livraison = actives.Where(l => !l.ALaDemande && l.DateLivraison is not null).Min(l => l.DateLivraison)
                ?? e.DateLivraisonEntete;

            commandes.Add(new CommandeListe(
                Cle: cle,
                TypeCommande: e.Cctypc,
                Client: $"{e.Rgen} {e.Rcli}",
                NomClient: e.Cnom,
                ClientFacture: e.NomFacture,
                ReferenceCommandeClient: e.Ccrefc,
                Designation: e.Ccdes2 != "" ? $"{e.Ccdes1} {e.Ccdes2}" : e.Ccdes1,
                DateOuverture: e.DateOuverture,
                DateCommandeClient: e.DateCommandeClient,
                DateLivraison: livraison,
                DateSolde: soldee ? e.DateSolde : null,
                Representant: e.Ccrep != 0 ? $"{e.Ccrep} {e.NomRepresentant}".Trim() : "",
                Fabricant: e.Ccinat,
                Enseigne: e.Ccense,
                Etat: Etat(soldee, lignes),
                Valeur: lignes.Sum(l => l.Valeur),
                NbDocuments: e.NbDocuments,
                Lignes: lignes));
        }
        return commandes;
    }

    // Soldée (CCTAGS) ; sinon d'après les lignes fermes non soldées : toutes livrées, en partie, ou pas encore.
    // Les lignes « à la demande » (statut 3) sont livrées par leurs appels : elles ne comptent pas.
    internal static EtatCommande Etat(bool soldee, IReadOnlyList<LigneCommandeListe> lignes)
    {
        if (soldee) return EtatCommande.Soldee;
        var fermes = lignes.Where(l => !l.Soldee && !l.ALaDemande && l.Quantite > 0).ToList();
        if (fermes.Count == 0) return EtatCommande.EnCours;
        if (fermes.All(l => l.QuantiteLivree >= l.Quantite)) return EtatCommande.Livree;
        return fermes.Any(l => l.QuantiteLivree > 0) ? EtatCommande.PartiellementLivree : EtatCommande.EnCours;
    }

    private static LigneCommandeListe VersLigne(CleCommande cle, LigneCommandeBrute l)
    {
        var gratuite = l.Dnopt2 == "O";
        return new LigneCommandeListe(
            Commande: cle,
            NumeroLigne: l.Dnnlig,
            Article: l.Dnart,
            Indice: l.Dnind,
            ReferenceImprime: l.Arfimp,
            Designation: l.Dndesa,
            Texte: l.Texte,
            ALaDemande: l.Dnstat == "3",
            EstAppel: l.Dnstat == "1" && l.Dnlori != 0,
            NumeroAppel: l.Dnappe,
            DateLivraison: l.Dndliv != 0 ? BaseDb2.DateAAAAMMJJ(l.Dndliv) : null,
            Quantite: l.Dnqtec,
            QuantiteLivree: l.Dnqtel,
            QuantiteFacturee: l.Dnqtef,
            // MAJ520 ne recalcule le reste (DNQTER) que sur les lignes actives : une ligne soldée n'a plus rien à livrer
            ResteALivrer: l.Dnsold == "O" ? 0 : l.Dnqter,
            PrixUnitaire: l.Dnpus,
            Unite: l.Dnuv,
            Gratuite: gratuite,
            PrixADefinir: l.Dnopt5 == "O",
            Valeur: gratuite ? 0 : Valeur(l.Dnpus, l.Dnqtec, l.Tnum),
            Norme: l.Dnnorm,
            Produit: l.Amodel,
            Categorie: l.Afam,
            SousCategorie: l.Asfam,
            Soldee: l.Dnsold == "O",
            NbCommentaires: l.Dnccom);
    }

    // Valeur d'une ligne (colonne « Valeur » de HistoriqueDossiers) : prix × quantité / diviseur de l'unité de vente ;
    // « Le tout » (TNUM 999999999) ou unité sans diviseur : le prix est celui de la ligne entière.
    // Une ligne sans quantité (ligne « à la demande » entièrement appelée) ne vaut rien, même « Le tout ».
    internal static decimal Valeur(decimal prix, int quantite, long tnum) =>
        quantite == 0 ? 0 : tnum is TnumLeTout or <= 0 ? prix : Math.Round(prix * quantite / tnum, 2, MidpointRounding.AwayFromZero);

    private static bool Vide(string? s) => string.IsNullOrWhiteSpace(s);
}
