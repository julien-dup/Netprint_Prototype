using NetPrint.Modules.Production.Contrats;

namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Lancement d'une commande (reprise de NEG515) : pour chaque article de la commande, ce qui la sert déjà ou pourrait la
// servir — OF en cours ou à ouvrir, stock des lots — et ce qui reste à affecter. Les décisions (lancer un OF, affecter du
// stock, rattacher à un OF) passent par les actions de l'écran ; l'OF lui-même est demandé à la production
// (IInitialisationProduction). Le suivi du BAG est affiché sans être modifié (il est validé dans le portail de BAG).
// =====================================================================
public interface ILancementCommande
{
    Task<LancementCommande?> LireAsync(CleCommande commande, CancellationToken ct = default);
}

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
    EtatBag? Bag);

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
