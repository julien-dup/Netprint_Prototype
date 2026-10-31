using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Impressions;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Édition du BL (EXP010S option 6 → EXP030 → EXP035P2) : périmètre de la consultation, données, modèle, PDF.
// Pas de droit PRT000P1 (EXP010S n'en contrôle pas pour éditer). Produire le PDF n'écrit rien (il peut être rouvert,
// préchargé…) ; la marque « édité » d'EXP009, seule écriture du RPG, est posée par l'action « Imprimer » de l'écran.
// Un représentant limité n'imprime que les éléments de ses dossiers, sans les totaux du BL, et ne le marque pas édité.
// =====================================================================
internal sealed class EditionBlService(EditionBlDb2 donnees, ISocietes societes, IUtilisateurCourant utilisateur) : IEditionBl
{
    public async Task<DocumentGenere> EditerAsync(int numeroBl, bool apercu, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var lues = await LireAsync(u, numeroBl, ct);
        var societe = await societes.LireIdentiteAsync(u.Societe, lues.Entete.Enseigne, DocumentBl.TypeLogo, ct);
        return DocumentsPdf.Generer($"BL {numeroBl}.pdf", new DocumentBl(BonLivraison.Construire(lues), societe, apercu));
    }

    public async Task MarquerEditeAsync(int numeroBl, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if ((await LireAsync(u, numeroBl, ct)).Partiel)
            throw new RegleMetierException("Vous ne voyez qu'une partie de ce BL : il n'est pas marqué édité.");
        await donnees.MarquerEditeAsync(u.Societe, numeroBl, DateOnly.FromDateTime(DateTime.Today), ct);
    }

    private async Task<DonneesEditionBl> LireAsync(UtilisateurConnecte u, int numeroBl, CancellationToken ct) =>
        await donnees.LireAsync(u.Societe, u.Profil, numeroBl, ct)
            ?? throw new RegleMetierException($"Le BL {numeroBl} n'existe pas ou ne vous est pas accessible.");
}
