using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using A = NetPrint.Modules.Ventes.Contrats.AvertissementsLigne;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Appels de livraison (option DE de NEG520, NEG300NAV pour le navigateur et l'EDI, NEG520B pour le report de prix).
// Un appel est une ligne : mêmes contrôles (CTL520), même écriture (MAJ520), même recalcul de l'en-tête ; s'y ajoutent
// les règles de l'origine (CalculAppels). Règles retenues là où les trois programmes divergeaient (rapport APPELS) :
//   - prix, unité, commission, article : toujours ceux de l'origine à la création, jamais ceux de l'appelant ;
//   - dépassement du reste à appeler : avertissement à accepter (NEG0029), pour tous les canaux ;
//   - origine recalculée depuis la quantité du contrat et la somme des appels, remise en cours si un reste réapparaît ;
//   - le solde d'un appel ne rend pas son reste à l'origine (comme le RPG) — à valider ;
//   - un appel créé par l'EDI (EDI105) ne se modifie et ne se supprime que par l'EDI.
// =====================================================================
internal sealed partial class SaisieLignesService : IAppelsLivraison
{
    public async Task<LigneSaisie> NouvelAsync(CleCommande commande, int ligneOrigine, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(commande, false, ct);
        var o = await OrigineAsync(u.Societe, commande, ligneOrigine, ct);
        var a = await donnees.ArticleAsync(u.Societe, o.Article, o.Indice, ct);
        if (a?.Remplacement == "C") throw new RegleMetierException(MessagesLigne.ArticleCloture);
        return NouvelAppel(o);
    }

    // Valeurs d'un nouvel appel : image de la ligne d'origine (ZTOUT = ETOUT de NEG520), sans quantité, date ni maxi
    internal static LigneSaisie NouvelAppel(LigneSaisie o)
    {
        var l = o.Copier();
        l.NumeroLigne = 0;
        l.Statut = StatutsLigne.Normal;
        l.LigneOrigine = o.NumeroLigne;
        l.ResteOrigine = o.Quantite ?? 0;
        l.Quantite = l.QuantiteUc = l.MaxiLivrable = null;
        l.MaxiSaisi = false;
        l.DateLivraison = l.DateConfirmee = null;
        l.QuantiteLivree = l.QuantiteFacturee = l.QuantiteOrigine = 0;
        l.Soldee = false;
        l.Montant = 0;
        l.ArticleEnregistre = l.IndiceEnregistre = l.Empreinte = "";
        l.AvertissementsAcceptes = [];
        Imposer(l, o);
        return l;
    }

    // Article et prix d'un appel : ceux de la source (l'origine à la création, l'appel lui-même en modification)
    internal static void Imposer(LigneSaisie l, LigneSaisie source)
    {
        l.Statut = StatutsLigne.Normal;
        l.CodeVente = source.CodeVente;
        l.Article = source.Article;
        l.Indice = source.Indice;
        l.Designation = source.Designation;
        l.Unite = source.Unite;
        l.PrixBrut = source.PrixBrut;
        l.Remise = source.Remise;
        l.PrixCession = source.PrixCession;
        l.Gratuite = source.Gratuite;
        l.PrixADefinir = source.PrixADefinir;
        l.TauxCommission = source.TauxCommission;
        l.NouvelleApplication = source.NouvelleApplication;
        l.PrixNet = MoteurPrixCommande.PrixNet(l.PrixBrut ?? 0, l.Remise ?? 0);
        l.SourcePrix = "prix de la ligne d'origine";
        l.PrixSaisi = l.CommissionSaisie = true;
        l.ArticleComplete = $"{l.Article}|{l.Indice}";
    }

    // Origine d'un appel : ligne « à la demande » existante (NEG520 DE : refus 67 NEG0026 sur une ligne normale).
    // Une origine soldée est acceptée (NEG520 DE) : elle est remise en cours si un reste réapparaît.
    private async Task<LigneSaisie> OrigineAsync(int societe, CleCommande commande, int ligneOrigine, CancellationToken ct)
    {
        var o = await donnees.LigneAsync(societe, commande, ligneOrigine, ct)
            ?? throw new RegleMetierException($"La ligne d'origine {ligneOrigine} n'existe plus.");
        if (o.Statut != StatutsLigne.ALaDemande) throw new RegleMetierException(MessagesLigne.AppelSurLigneNormale);
        return o;
    }

    // ------------------------------------------------------------------
    public async Task<int> EnregistrerAsync(CleCommande commande, LigneSaisie appel, CanalAppel canal, CancellationToken ct = default)
    {
        var creation = appel.NumeroLigne == 0;
        // Un appel sur une commande soldée « D » la remet en cours (NEG300NAV) ; en modification, la commande doit être en cours
        var (u, c) = await ContexteAsync(commande, true, ct, soldeeAdmise: creation);
        LigneSaisie? ancienne = null;
        if (!creation)
            ancienne = await donnees.LigneAsync(u.Societe, commande, appel.NumeroLigne, ct) ?? throw new RegleMetierException("Cette ligne n'existe plus.");
        return await EnregistrerAppelAsync(u, c, commande, appel, ancienne, canal, ct);
    }

    // ancienne : l'appel tel qu'enregistré (modification), null à la création
    private async Task<int> EnregistrerAppelAsync(UtilisateurConnecte u, ContexteLigne c, CleCommande commande, LigneSaisie appel,
        LigneSaisie? ancienne, CanalAppel canal, CancellationToken ct)
    {
        var l = Normaliser(appel);
        if (ancienne is not null)
        {
            if (!ancienne.EstAppel) throw new RegleMetierException(MessagesLigne.PasUnAppel);
            if (ancienne.Soldee) throw new RegleMetierException("Modification impossible, la ligne est soldée.");
            await RefusEdiAsync(u.Societe, commande, l.NumeroLigne, canal, ct);
            l.LigneOrigine = ancienne.LigneOrigine;
        }
        var o = await OrigineAsync(u.Societe, commande, l.LigneOrigine, ct);
        Imposer(l, ancienne ?? o);

        var a = await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct);
        if (a is not null) l = await CompleterAsync(u.Societe, commande, c, l, a, ct);
        // Reste lu avant la transaction : la couche données refuse l'écriture s'il a changé entre-temps (dépassement contrôlé
        // sur une valeur à jour)
        l.ResteOrigine = o.Quantite ?? 0;

        // Arrondi au conditionnement accepté : appliqué avant les contrôles, pour que le dépassement porte sur la quantité écrite
        if (l.AvertissementsAcceptes.Contains(A.QuantiteArrondie) && a is { FractionnableCommande: false, Emballage: > 0 } && l.Quantite is > 0)
            l.Quantite = (l.Quantite + a.Emballage - 1) / a.Emballage * a.Emballage;

        var v = await donnees.VerifierAsync(u.Societe, commande, c, l, a, ct);
        var (erreurs, avertissements) = ControlerAppel(c, l, a, v, ancienne, o, Aujourdhui);
        if (erreurs.Count > 0) throw new SaisieInvalideException(erreurs);
        if (avertissements.Count > 0) throw new SaisieInvalideException(avertissements) { Forcable = true };

        var numero = await donnees.EnregistrerAsync(u.Societe, u.Profil, commande, c, l, v.TnumUnite ?? 0, EmpreinteEcriture, ct);
        await RecalculerStocksAsync(u, c, a, l.Article, l.Indice, null, ct);
        return numero;
    }

    // Contrôles d'un appel : ceux d'une ligne (CTL520), le reste de l'origine étant déjà réservé, puis le dépassement du
    // reste à appeler (NEG0029). Les avertissements sur l'article (remplacé, autre client) ont été vus à la saisie de l'origine.
    internal static (List<ErreurSaisie> Erreurs, List<ErreurSaisie> Avertissements) ControlerAppel(
        ContexteLigne c, LigneSaisie l, ArticleLigne? a, VerificationsLigne v, LigneSaisie? ancienne, LigneSaisie origine, DateOnly aujourdhui)
    {
        var controle = l.Copier();
        controle.AvertissementsAcceptes.UnionWith([A.ArticleRemplace, A.ArticleAutreClient]);
        var reste = origine.Quantite ?? 0;
        var (erreurs, avertissements) = Controler(c, controle, a, v, ancienne, aujourdhui, reserveOrigine: reste);
        if (CalculAppels.Depasse(l.Quantite ?? 0, ancienne?.Quantite ?? 0, reste) && !l.AvertissementsAcceptes.Contains(A.DepassementOrigine))
            avertissements.Add(new(A.DepassementOrigine, $"{MessagesLigne.DepassementOrigine} ({reste:N0})."));
        return (erreurs, avertissements);
    }

    public async Task SupprimerAsync(CleCommande commande, int numeroAppel, CanalAppel canal, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, true, ct);
        var l = await donnees.LigneAsync(u.Societe, commande, numeroAppel, ct) ?? throw new RegleMetierException("Cette ligne n'existe plus.");
        if (!l.EstAppel) throw new RegleMetierException(MessagesLigne.PasUnAppel);
        await SupprimerLigneAsync(u, c, commande, l, canal, ct);
    }

    private async Task RefusEdiAsync(int societe, CleCommande commande, int ligne, CanalAppel canal, CancellationToken ct)
    {
        if (canal != CanalAppel.Edi && await donnees.AppelEdiAsync(societe, commande, ligne, ct))
            throw new RegleMetierException(MessagesLigne.AppelEdi);
    }

    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<int>> ReporterPrixAsync(CleCommande commande, int ligneOrigine, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, true, ct);
        await OrigineAsync(u.Societe, commande, ligneOrigine, ct);
        return await donnees.ReporterPrixAsync(u.Societe, u.Profil, commande, c, ligneOrigine, ct);
    }

    public async Task<int> CalculerMaxiAsync(CleCommande commande, int ligneOrigine, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(commande, false, ct);
        var (maxi, _, livre) = await donnees.MaxiOrigineAsync(u.Societe, commande, ligneOrigine, ct);
        return CalculAppels.MaxiSolde(maxi, livre);
    }

    // DOS009.PMCALC '2' : maxi proposé au prorata de celui de l'origine, tant que l'utilisateur ne l'a pas saisi
    private async Task MaxiAppelAsync(int societe, CleCommande commande, ContexteLigne c, LigneSaisie l, CancellationToken ct)
    {
        if (c.ModeMaxi != "2" || c.SansMaxi || l.MaxiSaisi || l.Quantite is not > 0) return;
        var (maxi, contrat, _) = await donnees.MaxiOrigineAsync(societe, commande, l.LigneOrigine, ct);
        l.MaxiLivrable = CalculAppels.MaxiProrata(l.Quantite.Value, maxi, contrat);
    }
}
