using System.Net.Mail;
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
// Envoi d'un BL (EXP010S option EE) : PDF de l'édition (IEditionBl), destinataires parmi les contacts « bon de livraison » du
// client (PRT051F), brouillon Outlook ou envoi SMTP au nom de l'utilisateur, trace ENV100 (type « BL », n° de BL dans ENNDOC,
// RDOSS à 0), BL marqué édité. Objet « Bon de livraison n° … » comme SNDEMAIL d'EXP030CLP.
// =====================================================================
internal sealed class EnvoiBlService(IEditionBl edition, IConsultationBl consultation, IContactsTiers contacts, IMessagerie messagerie,
    IJournalEnvois journal, IUtilisateurs utilisateurs, ISocietes societes, IUtilisateurCourant utilisateur,
    ILogger<EnvoiBlService> journalTechnique) : IEnvoiBl
{
    public async Task<OptionsEnvoiBl> OptionsAsync(int numeroBl, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var bl = await BlAsync(u, numeroBl, ct);
        var liste = bl.Entete.CompteGeneral == 0 ? []   // BL divers fournisseur : pas de contacts client
            : await contacts.LireAsync(u.Societe, bl.Entete.CompteGeneral, bl.Entete.CompteClient, DocumentEnvoye.BonLivraison, ct);
        var etat = await messagerie.EtatAsync(u.Societe, await utilisateurs.EmailAsync(u.Profil, ct), ct);
        var historique = (await journal.LireDocumentAsync(u.Societe, TypesEnvoi.Bl, numeroBl, ct))
            .Select(e => new EnvoiBlJournalise(e.Id, e.Date, e.Profil, e.Canal, e.Statut, e.Destinataires, e.Copies, e.Redirection, e.Erreur,
                e.AvecPdf)).ToList();
        var tous = !liste.Any(c => c.ParDefaut);   // sans contact « par défaut », tous sont proposés (comme pour l'ARC)
        return new OptionsEnvoiBl(liste.Select(c => new ContactBl(c.Nom, c.Email, c.ParDefaut, tous || c.ParDefaut)).ToList(), etat.Disponible,
            etat.Raison, etat.Expediteur, etat.Redirection, historique);
    }

    public async Task<DocumentGenere> PreparerMessageAsync(int numeroBl, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies,
        CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (message, bl) = await MessageAsync(u, numeroBl, destinataires, copies, ct);
        // Brouillon : rien ne dit qu'il partira, le BL n'est pas marqué édité
        await TracerAsync(u, message, bl, CanauxEnvoi.Outlook, StatutsEnvoi.Prepare, "", "", "");
        return message.VersEml($"BL {numeroBl}.eml");
    }

    public async Task EnvoyerAsync(int numeroBl, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (Adresses(destinataires).Count == 0) throw new RegleMetierException("Choisissez au moins un destinataire.");
        var expediteur = await utilisateurs.EmailAsync(u.Profil, ct);
        var etat = await messagerie.EtatAsync(u.Societe, expediteur, ct);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);   // rien n'est tenté, rien n'est tracé
        var (message, bl) = await MessageAsync(u, numeroBl, destinataires, copies, ct);
        try
        {
            await messagerie.EnvoyerAsync(u.Societe, message, expediteur, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TracerAsync(u, message, bl, CanauxEnvoi.Smtp, StatutsEnvoi.Echec, etat.Expediteur, etat.Redirection ?? "", ex.Message);
            throw new RegleMetierException($"Le bon de livraison {numeroBl} n'a pas été envoyé : {ex.Message}");
        }
        await TracerAsync(u, message, bl, CanauxEnvoi.Smtp, etat.Redirection is null ? StatutsEnvoi.Envoye : StatutsEnvoi.Test,
            etat.Expediteur, etat.Redirection ?? "", "");
        await MarquerEditeAsync(numeroBl);
    }

    public async Task<DocumentGenere?> PdfEnvoyeAsync(int numeroBl, long id, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        await ExigerCompletAsync(numeroBl, ct);   // le PDF envoyé contient tout le BL
        return await journal.LirePdfDocumentAsync(u.Societe, id, TypesEnvoi.Bl, numeroBl, ct) is { } pdf ? new DocumentGenere(pdf.Nom, pdf.Contenu) : null;
    }

    private async Task<FicheBl> BlAsync(UtilisateurConnecte u, int numeroBl, CancellationToken ct) =>
        await consultation.LireAsync(numeroBl, ct)
            ?? throw new RegleMetierException($"Le BL {numeroBl} n'existe pas ou ne vous est pas accessible.");

    private async Task ExigerCompletAsync(int numeroBl, CancellationToken ct)
    {
        if (!await edition.CompletAsync(numeroBl, ct))
            throw new RegleMetierException("Vous ne voyez qu'une partie de ce BL : il ne peut être envoyé que par un utilisateur qui le voit en entier.");
    }

    // Message : objet et texte comme l'envoi du RPG, PDF du BL en pièce jointe
    private async Task<(MessageBrouillon Message, FicheBl Bl)> MessageAsync(UtilisateurConnecte u, int numeroBl, IReadOnlyList<string> destinataires,
        IReadOnlyList<string> copies, CancellationToken ct)
    {
        var bl = await BlAsync(u, numeroBl, ct);
        await ExigerCompletAsync(numeroBl, ct);   // jamais un BL incomplet au client
        var a = Adresses(destinataires);
        var cc = Adresses(copies).Except(a, StringComparer.OrdinalIgnoreCase).ToList();
        var pdf = await edition.EditerAsync(numeroBl, apercu: false, ct);
        var societe = await societes.LireNomAsync(u.Societe, ct) ?? "";
        var date = bl.Entete.DateExpedition is { } d ? $" du {d:dd/MM/yyyy}" : "";
        var texte = $"Bonjour,\r\n\r\nVeuillez trouver ci-joint le bon de livraison n° {numeroBl}{date}.\r\n\r\nCordialement,\r\n{societe}".TrimEnd();
        return (new MessageBrouillon(a, cc, $"Bon de livraison n° {numeroBl}", texte, [new PieceJointe(pdf.Nom, pdf.Contenu)]), bl);
    }

    // Adresses valides, sans doublon (séparateurs ; et ,) ; aussi utilisé par l'écran pour afficher les mêmes destinataires
    internal static List<string> Adresses(IEnumerable<string> saisies) =>
        saisies.SelectMany(x => (x ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => MailAddress.TryCreate(x, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Le journal ne fait jamais échouer un envoi déjà parti
    private async Task TracerAsync(UtilisateurConnecte u, MessageBrouillon message, FicheBl bl, string canal, string statut, string expediteur,
        string redirection, string erreur)
    {
        try
        {
            var pj = message.PiecesJointes[0];
            await journal.EcrireAsync([new EnvoiDocument(u.Societe, Guid.NewGuid().ToString("N"), u.Profil, TypesEnvoi.Bl, 0, "",
                bl.Entete.CompteGeneral, bl.Entete.CompteClient, canal, statut, expediteur, message.Destinataires, message.Copies, redirection,
                message.Objet, erreur, pj.Nom, pj.Contenu, Document: bl.Entete.NumeroBl)], CancellationToken.None);
        }
        catch (Exception ex)
        {
            journalTechnique.LogWarning(ex, "Journal des envois (ENV100) non écrit pour le BL {Bl}", bl.Entete.NumeroBl);
        }
    }

    // BL envoyé = BL édité (EXP030 en mode PDF posait la même marque). Le message est déjà parti : un échec est seulement signalé
    private async Task MarquerEditeAsync(int numeroBl)
    {
        try { await edition.MarquerEditeAsync(numeroBl, CancellationToken.None); }
        catch (Exception ex) { journalTechnique.LogWarning(ex, "BL {Bl} envoyé mais non marqué édité", numeroBl); }
    }
}
