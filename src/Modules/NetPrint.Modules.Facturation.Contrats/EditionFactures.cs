using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Édition d'une facture en PDF (FAC199 / FAC201 → FAC202P5), recalculée à chaque fois comme aujourd'hui (David, 01/11 : pas de
// stockage du PDF pour l'instant). EditerDefinitivementAsync : édition définitive d'une facture « à éditer » (FAC201 en
// définitif : totaux enregistrés, BL facturés, commandes mises à jour) ; la facture devient éditée, donc plus modifiable.
// =====================================================================
public interface IEditionFactures
{
    Task<DocumentGenere> EditerAsync(int numero, DateOnly date, CancellationToken ct = default);

    Task EditerDefinitivementAsync(int numero, DateOnly date, CancellationToken ct = default);
}
