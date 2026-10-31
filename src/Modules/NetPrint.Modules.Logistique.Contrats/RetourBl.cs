namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Retour de marchandises : un seul chemin pour les deux de l'existant, EXP010S option RM (retour par lot, fabrication à la
// quantité) et ConsultationBL clic droit « Retour BL » (palettes SSCC des articles gérés à l'unité, que le RPG refusait).
// Le retour est un nouveau BL à l'état R lié au BL d'origine (élément par élément) ; sa suppression défait tout.
// =====================================================================
public interface IRetourBl
{
    Task<PreparationRetour> PreparerAsync(int numeroBl, CancellationToken ct = default);

    // Rend le n° du BL de retour créé
    Task<int> EnregistrerAsync(SaisieRetour saisie, CancellationToken ct = default);

    Task SupprimerAsync(int numeroBlRetour, CancellationToken ct = default);
}

// BL d'origine et valeurs proposées (date du jour, facturable, sans facturation automatique, client facturé du BL d'origine)
public sealed record PreparationRetour(
    int NumeroBl, DateOnly? DateBl, int CompteGeneral, int CompteClient, string NomClient, int CompteGeneralFacture,
    int CompteClientFacture, int Transporteur, IReadOnlyList<ElementRetour> Elements);

// Élément du BL d'origine. Fabrication : une quantité pour l'élément (livrée, déjà retournée), sans lot.
// Refus : raison pour laquelle l'élément ne peut pas être retourné ("" sinon).
public sealed record ElementRetour(
    int Element, int Dossier, string Cle, string Designation, bool Fabrication, int QuantiteLivree, int DejaRetourne,
    IReadOnlyList<LotRetour> Lots, string Refus)
{
    public int Retournable => Math.Max(0, QuantiteLivree - DejaRetourne);
}

// Lot livré d'une ligne (EXP016). Palettes : palettes SSCC livrées de ce lot ; s'il y en a, le retour se fait palette par palette.
public sealed record LotRetour(
    int LigneBl, int LigneCommande, string Article, string Indice, string Designation, string Lot, string Emplacement, int QuantiteLivree,
    int DejaRetourne, IReadOnlyList<PaletteRetour> Palettes)
{
    public int Retournable => Math.Max(0, QuantiteLivree - DejaRetourne);
}

public sealed record PaletteRetour(string Sscc, int Quantite, bool DejaRetournee);

// Motif : 3 lignes de 40 caractères au plus. Quantités : seulement les quantités retournées (> 0).
public sealed record SaisieRetour(
    int NumeroBl, DateOnly Date, int Transporteur, int Colis, int Poids, IReadOnlyList<string> Motif, bool Facturable,
    bool FacturationAutomatique, int CompteGeneralFacture, int CompteClientFacture, IReadOnlyList<QuantiteRetournee> Quantites);

// Fabrication : LigneBl = 0, Lot et Sscc vides. Lot : Sscc vide. Palette : Sscc renseigné.
public sealed record QuantiteRetournee(int Element, int LigneBl, string Lot, string Emplacement, string Sscc, int Quantite);
