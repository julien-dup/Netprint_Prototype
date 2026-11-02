using Microsoft.Extensions.Logging;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;
using NetPrint.Noyau.Utilisateurs;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Envoi groupé des pré-BL (FEN_EnvoideBL) : pré-BL non livrés d'un même client (50 au plus), un PDF chacun (IEditionPreBl),
// contacts « bon de livraison » du client (PRT051F), brouillon Outlook ou envoi SMTP au nom de l'utilisateur, trace ENV100 par
// pré-BL (type « PBL », n° du pré-BL dans ENNDOC : distinct des envois du BL qu'il deviendra). Pas repris : copie client (PRT054F.CFEDIT) et liste de colisage (CDRES2)
// d'EXPEMPNAV, qui n'existent pas encore pour les BL non plus.
// =====================================================================
internal sealed class EnvoiPreBlService(IPreBl preBl, IEditionPreBl edition, IContactsTiers contacts, IMessagerie messagerie,
    IJournalEnvois journal, IUtilisateurs utilisateurs, ISocietes societes, IUtilisateurCourant utilisateur,
    ILogger<EnvoiPreBlService> journalTechnique) : IEnvoiPreBl
{
    public const int Maxi = 50;
    public const string TypeEnvoi = "PBL";   // ENV100.ENTYPE (CHAR 3) : envoi d'un pré-BL

    public async Task<OptionsEnvoiBl> OptionsAsync(IReadOnlyList<int> numeros, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var fiches = await PreBlAsync(numeros, ct);
        var liste = fiches[0].CompteGeneral == 0 ? []
            : await contacts.LireAsync(u.Societe, fiches[0].CompteGeneral, fiches[0].CompteClient, DocumentEnvoye.BonLivraison, ct);
        var etat = await messagerie.EtatAsync(u.Societe, await utilisateurs.EmailAsync(u.Profil, ct), ct);
        var historique = new List<EnvoiBlJournalise>();
        foreach (var f in fiches)
            historique.AddRange((await journal.LireDocumentAsync(u.Societe, TypeEnvoi, f.Numero, ct)).Select(e => new EnvoiBlJournalise(e.Id, e.Date,
                e.Profil, e.Canal, e.Statut, e.Destinataires, e.Copies, e.Redirection, e.Erreur, e.AvecPdf, f.Numero)));
        var tous = !liste.Any(c => c.ParDefaut);   // sans contact « par défaut », tous sont proposés (comme pour le BL)
        return new OptionsEnvoiBl(liste.Select(c => new ContactBl(c.Nom, c.Email, c.ParDefaut, tous || c.ParDefaut)).ToList(), etat.Disponible,
            etat.Raison, etat.Expediteur, etat.Redirection, [.. historique.OrderByDescending(h => h.Date)]);
    }

    public async Task<DocumentGenere> PreparerMessageAsync(IReadOnlyList<int> numeros, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies,
        CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (message, fiches) = await MessageAsync(u, numeros, destinataires, copies, ct);
        await TracerAsync(u, message, fiches, CanauxEnvoi.Outlook, StatutsEnvoi.Prepare, "", "", "");
        return message.VersEml(fiches.Count == 1 ? $"Pré-BL {fiches[0].Numero}.eml" : $"Pré-BL {fiches[0].Numero} et {fiches.Count - 1} autre{(fiches.Count > 2 ? "s" : "")}.eml");
    }

    public async Task EnvoyerAsync(IReadOnlyList<int> numeros, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (EnvoiBlService.Adresses(destinataires).Count == 0) throw new RegleMetierException("Choisissez au moins un destinataire.");
        var expediteur = await utilisateurs.EmailAsync(u.Profil, ct);
        var etat = await messagerie.EtatAsync(u.Societe, expediteur, ct);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);   // rien n'est tenté, rien n'est tracé
        var (message, fiches) = await MessageAsync(u, numeros, destinataires, copies, ct);
        try
        {
            await messagerie.EnvoyerAsync(u.Societe, message, expediteur, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TracerAsync(u, message, fiches, CanauxEnvoi.Smtp, StatutsEnvoi.Echec, etat.Expediteur, etat.Redirection ?? "", ex.Message);
            throw new RegleMetierException($"Les pré-BL n'ont pas été envoyés : {ex.Message}");
        }
        await TracerAsync(u, message, fiches, CanauxEnvoi.Smtp, etat.Redirection is null ? StatutsEnvoi.Envoye : StatutsEnvoi.Test,
            etat.Expediteur, etat.Redirection ?? "", "");
    }

    // Pré-BL accessibles, non livrés, d'un même client
    private async Task<List<FichePreBl>> PreBlAsync(IReadOnlyList<int> numeros, CancellationToken ct)
    {
        var distincts = numeros.Distinct().Order().ToList();
        if (distincts.Count == 0) throw new RegleMetierException("Choisissez au moins un pré-BL.");
        if (distincts.Count > Maxi) throw new RegleMetierException($"Un envoi regroupe {Maxi} pré-BL au plus.");
        var fiches = new List<FichePreBl>();
        foreach (var n in distincts)
        {
            var f = await preBl.LireAsync(n, ct) ?? throw new RegleMetierException($"Le pré-BL {n} n'existe pas ou ne vous est pas accessible.");
            if (f.Etat == EtatsPreBl.Livre) throw new RegleMetierException($"Le pré-BL {n} est transformé en BL : envoyez le BL.");
            fiches.Add(f);
        }
        if (fiches.Select(f => (f.CompteGeneral, f.CompteClient)).Distinct().Count() > 1)
            throw new RegleMetierException("Les pré-BL envoyés ensemble doivent être du même client.");
        return fiches;
    }

    // Message : un PDF par pré-BL, objet « Bon(s) de livraison n° … »
    private async Task<(MessageBrouillon Message, List<FichePreBl> Fiches)> MessageAsync(UtilisateurConnecte u, IReadOnlyList<int> numeros,
        IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, CancellationToken ct)
    {
        var fiches = await PreBlAsync(numeros, ct);
        var a = EnvoiBlService.Adresses(destinataires);
        var cc = EnvoiBlService.Adresses(copies).Except(a, StringComparer.OrdinalIgnoreCase).ToList();
        var pieces = new List<PieceJointe>();
        foreach (var f in fiches)
        {
            var pdf = await edition.EditerAsync(f.Numero, apercu: false, ct);
            pieces.Add(new PieceJointe($"Bon de livraison {f.Numero}.pdf", pdf.Contenu));
        }
        var societe = await societes.LireNomAsync(u.Societe, ct) ?? "";
        var liste = string.Join(", ", fiches.Select(f => f.Numero));
        var (objet, phrase) = fiches.Count == 1
            ? ($"Bon de livraison n° {liste}", $"le bon de livraison n° {liste}" + (fiches[0].DateDepart is { } d ? $" (départ prévu le {d:dd/MM/yyyy})" : ""))
            : ($"Bons de livraison n° {liste}", $"les bons de livraison n° {liste}");
        var texte = $"Bonjour,\r\n\r\nVeuillez trouver ci-joint {phrase}.\r\n\r\nCordialement,\r\n{societe}".TrimEnd();
        return (new MessageBrouillon(a, cc, objet, texte, pieces), fiches);
    }

    // Une trace par pré-BL, avec son PDF ; le journal ne fait jamais échouer un envoi déjà parti
    private async Task TracerAsync(UtilisateurConnecte u, MessageBrouillon message, IReadOnlyList<FichePreBl> fiches, string canal, string statut,
        string expediteur, string redirection, string erreur)
    {
        try
        {
            var lot = Guid.NewGuid().ToString("N");
            await journal.EcrireAsync([.. fiches.Select((f, i) => new EnvoiDocument(u.Societe, lot, u.Profil, TypeEnvoi, 0, "", f.CompteGeneral,
                f.CompteClient, canal, statut, expediteur, message.Destinataires, message.Copies, redirection, message.Objet, erreur,
                message.PiecesJointes[i].Nom, message.PiecesJointes[i].Contenu, Document: f.Numero))], CancellationToken.None);
        }
        catch (Exception ex)
        {
            journalTechnique.LogWarning(ex, "Journal des envois (ENV100) non écrit pour les pré-BL {PreBl}", string.Join(", ", fiches.Select(f => f.Numero)));
        }
    }
}
