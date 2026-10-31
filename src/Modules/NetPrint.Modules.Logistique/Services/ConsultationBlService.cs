using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Consultation des BL (ConsultationBL / EXP010S) : société de la session, représentant limité appliqué côté serveur.
// ÉCART : EXP010S laissait aussi un « manager » voir les BL de son équipe ; ici, seul le représentant limité est filtré.
// Dans la fiche, un représentant limité ne voit que les éléments de ses dossiers.
// =====================================================================
internal sealed class ConsultationBlService(ConsultationBlDb2 donnees, IUtilisateurCourant utilisateur) : IConsultationBl
{
    public async Task<ResultatRecherche<ElementBl>> RechercherAsync(CriteresBl criteres, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (criteres is { Clients: false, Divers: false, SousTraitances: false, Retours: false })
            throw new RegleMetierException("Choisissez au moins un type de BL.");   // GEN0013
        if (criteres.Du is { } du && criteres.Au is { } au && du > au)
            throw new RegleMetierException("La date de début est postérieure à la date de fin.");
        return await donnees.RechercherAsync(u.Societe, u.Profil, criteres, ct);
    }

    public async Task<FicheBl?> LireAsync(int numeroBl, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return await donnees.LireAsync(u.Societe, u.Profil, numeroBl, ct);
    }
}
