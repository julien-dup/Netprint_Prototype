using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// Écran de lancement d'une commande (NEG515, NEG510 F10) : lecture et calculs, puis les actions. Toute action exige une
// commande modifiable (droit de modification, ni soldée ni sous-traitée) et se contrôle sur les quantités relues.
// Les OF sont demandés à la production ; l'affectation de stock est écrite ici.
internal sealed class LancementCommandeService(LancementCommandeDb2 donnees, ISourceCommandes commandes, IInitialisationProduction production,
    IUtilisateurCourant utilisateur) : ILancementCommande
{
    public async Task<LancementCommande?> LireAsync(CleCommande commande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var impose = await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        if (await donnees.LireAsync(u.Societe, commande, impose, ct) is not var (entete, d)) return null;
        // NEG510 R3 / R7 : commande sous-traitée (CCOP11) ou soldée : consultation seulement
        var raison = !droits.Modifier ? "Vous n'êtes pas autorisé à modifier les commandes."
            : entete.NonModifiable ? "Commande sous-traitée. Modification impossible."
            : entete.Soldee ? "Option impossible, dossier soldé."
            : "";
        return new LancementCommande(commande, entete.NomClient, raison == "", raison, CalculLancement.Calculer(d, commande));
    }

    public async Task<DemandeOf> PreparerAsync(CleCommande commande, IReadOnlyList<ReferenceArticle> articles, CancellationToken ct = default)
    {
        var l = await ExigerModifiableAsync(commande, ct);
        var retenus = articles.Select(r => Article(l, r)).Select(a => new ReferenceArticle(a.Article, a.Indice)).Distinct().ToList();
        if (retenus.Count == 0) throw new RegleMetierException("Choisissez au moins un article à lancer.");
        var d = await LirePreparationAsync(commande, retenus, ct);
        return PreparationLancement.Proposer(d, l.Articles.ToDictionary(a => new ReferenceArticle(a.Article, a.Indice), a => a.ResteAAffecter),
            Aujourdhui);
    }

    public async Task<NumeroOf> LancerAsync(CleCommande commande, DemandeOf demande, CancellationToken ct = default)
    {
        var l = await ExigerModifiableAsync(commande, ct);
        var d = demande with { Commande = new CommandeOrigine(commande.Numero, commande.Cle) };
        Exiger(PreparationLancement.ControlerAffectations(d, l.Articles, null, []));
        return await production.DemanderOfAsync(d, ct);
    }

    // Commande accessible à l'utilisateur (représentant imposé) avant de montrer l'OF
    public async Task<DemandeOf?> LireLancementAsync(CleCommande commande, NumeroOf of, CancellationToken ct = default)
    {
        _ = await LireAsync(commande, ct) ?? throw new RegleMetierException($"La commande {commande.Libelle} n'existe pas ou ne vous est pas accessible.");
        return (await production.LireDemandeOfAsync(of, new CommandeOrigine(commande.Numero, commande.Cle), ct))?.Demande;
    }

    public async Task ModifierLancementAsync(CleCommande commande, NumeroOf of, DemandeOf demande, CancellationToken ct = default)
    {
        var l = await ExigerModifiableAsync(commande, ct);
        var origine = new CommandeOrigine(commande.Numero, commande.Cle);
        var ancienne = await production.LireDemandeOfAsync(of, origine, ct)
            ?? throw new RegleMetierException($"Modification impossible : l'OF {of.Libelle} n'est plus à ouvrir.");
        var d = demande with { Commande = origine };
        Exiger(PreparationLancement.ControlerAffectations(d, l.Articles, of, ancienne.Demande.Articles));
        await production.ModifierDemandeOfAsync(of, d, ct);
    }

    public async Task SupprimerLancementAsync(CleCommande commande, NumeroOf of, CancellationToken ct = default)
    {
        await ExigerModifiableAsync(commande, ct);
        await production.SupprimerDemandeOfAsync(of, new CommandeOrigine(commande.Numero, commande.Cle), ct);
    }

    public async Task AffecterStockAsync(CleCommande commande, NumeroOf of, ReferenceArticle article, int quantite, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var l = await ExigerModifiableAsync(commande, ct);
        var a = Article(l, article);
        Exiger(PreparationLancement.ControlerAffectationStock(a, a.Sources.FirstOrDefault(s => s.Of == of), quantite));
        // Le total affecté ≤ commandé est revérifié sous verrou dans la transaction
        await donnees.AffecterAsync(u.Societe, commande, of, new(a.Article, a.Indice), quantite, ct);
    }

    public async Task SupprimerAffectationAsync(CleCommande commande, NumeroOf of, ReferenceArticle article, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var a = Article(await ExigerModifiableAsync(commande, ct), article);
        await donnees.SupprimerAffectationAsync(u.Societe, commande, of, new(a.Article, a.Indice), ct);
    }

    // AL limité aux OF à ouvrir : le reste à affecter de l'article est fabriqué et affecté par l'OF, à sa date de mise à
    // disposition. Un OF qui sert déjà la commande pour cet article se modifie par le lancement (NEG515 remplaçait la quantité).
    public async Task RattacherAsync(CleCommande commande, NumeroOf of, ReferenceArticle article, CancellationToken ct = default)
    {
        var l = await ExigerModifiableAsync(commande, ct);
        var a = Article(l, article);
        var s = a.Sources.FirstOrDefault(s => s.Of == of);
        if (s is { LieALaCommande: true })
            throw new RegleMetierException($"L'OF {of.Libelle} sert déjà la commande pour cet article : modifiez le lancement.");
        // L'OF doit être à ouvrir : vérifié par la production dans sa transaction (un OF masqué, sans disponible, est accepté)
        if (s is { Nature: not NatureSource.OfAOuvrir })
            throw new RegleMetierException($"Ajout impossible : l'OF {of.Libelle} n'est pas un dossier à ouvrir.");
        if (a.ResteAAffecter <= 0) throw new RegleMetierException("Il ne reste rien à affecter pour cet article.");
        await production.AjouterAOfAsync(of, new CommandeOrigine(commande.Numero, commande.Cle),
            new BesoinArticle(a.Article, a.Indice, a.ResteAAffecter, a.ResteAAffecter, Aujourdhui), ct);
    }

    // NEG510 F10 : refusée si un OF existe déjà pour la commande (ÉCART : tous ses liens sont examinés, pas seulement le
    // premier, et le refus est toujours signalé)
    public async Task<NumeroOf> CreerOfAutomatiqueAsync(CleCommande commande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var l = await ExigerModifiableAsync(commande, ct);
        if (l.Articles.Count == 0) throw new RegleMetierException("Création OF auto impossible car aucune ligne de commande.");
        if (await donnees.OfDejaCreeAsync(u.Societe, commande, ct)) throw new RegleMetierException("Un OF a déjà été créé pour cette commande.");
        var d = await LirePreparationAsync(commande, [.. l.Articles.Select(a => new ReferenceArticle(a.Article, a.Indice))], ct);
        var demande = PreparationLancement.Automatique(d, l.Articles.ToDictionary(a => new ReferenceArticle(a.Article, a.Indice), a => a.Commande),
            Aujourdhui);
        return await production.DemanderOfAsync(demande, ct);
    }

    // ------------------------------------------------------------------
    private static DateOnly Aujourdhui => DateOnly.FromDateTime(DateTime.Today);

    private async Task<LancementCommande> ExigerModifiableAsync(CleCommande commande, CancellationToken ct)
    {
        var l = await LireAsync(commande, ct) ?? throw new RegleMetierException($"La commande {commande.Libelle} n'existe pas ou ne vous est pas accessible.");
        if (!l.PeutModifier) throw new RegleMetierException(l.RaisonNonModifiable);
        return l;
    }

    private async Task<PreparationLancement.Donnees> LirePreparationAsync(CleCommande commande, IReadOnlyList<ReferenceArticle> articles,
        CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        return await donnees.LirePreparationAsync(u.Societe, commande, articles, ct)
            ?? throw new RegleMetierException($"La commande {commande.Libelle} n'existe plus.");
    }

    private static ArticleALancer Article(LancementCommande l, ReferenceArticle r) =>
        l.Articles.FirstOrDefault(a => string.Equals(a.Article, r.Article.Trim(), StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(a.Indice, r.Indice.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new RegleMetierException($"L'article {r.Article} n'est pas dans la commande.");

    private static void Exiger(List<ErreurDemandeOf> erreurs)
    {
        if (erreurs.Count > 0) throw new DemandeOfInvalideException(erreurs);
    }
}
