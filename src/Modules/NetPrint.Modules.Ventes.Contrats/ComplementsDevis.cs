namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Compléments de la consultation des devis (menus de FEN_ConsultationDevis et boutons de FEN_DetailDevis).
// Lot A, lecture seule : texte libre (FEN_VisuTexteLibre), récapitulatif des sous-totaux (FEN_RecapSousTotaux),
// tableau des coefficients (FEN_CalculRecap).
// Lot B, écriture dans les tables du WinDev (NP11F) : échec (FEN_SaisieEchec, D50006), documents (FEN_Documents,
// D50008), tags (FEN_Tags, TAG_DEVIS + D50010). Les règles non respectées lèvent RegleMetierException (message WinDev).
// Lot C : tableau des prix (FEN_TableauPrix du projet ComposantsExternes), récapitulatif PDF (DEV100NAV sur l'IBM i).
// Chaque méthode renvoie null si le devis n'existe pas ou n'est pas visible par le représentant imposé.
// =====================================================================
public interface IComplementsDevis
{
    // Texte libre du devis (élément 0, D50004) ou d'un élément (D60004)
    Task<TexteLibreDevis?> LireTexteLibreAsync(CleDevis cle, int element = 0, CancellationToken ct = default);

    Task<RecapSousTotaux?> LireSousTotauxAsync(CleDevis cle, CancellationToken ct = default);

    // Toutes les hypothèses (quantités) de la version du devis
    Task<TableauCoefficients?> LireCoefficientsAsync(CleDevis cle, CancellationToken ct = default);

    // ---------- Échec (un seul par n° de devis, toutes versions confondues) ----------
    Task<EchecDevis?> LireEchecAsync(CleDevis cle, CancellationToken ct = default);
    Task EnregistrerEchecAsync(CleDevis cle, SaisieEchec saisie, CancellationToken ct = default);
    Task SupprimerEchecAsync(CleDevis cle, CancellationToken ct = default);

    // ---------- Documents (rattachés au n° de devis) ----------
    Task<DocumentsDevis?> LireDocumentsAsync(CleDevis cle, CancellationToken ct = default);
    Task<DocumentDevis> AjouterDocumentAsync(CleDevis cle, string nomFichier, byte[] contenu, CancellationToken ct = default);
    Task SupprimerDocumentAsync(CleDevis cle, int id, CancellationToken ct = default);
    // Contenu d'un document (ouverture) : null si absent ou non visible
    Task<FichierDocument?> LireFichierAsync(int id, CancellationToken ct = default);

    // ---------- Tags (liste commune à toutes les sociétés, associés au devis / version / hypothèse) ----------
    Task<TagsDevis?> LireTagsAsync(CleDevis cle, CancellationToken ct = default);
    Task<TagDevis> CreerTagAsync(string libelle, CancellationToken ct = default);
    Task EnregistrerTagsAsync(CleDevis cle, IReadOnlyCollection<int> tags, CancellationToken ct = default);

    // ---------- Tableau des prix : toutes les quantités de la version, côte à côte ----------
    Task<TableauPrix?> LireTableauPrixAsync(CleDevis cle, CancellationToken ct = default);

    // ---------- Récapitulatif PDF du devis (programme IBM i DEV100NAV) ; null si le devis n'est pas accessible ----------
    Task<FichierDocument?> EditerRecapitulatifAsync(CleDevis cle, CancellationToken ct = default);
}

// ---------- Tableau des prix ----------
public enum StyleLignePrix { Ligne, Total, Unite, Coefficient, Vide }

// Une ligne : une valeur et une valeur « 1000 + » par quantité (dans l'ordre de TableauPrix.Quantites ; null = vide).
// AvecObservations : la bulle des cellules affiche l'observation de prix de la quantité (DEOBSP).
public sealed record LigneTableauPrix(
    string Libelle,
    StyleLignePrix Style,
    IReadOnlyList<decimal?> Valeurs,
    IReadOnlyList<decimal?> MillePlus,
    bool AvecObservations = false);

public sealed record TableauPrix(
    EnteteComplement Entete,
    string MethodeValorisation,             // AA000Z.CFTYPE : « A » frais généraux par poste, sinon global
    string Unite,                           // unité de vente du devis (lignes « pour … »)
    IReadOnlyList<int> Quantites,           // quantités devisées des hypothèses, croissantes
    IReadOnlyList<int> Hypotheses,          // n° d'hypothèse de chaque colonne
    IReadOnlyList<string> Observations,     // observation de prix de chaque colonne
    IReadOnlyList<LigneTableauPrix> Lignes);

public sealed record CodeLibelle(string Code, string Libelle);

// ---------- Échec ----------
public sealed record EchecDevis(
    EnteteComplement Entete,
    bool Existe,                         // un échec est déjà saisi pour ce n° de devis
    string Motif,
    DateOnly? DateCloture,
    string Descriptif,
    IReadOnlyList<CodeLibelle> Motifs);  // table de codes ME (AA00081)

public sealed record SaisieEchec(string Motif, DateOnly? DateCloture, string Descriptif);

// ---------- Documents ----------
public sealed record DocumentDevis(int Id, string Nom, DateTime? Date);

public sealed record DocumentsDevis(
    EnteteComplement Entete,
    IReadOnlyList<DocumentDevis> Documents,
    bool PeutSupprimer);                 // droit PRT000P1 « PCDOCUMENT » (AOPT01 = ** : suppression interdite)

public sealed record FichierDocument(string Nom, byte[] Contenu);

public static class LimitesDocuments
{
    public const long TailleMaximum = 7_097_152;   // octets (« 7 Mo » du WinDev)
    public const int LongueurNom = 80;
}

// ---------- Tags ----------
public sealed record TagDevis(int Id, string Libelle);

public sealed record TagsDevis(EnteteComplement Entete, IReadOnlyList<TagDevis> Tous, IReadOnlyCollection<int> Actifs);

public static class LimitesTags
{
    public const int Nombre = 50;
    public const int Longueur = 20;
}

// Bandeau commun des fenêtres de complément : devis, désignations, client ou prospect
public sealed record EnteteComplement(
    CleDevis Cle,
    bool EstClient,
    string CodeTiers,          // « 411000 1234 » ou n° de prospect
    string NomTiers,
    string Designation1,
    string Designation2);

public sealed record TexteLibreDevis(EnteteComplement Entete, int Element, IReadOnlyList<string> Lignes);

// Une ligne du récapitulatif : sous-total analytique (code ST), « DIVERS … », Support ou Total.
// Les montants non significatifs sont null (Support : seulement le total ; colonnes vides dans le WinDev).
public sealed record LigneSousTotal(
    string Libelle,
    decimal? FraisFixes,
    decimal? FraisVariables,
    decimal Total,
    decimal? MillePlus,
    bool EstTotal = false);

public sealed record RecapSousTotaux(EnteteComplement Entete, IReadOnlyList<LigneSousTotal> Lignes);

// Tableau des coefficients : une ligne par hypothèse, base (PRA ou PR total, ramenée à l'unité de vente)
// multipliée par chaque coefficient de D50009
public sealed record TableauCoefficients(
    EnteteComplement Entete,
    string LibelleBase,                       // « PRA (pour 1000) » ou « PR total (pour 1000) »
    IReadOnlyList<decimal> Coefficients,      // vide : aucun coefficient saisi pour cette version
    IReadOnlyList<LigneCoefficients> Lignes);

public sealed record LigneCoefficients(int Hypothese, int Quantite, decimal Base, IReadOnlyList<decimal> Prix);
