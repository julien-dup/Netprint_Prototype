namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Saisie d'un pré-BL (WinDev FEN_CreationPREBL, FEN_SaisiePREBL, FEN_AjoutLigneCde, FEN_AjoutLigne, FEN_AjoutLigneFrais,
// FI_AjoutPalettes, FI_AjoutPalettesEXP517) : un client et une adresse de livraison, des lignes de commande, pour chaque
// article stocké ses lots et ses palettes SSCC (ou une quantité par lot si la société est « sans palette », EXP005.FEPREB).
// Création (n° pris sur le compteur des BL), modification et suppression en une transaction. Droits des OPR (OPR100).
// =====================================================================
public interface ISaisiePreBl
{
    // Nouveau pré-BL pour un client et une adresse : valeurs par défaut (transport, observations de l'adresse, pied du client)
    Task<SaisiePreBl> NouveauAsync(int compteGeneral, int compteClient, int numeroAdresse, CancellationToken ct = default);

    // Pré-BL existant à modifier (refusé s'il est transformé en BL)
    Task<SaisiePreBl> OuvrirAsync(int numero, CancellationToken ct = default);

    // Lignes de la commande livrables par ce pré-BL (même client, même adresse, pas dans un autre pré-BL non livré)
    Task<IReadOnlyList<LigneCommandePreBl>> LignesCommandeAsync(SaisiePreBl saisie, int commande, string cle, CancellationToken ct = default);

    // Lots de l'article (dépôts facturés ou non selon « stock payé ») et leurs palettes disponibles pour ce pré-BL
    Task<IReadOnlyList<LotDisponiblePreBl>> LotsAsync(SaisiePreBl saisie, string article, string indice, CancellationToken ct = default);

    // Rend le n° du pré-BL (créé ou modifié)
    Task<int> EnregistrerAsync(SaisiePreBl saisie, CancellationToken ct = default);

    Task SupprimerAsync(int numero, CancellationToken ct = default);

    // Récapitulatif palettes (FEN_RecapPalettes) : lots des articles stockés, n° de palette mixte pour les lots incomplets ;
    // Calculer : n° des palettes sans rien écrire ; Enregistrer : n° des palettes (EXP516) et nombre de palettes (EXP509)
    Task<RecapPalettesPreBl> RecapAsync(int numero, CancellationToken ct = default);
    Task<RecapPalettesPreBl> CalculerRecapAsync(int numero, IReadOnlyList<GroupePaletteMixte> groupes, CancellationToken ct = default);
    Task EnregistrerRecapAsync(int numero, IReadOnlyList<GroupePaletteMixte> groupes, CancellationToken ct = default);
}

// Lot d'une ligne (n° de ligne, n° de lot) et n° de palette mixte choisi (0 : aucune)
public sealed record GroupePaletteMixte(int Ligne, int Lot, int PaletteMixte);

// Erreur : groupe refusé (lot incomplet sans palette mixte, palette mixte sur deux dépôts ou de plus de 8 lots) ; NombrePalettes :
// palettes complètes puis mixtes
public sealed record RecapPalettesPreBl(int Numero, IReadOnlyList<LigneRecapPreBl> Lignes, int NombrePalettes, string Erreur);

// Une ligne de lot : ExParPalette = emballage par × UC par palette ; ExHorsPalettes : quantité hors palettes complètes ;
// ColisPicking : colis pris en picking ; Incomplete : demande un n° de palette mixte ; Libelle : « N° 1 à 3 », « N° 4 et n° 7 »
public sealed record LigneRecapPreBl(
    int Ligne, int Lot, int Depot, string Article, string Indice, string RefImprime, string Designation, int Dossier, string Cle,
    string NumeroLot, int Quantite, int EmballagePar, string Caisse, int Colis, int ColisIncomplets, int ExIncomplets, int ExParPalette,
    int PalettesCompletes, int ExHorsPalettes, int ColisPicking, bool Incomplete, int PaletteMixte, string Libelle);

public sealed class SaisiePreBl
{
    public int Numero { get; set; }
    public bool SansPalette { get; set; }
    public int CompteGeneral { get; set; }
    public int CompteClient { get; set; }
    public string NomClient { get; set; } = "";
    public AdresseBl Adresse { get; set; } = new(0, "", "", "", "", "", "", "", "");
    public DateOnly DateDepart { get; set; }
    public int Transporteur { get; set; }
    public string TypeLivraison { get; set; } = "";
    public string TypePort { get; set; } = "";
    public bool StockPaye { get; set; }
    public string[] Observations { get; set; } = ["", "", ""];
    public string Mention { get; set; } = "";
    public int Poids { get; set; }
    public string Pied { get; set; } = "";
    public string Etat { get; set; } = EtatsPreBl.APreparer;
    public List<LigneSaisiePreBl> Lignes { get; set; } = [];
}

// Une ligne de commande du pré-BL ; Stocke : article géré en stock (lots et palettes), sinon frais (quantité seule).
// Conditionnement : SPF026 (client, adresse) sinon SPF010 ; exceptionnel saisi (emballage par et UC par palette, l'un impose
// l'autre). Numero : n° de ligne du pré-BL (0 = nouvelle), gardé à la modification (les palettes scannées y sont rattachées).
public sealed class LigneSaisiePreBl
{
    public int Numero { get; set; }
    public int Dossier { get; set; }
    public string Cle { get; set; } = "";
    public int LigneCommande { get; set; }
    public string ReferenceCommande { get; set; } = "";
    public string Appel { get; set; } = "";
    public string Article { get; set; } = "";
    public string Indice { get; set; } = "";
    public string RefImprime { get; set; } = "";
    public string Designation { get; set; } = "";
    public bool Stocke { get; set; }
    public int Reste { get; set; }
    public int Quantite { get; set; }
    public string[] Observations { get; set; } = ["", "", ""];
    public bool AppelASolder { get; set; }
    public string EtatLivraison { get; set; } = "T";
    public int EmballagePar { get; set; }
    public int UcParPalette { get; set; }
    public string TypeEmballage { get; set; } = "";
    public int EmballageParExceptionnel { get; set; }
    public int UcParPaletteExceptionnel { get; set; }
    public int CompteGeneralFacture { get; set; }
    public int CompteClientFacture { get; set; }
    public List<LotSaisiePreBl> Lots { get; set; } = [];
}

// Un lot d'une ligne ; Numero : n° de lot du pré-BL (0 = nouveau) ; Verrouille : palettes en préparation ou scannées, le lot ne
// peut plus être retiré ni changé ; Cloture, LibellePalettes, PaletteMixte : gardés tant que le lot ne change pas
public sealed class LotSaisiePreBl
{
    public int Numero { get; set; }
    public string Lot { get; set; } = "";
    public int Depot { get; set; }
    public int Quantite { get; set; }
    public bool Verrouille { get; set; }
    public List<PaletteSaisiePreBl> Palettes { get; set; } = [];
}

// Palette prise en entier, ou en partie (picking : Quantite < disponible)
public sealed class PaletteSaisiePreBl
{
    public string Sscc { get; set; } = "";
    public int Quantite { get; set; }
    public bool Picking { get; set; }
    public string Statut { get; set; } = "";
}

// Ligne de commande proposée ; Facture : client à facturer de la ligne, sinon du dossier
public sealed record LigneCommandePreBl(
    int Dossier, string Cle, int LigneCommande, string ReferenceCommande, string Appel, string Article, string Indice, string RefImprime,
    string Designation, int Reste, DateOnly? DateLivraison, bool Stocke, int EmballagePar, int UcParPalette, string TypeEmballage,
    int CompteGeneralFacture, int CompteClientFacture);

// Lot en stock (AL00F) d'un dépôt autorisé ; palettes de statut 1 à 4 hors OPR, avec leur quantité encore disponible
public sealed record LotDisponiblePreBl(string Lot, int Depot, string NomDepot, int Stock, IReadOnlyList<PaletteDisponiblePreBl> Palettes);

// Selectionnable : statut 1 ou 2 (fabriquée, en stock) ; 3 et 4 (en préparation) seulement si déjà dans ce pré-BL
public sealed record PaletteDisponiblePreBl(string Sscc, int Disponible, int QuantitePalette, string Statut, string Emplacement, DateOnly? Fabrication,
    bool Selectionnable);
