using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Impressions;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// ARC d'une commande (ARC105P) : droits, données, modèle, PDF. Rien n'est écrit en base (comme le RPG).
internal sealed class EditionArcService(ArcCommandeDb2 donnees, ISourceCommandes commandes, ISocietes societes, IContactsTiers contacts,
    IUtilisateurCourant utilisateur) : IEditionArc
{
    public async Task<OptionsArc> LireOptionsAsync(IReadOnlyList<CleCommande> commandes, CancellationToken ct = default)
    {
        var (u, impose, droits) = await ContexteAsync(ct);
        (bool AvecSoldees, int CompteGeneral, int CompteClient)? lu = null;
        foreach (var cle in commandes)
            if ((lu = await donnees.OptionsAsync(u.Societe, cle, impose, ct)) is not null) break;
        var o = lu ?? throw new RegleMetierException("Ces commandes n'existent pas ou ne vous sont pas accessibles.");
        var liste = await contacts.LireAsync(u.Societe, o.CompteGeneral, o.CompteClient, DocumentEnvoye.Arc, ct);
        return new OptionsArc(o.AvecSoldees, liste.Select(c => new ContactArc(c.Nom, c.Email, c.ParDefaut)).ToList(), droits.Editer);
    }

    public async Task<DocumentGenere> PreparerMessageAsync(IReadOnlyList<CleCommande> cles, IReadOnlyList<string> destinataires,
        IReadOnlyList<string> copies, bool? avecLignesSoldees = null, CancellationToken ct = default)
    {
        if (cles.Count == 0) throw new RegleMetierException("Choisissez au moins une commande.");
        if (cles.Count > MaximumParMessage) throw new RegleMetierException($"Pas plus de {MaximumParMessage} ARC par message.");
        var (u, impose, droits) = await ContexteAsync(ct);
        if (!droits.Editer) throw new RegleMetierException("Vous n'êtes pas autorisé à éditer les commandes.");
        var lues = new List<DonneesArc>();
        foreach (var cle in cles.Distinct())
            lues.Add(await donnees.LireAsync(u.Societe, cle, impose, ct)
                ?? throw new RegleMetierException($"La commande {cle.Libelle} n'existe pas ou ne vous est pas accessible."));
        if (lues.Select(d => (d.Entete.CompteGeneral, d.Entete.CompteClient)).Distinct().Count() > 1)
            throw new RegleMetierException("Les ARC d'un même message doivent concerner un seul client.");

        var aujourdhui = DateOnly.FromDateTime(DateTime.Today);
        var pieces = new List<PieceJointe>();
        var identites = new Dictionary<string, IdentiteSociete?>();
        foreach (var d in lues)
        {
            if (!identites.TryGetValue(d.Entete.Enseigne, out var societe))
                identites[d.Entete.Enseigne] = societe = await societes.LireIdentiteAsync(u.Societe, d.Entete.Enseigne, DocumentArc.TypeLogo, ct);
            var modele = ArcCommande.Construire(d, avecLignesSoldees ?? d.AvecSoldeesParDefaut, aujourdhui);
            var pdf = await Task.Run(() => DocumentsPdf.Generer(NomPdf(d.Entete.Cle, false), new DocumentArc(modele, societe, false)), ct);
            pieces.Add(new PieceJointe(pdf.Nom, pdf.Contenu));
        }
        var (objet, texte) = ArcCommande.Message(lues, aujourdhui);
        var nom = lues.Count == 1 ? $"ARC {cles[0].Numero:00000}-{cles[0].Cle}.eml" : $"ARC {lues[0].Entete.CompteClient} ({lues.Count}).eml";
        return new MessageBrouillon(Adresses(destinataires), Adresses(copies), objet, texte, pieces).VersEml(nom);
    }

    // Garde-fou : un message raisonnable pour la messagerie (pièces jointes)
    internal const int MaximumParMessage = 50;

    private static List<string> Adresses(IEnumerable<string> adresses) =>
        adresses.Select(a => a.Trim()).Where(a => a != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string NomPdf(CleCommande cle, bool apercu) => $"ARC {cle.Numero:00000}-{cle.Cle}{(apercu ? " (aperçu)" : "")}.pdf";

    public async Task<DocumentGenere> EditerAsync(CleCommande commande, bool? avecLignesSoldees = null, bool apercu = false,
        CancellationToken ct = default)
    {
        var (u, impose, droits) = await ContexteAsync(ct);
        if (!apercu && !droits.Editer) throw new RegleMetierException("Vous n'êtes pas autorisé à éditer les commandes.");
        var d = await donnees.LireAsync(u.Societe, commande, impose, ct)
            ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        var modele = ArcCommande.Construire(d, avecLignesSoldees ?? d.AvecSoldeesParDefaut, DateOnly.FromDateTime(DateTime.Today));
        var societe = await societes.LireIdentiteAsync(u.Societe, d.Entete.Enseigne, DocumentArc.TypeLogo, ct);
        var nom = NomPdf(commande, apercu);
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
