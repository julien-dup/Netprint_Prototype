using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Impressions;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Édition du pré-BL (EXP535P2) : données lues dans le modèle du BL, même document et même titre « Bon de livraison » (traduit) :
// il est envoyé au client avant la livraison (David, 01/11). Droit d'accès des OPR (comme la liste des pré-BL).
// =====================================================================
internal sealed class EditionPreBlService(EditionBlDb2 donnees, PreBlDb2 preBl, OprDb2 opr, ISocietes societes, IUtilisateurCourant utilisateur)
    : IEditionPreBl
{
    public const int MaxiCamion = 50;

    public async Task<DocumentGenere> EditerAsync(int numero, bool apercu, CancellationToken ct = default)
    {
        var u = await AccesAsync(ct);
        var lues = await donnees.LirePreBlAsync(u.Societe, u.Profil, numero, ct)
                   ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        var societe = await societes.LireIdentiteAsync(u.Societe, lues.Entete.Enseigne, DocumentBl.TypeLogo, ct);
        return await Task.Run(() => DocumentsPdf.Generer($"Pré-BL {numero}.pdf", new DocumentBl(BonLivraison.Construire(lues), societe, apercu)), ct);
    }

    public async Task<DocumentGenere> FeuillePreparationAsync(int numero, int? depot, bool codesBarres, CancellationToken ct = default)
    {
        var u = await AccesAsync(ct);
        var lues = await preBl.FeuillePreparationAsync(u.Societe, numero, depot, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
                   ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        var feuille = new FeuillePreparation(await societes.LireNomAsync(u.Societe, ct) ?? "", lues.EnTete, lues.Palettes, codesBarres);
        return await Task.Run(() => DocumentsPdf.Generer($"Préparation pré-BL {numero}.pdf", new DocumentFeuillePreparation(feuille)), ct);
    }

    public async Task<DocumentGenere> FeuilleCamionAsync(IReadOnlyList<int> numeros, bool valorisee, CancellationToken ct = default)
    {
        var u = await AccesAsync(ct);
        if (numeros.Distinct().Count() > MaxiCamion) throw new RegleMetierException($"Une feuille camion regroupe {MaxiCamion} pré-BL au plus.");
        var (entetes, lots, poidsPalette) = await preBl.FeuilleCamionAsync(u.Societe, numeros,
            await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct);
        if (entetes.Count < numeros.Distinct().Count()) throw new RegleMetierException("Un des pré-BL n'existe pas ou ne vous est pas accessible.");
        if (ReglesFeuillesPreBl.RefusCamion(entetes) is { Length: > 0 } refus) throw new RegleMetierException(refus);
        var (lignes, totaux, parDepot) = ReglesFeuillesPreBl.Lignes(lots, entetes.ToDictionary(e => e.Numero, e => e.PoidsSaisi), poidsPalette);
        var feuille = new FeuilleCamion(await societes.LireNomAsync(u.Societe, ct) ?? "", entetes[0], lignes, totaux, parDepot, totaux.Sum(t => t.Palettes),
            totaux.Sum(t => t.PoidsSaisi > 0 ? t.PoidsSaisi : t.Poids), valorisee);
        return await Task.Run(() => DocumentsPdf.Generer(entetes.Count == 1 ? $"Feuille camion {entetes[0].Numero}.pdf"
                : $"Feuille camion {entetes[0].Numero} et {entetes.Count - 1} autre{(entetes.Count > 2 ? "s" : "")}.pdf",
            new DocumentFeuilleCamion(feuille)), ct);
    }

    private async Task<UtilisateurConnecte> AccesAsync(CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        if (!ReglesOpr.Droits(await opr.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct)).Acces)
            throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des pré-BL (droits des OPR).");
        return u;
    }
}
