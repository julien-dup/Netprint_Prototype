using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Impressions;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Édition du pré-BL (EXP535P2) : données lues dans le modèle du BL, même document, titre « Pré-BL ». Droit d'accès des OPR
// (comme la liste des pré-BL). EXP535P2 imprimait « Bon de livraison » : titre à confirmer par David (docs/points-en-attente.md).
// =====================================================================
internal sealed class EditionPreBlService(EditionBlDb2 donnees, OprDb2 opr, ISocietes societes, IUtilisateurCourant utilisateur) : IEditionPreBl
{
    public const string Titre = "Pré-BL";

    public async Task<DocumentGenere> EditerAsync(int numero, bool apercu, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (!ReglesOpr.Droits(await opr.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct)).Acces)
            throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des pré-BL (droits des OPR).");
        var lues = await donnees.LirePreBlAsync(u.Societe, u.Profil, numero, ct)
                   ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        var societe = await societes.LireIdentiteAsync(u.Societe, lues.Entete.Enseigne, DocumentBl.TypeLogo, ct);
        return await Task.Run(() => DocumentsPdf.Generer($"Pré-BL {numero}.pdf", new DocumentBl(BonLivraison.Construire(lues), societe, apercu, Titre)), ct);
    }
}
