using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// Écran de lancement d'une commande (NEG515) : lecture et calculs. Les actions viennent ensuite.
internal sealed class LancementCommandeService(LancementCommandeDb2 donnees, ISourceCommandes commandes, IUtilisateurCourant utilisateur)
    : ILancementCommande
{
    public async Task<LancementCommande?> LireAsync(CleCommande commande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var impose = await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        if (await donnees.LireAsync(u.Societe, commande, impose, ct) is not var (entete, d)) return null;
        // NEG510 R3 / R7 : commande sous-traitée (CCOP11) ou soldée : consultation seulement
        var raison = !droits.Modifier ? "Vous n'êtes pas autorisé à modifier les commandes."
            : entete.NonModifiable ? "Commande sous-traitée. Modification impossible."
            : entete.Soldee ? "Option impossible, dossier soldé."
            : "";
        return new LancementCommande(commande, entete.NomClient, raison == "", raison, CalculLancement.Calculer(d, commande));
    }
}
