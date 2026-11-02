using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Consultation des factures : société de la session, représentant limité appliqué par la lecture. Pas de droit PRT000P1
// (ConsultationFactures n'en contrôle pas pour consulter). Contrôles des critères comme le WinDev : dates et montants dans
// l'ordre.
// =====================================================================
internal sealed class ConsultationFacturesService(ConsultationFacturesDb2 donnees, IUtilisateurCourant utilisateur) : IConsultationFactures
{
    public async Task<ResultatRecherche<FactureListe>> RechercherAsync(CriteresFactures criteres, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var erreurs = Controler(criteres);
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs));
        return await donnees.RechercherAsync(u.Societe, u.Profil, criteres, ct);
    }

    public async Task<FicheFacture?> LireAsync(int numero, DateOnly date, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return await donnees.LireAsync(u.Societe, u.Profil, numero, date, ct);
    }

    internal static List<string> Controler(CriteresFactures c)
    {
        var e = new List<string>();
        if (c.Du is { } du && c.Au is { } au && du > au) e.Add("La date de début est postérieure à la date de fin.");
        if (c.EcheanceDu is { } ed && c.EcheanceAu is { } ea && ed > ea) e.Add("L'échéance de début est postérieure à l'échéance de fin.");
        if (c.MontantHtDe is { } de && c.MontantHtA is { } a && de > a) e.Add("Le montant HT de début est supérieur au montant de fin.");
        if (!string.IsNullOrWhiteSpace(c.Cle) && c.Commande is not > 0) e.Add("La clé de commande va avec un n° de commande.");
        return e;
    }
}
