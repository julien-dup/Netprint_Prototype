using NetPrint.Socle;

namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Consultation des commandes clients de négoce (CCTYPC 3) et de produit fini (CCTYPC 4).
// Reprise de la liste des dossiers DOS500S (5250) et de l'onglet « Cdes PFS » de HistoriqueDossiers (WinDev).
// Lecture seule. La société, le représentant imposé et les droits viennent toujours de la session.
// Les OF (type 2) et les commandes simples (type 1) relèvent du module Production.
// =====================================================================
public interface IConsultationCommandes
{
    // Contexte de l'écran : représentant imposé (B000F.ILIMIT = 'O'), droits PRT000P1 du programme DOS500C
    Task<ContexteCommandes> LireContexteAsync(CancellationToken ct = default);

    // Recherche multicritère : une ligne par ligne de commande (NEG100), regroupées par commande
    Task<ResultatRechercheCommandes> RechercherAsync(CriteresCommandes criteres, CancellationToken ct = default);

    // Fiche d'une commande (NEG510 / FEN_commandes) : null si elle n'existe pas ou n'est pas visible par le représentant imposé
    Task<FicheCommande?> LireAsync(CleCommande cle, CancellationToken ct = default);
}

// Clé d'une commande : n° de dossier (RDOSS) + lettre-clé (CCDCLE), souvent un chiffre : affichée « 68916/9 »
public sealed record CleCommande(int Numero, string Cle)
{
    public string Libelle => $"{Numero:00000}/{Cle}";
}

public static class TypesCommande
{
    public const string Negoce = "3";
    public const string ProduitFini = "4";

    public static string Libelle(string type) => type switch
    {
        Negoce => "Négoce",
        ProduitFini => "Produit fini",
        _ => type,
    };
}

// Droits de la gestion des dossiers (PRT000P1, programme DOS500C ; « ** » = interdit)
public sealed record DroitsCommandes(bool Creer, bool Modifier, bool Supprimer, bool Editer)
{
    public static readonly DroitsCommandes Tous = new(true, true, true, true);
}

public sealed record ContexteCommandes(int? RepresentantImpose, DroitsCommandes Droits);

// ---------------------------------------------------------------------
// Critères (DOS50001 + FEN_Historiquedossiers). Tous facultatifs.
// ---------------------------------------------------------------------
public sealed class CriteresCommandes
{
    // Principaux (au moins un obligatoire)
    public int? Numero { get; set; }
    public string? Cle { get; set; }
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public int? Representant { get; set; }
    public int? Devis { get; set; }
    public int? Produit { get; set; }                 // n° de produit de l'article (SPF010.AMODEL) ; client obligatoire
    public string? Article { get; set; }              // jokers « * » en début ou en fin
    public string? Indice { get; set; }
    public string? ReferenceImprime { get; set; }     // commence par
    public string? ReferenceCommandeClient { get; set; }  // commence par
    public string? NumeroAppel { get; set; }
    public string? Fabricant { get; set; }
    public int? BonLivraison { get; set; }
    public int? Facture { get; set; }
    public string? Categorie { get; set; }            // famille de l'article (SPF010.AFAM)
    public string? SousCategorie { get; set; }
    public string? Designation { get; set; }          // chaque mot dans la désignation de la ligne ou de la commande
    public DateOnly? DateDu { get; set; }             // date d'ouverture du dossier
    public DateOnly? DateAu { get; set; }
    public DateOnly? LivraisonDu { get; set; }        // date de livraison de la ligne
    public DateOnly? LivraisonAu { get; set; }

    // Filtres qui ne suffisent pas seuls
    public string TypeCommande { get; set; } = "";    // "" les deux, "3" négoce, "4" produit fini
    public bool AvecSoldees { get; set; }
    public string? Enseigne { get; set; }
    public decimal? FiniLargeur { get; set; }         // format fini de l'article
    public decimal? FiniHauteur { get; set; }
    public decimal? FiniTroisiemeCote { get; set; }

    public CriteresCommandes Copier() => (CriteresCommandes)MemberwiseClone();
}

// ---------------------------------------------------------------------
// Résultat : commandes (en-tête AB001) et leurs lignes (NEG100)
// ---------------------------------------------------------------------
public enum EtatCommande { EnCours, PartiellementLivree, Livree, Soldee }

public sealed record ResultatRechercheCommandes(IReadOnlyList<CommandeListe> Commandes, bool Tronque)
{
    public IEnumerable<LigneCommandeListe> Lignes => Commandes.SelectMany(c => c.Lignes);
}

public sealed record CommandeListe(
    CleCommande Cle,
    string TypeCommande,
    string Client,                 // « 411000 1234 »
    string NomClient,
    string ClientFacture,          // nom du client facturé s'il diffère
    string ReferenceCommandeClient,
    string Designation,
    DateOnly? DateOuverture,
    DateOnly? DateCommandeClient,
    DateOnly? DateLivraison,       // première date de livraison des lignes non soldées, sinon celle de l'en-tête
    DateOnly? DateSolde,
    string Representant,
    string Fabricant,
    string Enseigne,
    EtatCommande Etat,
    decimal Valeur,                // somme des valeurs des lignes
    int NbDocuments,
    IReadOnlyList<LigneCommandeListe> Lignes)
{
    public string Numero => Cle.Libelle;
    public int NbLignes => Lignes.Count;
    public bool Soldee => Etat == EtatCommande.Soldee;

    public decimal? TauxLivraison => AvancementLivraison.Taux(Lignes);
}

public static class AvancementLivraison
{
    // Part livrée des lignes fermes (hors lignes « à la demande », livrées par leurs appels), de 0 à 1 ;
    // une sur-livraison compte pour la quantité commandée ; null si aucune quantité ferme
    public static decimal? Taux(IEnumerable<LigneCommandeListe> lignes)
    {
        var fermes = lignes.Where(l => !l.ALaDemande && l.Quantite > 0).ToList();
        var total = fermes.Sum(l => (decimal)l.Quantite);
        return total == 0 ? null : fermes.Sum(l => (decimal)Math.Min(l.QuantiteLivree, l.Quantite)) / total;
    }
}

public sealed record LigneCommandeListe(
    CleCommande Commande,
    int NumeroLigne,
    string Article,
    string Indice,
    string ReferenceImprime,
    string Designation,
    string Texte,                  // texte libre de la ligne (NEG100T), pour la bulle
    bool ALaDemande,               // statut 3 : commande ouverte avec appels de livraison
    bool EstAppel,                 // appel de livraison (statut 1 rattaché à une ligne d'origine)
    string NumeroAppel,
    DateOnly? DateLivraison,
    int Quantite,
    int QuantiteLivree,
    int QuantiteFacturee,
    int ResteALivrer,
    decimal PrixUnitaire,
    string Unite,
    bool Gratuite,
    bool PrixADefinir,
    decimal Valeur,
    string Norme,
    int Produit,
    string Categorie,
    string SousCategorie,
    bool Soldee,
    int NbCommentaires)
{
    public string Prix => Gratuite ? "Gratuit" : PrixADefinir ? "Prix à définir" : PrixUnitaire == 0 ? "" : PrixUnitaire.ToString("N2");
}

// ---------------------------------------------------------------------
// Fiche d'une commande (NEG510 : menu de la commande ; FEN_commandes de HistoriqueDossiers)
// ---------------------------------------------------------------------
public sealed record FicheCommande(
    EnteteCommande Entete,
    IReadOnlyList<LigneFiche> Lignes,
    IReadOnlyList<LivraisonCommande> Livraisons,
    IReadOnlyList<FactureCommande> Factures,
    IReadOnlyList<EvenementLigne> Journal,          // NEG100E : créations, modifications, suppressions de lignes
    IReadOnlyList<HistoriqueDossier> Historique,    // DOS040 : créations, modifications, soldes du dossier
    int NbDocuments,
    DroitsCommandes Droits,
    bool JournalTronque = false,                    // seuls les mouvements les plus récents sont lus
    bool HistoriqueTronque = false)
{
    public decimal? TauxLivraison => AvancementLivraison.Taux(Lignes.Select(l => l.Ligne));
}

public static class TypesFacturation
{
    public static string Libelle(string code) => code switch
    {
        "1" => "À chaque livraison",
        "2" => "En totalité",
        "3" => "En totalité, multi-échéance",
        _ => code,
    };
}

public sealed record EnteteCommande(
    CleCommande Cle,
    string TypeCommande,
    string TypeFacturation,
    string Client,                    // « 411100 376 »
    string NomClient,
    string ClientFacture,             // « 411100 376 NOM » s'il diffère du client
    string Enseigne,
    string Designation1,
    string Designation2,
    string ReferenceCommandeClient,
    string Interlocuteur,
    DateOnly? DateOuverture,
    DateOnly? DateCommandeClient,
    DateOnly? DateLivraison,
    string SemaineLivraison,          // « S45/2026 » si la livraison est donnée à la semaine
    string Representant,
    decimal TauxCommission,
    string Fabricant,
    string Categorie,
    string SousCategorie,
    string Profil,
    string Livraison,                 // « À livrer » / « Enlèvement par le client »
    string Port,                      // CCPORT D / A / F
    string Reglement,                 // mode + échéance
    string Devise,
    string CodeVente,                 // code ligne de vente par défaut (CCTYPV)
    int TauxRemise,                   // CTXRM
    bool Revendeur,                   // CCRCVD
    int AdresseArc,                   // n° d'adresse d'envoi de l'ARC (CCNAEA)
    bool NonModifiable,               // CCOP11 = 'O' : « Commande sous-traitée. Modification impossible » (NEG510 R3)
    bool SousTraitee,                 // CCOP10 = 'O' : sous-traitée à un autre site (zone « Sous-traité » de DOS505, SYN100)
    DateOnly? DateSolde,
    EtatCommande Etat,
    decimal Valeur)
{
    public bool Soldee => Etat == EtatCommande.Soldee;
}

// Ligne de la fiche : la ligne de liste et ses compléments
public sealed record LigneFiche(
    LigneCommandeListe Ligne,
    string AdresseLivraison,
    DateOnly? DateConfirmee,
    string Commentaires,
    decimal TauxCommission,
    int TauxRemise,
    int LigneOrigine,                 // appel de livraison : n° de la ligne « à la demande »
    int QuantiteAppelee,              // ligne « à la demande » : total de ses appels
    int MaxiLivrable,
    decimal PrixCession,
    string Devis,                     // « 12345/0/1 »
    string ClientFacture,
    string Emplacement);

public sealed record LivraisonCommande(
    int BonLivraison,
    DateOnly? DateExpedition,
    string Adresse,
    bool Facture,
    int NumeroFacture,
    DateOnly? DateFacture,
    IReadOnlyList<LigneLivree> Lignes);

public sealed record LigneLivree(int LigneCommande, string Article, string Designation, int Quantite);

public sealed record FactureCommande(int Numero, DateOnly? Date, int Quantite, decimal Montant);

public sealed record EvenementLigne(
    DateTime? Date, string Type, int Ligne, string Profil,
    string AncienArticle, string Article, int AncienneQuantite, int Quantite, decimal AncienPrix, decimal Prix)
{
    public string LibelleType => Type switch { "C" => "Création", "M" => "Modification", "S" => "Suppression", _ => Type };
}

public sealed record HistoriqueDossier(DateTime? Date, string Type, string Initiales);
