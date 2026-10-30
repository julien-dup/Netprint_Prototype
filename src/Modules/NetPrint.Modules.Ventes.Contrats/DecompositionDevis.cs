namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Lot E : décomposition analytique et prix remis (ex-FEN_Decomposition, FEN_Legendes).
// Seul écran de la consultation qui modifie le chiffrage : prix remis et unité de vente (D5000F), légende
// (D50007) et ventilation de chaque ligne sur 15 chapitres A à O (D60001 papier, D60002 fabrication,
// D50001 transport). Règles R15 à R24 (rétro-doc ConsultationDevis.md, docs/compte-rendu-decomposition.md).
// =====================================================================
public interface IDecompositionDevis
{
    // null si le devis n'existe pas ou n'est pas visible par le représentant imposé
    Task<DecompositionDevis?> LireAsync(CleDevis cle, CancellationToken ct = default);

    // Calcul des PR / PV / PV unitaires de chaque ligne pour un prix remis et une unité de vente (R20 à R23).
    // Pur (aucun accès à la base) : appelé à chaque saisie du prix ou de l'unité.
    CalculDecomposition Calculer(DecompositionDevis decomposition, decimal prixRemis, string unite);

    // Contrôle de la ventilation des lignes (R18) : première erreur rencontrée, null si tout est correct
    ErreurDecomposition? Controler(IReadOnlyList<ChapitreLegende> legende, IEnumerable<VentilationLigne> lignes);

    // Enregistrement (R19) dans une transaction. RegleMetierException : contrôle non respecté ou devis modifié
    // par ailleurs depuis la lecture. Renvoie les prix du devis après enregistrement.
    Task<PrixDevis> ValiderAsync(CleDevis cle, SaisieDecomposition saisie, CancellationToken ct = default);
}

public static class LimitesDecomposition
{
    public const string Lettres = "ABCDEFGHIJKLMNO";
    public const int Chapitres = 15;          // A à O
    public const int Valeurs = 99;            // valeurs 1 à 99 par chapitre
    public const int LongueurTexte = 60;      // F7TXT, MLDTXT
    public const int PourcentMax = 99;        // colonnes …PC en S 2 0 : 100 % implicite quand le % est vide
    public const int TnumLeTout = 999999999;  // unité de vente « le tout »

    public static string Lettre(int chapitre) => Lettres[chapitre].ToString();
}

// Un chapitre de la légende : la lettre est sa position (0 = A). Valeurs[0] = valeur 1.
public sealed record ChapitreLegende(string Titre, IReadOnlyList<string> Valeurs);

public sealed record UniteVente(string Code, string Libelle, int Tnum);

public enum NatureLigneDecomposition { Element, Rubrique, Section, Papier, Operation, Matiere, SousTraitance, Transport }

// Table où s'écrit la ventilation d'une feuille
public enum TableVentilation { Papier, Fabrication, Transport }   // D60001, D60002, D50001

// Lignes : n° DDLIG de D60002 (plusieurs quand des lignes identiques consécutives ont été fusionnées à l'affichage)
public sealed record CibleVentilation(TableVentilation Table, int Element, IReadOnlyList<int> Lignes);

// Une ligne de l'arbre, dans l'ordre d'affichage (parcours en profondeur). Les nœuds (élément, rubrique, section)
// n'ont pas de cible : leur ventilation affichée est celle de leurs feuilles quand elles l'ont toutes en commun.
public sealed record LigneDecomposition(
    int Id,
    int? Parent,
    int Niveau,
    NatureLigneDecomposition Nature,
    string Libelle,
    string Commentaire,
    CibleVentilation? Cible,
    decimal Valeur,                       // feuille : coût de la ligne (DDVALO, papier, port)
    IReadOnlyList<int> Valeurs,           // feuille : valeur de légende par chapitre (15), 0 = vide
    IReadOnlyList<int> Pourcents,         // feuille : % par chapitre (15), 0 = vide
    bool PapierStock = false,             // papier de provenance S : pas de majoration de frais généraux
    decimal Quantite = 0,                 // opération : résultat (DDRSQT)
    decimal PrixMachine = 0,              // opération : DDPRMA
    decimal PrixMainOeuvre = 0)           // opération : DDPRMO
{
    public bool EstFeuille => Cible is not null;
}

// Montants du devis utilisés par le calcul (D5000F et frais généraux de la société AA000Z)
public sealed record ParametresDecomposition(
    string MethodeValorisation,           // AA000Z.CFTYPE : « B » = valorisation globale
    int Quantite,                         // DEQTED
    decimal Pra,                          // DEPRA
    decimal PrixRevient,                  // DEPR
    decimal Commission,                   // DEMCO
    decimal Escompte,                     // DEMES
    decimal Ristourne,                    // DEMRI
    decimal MajorationReglement,          // DEMCR
    decimal FraisFinanciers,              // DEMFF
    decimal FraisStockage,                // DEMFS
    decimal FraisAdministratifs,          // DEMFA
    decimal FraisManutention,             // DEMFMA
    decimal FgPapier,                     // CKFG1 (%)
    decimal FgMainOeuvre,                 // CKFG2
    decimal FgMachine,                    // CKFG3
    decimal FgSousTraitance,              // CKFG4
    decimal FgMatiere);                   // CKFG5

public sealed record DecompositionDevis(
    EnteteComplement Entete,
    decimal PrixRemis,                    // DEPVR lu, dans l'unité du devis
    string Unite,                         // DEUNIT lu
    IReadOnlyList<UniteVente> Unites,     // AA00083 « UV »
    IReadOnlyList<ChapitreLegende> Legende,
    IReadOnlyList<LigneDecomposition> Lignes,
    ParametresDecomposition Parametres);

public sealed record MontantsDecomposition(decimal PrixRevient, decimal PrixVente, decimal PrixVenteUnitaire);

public sealed record CalculDecomposition(
    IReadOnlyDictionary<int, MontantsDecomposition> Lignes,   // par Id (nœuds : cumul de leurs feuilles)
    decimal TotalPrixRevient,
    decimal TotalPrixVente,
    decimal? Coefficient,                 // null tant qu'aucun prix remis n'est saisi
    int Tnum,                             // diviseur de l'unité choisie (999999999 = le tout)
    string LibelleUnitaire);              // « PV unitaire pour 1000 » ou « PV pour le tout »

// Ventilation d'une feuille à enregistrer (Id : pour signaler l'erreur à l'écran)
public sealed record VentilationLigne(int Id, CibleVentilation Cible, IReadOnlyList<int> Valeurs, IReadOnlyList<int> Pourcents);

// Chapitre : index 0 à 14 de la cellule en erreur, null si l'erreur porte sur la ligne entière
public sealed record ErreurDecomposition(int IdLigne, int? Chapitre, string Message);

public sealed record SaisieDecomposition(
    decimal PrixRemis,
    string Unite,
    IReadOnlyList<ChapitreLegende> Legende,
    IReadOnlyList<VentilationLigne> Lignes,
    decimal PrixRemisLu,                  // valeurs lues à l'ouverture : refus si le devis a changé entre-temps
    string UniteLue);

// Prix du devis après enregistrement (mise à jour de la liste des devis)
public sealed record PrixDevis(decimal PvMini, decimal PvConseille, decimal PvRemis);
