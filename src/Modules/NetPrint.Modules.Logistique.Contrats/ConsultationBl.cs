using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Consultation des bons de livraison : reprise de ConsultationBL (WinDev) et de la liste EXP010S (5250), en lecture seule.
// Un BL (EXP009) porte un ou plusieurs éléments (EXP010 : un dossier livré, unité de facturation), chacun ses lignes
// (EXP015) et ses lots / emplacements (EXP016). La société vient de la session ; un représentant « limité » ne voit que
// les BL de ses dossiers (B000F.ILIMIT, comme EXP010S R6).
// =====================================================================
public interface IConsultationBl
{
    // Une ligne par élément de BL, les plus récents d'abord ; garde-fou LimitesRecherche.Maximum
    Task<ResultatRecherche<ElementBl>> RechercherAsync(CriteresBl criteres, CancellationToken ct = default);

    // Fiche d'un BL ; null s'il n'existe pas ou n'est pas accessible
    Task<FicheBl?> LireAsync(int numeroBl, CancellationToken ct = default);
}

// État d'un élément (EXP010.HETABL)
public static class EtatsBl
{
    public const string Client = "C";
    public const string Divers = "D";
    public const string SousTraitance = "S";
    public const string Retour = "R";
    public const string Vierge = "V";

    public static string Libelle(string etat) => etat switch
    {
        Client => "Client",
        Divers => "Divers",
        SousTraitance => "Sous-traitance",
        Retour => "Retour",
        Vierge => "Vierge",
        _ => etat,
    };
}

public enum FiltreFacturation { Tous, NonFactures, Factures, NonFacturables }

public sealed class CriteresBl
{
    public int? NumeroBl { get; set; }
    public int? NumeroCommande { get; set; }
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public string? ReferenceCommande { get; set; }       // commence par
    public DateOnly? Du { get; set; }                    // date d'expédition
    public DateOnly? Au { get; set; }
    public string? Article { get; set; }                 // * pour compléter
    public int? NumeroFacture { get; set; }
    public int? Transporteur { get; set; }
    public string? Ville { get; set; }                   // commence par
    public string? Profil { get; set; }                  // créateur du BL
    public FiltreFacturation Facturation { get; set; } = FiltreFacturation.Tous;
    // Types d'élément (tous par défaut, comme EXP010S R4)
    public bool Clients { get; set; } = true;
    public bool Divers { get; set; } = true;
    public bool SousTraitances { get; set; } = true;
    public bool Retours { get; set; } = true;

    public bool AucunCritere => NumeroBl is null && NumeroCommande is null && CompteClient is null && CompteGeneral is null
        && string.IsNullOrWhiteSpace(ReferenceCommande)
        && Du is null && Au is null && string.IsNullOrWhiteSpace(Article) && NumeroFacture is null && Transporteur is null
        && string.IsNullOrWhiteSpace(Ville) && string.IsNullOrWhiteSpace(Profil);
}

// Élément de BL (une ligne de la liste)
public sealed record ElementBl(
    int NumeroBl, int Element, DateOnly? DateExpedition, string Etat,
    int Dossier, string CleDossier, string ReferenceCommande,
    int CompteGeneral, int CompteClient, string MotDirecteur, string NomClient,
    string ClientFacture,                 // « gén aux mot directeur » s'il diffère du client
    int Quantite, int Colis, int Palettes, int Poids,
    string CodePostal, string Ville, string Transporteur,
    bool Facturable, int NumeroFacture, DateOnly? DateFacture,
    string Profil, int Representant, string NomRepresentant, bool Edite,   // édité = date d'édition renseignée (EXP009.HDEDB*)
    int BlOrigine)                        // retour : BL d'origine
{
    public string Libelle => $"{NumeroBl}" + (Element > 1 ? $"/{Element}" : "");
    public bool Facture => NumeroFacture > 0;
}

// Fiche d'un BL
public sealed record FicheBl(EnteteBl Entete, IReadOnlyList<DetailElementBl> Elements, IReadOnlyList<string> TextePied);

public sealed record EnteteBl(
    int NumeroBl, DateOnly? DateExpedition, int CompteGeneral, int CompteClient, string NomClient,
    int NumeroAdresse, string RaisonSociale, string Rue1, string Rue2, string CodePostal, string Ville, string Pays,
    IReadOnlyList<string> Observations, string Mention,
    int Colis, int Palettes, int Poids, string Transporteur, string TypeLivraison, string TypePort, int Recepisse,
    string Enseigne, string Profil, bool Edite, DateOnly? DateEdition, bool SousTraitance);

public sealed record DetailElementBl(
    int Element, string Etat, int Dossier, string CleDossier, string DesignationDossier, string ReferenceCommande,
    DateOnly? DateCommande, string ClientFacture, string NomRepresentant,
    bool Facturable, bool FacturationAutomatique, int NumeroFacture, DateOnly? DateFacture,
    int BlOrigine, int ElementOrigine, IReadOnlyList<LigneBl> Lignes);

public sealed record LigneBl(
    int Ligne, string Article, string Indice, string Designation, int Quantite, int Colis, int LigneCommande,
    string EtatLivraison, string CodeVente, IReadOnlyList<LotBl> Lots)
{
    public bool Texte => Article == "" && Quantite == 0;
}

public sealed record LotBl(string Lot, string Emplacement, int Quantite);
