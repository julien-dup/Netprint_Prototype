using Microsoft.Extensions.Logging;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Impressions;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// ARC d'une commande (ARC105P) : droits, données, modèle, PDF, message. L'édition n'écrit rien (comme le RPG) ; chaque
// envoi, ou brouillon préparé pour Outlook, est tracé dans le journal des envois avec le PDF (ENV100).
// Expéditeur : l'utilisateur (AI00F.ACMELE), à défaut la société (FAC010.PEXPEN).
internal sealed class EditionArcService(ArcCommandeDb2 donnees, ISourceCommandes commandes, ISocietes societes, IContactsTiers contacts,
    IMessagerie messagerie, IJournalEnvois journal, NetPrint.Noyau.Utilisateurs.IUtilisateurs utilisateurs, IUtilisateurCourant utilisateur,
    Microsoft.Extensions.Logging.ILogger<EditionArcService> journalTechnique) : IEditionArc
{
    // Garde-fous : un message raisonnable pour la messagerie (pièces jointes), un envoi groupé raisonnable
    internal const int MaximumParMessage = 50;
    internal const int MaximumEnvoiGroupe = 200;

    public async Task<OptionsArc> LireOptionsAsync(IReadOnlyList<CleCommande> commandes, CancellationToken ct = default)
    {
        var (u, impose, droits) = await ContexteAsync(ct);
        (bool AvecSoldees, int CompteGeneral, int CompteClient, string Nom)? lu = null;
        foreach (var cle in commandes)
            if ((lu = await donnees.OptionsAsync(u.Societe, cle, impose, ct)) is not null) break;
        var o = lu ?? throw new RegleMetierException("Ces commandes n'existent pas ou ne vous sont pas accessibles.");
        var liste = await contacts.LireAsync(u.Societe, o.CompteGeneral, o.CompteClient, DocumentEnvoye.Arc, ct);
        var (a, cc) = DestinatairesParDefaut(liste);
        var proposes = a.Concat(cc).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var etat = await messagerie.EtatAsync(u.Societe, await utilisateurs.EmailAsync(u.Profil, ct), ct);
        return new OptionsArc(o.AvecSoldees, liste.Select(c => new ContactArc(c.Nom, c.Email, c.ParDefaut, proposes.Contains(c.Email))).ToList(),
            droits.Editer, etat.Disponible, etat.Raison, etat.Expediteur, etat.Redirection);
    }

    // ------------------------------------------------------------------
    // Un message (un client) : brouillon Outlook ou envoi direct
    // ------------------------------------------------------------------
    public async Task<DocumentGenere> PreparerMessageAsync(IReadOnlyList<CleCommande> cles, IReadOnlyList<string> destinataires,
        IReadOnlyList<string> copies, bool? avecLignesSoldees = null, CancellationToken ct = default)
    {
        var (u, impose, _) = await ContexteEditionAsync(ct);
        var (message, lues) = await MessageAsync(u, impose, cles, destinataires, copies, avecLignesSoldees, ct);
        var nom = lues.Count == 1 ? $"ARC {cles[0].Numero:00000}-{cles[0].Cle}.eml" : $"ARC {lues[0].Entete.CompteClient} ({lues.Count}).eml";
        await TracerAsync(u, message, lues, CanauxEnvoi.Outlook, StatutsEnvoi.Prepare, "", "", "", ct, avecPdf: false);
        return message.VersEml(nom);
    }

    public async Task EnvoyerMessageAsync(IReadOnlyList<CleCommande> cles, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies,
        bool? avecLignesSoldees = null, CancellationToken ct = default)
    {
        var (u, impose, _) = await ContexteEditionAsync(ct);
        if (Adresses(destinataires).Count == 0) throw new RegleMetierException("Choisissez au moins un destinataire.");
        var expediteur = await utilisateurs.EmailAsync(u.Profil, ct);
        var etat = await messagerie.EtatAsync(u.Societe, expediteur, ct);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);   // rien n'est tenté, rien n'est tracé
        var (message, lues) = await MessageAsync(u, impose, cles, destinataires, copies, avecLignesSoldees, ct);
        await EnvoyerEtTracerAsync(u, message, lues, expediteur, etat, ct);
    }

    // Envoi par le serveur de la société, au nom de l'utilisateur, puis trace (envoyé, test ou échec) ; l'échec est relancé.
    // expediteur et etat sont lus une fois par l'appelant (une fois pour tout un envoi groupé).
    private async Task EnvoyerEtTracerAsync(UtilisateurConnecte u, MessageBrouillon message, List<DonneesArc> lues, string expediteur,
        EtatMessagerie etat, CancellationToken ct)
    {
        try
        {
            await messagerie.EnvoyerAsync(u.Societe, message, expediteur, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TracerAsync(u, message, lues, CanauxEnvoi.Smtp, StatutsEnvoi.Echec, etat.Expediteur, etat.Redirection ?? "", ex.Message, ct);
            throw;
        }
        await TracerAsync(u, message, lues, CanauxEnvoi.Smtp, etat.Redirection is null ? StatutsEnvoi.Envoye : StatutsEnvoi.Test,
            etat.Expediteur, etat.Redirection ?? "", "", ct);
    }

    // Une ligne de journal par commande du message, avec son PDF. Le journal ne fait jamais échouer un envoi déjà parti.
    private async Task TracerAsync(UtilisateurConnecte u, MessageBrouillon message, List<DonneesArc> lues, string canal, string statut,
        string expediteur, string redirection, string erreur, CancellationToken ct, bool avecPdf = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var lignes = lues.Select((d, i) => new EnvoiDocument(u.Societe, id, u.Profil, TypesEnvoi.Arc, d.Entete.Cle.Numero, d.Entete.Cle.Cle,
            d.Entete.CompteGeneral, d.Entete.CompteClient, canal, statut, expediteur, message.Destinataires, message.Copies, redirection,
            message.Objet, erreur, message.PiecesJointes[i].Nom, avecPdf ? message.PiecesJointes[i].Contenu : null)).ToList();
        try
        {
            await journal.EcrireAsync(lignes, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Trace impossible (verrou, table non journalisée...) : l'envoi reste fait, l'incident est signalé dans les journaux
            journalTechnique.LogWarning(ex, "Journal des envois (ENV100) non écrit pour {Commandes}",
                string.Join(", ", lues.Select(d => d.Entete.Cle.Libelle)));
        }
    }

    public async Task<IReadOnlyList<EnvoiArcTrace>> LireEnvoisAsync(CleCommande commande, CancellationToken ct = default)
    {
        var (u, impose, _) = await ContexteAsync(ct);
        if (await donnees.OptionsAsync(u.Societe, commande, impose, ct) is null) return [];
        return (await journal.LireAsync(u.Societe, TypesEnvoi.Arc, commande.Numero, commande.Cle, ct))
            .Select(e => new EnvoiArcTrace(e.Id, e.Date, e.Profil, e.Canal == CanauxEnvoi.Smtp ? "Envoi direct" : "Outlook",
                e.Statut switch
                {
                    StatutsEnvoi.Envoye => "Envoyé", StatutsEnvoi.Test => "Test", StatutsEnvoi.Echec => "Échec", _ => "Préparé",
                }, e.Expediteur, e.Destinataires, e.Copies, e.Redirection, e.Objet, e.Erreur, e.AvecPdf)).ToList();
    }

    public async Task<DocumentGenere?> LirePdfEnvoyeAsync(CleCommande commande, long id, CancellationToken ct = default)
    {
        var (u, impose, _) = await ContexteEditionAsync(ct);   // PDF final, sans filigrane : droit « Édition »
        if (await donnees.OptionsAsync(u.Societe, commande, impose, ct) is null) return null;
        return await journal.LirePdfAsync(u.Societe, id, TypesEnvoi.Arc, commande.Numero, commande.Cle, ct) is { } pdf
            ? new DocumentGenere(pdf.Nom, pdf.Contenu) : null;
    }

    private async Task<(MessageBrouillon Message, List<DonneesArc> Lues)> MessageAsync(UtilisateurConnecte u, int? impose,
        IReadOnlyList<CleCommande> cles, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, bool? avecLignesSoldees,
        CancellationToken ct)
    {
        if (cles.Count == 0) throw new RegleMetierException("Choisissez au moins une commande.");
        if (cles.Count > MaximumParMessage) throw new RegleMetierException($"Pas plus de {MaximumParMessage} ARC par message.");
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
        return (new MessageBrouillon(Adresses(destinataires), Adresses(copies), objet, texte, pieces), lues);
    }

    // ------------------------------------------------------------------
    // Envoi groupé (EnvoiARC étendu à plusieurs clients) : un message par client, par le serveur de la société
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<EnvoiArcClient>> PlanifierEnvoiAsync(IReadOnlyList<CleCommande> cles, CancellationToken ct = default)
    {
        var (u, impose, _) = await ContexteEditionAsync(ct);
        return await PlanAsync(u, impose, cles, ct);
    }

    // Le plan confirmé est envoyé tel quel ; chaque message est revérifié (commandes accessibles, un seul client)
    public async Task<IReadOnlyList<EnvoiArcClient>> EnvoyerParClientAsync(IReadOnlyList<EnvoiArcClient> plan, bool? avecLignesSoldees = null,
        CancellationToken ct = default)
    {
        var (u, impose, _) = await ContexteEditionAsync(ct);
        if (plan.Sum(e => e.Commandes.Count) > MaximumEnvoiGroupe)
            throw new RegleMetierException($"Pas plus de {MaximumEnvoiGroupe} commandes par envoi groupé.");
        var expediteur = await utilisateurs.EmailAsync(u.Profil, ct);
        var etat = await messagerie.EtatAsync(u.Societe, expediteur, ct);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);
        var resultats = new List<EnvoiArcClient>();
        foreach (var envoi in plan)
        {
            if (envoi.Erreur is not null || envoi.Envoye || Adresses(envoi.Destinataires).Count == 0)
            {
                resultats.Add(envoi.Erreur is null && !envoi.Envoye ? envoi with { Erreur = "Aucun destinataire." } : envoi);
                continue;
            }
            try
            {
                var (message, lues) = await MessageAsync(u, impose, envoi.Commandes, envoi.Destinataires, envoi.Copies, avecLignesSoldees, ct);
                await EnvoyerEtTracerAsync(u, message, lues, expediteur, etat, ct);
                resultats.Add(envoi with { Envoye = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Adresse mal formée, PDF, serveur... : ce client est en échec, les autres partent
                resultats.Add(envoi with { Erreur = ex.Message });
            }
        }
        return resultats;
    }

    private async Task<List<EnvoiArcClient>> PlanAsync(UtilisateurConnecte u, int? impose, IReadOnlyList<CleCommande> cles, CancellationToken ct)
    {
        if (cles.Count == 0) throw new RegleMetierException("Choisissez au moins une commande.");
        if (cles.Count > MaximumEnvoiGroupe) throw new RegleMetierException($"Pas plus de {MaximumEnvoiGroupe} commandes par envoi groupé.");
        var distinctes = cles.Distinct().ToList();
        var clients = await donnees.ClientsAsync(u.Societe, distinctes, impose, ct);
        var parClient = new Dictionary<(int, int), (string Nom, List<CleCommande> Cles)>();
        foreach (var cle in distinctes)
        {
            if (!clients.TryGetValue(cle, out var o))
                throw new RegleMetierException($"La commande {cle.Libelle} n'existe pas ou ne vous est pas accessible.");
            var client = (o.CompteGeneral, o.CompteClient);
            if (!parClient.TryGetValue(client, out var g)) parClient[client] = g = (o.Nom, []);
            g.Cles.Add(cle);
        }
        var plan = new List<EnvoiArcClient>();
        foreach (var ((gen, cli), (nom, liste)) in parClient)
        {
            var (a, cc) = DestinatairesParDefaut(await contacts.LireAsync(u.Societe, gen, cli, DocumentEnvoye.Arc, ct));
            string? erreur = a.Count == 0 ? "Aucun contact ARC avec une adresse e-mail pour ce client."
                : liste.Count > MaximumParMessage ? $"Plus de {MaximumParMessage} commandes pour ce client : envoyez-les en plusieurs fois." : null;
            plan.Add(new EnvoiArcClient($"{gen} {cli}", nom, liste, a, cc, erreur, false));
        }
        return plan;
    }

    // Contacts « par défaut » (CNARCD), sinon tous les contacts ARC ; le premier en destinataire, les autres en copie (EnvoiARC)
    internal static (List<string> A, List<string> Copies) DestinatairesParDefaut(IReadOnlyList<ContactEnvoi> contacts)
    {
        var retenus = contacts.Where(c => c.ParDefaut).ToList();
        var adresses = Adresses((retenus.Count > 0 ? retenus : contacts).Select(c => c.Email));
        return (adresses.Take(1).ToList(), adresses.Skip(1).ToList());
    }

    // ------------------------------------------------------------------
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

    private static List<string> Adresses(IEnumerable<string> adresses) =>
        adresses.Select(a => a.Trim()).Where(a => a != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string NomPdf(CleCommande cle, bool apercu) => $"ARC {cle.Numero:00000}-{cle.Cle}{(apercu ? " (aperçu)" : "")}.pdf";

    private async Task<(UtilisateurConnecte U, int? Impose, DroitsCommandes Droits)> ContexteEditionAsync(CancellationToken ct)
    {
        var contexte = await ContexteAsync(ct);
        if (!contexte.Droits.Editer) throw new RegleMetierException("Vous n'êtes pas autorisé à éditer les commandes.");
        return contexte;
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
