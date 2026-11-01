using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Édition d'une facture en PDF (FAC199 / FAC201 → FAC202P5), recalculée à chaque fois comme aujourd'hui (David, 01/11 : pas de
// stockage du PDF pour l'instant). Lecture seule : l'édition définitive (BL facturés, commandes, historique) viendra ensuite.
// =====================================================================
public interface IEditionFactures
{
    Task<DocumentGenere> EditerAsync(int numero, DateOnly date, CancellationToken ct = default);
}
