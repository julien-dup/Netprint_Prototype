using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Livraisons à effectuer (LivraisonsAEffectuer) : société et profil de la session, représentant limité imposé (B000F.ILIMIT),
// pas de droit PRT000P1 (le WinDev n'en contrôle pas). Garde-fou de 10 000 lignes.
// =====================================================================
internal sealed class LivraisonsService(LivraisonsDb2 donnees, IUtilisateurCourant utilisateur) : ILivraisons
{
    public async Task<ResultatRecherche<LigneALivrer>> RechercherAsync(CriteresLivraisons criteres, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (criteres.Periode == PeriodeLivraison.Dates)
        {
            if (criteres.Au is null) throw new RegleMetierException("Indiquez la date de fin de la période.");
            if (criteres.Du is { } du && du > criteres.Au) throw new RegleMetierException("La date de début est postérieure à la date de fin.");
        }
        return await donnees.RechercherAsync(u.Societe, u.Profil, criteres, DateOnly.FromDateTime(DateTime.Today), ct);
    }

    // Lignes cochées utilisables pour un BL : toutes du même dossier (un BL se crée dossier par dossier, puis on y ajoute les autres)
    internal static string? ControlerPourBl(IReadOnlyList<LigneALivrer> lignes) =>
        lignes.Count == 0 ? "Cochez au moins une ligne."
        : lignes.Select(l => (l.Dossier, l.Cle)).Distinct().Count() > 1
            ? "Cochez les lignes d'un seul dossier : le BL se crée dossier par dossier (vous pourrez ensuite y ajouter les autres)."
            : null;
}
