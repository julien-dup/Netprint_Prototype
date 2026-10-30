using NetPrint.Socle;

namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Consultation des devis (reprise de ConsultationDevis WinDev, branche DLLNew)
// Lecture seule : recherche (FEN_ConsultationDevis) et détail (FEN_DetailDevis).
// La société et la restriction représentant viennent toujours de la session.
// =====================================================================
public interface IConsultationDevis
{
    // Contexte de l'écran : représentant imposé (B000F.ILIMIT = 'O' pour le profil connecté)
    Task<ContexteConsultationDevis> LireContexteAsync(CancellationToken ct = default);

    // Bouton « Chargement » de FEN_ConsultationDevis (contrôles de la procédure Controle)
    Task<ResultatRecherche<LigneListeDevis>> RechercherAsync(CriteresDevis criteres, CancellationToken ct = default);

    // FEN_DetailDevis : null si le devis n'existe pas (ou n'est pas visible par le représentant imposé)
    Task<DetailDevis?> LireDetailAsync(CleDevis cle, CancellationToken ct = default);
}

public sealed record ContexteConsultationDevis(int? RepresentantImpose);

// Clé d'un devis : n° + version + hypothèse de quantité + type de coût (RCOUT : T devis type, C devis client)
public sealed record CleDevis(int Numero, int Version, int Hypothese, string TypeCout)
{
    public string Libelle => $"{Numero:00000}/{Version}/{Hypothese}";
}

// ---------------------------------------------------------------------
// Critères (zones SAI_* de FEN_ConsultationDevis). Tous facultatifs.
// ---------------------------------------------------------------------
public sealed class CriteresDevis
{
    // Principaux (au moins un obligatoire)
    public int? Numero { get; set; }
    public int? Etude { get; set; }
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public int? Prospect { get; set; }
    public string? Profil { get; set; }
    public string? Categorie { get; set; }
    public string? SousCategorie { get; set; }
    public DateOnly? DateDu { get; set; }
    public DateOnly? DateAu { get; set; }
    public string? Fabricant { get; set; }
    public int? Representant { get; set; }
    public int? QuantiteDe { get; set; }
    public int? QuantiteA { get; set; }
    public int? Dossier { get; set; }                 // N° OF

    // Filtres de liste
    public string EtatDevis { get; set; } = "";       // "" tous, "1" en cours, "2" en commande (COMBO_Devis)
    public string TypeDevis { get; set; } = "";       // "" tous, "T" devis type, "C" devis client (COMBO_TypeDevis)
    public string? Designation { get; set; }          // chaque mot doit figurer dans DEDES1 ou DEDES2
    public string? ReferenceImprime { get; set; }     // commence par

    // Commercial complémentaire
    public string? Norme { get; set; }
    public int? NbElements { get; set; }
    public int? Modele { get; set; }
    public int? DossierPrecedent { get; set; }
    public int? NbElementsExemplaire { get; set; }
    public string? MotifEchec { get; set; }
    public DateOnly? EchecDu { get; set; }
    public DateOnly? EchecAu { get; set; }

    // Formats du devis
    public decimal? FiniLargeur { get; set; }
    public decimal? FiniHauteur { get; set; }
    public decimal? FiniTroisiemeCote { get; set; }
    public decimal? OuvertLargeur { get; set; }
    public decimal? OuvertHauteur { get; set; }

    // Papier (éléments D60001)
    public int? Papier { get; set; }
    public string? Sorte { get; set; }
    public string? SousSorte { get; set; }
    public int? Grammage { get; set; }
    public int? LargeurPapier { get; set; }
    public int? HauteurPapier { get; set; }
    public string? Coloris { get; set; }
    public int? Epaisseur { get; set; }
    public string UniteEpaisseur { get; set; } = "";  // "", "MM", "/10", "µ"

    // Fabrication (éléments D60001, outils D6001C, lignes D60002)
    public string? MachineTirage { get; set; }
    public int? TirageLargeur { get; set; }
    public int? TirageHauteur { get; set; }
    public int? NbPoses { get; set; }
    public int? Outil { get; set; }
    public string? Section { get; set; }              // « Autre machine »
    public int? Operation { get; set; }
    public int? Matiere { get; set; }
    public int? FournisseurSousTraitance { get; set; }
    public int? OperationSousTraitance { get; set; }

    public CriteresDevis Copier() => (CriteresDevis)MemberwiseClone();
}

// ---------------------------------------------------------------------
// Une ligne de la liste (TABLE_Devis)
// ---------------------------------------------------------------------
public sealed record LigneListeDevis(
    CleDevis Cle,
    string CodeClientProspect,
    string NomClientProspect,
    DateOnly? DateDevis,
    int Quantite,
    string Designation1,
    string Designation2,
    string FormatFini,
    string FormatOuvert,
    int NbElements,
    string Norme,
    string Representant,
    string Fabricant,
    string Contact,
    string Profil,
    decimal PvMini,
    decimal PvConseille,
    decimal PvRemis,
    DateOnly? DatePrix,
    string Categorie,
    string SousCategorie,
    int Modele,
    string Dossier,
    string DossierPrecedent,
    int Etude,
    DateOnly? DateEchec,
    string MotifEchec,
    bool AvecOffre,
    bool EnCommande,
    int NbDocuments,
    decimal? ValeurDevis,
    string ReferenceImprime,
    DateOnly? DateDemande,
    string Observations)
{
    public string Numero => Cle.Libelle;
    public bool EnEchec => MotifEchec != "";
}

// ---------------------------------------------------------------------
// Détail d'un devis (FEN_DetailDevis)
// ---------------------------------------------------------------------
public sealed record DetailDevis(
    CleDevis Cle,
    EnteteDetailDevis Entete,
    IReadOnlyList<OngletAtelier> Ateliers,                 // vue « par atelier »
    IReadOnlyList<LigneMatiere> Matieres,
    IReadOnlyList<LignePapier> Papiers,
    IReadOnlyList<LigneSousTraitance> SousTraitances,
    IReadOnlyList<LigneTransport> Transports,
    IReadOnlyList<LigneFraisAnnexes> FraisAnnexes,
    IReadOnlyList<ElementDetail> Elements);                // vue « par élément »

public sealed record EnteteDetailDevis(
    bool EstClient,
    string CodeTiers,          // « 411000 1234 » ou n° de prospect
    string NomTiers,
    string Designation1,
    string Designation2,
    int Quantite,
    int NbElements,
    string FormatFini,
    string FormatOuvert);

// Nature d'une ligne affichée : titre d'élément (gras), ligne, sous-total de section (gras, bleu)
public enum NatureLigne { Titre, Ligne, SousTotal }

public sealed record LigneOperation(
    NatureLigne Nature,
    string Libelle,
    decimal Base,
    int Nombre,
    string Temps,
    int Cadence,
    int Quantite,
    int Diviseur,
    string Resultat,
    decimal TauxHoraire,
    decimal Valeur,
    decimal ValeurMillePlus,
    string Commentaire);

public sealed record OngletAtelier(string Libelle, IReadOnlyList<LigneOperation> Lignes);

public sealed record LigneMatiere(
    NatureLigne Nature, string Reference, string Designation, decimal Quantite, decimal PrixUnitaire,
    decimal Valeur, decimal ValeurMillePlus, string Commentaire);

public sealed record LigneSousTraitance(
    NatureLigne Nature, string Designation, decimal Quantite, decimal PrixFixe, decimal PrixProportionnel,
    int QuantiteProportionnelle, decimal Valeur, decimal MillePlus, string Commentaire);

public sealed record LignePapier(
    int Element, string Designation, string Sorte, string SousSorte, int Grammage, string FormatPapier,
    string Coloris, string Provenance, string FormatTirage, int Pages, int Recto, int Verso, int Basculees,
    int GacheFixe, int PourcentageGache, int Poids, int Metrage, decimal Prix, string UniteAchat, decimal Valeur,
    int NbPoses, int TotalGache, int HorsGache, int Total, int Conditionnement, int Epaisseur, string UniteEpaisseur);

public sealed record LigneTransport(
    int Quantite, string Departement, int Bareme, string Transporteur, int Poids, decimal Valeur,
    decimal ValeurMillePlus, string Ville, string Observation, string RaisonSociale);

public sealed record LigneFraisAnnexes(
    string Article, string Designation, int Quantite, decimal PrixRevient, decimal Marge, decimal PrixVente);

public sealed record RubriqueReponse(string Rubrique, string Question, string Reponse);

// Panneau « Support » d'un élément (InitPapier)
public sealed record PapierElement(
    string Reference, string Nature, string Sorte, string SousSorte, string GrammageEpaisseur, string Coloris,
    string Norme, string Provenance, bool Bobine,
    string GacheFixe, string GachePourcentage, string TauxGache, string TotalGache, string BonnesFeuilles,
    string Total, string FormatTirage, string FormatAppro, string Metrage, string Poids, string Conditionnement,
    string Prix, string Valeur, string NbBobines,
    // Équivalents en mètres linéaires (papier en bobine)
    string GacheFixeMl, string GachePourcentageMl, string TotalGacheMl, string BonnesFeuillesMl, string TotalMl,
    string MetrageMl);

// Outil de découpe (FOU001P)
public sealed record OutilElement(
    int Numero, string Forme, decimal Laize, decimal Avance, decimal CoteAvance, decimal Pas, int PosesLaize,
    int PosesDeveloppement, decimal Rayon, decimal EcartLaize, decimal EcartAvance, decimal LaizeUtile,
    decimal AvanceUtile);

public sealed record ElementDetail(
    int Numero,                   // 31 = Prépresse, 32 = Façonnage
    string Titre,                 // libellé de l'onglet
    string Description,           // quantité, formats, poses, couleurs, outils
    IReadOnlyList<LigneOperation> Fabrication,
    IReadOnlyList<RubriqueReponse> Rubriques,
    PapierElement? Papier,
    IReadOnlyList<LigneMatiere> Matieres,
    IReadOnlyList<LigneSousTraitance> SousTraitances,
    OutilElement? Outil,
    bool AvecTexteLibre);
