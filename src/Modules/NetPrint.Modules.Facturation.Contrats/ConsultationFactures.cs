using NetPrint.Socle;

namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Consultation des factures (WinDev ConsultationFactures, FAC080S en lecture) : factures et avoirs (FAC080), leurs éléments
// (FAC090 : un dossier ou une catégorie) et leurs lignes (FAC100). Lecture seule ; montants des avoirs en négatif.
// Clé d'une facture : n° + date (la numérotation peut être annuelle).
// =====================================================================
public interface IConsultationFactures
{
    Task<ResultatRecherche<FactureListe>> RechercherAsync(CriteresFactures criteres, CancellationToken ct = default);
    Task<FicheFacture?> LireAsync(int numero, DateOnly date, CancellationToken ct = default);
}

public static class StatutsFacture
{
    public const string AEditer = "À éditer";
    public const string Editee = "Éditée";
    public const string Comptabilisee = "Éditée et comptabilisée";
    public const string Attente = "En attente";
}

public sealed class CriteresFactures
{
    public int? Numero { get; set; }
    public int? CompteGeneralFacture { get; set; }
    public int? CompteClientFacture { get; set; }
    public int? CompteGeneral { get; set; }        // donneur d'ordre
    public int? CompteClient { get; set; }
    public int? Commande { get; set; }
    public string? Cle { get; set; }
    public string? ReferenceCommande { get; set; }
    public int? Bl { get; set; }
    public string? Article { get; set; }
    public string? Indice { get; set; }
    public int? Representant { get; set; }
    public DateOnly? Du { get; set; }
    public DateOnly? Au { get; set; }
    public DateOnly? EcheanceDu { get; set; }
    public DateOnly? EcheanceAu { get; set; }
    public decimal? MontantHtDe { get; set; }
    public decimal? MontantHtA { get; set; }
    public bool AvoirsSeulement { get; set; }
}

// Montants signés (avoir en négatif) ; Ttc = HT + port (taxable et exonéré) - escompte + TVA, comme ConsultationFactures
public sealed record FactureListe(
    int Numero, DateOnly Date, bool Avoir, int CompteGeneralFacture, int CompteClientFacture, string NomFacture, string Ville,
    int CompteGeneral, int CompteClient, string NomClient, DateOnly? Echeance, decimal Ht, decimal Port, decimal Escompte, decimal Tva,
    decimal Ttc, decimal Acompte, string Statut, int Representant, string Envoi, string Enseigne)
{
    public string Cle => $"{Numero}-{Date:yyyyMMdd}";
}

public sealed record FicheFacture(
    FactureListe Facture, string FormeJuridique, string Rue1, string Rue2, string CodePostal, string ModeReglement, string AlAttention,
    string ReferenceFacture, string TvaIntracom, IReadOnlyList<ElementFacture> Elements, IReadOnlyList<LigneFacture> Lignes);

// Un élément : un dossier (commande) ou une catégorie
public sealed record ElementFacture(int Numero, int Dossier, string Cle, string ReferenceCommande, DateOnly? DateCommande, string Categorie,
    string RefImprime, int Representant);

// Une ligne ; Textes : lignes de texte qui la suivent (code ligne 9 ou type T) ; montants signés (avoir, remise)
public sealed record LigneFacture(int Element, int Numero, string CodeLigne, string TypeLigne, string Article, string Indice, decimal Quantite,
    string Designation, IReadOnlyList<string> Textes, decimal PrixUnitaire, string Unite, decimal Montant, int Bl, DateOnly? DateLivraison,
    string Appel, string RefImprime);
