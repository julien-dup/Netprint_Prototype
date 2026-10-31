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

public sealed record DroitsOpr(bool Acces, bool Creer, bool Modifier, bool Supprimer);

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
