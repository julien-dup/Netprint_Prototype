using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Pré-BL palettes (WinDev PreBL / PreBLSansPalette, FEN_GestionPreBL) : brouillon de BL saisi pour un client et une adresse,
// lignes de commande, lots et palettes SSCC (EXP509, EXP510, EXP516, EXP517, EXP512). Les palettes sont pointées sur les
// terminaux (web services PHP : PROD_ENTITE statut 4), puis le pré-BL est transformé en BL de même numéro.
// Variante sans palette (EXP005.FEPREB = O) : on saisit une quantité par lot, les palettes viennent des scans.
// Droits : ceux des OPR (PRT000P1, programme OPR100), comme le WinDev.
// =====================================================================
public interface IPreBl
{
    Task<DroitsOpr> DroitsAsync(CancellationToken ct = default);
    Task<ResultatRecherche<ElementPreBl>> RechercherAsync(CriteresPreBl criteres, CancellationToken ct = default);
    Task<FichePreBl?> LireAsync(int numero, CancellationToken ct = default);
}

public static class EtatsPreBl
{
    public const string APreparer = "À préparer";
    public const string EnPreparation = "En préparation";
    public const string Livre = "Livré";
}

public sealed class CriteresPreBl
{
    public int? Numero { get; set; }
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public int? Commande { get; set; }
    public string? Cle { get; set; }
    public DateOnly? DateDepart { get; set; }
    public bool AvecLivres { get; set; }
}

// Palettes : palettes SSCC des lots du pré-BL (EXP517) ; Pointees : au statut 4 (conditionnées au terminal) ; Picking : prises
// en partie ; Pret : toutes pointées, sans picking (la fiche contrôle en plus les lignes sans palette et les lots à produire)
public sealed record ElementPreBl(
    int Numero, string Etat, int CompteGeneral, int CompteClient, string NomClient, DateOnly? DateDepart, AdresseBl Adresse,
    int Transporteur, string NomTransporteur, string Profil, DateOnly? DateCreation, bool StockPaye, int Lignes, int Palettes,
    int Pointees, int Picking, bool Pret);

// Refus : pourquoi le pré-BL ne peut pas être transformé en BL ("" s'il le peut ; jamais vide pour un pré-BL livré)
public sealed record FichePreBl(
    int Numero, string Etat, bool SansPalette, int CompteGeneral, int CompteClient, string NomClient, AdresseBl Adresse,
    DateOnly? DateDepart, int Transporteur, string NomTransporteur, string TypeLivraison, string TypePort,
    IReadOnlyList<string> Observations, string Mention, int Colis, int Palettes, int Poids, bool StockPaye, string Profil,
    DateOnly? DateCreation, IReadOnlyList<LignePreBl> Lignes, IReadOnlyList<string> Pied, string Refus);

// Une ligne de commande livrée par le pré-BL (EXP510) ; Frais : article non stocké (pas de lot ni de palette)
public sealed record LignePreBl(
    int Numero, int Dossier, string Cle, int LigneCommande, string ReferenceCommande, string Appel, string Article, string Indice,
    string RefImprime, int Quantite, IReadOnlyList<string> Designations, bool AppelASolder, string EtatLivraison, int Palettes,
    int Colis, int Incomplets, bool Frais, IReadOnlyList<LotPreBl> Lots);

// Un lot prévu (EXP516) ; AProduire : OF pas encore produit (type 2, « A PROD ») ; Cloture : préparation clôturée au terminal
public sealed record LotPreBl(
    int Numero, bool AProduire, string Lot, int Depot, int Quantite, int Palettes, int PaletteMixte, int Colis,
    string LibellePalettes, bool Cloture, IReadOnlyList<string> Observations, IReadOnlyList<PalettePreBl> PalettesSscc);

// Statut : PROD_ENTITE.STATUT ("" si la palette n'existe plus) ; Pointee : statut 4 (conditionnée au terminal)
public sealed record PalettePreBl(string Sscc, int Quantite, bool Picking, string Statut, string Observation)
{
    public bool Pointee => Statut == "4";
}
