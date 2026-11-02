using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Fiche de préparation des commandes (reprise de l'état RPG EXP078) en PDF : une fiche par jour de livraison × client × adresse,
// les commandes et leurs lignes restant à livrer (conditionnement, textes, lots en stock, en fabrication), cases à remplir.
// =====================================================================
public interface IPreparation
{
    Task<DocumentGenere> EditerAsync(CriteresPreparation criteres, CancellationToken ct = default);
}

public sealed record CriteresPreparation(
    DateOnly Du, DateOnly Au, int? CompteGeneral = null, int? CompteClient = null, int? NumeroCommande = null, bool ArticlesNonStockes = false,
    string? Fabricant = null, string? Enseigne = null, string? Article = null, int? NumeroAdresse = null, string? ReferenceImprime = null,
    string? Appel = null, int? NumeroOpr = null, FiltreOpr Opr = FiltreOpr.Tous, int? Representant = null);
