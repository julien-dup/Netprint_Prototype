namespace NetPrint.Modules.Production.Contrats;

// =====================================================================
// Initialisation de la production à partir d'une commande (NEG515, NEG510 F10) : la vente demande un OF, la production
// le numérote et l'enregistre comme « dossier à ouvrir » (en-tête AB001T, liens AB0020 article ↔ OF ↔ commande).
// L'ouverture de l'OF (dossier de fabrication AB001 type 2, éléments, gamme) reste faite par DOS500S / DOS500 dans le
// 5250 pendant le pilote : un OF ouvert (présent dans AB001) ne se modifie ni ne se supprime plus ici.
// La société vient de la session. Toutes les écritures d'une demande se font en une transaction.
// =====================================================================
public interface IInitialisationProduction
{
    // Nouvel OF : n° attribué (n° de la commande + premier chiffre de clé libre, comme les OF existants avec
    // DOS005.POPT02 = 'O' ; sinon compteur de l'enseigne ou de la catégorie),
    // modèle attribué si la demande n'en reprend pas, en-tête et liens écrits ensemble
    Task<NumeroOf> DemanderOfAsync(DemandeOf demande, CancellationToken ct = default);

    // OF pas encore ouvert : en-tête et liens de cette commande remplacés (option 2 de NEG515)
    Task ModifierDemandeOfAsync(NumeroOf of, DemandeOf demande, CancellationToken ct = default);

    // OF pas encore ouvert : liens de cette commande supprimés, puis l'en-tête et le suivi du BAG s'il ne sert plus à
    // aucune commande (option 4 de NEG515)
    Task SupprimerDemandeOfAsync(NumeroOf of, CommandeOrigine commande, CancellationToken ct = default);

    // Demande d'OF telle qu'enregistrée (fenêtre de modification), null si l'OF n'est pas (ou plus) à ouvrir
    Task<DemandeOfLue?> LireDemandeOfAsync(NumeroOf of, CommandeOrigine commande, CancellationToken ct = default);

    // État d'un OF : à ouvrir (AB001T), ouvert (AB001), inconnu
    Task<EtatOf> EtatAsync(NumeroOf of, CancellationToken ct = default);
}

public sealed record NumeroOf(int Numero, string Cle)
{
    public string Libelle => $"{Numero:00000}/{Cle}";
}

public sealed record CommandeOrigine(int Numero, string Cle);

public enum EtatOf { Inconnu, AOuvrir, Ouvert }

// Ce que la vente demande : l'OF (en-tête) et, par article de la commande, la quantité à fabriquer et à affecter
public sealed record DemandeOf(
    CommandeOrigine Commande, int CompteGeneral, int CompteClient, string Enseigne, string ReferenceCommande, string Interlocuteur,
    string Categorie, string SousCategorie, string Designation1, string Designation2,
    DateOnly DateMiseADisposition,       // CCDTL : date de livraison de l'OF
    DateOnly DateDossier,                // date du jour (NEG515) ou de la commande (F10)
    DateOnly? DateEntreeCommande, DateOnly? DateMadClient,
    int QuantiteLancee, decimal Prix, string Unite,
    DevisOrigine? Devis, NumeroOf? DossierPrecedent, int Modele, string IndiceComposition, string Norme, string CheminFaconnage,
    bool SousTraitee,                    // CCOP10 de la commande
    IReadOnlyList<BesoinArticle> Articles);

public sealed record DevisOrigine(int Numero, int Version, int Hypothese);

// LAQTEC (à fabriquer pour cette commande), LAQTEA (affecté à la commande), LADTFF (date de fin de fabrication)
public sealed record BesoinArticle(string Article, string Indice, int AFabriquer, int AAffecter, DateOnly DateFinFabrication);

public sealed record DemandeOfLue(NumeroOf Of, DemandeOf Demande);

// Demande refusée : une erreur par zone (fenêtre de lancement NEG515C2)
public sealed class DemandeOfInvalideException(IReadOnlyList<ErreurDemandeOf> erreurs)
    : Exception(string.Join(" ", erreurs.Select(e => e.Message)))
{
    public IReadOnlyList<ErreurDemandeOf> Erreurs { get; } = erreurs;
}

public sealed record ErreurDemandeOf(string Zone, string Message);
