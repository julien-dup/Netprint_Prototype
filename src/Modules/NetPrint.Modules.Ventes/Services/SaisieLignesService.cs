using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using A = NetPrint.Modules.Ventes.Contrats.AvertissementsLigne;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Saisie des lignes de commande : valeurs par défaut (NEG520), contrôles et prix (CTL520), écriture (MAJ520).
// Règles relues dans le source (rapport NEG520-lignes). Écarts volontaires :
//   - un prix saisi par l'utilisateur n'est jamais remplacé par le tarif (le RPG l'écrasait si la cession était vide) ;
//   - chaque avertissement forçable est accepté explicitement (le RPG passait à la 2e Entrée et masquait ensuite
//     les contrôles de date pour toute la saisie) ;
//   - une ligne est enregistrée seule, en transaction, n° de ligne pris en base ; réservations recalculées (STOCK03)
//     pour l'article touché seulement ;
//   - non repris pour l'instant : articles libres « 999… », articles avec nomenclature (lignes composants),
//     contrôle de la norme (NOR010), reprise de devis, import Excel.
// =====================================================================
internal sealed class SaisieLignesService(LignesCommandeDb2 donnees, ISourceCommandes commandes, IUtilisateurCourant utilisateur)
    : ISaisieLignesCommande
{
    private static DateOnly Aujourdhui => DateOnly.FromDateTime(DateTime.Today);

    private async Task<(UtilisateurConnecte U, ContexteLigne C)> ContexteAsync(CleCommande cle, bool ecriture, CancellationToken ct,
        bool soldeeAdmise = false)
    {
        var u = await utilisateur.ExigerAsync();
        var impose = await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var c = await donnees.ContexteAsync(u.Societe, cle, impose, ct)
            ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        if (ecriture)
        {
            var droits = ConsultationCommandesService.Droits(
                await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
            if (!droits.Modifier) throw new RegleMetierException("Vous n'êtes pas autorisé à modifier les commandes.");
            if (c.Soldee && !soldeeAdmise) throw new RegleMetierException("Option impossible, dossier soldé.");
            if (c.NonModifiable) throw new RegleMetierException("Commande sous-traitée. Modification impossible.");
        }
        return (u, c);
    }

    // ------------------------------------------------------------------
    public async Task<LigneSaisie> NouvelleAsync(CleCommande commande, CancellationToken ct = default)
    {
        var (_, c) = await ContexteAsync(commande, false, ct);
        return Nouvelle(c);
    }

    // Valeurs d'une nouvelle ligne (F6 de NEG520)
    internal static LigneSaisie Nouvelle(ContexteLigne c)
    {
        var l = new LigneSaisie { CodeVente = c.CodeVente, Statut = StatutsLigne.Normal };
        if (c.Enlevement == "E") l.AdresseRaisonSociale = "Enlevement client";
        else if (c.AdresseParDefaut != 0) l.NumeroAdresse = c.AdresseParDefaut;
        if (c.LivraisonDeLEnTete) l.DateLivraison = c.DateLivraison;
        return l;
    }

    public async Task<LigneSaisie?> LireAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(commande, false, ct);
        var l = await donnees.LigneAsync(u.Societe, commande, numeroLigne, ct);
        if (l is not null) l.Empreinte = Empreinte(l);
        return l;
    }

    // ------------------------------------------------------------------
    // Valeurs de l'article, prix, montant, commission, maxi livrable
    // ------------------------------------------------------------------
    public async Task<LigneSaisie> CompleterAsync(CleCommande commande, LigneSaisie ligne, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, false, ct);
        var l = Normaliser(ligne);
        if (l.Article == "") return l;
        var a = await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct);
        if (a is null) return l;
        var tarif = await donnees.DonneesTarifAsync(u.Societe, l.Article, l.Indice, c.CompteGeneral, c.CompteClient, c.Representant,
            Aujourdhui, ct);
        long? tnum = null;
        if (l.Unite != "" || a.Unite != "")
            tnum = (await donnees.VerifierAsync(u.Societe, commande, c, l.Unite == "" ? Avec(l, x => x.Unite = a.Unite) : l, null, ct)).TnumUnite;
        return Completer(c, l, a, tarif, tnum, Aujourdhui);
    }

    internal static LigneSaisie Completer(ContexteLigne c, LigneSaisie ligne, ArticleLigne a, MoteurPrixCommande.DonneesTarif? tarif,
        long? tnum, DateOnly aujourdhui)
    {
        var l = ligne.Copier();
        var nouvelArticle = l.Article != l.ArticleEnregistre || l.Indice != l.IndiceEnregistre;
        if (l.Designation == "" || nouvelArticle && l.NumeroLigne == 0) l.Designation = a.Designation;
        if (l.Unite == "" || nouvelArticle) l.Unite = a.Unite;
        if (l.Norme == "") l.Norme = a.Norme;
        if (l.Emplacement == "" && l.NumeroLigne == 0) l.Emplacement = a.Emplacement;
        if (l.Texte == "" && l.NumeroLigne == 0) l.Texte = string.Join("\n", a.Textes).TrimEnd('\n');
        l.Emballage = a.Emballage;
        l.SaisieEnUc = a.SaisieEnUc;

        // Quantités en UC (CTL520 contrôle 17)
        if (a.SaisieEnUc && a.Emballage > 0)
        {
            if (l.Quantite is null or 0 && l.QuantiteUc is > 0) l.Quantite = l.QuantiteUc * a.Emballage;
            else if (l.QuantiteUc is null or 0 && l.Quantite is > 0)
            {
                l.QuantiteUc = (int)Math.Ceiling((decimal)l.Quantite.Value / a.Emballage);
                l.Quantite = l.QuantiteUc * a.Emballage;
            }
        }

        // Prix : tarif proposé tant que l'utilisateur n'a pas saisi de prix
        if (!l.PrixSaisi && !l.Gratuite && !l.PrixADefinir && tarif is not null)
        {
            var r = MoteurPrixCommande.Tarif(tarif, l.Quantite ?? 0, aujourdhui, l.TauxCommission ?? c.TauxCommission);
            if (r.Trouve)
            {
                l.PrixBrut = r.Prix;
                if (r.Cession != 0) l.PrixCession = r.Cession;
                if (r.Unite is { } uv) l.Unite = uv;
            }
            l.SourcePrix = r.Source;
        }
        if (l.Gratuite || l.PrixADefinir) l.PrixBrut = 0;
        if (l.PrixCession is null or 0)
        {
            var cession = c.PrixCessionObligatoire ? a.PrixCession : a.PrixRevient;
            if (cession > 0) l.PrixCession = cession;
        }

        l.PrixNet = MoteurPrixCommande.PrixNet(l.PrixBrut ?? 0, l.Remise ?? 0);
        l.Montant = tnum is null ? 0 : MoteurPrixCommande.Montant(l.PrixNet, l.Quantite ?? 0, tnum.Value);

        // Commission (SPRCTC) si non saisie
        if (l.TauxCommission is null)
        {
            var (seuils, taux) = MoteurPrixCommande.TableCommission(c.SeuilsCommission, c.TauxCommissions);
            l.TauxCommission = MoteurPrixCommande.Commission(l.PrixNet, l.PrixCession ?? 0, c.TypeCommission, seuils, taux, c.TauxCommission);
        }

        // Maxi livrable : paramètre DOS009, client non exclu, ligne nouvelle ou « à la demande », non saisi
        if (c.CalculMaxi && !c.SansMaxi && l.MaxiLivrable is null or 0 && l.Quantite is > 0
            && (l.NumeroLigne == 0 || l.Statut == StatutsLigne.ALaDemande))
            l.MaxiLivrable = MoteurPrixCommande.MaxiLivrable(l.Quantite.Value, c.MaxiClient, c.MaxiSociete);
        return l;
    }

    // ------------------------------------------------------------------
    // Contrôles de CTL520 (dans l'ordre) : erreurs bloquantes, puis avertissements à accepter
    // ------------------------------------------------------------------
    internal static (List<ErreurSaisie> Erreurs, List<ErreurSaisie> Avertissements) Controler(
        ContexteLigne c, LigneSaisie l, ArticleLigne? a, VerificationsLigne v, LigneSaisie? ancienne, DateOnly aujourdhui)
    {
        var erreurs = new List<ErreurSaisie>();
        var avertissements = new List<ErreurSaisie>();
        void E(bool condition, string zone, string message)
        {
            if (condition && erreurs.All(x => x.Zone != zone)) erreurs.Add(new(zone, message));
        }
        void W(bool condition, string code, string message)
        {
            if (condition && !l.AvertissementsAcceptes.Contains(code)) avertissements.Add(new(code, message));
        }
        var creation = l.NumeroLigne == 0;

        // Code vente (FAC020)
        E(l.CodeVente == "", nameof(l.CodeVente), MessagesLigne.Obligatoire);
        E(l.CodeVente != "" && v.TypeCodeVente is null, nameof(l.CodeVente), MessagesLigne.CodeLigneInexistant);
        E(v.TypeCodeVente == "T", nameof(l.CodeVente), MessagesLigne.CodeLigneTexte);
        E(v.TypeCodeVente == "R", nameof(l.CodeVente), MessagesLigne.TypeLigneNonAutorise);
        E(v.TypeCodeVente is "E" or "P", nameof(l.CodeVente), "Les lignes de frais (codes E et P) ne sont pas encore reprises : saisissez-les dans le 5250.");
        E(l.PrixADefinir && (l.PrixBrut ?? 0) != 0, nameof(l.PrixADefinir), MessagesLigne.PrixADefinir);

        // Article
        E(l.Article == "", nameof(l.Article), MessagesLigne.Obligatoire);
        E(l.Article.Length > 0 && l.Article.Trim('9') == "", nameof(l.Article), "Les articles libres (999…) ne sont pas encore repris : saisissez-les dans le 5250.");
        if (l.Article != "" && a is null) E(true, nameof(l.Article), MessagesLigne.ArticleInexistant);
        if (a is not null)
        {
            E(a.Remplacement == "C", nameof(l.Article), MessagesLigne.ArticleCloture);
            W(creation && a.Remplacement == "O", A.ArticleRemplace, MessagesLigne.ArticleRemplace);
            W(creation && a.Type == "2" && c.TypeFacturation is "2" or "3" && (a.CompteGeneral != c.CompteGeneral || a.CompteClient != c.CompteClient),
                A.ArticleAutreClient, MessagesLigne.ArticleAutreClient);
            E(c.TypeCommande == TypesCommande.Negoce && a.Type != "1" || c.TypeCommande == TypesCommande.ProduitFini && a.Type != "2",
                nameof(l.Article), MessagesLigne.TypeArticle);
            E(c.TypeCommande == TypesCommande.ProduitFini && a.TypeFacturation != c.TypeFacturation, nameof(l.Article), MessagesLigne.FacturationArticle);
            E(v.ReferenceFournisseurManquante, nameof(l.Article), MessagesLigne.ReferenceFournisseur);
            E(a.Nomenclature && creation, nameof(l.Article), "Les articles avec nomenclature ne sont pas encore repris : saisissez-les dans le 5250.");

            // Stock disponible (forçable)
            if (v.Stock is { } s)
            {
                var dispo = s.Entree - s.Reserve - (l.Quantite ?? 0) + (ancienne?.Quantite ?? 0);
                W(dispo < 0, A.StockInsuffisant, $"Disponible insuffisant ({s.Entree - s.Reserve + (ancienne?.Quantite ?? 0):N0}).");
            }

            // Quantités et conditionnement
            if (a.SaisieEnUc && a.Emballage > 0 && l.Quantite is > 0 && l.QuantiteUc is > 0)
                E(l.Quantite.Value / a.Emballage != l.QuantiteUc, nameof(l.QuantiteUc), MessagesLigne.IncoherenceUc);
            E(a.TarifParEmballage && a.Emballage == 0 && l.SourcePrix.StartsWith("Tranche"), nameof(l.Quantite), MessagesLigne.ConditionnementManquant);
            if (!a.FractionnableCommande && a.Emballage > 0 && l.Quantite is > 0 && l.Quantite % a.Emballage != 0)
                W(true, A.QuantiteArrondie, $"Article non fractionnable à la commande : quantité à arrondir à {(l.Quantite + a.Emballage - 1) / a.Emballage * a.Emballage:N0}.");
        }
        var quantiteObligatoire = l.Statut == StatutsLigne.Normal || creation
            || ancienne is { QuantiteFacturee: 0 } && (ancienne.Quantite ?? 0) > 0;
        E(quantiteObligatoire && l.Quantite is not > 0, nameof(l.Quantite), MessagesLigne.Obligatoire);
        W(ancienne is not null && l.Quantite != ancienne.Quantite && ancienne.QuantiteLivree > (l.Quantite ?? 0),
            A.QuantiteSousLivre, MessagesLigne.QuantiteSousLivre);

        // Unité et prix
        E(l.Unite == "", nameof(l.Unite), MessagesLigne.Obligatoire);
        E(l.Unite != "" && !v.UniteExiste, nameof(l.Unite), MessagesLigne.UniteInexistante);
        E(!l.Gratuite && !l.PrixADefinir && (l.PrixBrut ?? 0) <= 0, nameof(l.PrixBrut), MessagesLigne.Obligatoire);
        E(l.Gratuite && l.PrixADefinir, nameof(l.Gratuite), MessagesLigne.PrixADefinir);
        E(c.NouvelleApplication && l.NouvelleApplication is not ("O" or "N"), nameof(l.NouvelleApplication), MessagesLigne.OuiNon);
        E(c.PrixCessionObligatoire && (l.PrixCession ?? 0) <= 0, nameof(l.PrixCession), MessagesLigne.Obligatoire);
        E(l.Remise is < 0 or >= 100, nameof(l.Remise), "La remise doit être comprise entre 0 et 99 %.");

        // Dates (SPDATE) : obligatoire, puis calendrier, date passée, jour travaillé, jour autorisé par le client
        E(l.DateLivraison is null, nameof(l.DateLivraison), MessagesLigne.Obligatoire);
        if (l.DateLivraison is { } d && (ancienne?.DateLivraison != d))
        {
            W(!v.DateAuCalendrier, A.DateHorsCalendrier, MessagesLigne.DateHorsCalendrier);
            W(d < aujourdhui, A.DatePassee, MessagesLigne.DatePassee);
            W(v.DateAuCalendrier && v.TypeJour != "T", A.JourNonTravaille, MessagesLigne.JourNonTravaille);
            W(l.Statut == StatutsLigne.Normal && l.DateConfirmee is null && v.JoursAutorises is { Length: 6 } jours
                && v.JourSemaine is >= 1 and <= 6 && jours[v.JourSemaine - 1] != 'O', A.JourNonAutorise, MessagesLigne.JourNonAutorise);
        }
        E(l.Statut == StatutsLigne.ALaDemande && l.DateConfirmee is not null, nameof(l.DateConfirmee), MessagesLigne.NePasRenseigner);

        // Ligne d'un ordre de préparation non livré : ni quantité, ni date, ni adresse
        if (v.OprNonLivre is { } opr && ancienne is not null)
        {
            E(l.Quantite != ancienne.Quantite, nameof(l.Quantite), $"La ligne fait partie de l'OPR n° {opr}.");
            E(l.DateLivraison != ancienne.DateLivraison, nameof(l.DateLivraison), $"La ligne fait partie de l'OPR n° {opr}.");
        }

        // Adresse
        var adresseLibreVide = l.AdresseRaisonSociale == "" || l.AdresseRue1 == "" && l.AdresseVille == "";
        E(l.Statut == StatutsLigne.Normal && !l.AdresseSiege && l.NumeroAdresse is not > 0 && adresseLibreVide,
            nameof(l.NumeroAdresse), MessagesLigne.AdresseObligatoire);
        E(l.AdresseSiege && (l.NumeroAdresse is > 0 || l.AdresseRue1 != "" || l.AdresseVille != ""), nameof(l.NumeroAdresse), MessagesLigne.AdresseObligatoire);
        E(l.NumeroAdresse is > 0 && !v.AdresseExiste, nameof(l.NumeroAdresse), MessagesLigne.AdresseInexistante);

        // Statut et appels
        E(l.Statut is not (StatutsLigne.Normal or StatutsLigne.ALaDemande), nameof(l.Statut), MessagesLigne.Statut);
        E(ancienne is { Statut: StatutsLigne.ALaDemande } && l.Statut == StatutsLigne.Normal && v.AppelsExistent,
            nameof(l.Statut), MessagesLigne.AppelsExistent);
        E(l.Statut == StatutsLigne.Normal && l.NumeroAppel == "" && v.ClientEdi, nameof(l.NumeroAppel), MessagesLigne.NumeroAppelEdi);

        // Maxi livrable multiple du conditionnement (forçable)
        if (a is not null && (!a.FractionnableLivraison || a.SaisieEnUc) && l.Emballage > 0 && l.MaxiLivrable is > 0)
            W(l.MaxiLivrable % l.Emballage != 0, A.MaxiNonMultiple, MessagesLigne.MaxiMultiple);

        E(l.CompteClientFacture is > 0 && !v.ClientFactureExiste, nameof(l.CompteClientFacture), MessagesLigne.ClientInexistant);
        E(!v.EmplacementExiste, nameof(l.Emplacement), MessagesLigne.EmplacementInexistant);
        return (erreurs, avertissements);
    }

    // ------------------------------------------------------------------
    public async Task<int> EnregistrerAsync(CleCommande commande, LigneSaisie ligne, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, true, ct);
        var l = Normaliser(ligne);
        var a = l.Article == "" ? null : await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct);
        var ancienne = l.NumeroLigne > 0 ? await donnees.LigneAsync(u.Societe, commande, l.NumeroLigne, ct) : null;
        if (l.NumeroLigne > 0 && ancienne is null) throw new RegleMetierException("Cette ligne n'existe plus.");
        if (ancienne is { Soldee: true }) throw new RegleMetierException("Cette ligne est soldée : réactivez-la avant de la modifier.");
        if (a is not null)
        {
            var tarif = await donnees.DonneesTarifAsync(u.Societe, l.Article, l.Indice, c.CompteGeneral, c.CompteClient, c.Representant, Aujourdhui, ct);
            var tnum0 = (await donnees.VerifierAsync(u.Societe, commande, c, l.Unite == "" ? Avec(l, x => x.Unite = a.Unite) : l, null, ct)).TnumUnite;
            l = Completer(c, l, a, tarif, tnum0, Aujourdhui);
        }
        var v = await donnees.VerifierAsync(u.Societe, commande, c, l, a, ct);
        var (erreurs, avertissements) = Controler(c, l, a, v, ancienne, Aujourdhui);
        if (erreurs.Count > 0) throw new SaisieInvalideException(erreurs);
        if (avertissements.Count > 0) throw new SaisieInvalideException(avertissements) { Forcable = true };

        // Arrondi d'emballage accepté : la quantité est arrondie (2e Entrée du 5250)
        if (l.AvertissementsAcceptes.Contains(A.QuantiteArrondie) && a is { FractionnableCommande: false, Emballage: > 0 } && l.Quantite is > 0)
            l.Quantite = (l.Quantite + a.Emballage - 1) / a.Emballage * a.Emballage;

        var numero = await donnees.EnregistrerAsync(u.Societe, u.Profil, commande, c, l, v.TnumUnite ?? 0, EmpreinteEcriture, ct);
        await RecalculerStocksAsync(u, c, a, l.Article, l.Indice, ancienne, ct);
        return numero;
    }

    public async Task SupprimerAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, true, ct);
        var l = await donnees.LigneAsync(u.Societe, commande, numeroLigne, ct) ?? throw new RegleMetierException("Cette ligne n'existe plus.");
        var v = await donnees.VerifierAsync(u.Societe, commande, c, l, null, ct);
        if (RefusSuppression(l, v) is { } refus) throw new RegleMetierException(refus);
        await donnees.SupprimerAsync(u.Societe, u.Profil, commande, c, numeroLigne, ct);
        var a = await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct);
        await RecalculerStocksAsync(u, c, a, l.Article, l.Indice, null, ct);
    }

    // Option S de NEG520 (SPCTL) : ligne « à la demande » avec appels, soldée, livrée ou facturée, dans un OPR non livré
    internal static string? RefusSuppression(LigneSaisie l, VerificationsLigne v) =>
        l.Statut == StatutsLigne.ALaDemande && v.AppelsExistent ? "Suppression impossible : il existe des appels de livraison."
        : l.Soldee ? "Suppression impossible : la ligne est soldée."
        : l.QuantiteLivree != 0 || l.QuantiteFacturee != 0 ? "Suppression impossible : la ligne est livrée ou facturée."
        : v.OprNonLivre is { } opr ? $"La ligne fait partie de l'OPR n° {opr}."
        : null;

    public async Task SolderAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, true, ct);
        var l = await donnees.LigneAsync(u.Societe, commande, numeroLigne, ct) ?? throw new RegleMetierException("Cette ligne n'existe plus.");
        if (l.Soldee) throw new RegleMetierException("Cette ligne est déjà soldée.");
        await donnees.SolderAsync(u.Societe, u.Profil, commande, c, numeroLigne, true, ct);
        await RecalculerStocksAsync(u, c, await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct), l.Article, l.Indice, null, ct);
    }

    // ÉCART : réactiver une ligne réactive aussi la commande soldée (le RPG ne revenait jamais sur le solde automatique)
    public async Task ReactiverAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, true, ct, soldeeAdmise: true);
        var l = await donnees.LigneAsync(u.Societe, commande, numeroLigne, ct) ?? throw new RegleMetierException("Cette ligne n'existe plus.");
        if (!l.Soldee) throw new RegleMetierException("Cette ligne n'est pas soldée.");
        var a = await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct);
        if (a?.Remplacement == "C") throw new RegleMetierException(MessagesLigne.ArticleCloture);
        await donnees.SolderAsync(u.Societe, u.Profil, commande, c, numeroLigne, false, ct);
        await RecalculerStocksAsync(u, c, a, l.Article, l.Indice, null, ct);
    }

    // STOCK03 pour l'ancien et le nouvel article gérés en stock ; après la validation, sans bloquer l'utilisateur
    private async Task RecalculerStocksAsync(UtilisateurConnecte u, ContexteLigne c, ArticleLigne? a, string article, string indice,
        LigneSaisie? ancienne, CancellationToken ct)
    {
        var articles = new List<(ArticleLigne Art, string Code, string Ind)>();
        if (a is not null) articles.Add((a, article, indice));
        if (ancienne is not null && (ancienne.Article != article || ancienne.Indice != indice)
            && await donnees.ArticleAsync(u.Societe, ancienne.Article, ancienne.Indice, ct) is { } aa)
            articles.Add((aa, ancienne.Article, ancienne.Indice));
        foreach (var (art, code, ind) in articles)
        {
            if (LignesCommandeDb2.TypeStock(art, c.TypesStock) is not { } type) continue;
            try
            {
                await donnees.RecalculerStockAsync(u.Societe, type, c.Depot, code, ind, CancellationToken.None);
            }
            catch (Exception)
            {
                // Réservation recalculée au prochain passage de STOCK03 ; la ligne est enregistrée
            }
        }
    }

    // ------------------------------------------------------------------
    // Zones comparées pour détecter une modification concurrente de la ligne
    internal static string EmpreinteEcriture(LigneSaisie l) => string.Join("|",
        l.Article, l.Indice, l.Quantite, l.PrixNet, l.Unite, l.PrixCession ?? 0, l.Statut, l.QuantiteLivree, l.QuantiteFacturee,
        l.Soldee, l.CodeVente, l.Designation, l.PrixBrut ?? 0, l.Remise ?? 0);

    internal static string Empreinte(LigneSaisie l) => EmpreinteEcriture(l);

    internal static LigneSaisie Normaliser(LigneSaisie ligne)
    {
        var l = ligne.Copier();
        static string Code(string? v, int n) => Coupe((v ?? "").Trim().ToUpperInvariant(), n);
        static string Texte(string? v, int n) => Coupe((v ?? "").Trim(), n);
        static string Coupe(string v, int n) => v.Length > n ? v[..n] : v;
        l.CodeVente = Code(l.CodeVente, 1);
        l.Article = Code(l.Article, 15);
        l.Indice = Code(l.Indice, 2);
        l.Designation = Texte(l.Designation, 40);
        l.Unite = Code(l.Unite, 2);
        l.AdresseRaisonSociale = Texte(l.AdresseRaisonSociale, 34);
        l.AdresseRue1 = Texte(l.AdresseRue1, 38);
        l.AdresseRue2 = Texte(l.AdresseRue2, 38);
        l.AdresseCodePostal = Texte(l.AdresseCodePostal, 10);
        l.AdresseVille = Texte(l.AdresseVille, 38);
        l.AdressePays = Code(l.AdressePays, 3);
        l.NumeroAppel = Texte(l.NumeroAppel, 15);
        l.Commentaire1 = Texte(l.Commentaire1, 40);
        l.Commentaire2 = Texte(l.Commentaire2, 40);
        l.Commentaire3 = Texte(l.Commentaire3, 40);
        l.Norme = Code(l.Norme, 4);
        l.Emplacement = Code(l.Emplacement, 8);
        l.NouvelleApplication = Code(l.NouvelleApplication, 1);
        if (l.AdresseSiege)
        {
            l.NumeroAdresse = null;
            l.AdresseRaisonSociale = l.AdresseRue1 = l.AdresseRue2 = l.AdresseCodePostal = l.AdresseVille = l.AdressePays = "";
        }
        return l;
    }

    private static LigneSaisie Avec(LigneSaisie l, Action<LigneSaisie> modifier)
    {
        var c = l.Copier();
        modifier(c);
        return c;
    }
}

// Messages de PRTMSG (NP11P) utilisés par CTL520, sans l'identifiant en tête
internal static class MessagesLigne
{
    public const string Obligatoire = "Cette zone doit être renseignée.";                                                    // GEN0001
    public const string NePasRenseigner = "Cette zone ne doit pas être renseignée.";                                          // GEN0012
    public const string OuiNon = "Cette zone doit être égale à O ou N.";                                                      // GEN0006
    public const string CodeLigneInexistant = "Ce code ligne n'existe pas.";                                                  // FAC0045
    public const string CodeLigneTexte = "Le type de ligne ne doit pas être de type Texte.";                                  // FAC0050
    public const string TypeLigneNonAutorise = "Type de ligne non autorisé.";                                                 // FAC0067
    public const string PrixADefinir = "Réponse incorrecte si prix renseigné ou ligne gratuite.";                            // NEG0042
    public const string ArticleInexistant = "Cet article n'existe pas.";
    public const string ArticleCloture = "Cet article est clôturé.";                                                          // ART0035
    public const string ArticleRemplace = "Attention, cet article est remplacé.";                                             // NEG0037
    public const string ArticleAutreClient = "Attention, le client d'appartenance de l'article est différent du client de la commande."; // ART0014
    public const string TypeArticle = "Type de l'article non conforme au type de la commande.";                               // ART0009
    public const string FacturationArticle = "Type de facturation de l'article et type de facturation de la commande non conformes."; // ART0010
    public const string ReferenceFournisseur = "Référence fournisseur non trouvée pour cet article sous-traité.";             // SYN0003
    public const string IncoherenceUc = "Incohérence entre quantité en exemplaires et quantité en UC.";                       // NEG0040
    public const string ConditionnementManquant = "Conditionnement non renseigné, vérifier la fiche article.";                 // NEG0038
    public const string QuantiteSousLivre = "Attention : la quantité commandée est inférieure à la quantité livrée.";          // NEG0028
    public const string UniteInexistante = "Cette unité de vente n'existe pas.";                                              // FAC0004
    public const string DateHorsCalendrier = "Cette date n'existe pas dans le calendrier.";                                   // DAT0023
    public const string DatePassee = "La date de livraison est antérieure à aujourd'hui.";                                    // DAT0005
    public const string JourNonTravaille = "Ce jour n'est pas un jour travaillé.";                                            // DAT0017
    public const string JourNonAutorise = "Ce jour de livraison n'est pas un jour autorisé par le client.";                  // BL00049
    public const string AdresseObligatoire = "L'adresse ou son n° doit être renseigné quand le statut est Normal.";           // ADR0001
    public const string AdresseInexistante = "N° d'adresse inexistant.";                                                      // ADR0003
    public const string Statut = "Cette zone doit être égale à « Normal » ou « À la demande ».";                               // GEN0057
    public const string AppelsExistent = "Modification impossible, il existe des appels de livraison.";                      // NEG0045
    public const string NumeroAppelEdi = "Le client est en transfert EDI. Le numéro d'appel est obligatoire.";                // CLI0006
    public const string MaxiMultiple = "Le maxi livrable doit être un multiple du conditionnement.";                          // NEG0041
    public const string ClientInexistant = "Ce compte client n'existe pas.";                                                  // CPT0001
    public const string EmplacementInexistant = "Cet emplacement de stock n'existe pas.";                                     // STO0076
}
