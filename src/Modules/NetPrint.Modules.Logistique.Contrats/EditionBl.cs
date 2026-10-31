using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Contrats;

// Édition d'un bon de livraison en PDF (reprise d'EXP035P2, sans programme IBM i). Produire le PDF n'écrit rien
// (apercu : filigrane « APERÇU ») ; l'impression demandée par l'utilisateur marque ensuite le BL édité.
public interface IEditionBl
{
    Task<DocumentGenere> EditerAsync(int numeroBl, bool apercu, CancellationToken ct = default);

    // Marque « édité » d'EXP035P2 : EXP009.HEDBL = 'N' et date d'édition. Refusée si le profil ne voit pas tout le BL.
    Task MarquerEditeAsync(int numeroBl, CancellationToken ct = default);

    // Le profil voit-il tout le BL ? (un représentant limité n'en voit que ses éléments, sans les totaux : ni marque, ni envoi)
    Task<bool> CompletAsync(int numeroBl, CancellationToken ct = default);
}
