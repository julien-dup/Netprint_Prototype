using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Ordres de préparation (OPR, WinDev OPR : FEN_GestionOPR, FEN_CreationOPR, FEN_SelectionLignesCde, FEN_SaisieOPR,
// FEN_ModificationOPR). Un OPR regroupe des lignes de commande à préparer pour un même client, une même adresse, une même
// date et un même transporteur ; il est ensuite préparé (palettes, colis) puis livré par un BL.
// Droits PRT000P1, programme OPR100 : 1 total, 2 création, 3 modification, 4 suppression.
// =====================================================================
public interface IOpr
{
    Task<DroitsOpr> DroitsAsync(CancellationToken ct = default);
    Task<ResultatRecherche<ElementOpr>> RechercherAsync(CriteresOpr criteres, CancellationToken ct = default);

    // Lignes de commande à mettre en préparation ; criteres.NumeroOpr > 0 : lignes qu'on peut ajouter à cet OPR
    Task<ResultatRecherche<LigneCandidateOpr>> CandidatesAsync(CriteresCandidatesOpr criteres, CancellationToken ct = default);

    // Rend les n° des OPR créés (un par client / adresse / date / transporteur…)
    Task<IReadOnlyList<int>> CreerAsync(IReadOnlyList<LigneSaisieOpr> lignes, CancellationToken ct = default);
    Task AjouterAsync(int numeroOpr, IReadOnlyList<LigneSaisieOpr> lignes, CancellationToken ct = default);

    Task<FicheOpr?> LireAsync(int numeroOpr, CancellationToken ct = default);
    Task ModifierAsync(ModificationOpr modification, CancellationToken ct = default);
    Task SupprimerAsync(int numeroOpr, CancellationToken ct = default);
    Task SupprimerElementAsync(int numeroOpr, int dossier, string cle, CancellationToken ct = default);
}

public sealed record DroitsOpr(bool Acces, bool Creer, bool Modifier, bool Supprimer, bool Editer = true);

public static class EtatsOpr
{
    public const string APreparer = "À préparer";
    public const string EnPreparation = "En préparation";
    public const string Livre = "Livré";
}

public sealed class CriteresOpr
{
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public int? Commande { get; set; }
    public string? Cle { get; set; }
    public DateOnly? DatePrevue { get; set; }
    public int? NumeroOpr { get; set; }
    public bool AvecLivres { get; set; }
}

// Un élément = une commande dans un OPR (ce que liste FEN_GestionOPR) ; date, adresse et transporteur de sa première ligne
public sealed record ElementOpr(
    int NumeroOpr, int Dossier, string Cle, int CompteGeneral, int CompteClient, string NomClient, string ReferenceCommande,
    DateOnly? DatePrevue, AdresseBl Adresse, int Transporteur, string NomTransporteur, string Etat, bool ClientBloque, string Profil,
    DateOnly? DateCreation, int Lignes, bool SousTraite);

public sealed class CriteresCandidatesOpr
{
    public DateOnly? Du { get; set; }
    public DateOnly? Au { get; set; }
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public int? Commande { get; set; }
    public string? Cle { get; set; }
    public int? NumeroAdresse { get; set; }
    public List<string> Fabricants { get; set; } = [];
    public string? Appel { get; set; }
    public string? Categorie { get; set; }
    public string? Enseigne { get; set; }
    public int NumeroOpr { get; set; }
}

// Ligne de commande livrable : date de livraison (NEG100.DNDLIV) et date confirmée (DNDATB), reste, maxi livrable, adresse
// effective (A500F, sinon adresse de la ligne, sinon client), transporteur du client ; observation de préparation (EVE100
// « PREP ») ; AlertePrelevement : stock en retour usine non sorti (délégation de prélèvement)
public sealed record LigneCandidateOpr(
    int Dossier, string Cle, int LigneCommande, int CompteGeneral, int CompteClient, string NomClient, string ReferenceCommande,
    string Appel, string Article, string Indice, string RefImprime, string Designation, DateOnly? DateLivraison, DateOnly? DateConfirmee,
    int Reste, int MaxiLivrable, AdresseBl Adresse, int TransporteurClient, string NomTransporteur, bool SousTraite, bool Stocke,
    bool PrixADefinir, bool ClientBloque, bool AlertePrelevement, string Observation, string Marche);

// Ligne à mettre en préparation (création ou ajout) ; observation : 400 caractères au plus
public sealed record LigneSaisieOpr(
    int Dossier, string Cle, int LigneCommande, DateOnly DatePrevue, int Quantite, int Transporteur, bool Imperatif, bool Mecompte,
    bool ASolder, bool Prelevement, bool Acompte, string Observation);

public sealed record FicheOpr(
    int Numero, string Etat, string Profil, DateOnly? DateCreation, int CompteGeneral, int CompteClient, string NomClient, AdresseBl Adresse,
    bool SousTraite, IReadOnlyList<string> Textes, IReadOnlyList<LigneOpr> Lignes);

// Etat : APreparer (modifiable), EnPreparation (palettes ou quantité préparée), ou ligne livrée (Bl)
public sealed record LigneOpr(
    int NumeroLigne, int Dossier, string Cle, int LigneCommande, string ReferenceCommande, string Appel, string Article, string Indice,
    string RefImprime, string Designation, DateOnly? DatePrevue, int Quantite, int Reste, int MaxiLivrable, int Transporteur,
    string NomTransporteur, bool Imperatif, bool Mecompte, bool ASolder, bool Prelevement, bool Acompte, string Observation, string Etat,
    bool Stocke, int QuantitePreparee, int Bl)
{
    public bool Modifiable => Etat == EtatsOpr.APreparer;
}

public sealed record ModificationOpr(int Numero, IReadOnlyList<string> Textes, IReadOnlyList<LigneModifieeOpr> Lignes);

public sealed record LigneModifieeOpr(
    int NumeroLigne, bool Supprimer, DateOnly DatePrevue, int Quantite, int Transporteur, bool Imperatif, bool Mecompte, bool ASolder,
    bool Prelevement, bool Acompte, string Observation);

// Édition d'un OPR en PDF (reprise d'OPR150) : tout l'OPR ou seulement les lignes jamais éditées ; produire le PDF n'écrit
// rien, l'impression demandée marque ensuite les lignes éditées
public interface IEditionOpr
{
    Task<NetPrint.Socle.Documents.DocumentGenere> EditerAsync(int numeroOpr, bool ajoutsSeulement, CancellationToken ct = default);

    // Tout l'OPR imprimé : toutes ses lignes marquées éditées (comme OPR150)
    Task MarquerEditeAsync(int numeroOpr, CancellationToken ct = default);

    // Seulement les lignes jamais éditées : PDF produit, puis exactement ces lignes marquées éditées
    Task<NetPrint.Socle.Documents.DocumentGenere> ImprimerAjoutsAsync(int numeroOpr, CancellationToken ct = default);
}

// =====================================================================
// Livraison d'un OPR (WinDev OPR : FEN_BonLivraison, FEN_DetailColis, FEN_CreationBL, FEN_DetailBL) : situation de la
// préparation (palettes conditionnées, cartons clôturés), génération des BL depuis les palettes conditionnées selon le mode
// du client (AA0003.CDRES1 : 1 par colis, 2 par commande, 3 par article, 4 total), puis palettes, poids et pied des BL.
// =====================================================================
public interface ILivraisonOpr
{
    Task<SituationOpr> SituationAsync(int numeroOpr, CancellationToken ct = default);
    Task<GenerationOprResultat> GenererBlAsync(GenerationBlOpr generation, CancellationToken ct = default);
    Task FinaliserBlAsync(int numeroOpr, IReadOnlyList<FinalisationBlOpr> bls, CancellationToken ct = default);
}

// PeutGenerer : toutes les lignes prêtes (ou frais) et au moins une palette conditionnée ou un frais ; Refus sinon
public sealed record SituationOpr(
    int Numero, string Mode, string LibelleMode, bool PeutGenerer, string Refus, IReadOnlyList<LigneSituationOpr> Lignes, DefautsBlOpr Defauts);

// Valeurs proposées pour les BL : transporteur de l'OPR s'il est unique, port et mode de livraison de la société (EXP005),
// observations de l'adresse de livraison, poids d'une palette (EXP005.FPDSP)
public sealed record DefautsBlOpr(int Transporteur, string TypePort, string ModeLivraison, IReadOnlyList<string> Observations, int PoidsPalette);

// Quantités : à préparer (ODQTEA), préparée (ODQPREP), conditionnée (palettes au statut 4 ou 5) ; Bloquante : empêche le BL
public sealed record LigneSituationOpr(
    int NumeroLigne, int Dossier, string Cle, string Article, string Indice, string RefImprime, int Quantite, int Preparee, int Conditionnee,
    string Etat, bool Bloquante, bool Frais, IReadOnlyList<ColisOpr> Colis);

public sealed record ColisOpr(string Sscc, string Lot, int Quantite, string Statut, int Carton, bool CartonClos, string Operateur, string Matiere);

// Observations : 3 lignes de 40 caractères ; transporteur obligatoire sauf enlèvement (port « E »)
public sealed record GenerationBlOpr(
    int Numero, DateOnly Date, int Transporteur, string TypePort, string ModeLivraison, IReadOnlyList<string> Observations, string Incoterm);

public sealed record GenerationOprResultat(IReadOnlyList<BlGenereOpr> Bls, IReadOnlyList<string> Avertissements);

public sealed record BlGenereOpr(int NumeroBl, string Enseigne, int Colis, int PoidsCartons, IReadOnlyList<string> TextePied);

// Poids du BL = poids des cartons + palettes × poids d'une palette
public sealed record FinalisationBlOpr(int NumeroBl, int Palettes, int PoidsPalette, IReadOnlyList<string> TextePied);
