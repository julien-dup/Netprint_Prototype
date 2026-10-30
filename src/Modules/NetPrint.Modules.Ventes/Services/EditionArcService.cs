using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Impressions;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// ARC d'une commande (ARC105P) : droits, données, modèle, PDF. Rien n'est écrit en base (comme le RPG).
internal sealed class EditionArcService(ArcCommandeDb2 donnees, ISourceCommandes commandes, ISocietes societes, IUtilisateurCourant utilisateur)
    : IEditionArc
{
    public async Task<OptionsArc> LireOptionsAsync(CleCommande commande, CancellationToken ct = default)
    {
        var (u, impose, droits) = await ContexteAsync(ct);
        var o = await donnees.OptionsAsync(u.Societe, commande, impose, ct)
            ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        return new OptionsArc(o.AvecSoldees, o.Email, droits.Editer);
    }

    public async Task<DocumentGenere> EditerAsync(CleCommande commande, bool? avecLignesSoldees = null, bool apercu = false,
        CancellationToken ct = default)
    {
        var (u, impose, droits) = await ContexteAsync(ct);
        if (!apercu && !droits.Editer) throw new RegleMetierException("Vous n'êtes pas autorisé à éditer les commandes.");
        var d = await donnees.LireAsync(u.Societe, commande, impose, ct)
            ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        var modele = ArcCommande.Construire(d, avecLignesSoldees ?? d.AvecSoldeesParDefaut, DateOnly.FromDateTime(DateTime.Today));
        var societe = await societes.LireIdentiteAsync(u.Societe, d.Entete.Enseigne, ct);
        var nom = $"ARC {commande.Numero:00000}-{commande.Cle}{(apercu ? " (aperçu)" : "")}.pdf";
        // Rendu QuestPDF (calcul) hors du fil de la requête
        return await Task.Run(() => DocumentsPdf.Generer(nom, new DocumentArc(modele, societe, apercu)), ct);
    }

    private async Task<(UtilisateurConnecte U, int? Impose, DroitsCommandes Droits)> ContexteAsync(CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var impose = await commandes.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        return (u, impose, droits);
    }
}
