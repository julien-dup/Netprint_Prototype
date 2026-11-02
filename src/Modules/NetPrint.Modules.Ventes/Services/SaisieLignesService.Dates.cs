using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Report de date de livraison en masse (NEG520 F19, fenêtre NEG520CW). Comme NEG520, la nouvelle date n'est contrôlée que
// sur sa validité (pas de contrôle de calendrier ni de jour de livraison du client).
// =====================================================================
internal sealed partial class SaisieLignesService
{
    async Task<int> IActionsCommande.ReporterDatesAsync(CleCommande commande, ReportDates report, CancellationToken ct)
    {
        var erreurs = new List<ErreurSaisie>();
        if (report.DateOrigine is null) erreurs.Add(new(nameof(ReportDates.DateOrigine), MessagesLigne.Obligatoire));
        if (report.DateDestination is null) erreurs.Add(new(nameof(ReportDates.DateDestination), MessagesLigne.Obligatoire));
        if (erreurs.Count > 0) throw new SaisieInvalideException(erreurs);
        var (u, c) = await ContexteAsync(commande, true, ct);
        var reportees = await donnees.ReporterDatesAsync(u.Societe, u.Profil, commande, c,
            l => ReporterDate(l.Statut, l.Livraison, l.Confirmee, report), ct);
        await RecalculerStocksCommandeAsync(u, c, [.. reportees.Where(a => a.Article != "").Distinct()], ct);
        return reportees.Count;
    }

    // Date de livraison et date confirmée d'une ligne après le report ; null si la ligne n'est pas concernée (sa date
    // d'origine ne vaut pas celle du report). Une date confirmée égale à la date de livraison n'est plus distincte ; une
    // ligne « à la demande » ne reçoit pas de date confirmée (interdite à la saisie).
    internal static (DateOnly Livraison, DateOnly? Confirmee)? ReporterDate(string statut, DateOnly? livraison, DateOnly? confirmee, ReportDates r)
    {
        var origine = r.ZoneOrigine == ZoneDate.Livraison ? livraison : confirmee;
        if (origine is null || origine != r.DateOrigine || r.DateDestination is not { } nouvelle) return null;
        if (r.ZoneDestination == ZoneDate.Livraison) livraison = nouvelle;
        else if (statut == StatutsLigne.ALaDemande) return null;
        else confirmee = nouvelle;
        if (livraison is not { } l) return null;
        return (l, confirmee == l ? null : confirmee);
    }
}
