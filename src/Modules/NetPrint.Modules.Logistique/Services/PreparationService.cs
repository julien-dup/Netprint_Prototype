using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Impressions;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// Fiche de préparation des commandes (EXP078) : société et profil de la session (représentant limité imposé), PDF natif.
// Pas d'écriture : EXP078 mémorisait seulement les derniers paramètres (PREF01F), ici gardés par l'écran.
internal sealed class PreparationService(PreparationDb2 donnees, IUtilisateurCourant utilisateur) : IPreparation
{
    public async Task<DocumentGenere> EditerAsync(CriteresPreparation criteres, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (criteres.Du > criteres.Au) throw new RegleMetierException("La date de début est postérieure à la date de fin.");
        if (criteres.Au.DayNumber - criteres.Du.DayNumber > 366) throw new RegleMetierException("Choisissez une période d'un an au plus.");
        var (lignes, lots, fabrication, tronque) = await donnees.LireAsync(u.Societe, u.Profil, criteres, ct);
        var fiches = Preparations.Construire(lignes, lots, fabrication);
        return DocumentsPdf.Generer($"Préparation {criteres.Du:yyyy-MM-dd} au {criteres.Au:yyyy-MM-dd}.pdf",
            new DocumentPreparation(fiches, criteres.Du, criteres.Au, tronque));
    }
}
