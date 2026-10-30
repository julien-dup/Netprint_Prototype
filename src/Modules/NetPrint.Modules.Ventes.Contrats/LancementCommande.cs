using NetPrint.Modules.Production.Contrats;

namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Lancement d'une commande (reprise de NEG515) : pour chaque article de la commande, ce qui la sert déjà ou pourrait la
// servir — OF en cours ou à ouvrir, stock des lots — et ce qui reste à affecter. Les décisions (lancer un OF, affecter du
// stock, rattacher à un OF) passent par les actions de l'écran ; l'OF lui-même est demandé à la production
// (IInitialisationProduction), l'affectation de stock (lien AB0020 de type « S ») est écrite par la vente. Le suivi du BAG est affiché sans être modifié (il est validé dans le portail de BAG).
// =====================================================================
public interface ILancementCommande
{
    Task<LancementCommande?> LireAsync(CleCommande commande, CancellationToken ct = default);

    // Valeurs proposées pour lancer un OF pour un ou plusieurs articles (option CR, groupage) : reste à affecter, date de
    // livraison de la commande, catégorie, désignation, norme, devis, dossier précédent et modèle (NEG515 SPCHG2)
    Task<DemandeOf> PreparerAsync(CleCommande commande, IReadOnlyList<ReferenceArticle> articles, CancellationToken ct = default);

    // Lancement d'un OF (à ouvrir dans le 5250) : contrôles de la commande (affecté ≤ commandé), puis demande à la production
    Task<NumeroOf> LancerAsync(CleCommande commande, DemandeOf demande, CancellationToken ct = default);

    // OF à ouvrir de la commande : lecture, modification (option 2), suppression (option 4)
    Task<DemandeOf?> LireLancementAsync(CleCommande commande, NumeroOf of, CancellationToken ct = default);
    Task ModifierLancementAsync(CleCommande commande, NumeroOf of, DemandeOf demande, CancellationToken ct = default);
    Task SupprimerLancementAsync(CleCommande commande, NumeroOf of, CancellationToken ct = default);

    // Affectation du stock (ou de la production) d'un OF ouvert à la commande (option AF), et sa suppression (option SA)
    Task AffecterStockAsync(CleCommande commande, NumeroOf of, ReferenceArticle article, int quantite, CancellationToken ct = default);
    Task SupprimerAffectationAsync(CleCommande commande, NumeroOf of, ReferenceArticle article, CancellationToken ct = default);

    // Rattachement du reste à affecter d'un article à un OF pas encore ouvert (option AL)
    Task RattacherAsync(CleCommande commande, NumeroOf of, ReferenceArticle article, CancellationToken ct = default);

    // Création automatique (NEG510 F10) : un OF pour tous les articles de la commande, tout le commandé fabriqué et affecté
    Task<NumeroOf> CreerOfAutomatiqueAsync(CleCommande commande, CancellationToken ct = default);
}

public sealed record ReferenceArticle(string Article, string Indice);

public sealed record LancementCommande(CleCommande Commande, string NomClient, bool PeutModifier, string RaisonNonModifiable,
    IReadOnlyList<ArticleALancer> Articles);

// Un article de la commande (lignes regroupées par article, indice et désignation, lignes soldées comprises, comme NEG515)
public sealed record ArticleALancer(
    string Article, string Indice, string Designation, string ReferenceImprime, bool Stocke,
    int Commande,                 // quantité commandée
    int Affecte,                  // affecté à la commande, tous OF et lots (TOTAFF)
    IReadOnlyList<SourceArticle> Sources)
{
    public int ResteAAffecter => Commande - Affecte;
}

public enum NatureSource { OfAOuvrir, OfOuvert, Lot }

// Un OF de l'article (lien AB0020, avec le stock de son lot) ou un lot sans lien
public sealed record SourceArticle(
    NatureSource Nature, NumeroOf Of,
    int AffecteCommande,          // affecté à cette commande sur cet OF (WAFCDE)
    int AFabriquer,               // reste à fabriquer (lancé − fabriqué)
    int Lance, int Fabrique,
    int Stock, int Surplus,       // stock du lot (dépôts « M » / surplus « S », « A »)
    int AffecteTotal,             // affecté toutes commandes
    int Disponible,               // à fabriquer + stock + surplus − affecté (+ livré de cette commande), jamais négatif
    int Livre, int LivreCommande, // livré (EX − RE) sur le dépôt principal : tout le lot, pour cette commande
    bool AffectationStock,        // dernier lien de type « S » (affectation de stock)
    bool LotBloque,               // CQU150 : affiché seulement
    EtatBag? Bag,
    bool LieALaCommande);         // la commande a un lien vers cet OF (même sans quantité affectée)

// Dernier cycle du BAG de l'OF × article (AB009)
public sealed record EtatBag(int Cycle, DateOnly? ReceptionDocuments, DateOnly? EnvoiPrevu, DateOnly? Envoye, DateOnly? Retour, string Action)
{
    public string Libelle => Action switch
    {
        "O" => "BAG OK",
        "M" => "BAG à modifier",
        _ when Retour is not null => "BAG revenu",
        _ when Envoye is not null => "BAG envoyé",
        _ => "BAG attendu",
    };
}
