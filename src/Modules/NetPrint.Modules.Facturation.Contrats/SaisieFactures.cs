namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Saisie d'une facture à partir de BL (FAC085 → FAC150, option FA de la fiche BL) : on choisit un BL, une commande ou un donneur
// d'ordre, on coche ses BL facturables pas encore facturés, la facture est préparée comme en facturation automatique (une
// commande = un élément), on corrige l'en-tête et les lignes, puis on la crée (n° pris à la création, BL marqués facturés).
// Les BL n'ont pas besoin d'être « à prendre en facturation automatique ».
// =====================================================================

// Clé de facturation (FAC085) : un BL, une commande (n° + clé) ou un donneur d'ordre ; une seule
public sealed class CleFacturation
{
    public int? Bl { get; set; }
    public int? Commande { get; set; }
    public string Cle { get; set; } = "";
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
}

public sealed record ElementDeBl(int Bl, int Element);

// Élément de BL facturable pas encore facturé ; Propose : coché d'office (BL ou commande de la clé)
public sealed record BlAFacturer(int Bl, int Element, DateOnly? Expedition, int Commande, string Cle, string Designation, string Reference,
    decimal Quantite, bool Retour, bool Propose);

// BL du donneur d'ordre ; Erreurs : clé refusée ; Ecartes : BL facturables qu'on ne peut pas facturer (commande introuvable)
public sealed record BlsAFacturer(IReadOnlyList<ErreurLancement> Erreurs, int CompteGeneral, int CompteClient, string NomClient,
    IReadOnlyList<BlAFacturer> Bls, IReadOnlyList<BlEcarte> Ecartes, bool Tronque);

// Type de ligne de la société (FAC020) : V vente, P port taxable, E port exonéré, R remise, T texte
public sealed record CodeLigneFacture(string Code, string Type, string Libelle);

public sealed record CodeLibelle(string Code, string Libelle);

// Ligne de facture modifiable ; Remise : pourcentage d'une ligne de remise (sinon montant saisi dans Pu) ; Montant calculé.
// Les zones en init gardent l'origine de la ligne (article, ligne de commande, BL) jusqu'à FAC100 ; PrixLibre : prix à 0 admis
// (article gratuit de la commande, complément du dossier, élément en plus du BL), comme en facturation automatique.
public sealed class LigneSaisie
{
    public string CodeLigne { get; set; } = "";
    public string Designation { get; set; } = "";
    public decimal Quantite { get; set; }
    public string Unite { get; set; } = "";
    public decimal Pu { get; set; }
    public int Remise { get; set; }
    public decimal Montant { get; set; }
    public bool PrixLibre { get; init; }
    public string Origine { get; init; } = "";
    public object? Conserve { get; init; }        // zones FAC100 non gérées d'une ligne existante, réécrites telles quelles   // complément de la commande ou élément en plus du BL : supprimé, il reste à facturer
    public string Article { get; init; } = "";
    public string Indice { get; init; } = "";
    public decimal TauxCommission { get; init; }
    public string LigneBl { get; init; } = "N";
    public int LigneCommande { get; init; }
    public int Bl { get; init; }
    public int ElementBl { get; init; }
    public int LigneDuBl { get; init; }
}

// Élément de facture : une commande et ses BL ; Monnaie : celle de la commande, reportée sur les lignes (FAC100.FGMON)
public sealed class ElementSaisie
{
    public int Numero { get; init; }
    public string Monnaie { get; init; } = "";
    public int Commande { get; init; }
    public string Cle { get; init; } = "";
    public string Designation { get; init; } = "";
    public string Categorie { get; set; } = "";       // AA0009, choisie seulement pour un élément sans commande
    public string SousCategorie { get; set; } = "";   // AE0003
    public IReadOnlyList<int> Bls { get; init; } = [];
    public List<LigneSaisie> Lignes { get; init; } = [];
    public decimal Ventes { get; set; }
}

// Facture en cours de saisie ; Avoir : BL de retour ; Bls : éléments de BL facturés (relus à la création) ;
// Numero et DateOrigine : facture existante en modification ou à compléter (null en création) ; Ajout : BL ajoutés à cette facture
// (nouveaux éléments, en-tête inchangé)
public sealed class FactureSaisie
{
    public int? Numero { get; init; }
    public DateOnly? DateOrigine { get; init; }
    public bool Ajout { get; init; }
    public (int Numero, string Cle)? CommandeSansBl { get; init; }   // facture d'une commande sans BL (relue à la création)
    public string? Avertissement { get; init; }   // à lire avant de créer (ex. : commande déjà sur une facture pas encore éditée)
    public bool SansCommande { get; init; }   // facture d'un client sans commande : catégorie et sous-catégorie à choisir
    public string CodeVente { get; init; } = "";   // code vente par défaut (client ou commande), pour les lignes ajoutées
    public DateOnly? PeriodeDebut { get; set; }   // avoir sans facture d'origine : période de référence (remise de fin d'année)
    public DateOnly? PeriodeFin { get; set; }
    public bool Modification => Numero is not null && !Ajout;
    public bool Avoir { get; init; }
    public int CompteGeneral { get; init; }
    public int CompteClient { get; init; }
    public string NomClient { get; init; } = "";
    public DateOnly? Date { get; set; }
    public int? CompteGeneralFacture { get; set; }
    public int? CompteClientFacture { get; set; }
    public string ModeReglement { get; set; } = "";
    public int NombreJours { get; set; }
    public int Le { get; set; }
    public int Arrete { get; set; }
    public decimal Escompte { get; set; }
    public int? Representant { get; set; }
    public string Reference { get; set; } = "";
    public string AlAttention { get; set; } = "";
    public decimal Acompte { get; init; }
    public bool PeutEditerDefinitif { get; init; }
    public IReadOnlyList<ElementDeBl> Bls { get; init; } = [];
    public string Empreinte { get; init; } = "";   // ce que la préparation a lu (BL, lignes, prix) : contrôlé à la création
    public List<ElementSaisie> Elements { get; init; } = [];

    // Listes de choix et unités de vente (code → diviseur, table UV) pour le calcul des montants
    public IReadOnlyList<CodeLigneFacture> CodesLigne { get; init; } = [];
    public IReadOnlyList<CodeLibelle> ModesReglement { get; init; } = [];
    public IReadOnlyList<CodeLibelle> Representants { get; init; } = [];
    public IReadOnlyDictionary<string, decimal> Unites { get; init; } = new Dictionary<string, decimal>();
    public IReadOnlyList<CodeLibelle> Categories { get; init; } = [];
    public IReadOnlyList<CodeLibelle> SousCategories { get; init; } = [];   // code « catégorie/sous-catégorie »
}

// Contrôle de la saisie : Erreurs bloquantes ; Anomalies : la facture sera créée « à contrôler » (comme en automatique)
public sealed record ControleFactureSaisie(IReadOnlyList<ErreurLancement> Erreurs, IReadOnlyList<string> Anomalies, decimal Ventes);

public sealed record ChiffreAffairesPeriode(decimal ChiffreAffaires, int Documents);

// Facture créée ; Avertissement : créée, mais l'édition définitive demandée a échoué
public sealed record FactureCreee(int Numero, DateOnly Date, bool AControler, bool EditeeDefinitif, string? Avertissement);

public interface ISaisieFactures
{
    // BL facturables du donneur d'ordre de la clé (FAC085 R1 à R6)
    Task<BlsAFacturer> ChercherBlsAsync(CleFacturation cle, CancellationToken ct = default);

    // Facture préparée à partir des BL cochés (lignes des BL et des commandes, compléments, remise), modifiable avant la création
    Task<FactureSaisie> PreparerAsync(IReadOnlyList<ElementDeBl> bls, CancellationToken ct = default);

    // Facture d'une commande sans BL (acompte, frais, prestation) : reste à facturer de la commande (fabrication : quantité
    // commandée − facturée ; négoce : lignes pas entièrement facturées), compléments, remise ; ses BL restent à facturer
    Task<FactureSaisie> PreparerCommandeAsync(int commande, string cle, CancellationToken ct = default);

    // Facture d'un client sans commande (FAC085 clé client, élément sans dossier) : en-tête du client, catégorie et sous-catégorie
    // à choisir, lignes saisies
    // avoir : avoir du client sans facture d'origine (remise de fin d'année : période de référence)
    Task<FactureSaisie> PreparerClientAsync(int compteGeneral, int compteClient, bool avoir = false, CancellationToken ct = default);

    // Chiffre d'affaires HT facturé au client sur une période (factures − avoirs édités, hors remises de fin d'année), pour calculer
    // une remise de fin d'année
    Task<ChiffreAffairesPeriode> ChiffreAffairesAsync(int compteGeneral, int compteClient, DateOnly du, DateOnly au, CancellationToken ct = default);

    // Montants des lignes et des éléments recalculés, saisie contrôlée (sans base)
    ControleFactureSaisie Recalculer(FactureSaisie facture);

    // Création (FAC150 F9) dans une transaction : BL relus, n° pris au compteur ; puis édition définitive si demandée et sans anomalie
    Task<FactureCreee> CreerAsync(FactureSaisie facture, bool editerDefinitif, CancellationToken ct = default);

    // Facture existante pas encore éditée (« à éditer » ou « à contrôler ») ouverte en modification (FAC150 mode M)
    Task<FactureSaisie> OuvrirAsync(int numero, DateOnly date, CancellationToken ct = default);

    // Enregistrement de la modification dans une transaction : en-tête, lignes réécrites, et changement de date reporté sur tous les
    // fichiers de la facture (FAC, BL, compléments) ; puis édition définitive si demandée et sans anomalie
    Task<FactureCreee> ModifierAsync(FactureSaisie facture, bool editerDefinitif, CancellationToken ct = default);

    // Suppression d'une facture pas encore éditée, ou d'un seul de ses éléments (FAC160, FAC165) : ses BL, éléments en plus et
    // compléments redeviennent à facturer. Renvoie vrai si toute la facture est supprimée (demandée entière ou dernier élément).
    Task<bool> SupprimerAsync(int numero, DateOnly date, int? element = null, CancellationToken ct = default);

    // Facture éditée mais pas comptabilisée remise « à éditer » (FAC166) pour pouvoir la modifier : édition définitive annulée
    Task RemettreAEditerAsync(int numero, DateOnly date, CancellationToken ct = default);

    // Ajout de BL à une facture pas encore éditée (FAC080S option AE → FAC085 → FAC150 mode A) : BL facturables de son donneur
    // d'ordre, du même type (livrés pour une facture, retours pour un avoir), aucun coché d'office
    Task<BlsAFacturer> ChercherBlsAjoutAsync(int numero, DateOnly date, CancellationToken ct = default);

    // Nouveaux éléments préparés (une commande = un élément), lignes modifiables ; l'en-tête de la facture ne change pas
    Task<FactureSaisie> PreparerAjoutAsync(int numero, DateOnly date, IReadOnlyList<ElementDeBl> bls, CancellationToken ct = default);

    // Ajout dans une transaction (éléments numérotés à la suite, BL marqués facturés) ; puis édition définitive si demandée et sans anomalie
    Task<FactureCreee> AjouterAsync(FactureSaisie facture, bool editerDefinitif, CancellationToken ct = default);

    // Copie d'une facture (FAC080S option 3 → FAC150 mode D) : nouvelle facture du jour, n° pris au compteur, « à éditer », mêmes
    // en-tête, éléments et lignes, sans acompte ni BL rattachés ; à corriger ensuite en modification
    Task<FactureCreee> CopierAsync(int numero, DateOnly date, CancellationToken ct = default);

    // Avoir d'une facture éditée (FAC080S option SA) : avoir du jour, « à éditer », facture d'origine renseignée ; total (bl null :
    // toute la facture, ses BL redeviennent à facturer) ou d'un BL (ses lignes, ce BL redevient à facturer) ; à ajuster en modification.
    // nePlusFacturer : annulation définitive, les BL libérés deviennent « non facturables » au lieu de « à facturer »
    Task<FactureCreee> CreerAvoirAsync(int numero, DateOnly date, int? bl = null, bool nePlusFacturer = false, CancellationToken ct = default);
}
