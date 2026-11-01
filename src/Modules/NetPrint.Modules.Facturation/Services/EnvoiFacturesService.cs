using System.Net.Mail;
using Microsoft.Extensions.Logging;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;
using NetPrint.Noyau.Utilisateurs;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Envoi des factures par e-mail (EnvoideFactures, onglet manuel) : un message pour les factures cochées d'un client facturé, PDF
// de chaque facture (IEditionFactures, FAC202P5) en pièce jointe ; destinataires PRT057F.PFDEST1-5 sinon contacts « facture »
// (PRT051F CNFAC / CNFACD) ; objet PRT057F.PFOBJM (&NOFAC, &NOM, &NBFAC) sinon « Facture n° … » ; brouillon Outlook ou SMTP
// (expéditeur : l'utilisateur, comme Outlook dans le WinDev) ; trace ENV100 par facture (type FAC, n° de facture dans ENNDOC) ;
// marque d'envoi FAC080 après l'envoi réel seulement (le WinDev marquait avant, même si l'envoi échouait). Pour l'instant : mail
// en PDF ; les clients en FTP, plateforme (SuperPDP) ou formats Factur-X / EDI / CSV sont signalés, pas encore pris en charge.
// =====================================================================
internal sealed class EnvoiFacturesService(EnvoiFacturesDb2 donnees, ConsultationFacturesDb2 consultation, IEditionFactures edition, IContactsTiers contacts, IMessagerie messagerie,
    IJournalEnvois journal, IUtilisateurs utilisateurs, ISocietes societes, IUtilisateurCourant utilisateur,
    ILogger<EnvoiFacturesService> journalTechnique) : IEnvoiFactures
{
    public const string TypeEnvoi = "FAC";   // ENV100.ENTYPE (CHAR 3) : envoi d'une facture
    public const int Maxi = LimitesRecherche.Maximum;   // factures listées pour un client (garde-fou de la charte)
    public const string FormatPdf = "PDF";

    // Mail en PDF : canal N ou blanc, format blanc, PDF ou PDF1 (EDIP : PDF + fichier EDI, pas encore)
    internal static bool PrisEnCharge(ParametresEnvoiClient? p) => p is null || (p.Canal is "" or "N" && p.Format is "" or "PDF" or "PDF1");

    public async Task<FacturesAEnvoyer> ChercherAsync(CriteresEnvoiFactures c, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (c.CompteGeneral is not { } gen || c.CompteClient is not { } cli)
            return Refus(new ErreurLancement(nameof(c.CompteGeneral), "Indiquez le compte général et le compte client du client facturé."));
        if (c.Du > c.Au) return Refus(new ErreurLancement(nameof(c.Du), "Période : le début est après la fin."));
        var (nom, p) = await donnees.ClientAsync(u.Societe, gen, cli, ct);
        if (nom is null) return Refus(new ErreurLancement(nameof(c.CompteGeneral), $"Le client {gen} {cli} n'existe pas."));
        var liste = await contacts.LireAsync(u.Societe, gen, cli, DocumentEnvoye.Facture, ct);
        var tous = !liste.Any(x => x.ParDefaut);   // sans contact « par défaut », tous sont proposés (comme pour le BL)
        var etat = await messagerie.EtatAsync(u.Societe, await utilisateurs.EmailAsync(u.Profil, ct), ct);
        var (factures, tronque) = await donnees.FacturesAsync(u.Societe, gen, cli, c.Du, c.Au, c.AvecDejaEnvoyees, Maxi, ct,
            await consultation.RepresentantImposeAsync(u.Societe, u.Profil, ct));
        return new FacturesAEnvoyer([], gen, cli, nom, p?.Canal ?? "", p?.Format ?? "", PrisEnCharge(p), p?.Destinataires ?? [],
            [.. liste.Select(x => new ContactFacture(x.Nom, x.Email, x.ParDefaut, (p?.Destinataires.Count ?? 0) == 0 && (tous || x.ParDefaut)))],
            etat.Disponible, etat.Raison, etat.Expediteur, etat.Redirection, factures, tronque);

        static FacturesAEnvoyer Refus(ErreurLancement e) => new([e], 0, 0, "", "", "", false, [], [], false, "", "", null, [], false);
    }

    public async Task<DocumentGenere> PreparerMessageAsync(EnvoiFactures envoi, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (message, factures) = await MessageAsync(u, envoi, ct);
        // Brouillon : rien ne dit qu'il partira, les factures ne sont pas marquées envoyées
        await TracerAsync(u, envoi, message, factures, CanauxEnvoi.Outlook, StatutsEnvoi.Prepare, "", "", "");
        return message.VersEml(factures.Count == 1 ? $"Facture {factures[0].Facture.Numero}.eml" : $"Factures {envoi.CompteGeneral} {envoi.CompteClient}.eml");
    }

    public async Task EnvoyerAsync(EnvoiFactures envoi, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (Adresses(envoi.Destinataires).Count == 0) throw new RegleMetierException("Choisissez au moins un destinataire.");
        var expediteur = await utilisateurs.EmailAsync(u.Profil, ct);
        var etat = await messagerie.EtatAsync(u.Societe, expediteur, ct);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);   // rien n'est tenté, rien n'est tracé
        var (message, factures) = await MessageAsync(u, envoi, ct);
        try
        {
            await messagerie.EnvoyerAsync(u.Societe, message, expediteur, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await TracerAsync(u, envoi, message, factures, CanauxEnvoi.Smtp, StatutsEnvoi.Echec, etat.Expediteur, etat.Redirection ?? "", ex.Message);
            throw new RegleMetierException($"Les factures n'ont pas été envoyées : {ex.Message}");
        }
        await TracerAsync(u, envoi, message, factures, CanauxEnvoi.Smtp, etat.Redirection is null ? StatutsEnvoi.Envoye : StatutsEnvoi.Test,
            etat.Expediteur, etat.Redirection ?? "", "");
        // Envoi de test (redirection) : la facture n'est pas partie chez le client, elle n'est pas marquée envoyée
        if (etat.Redirection is null) await MarquerAsync(u, factures.Select(f => f.Facture), CancellationToken.None);
    }

    public async Task MarquerEnvoyeesAsync(EnvoiFactures envoi, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var factures = await RelireAsync(u, envoi, ct);   // du client, éditées, envoyables, accessibles, client en mail PDF
        await donnees.MarquerAsync(u.Societe, factures.Select(f => (f.Numero, f.Date)), manuel: true, "M", FormatPdf, DateTime.Now, ct);
    }

    // Factures cochées relues par leur clé : toujours du client facturé, éditées, envoyables, accessibles au profil ; client en mail PDF
    private async Task<List<FactureAEnvoyer>> RelireAsync(UtilisateurConnecte u, EnvoiFactures envoi, CancellationToken ct)
    {
        if (envoi.Factures.Count == 0) throw new RegleMetierException("Cochez au moins une facture.");
        var (nom, p) = await donnees.ClientAsync(u.Societe, envoi.CompteGeneral, envoi.CompteClient, ct);
        if (nom is null) throw new RegleMetierException($"Le client {envoi.CompteGeneral} {envoi.CompteClient} n'existe pas.");
        if (!PrisEnCharge(p))
            throw new RegleMetierException($"Ce client reçoit ses factures par {CanalLisible(p!.Canal, p.Format)} : cet envoi n'est pas encore pris en charge ici.");
        var cles = envoi.Factures.Select(f => (f.Numero, f.Date)).Distinct().ToList();
        var (lues, _) = await donnees.FacturesAsync(u.Societe, envoi.CompteGeneral, envoi.CompteClient, null, null, avecDejaEnvoyees: true, cles.Count, ct,
            await consultation.RepresentantImposeAsync(u.Societe, u.Profil, ct), cles);
        if (cles.FirstOrDefault(k => !lues.Any(x => (x.Numero, x.Date) == k)) is { Numero: > 0 } absente)
            throw new RegleMetierException($"La facture {absente.Numero} du {absente.Date:dd/MM/yyyy} ne peut pas être envoyée à ce client (pas éditée, supprimée, exclue de l'envoi ou pas accessible).");
        return lues;
    }

    // Factures cochées relues (toujours du client, éditées, envoyables), PDF de chacune, objet et texte du message
    private async Task<(MessageBrouillon Message, List<(FactureAEnvoyer Facture, DocumentGenere Pdf)> Factures)> MessageAsync(UtilisateurConnecte u,
        EnvoiFactures envoi, CancellationToken ct)
    {
        var (nom, p) = await donnees.ClientAsync(u.Societe, envoi.CompteGeneral, envoi.CompteClient, ct);
        var factures = new List<(FactureAEnvoyer, DocumentGenere)>();
        foreach (var lue in await RelireAsync(u, envoi, ct)) factures.Add((lue, await edition.EditerAsync(lue.Numero, lue.Date, ct)));
        nom ??= "";
        var a = Adresses(envoi.Destinataires);
        var cc = Adresses(envoi.Copies).Except(a, StringComparer.OrdinalIgnoreCase).ToList();
        var societe = await societes.LireNomAsync(u.Societe, ct) ?? "";
        var numeros = string.Join(", ", factures.Select(f => f.Item1.Numero));
        var objet = Objet(p?.Objet ?? "", factures.Select(f => f.Item1).ToList(), nom);
        var quoi = factures.Count == 1
            ? $"{(factures[0].Item1.Avoir ? "l'avoir" : "la facture")} n° {factures[0].Item1.Numero} du {factures[0].Item1.Date:dd/MM/yyyy}"
            : $"nos factures et avoirs n° {numeros}";
        var texte = $"Bonjour,\r\n\r\nVeuillez trouver ci-joint {quoi}.\r\n\r\nCordialement,\r\n{societe}".TrimEnd();
        return (new MessageBrouillon(a, cc, objet, texte, [.. factures.Select(f => new PieceJointe(f.Item2.Nom, f.Item2.Contenu))]), factures);
    }

    // Objet PRT057F.PFOBJM (variables du WinDev : &NOFAC n° de facture(s), &NOM client, &NBFAC nombre) ; sinon « Facture n° … »
    internal static string Objet(string modele, IReadOnlyList<FactureAEnvoyer> factures, string nom)
    {
        var numeros = string.Join(", ", factures.Select(f => f.Numero));
        if (modele.Trim() != "")
            return System.Text.RegularExpressions.Regex.Replace(
                modele.Replace("&NOFAC", numeros).Replace("&NOM", nom).Replace("&NBFAC", factures.Count.ToString()), @"\s+", " ").Trim();
        return factures.Count == 1 ? $"{(factures[0].Avoir ? "Avoir" : "Facture")} n° {numeros}" : $"Factures n° {numeros}";
    }

    // Canal d'envoi du client en clair (PRT057F.PFFTP, PFFMT)
    public static string CanalLisible(string canal, string format) => canal switch
    {
        "O" => "FTP",
        "X" => "la plateforme de facturation électronique",
        _ => $"e-mail au format {format}",
    };

    // Adresses valides, sans doublon (séparateurs ; et ,)
    internal static List<string> Adresses(IEnumerable<string> saisies) =>
        saisies.SelectMany(x => (x ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => MailAddress.TryCreate(x, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Le journal ne fait jamais échouer un envoi déjà parti : une trace par facture, avec son PDF, même identifiant de message
    private async Task TracerAsync(UtilisateurConnecte u, EnvoiFactures envoi, MessageBrouillon message,
        IReadOnlyList<(FactureAEnvoyer Facture, DocumentGenere Pdf)> factures, string canal, string statut, string expediteur, string redirection,
        string erreur)
    {
        try
        {
            var id = Guid.NewGuid().ToString("N");
            await journal.EcrireAsync([.. factures.Select(f => new EnvoiDocument(u.Societe, id, u.Profil, TypeEnvoi, 0, "", envoi.CompteGeneral,
                envoi.CompteClient, canal, statut, expediteur, message.Destinataires, message.Copies, redirection, message.Objet, erreur, f.Pdf.Nom,
                f.Pdf.Contenu, Document: f.Facture.Numero))], CancellationToken.None);
        }
        catch (Exception ex)
        {
            journalTechnique.LogWarning(ex, "Journal des envois (ENV100) non écrit pour les factures {Factures}",
                string.Join(", ", factures.Select(f => f.Facture.Numero)));
        }
    }

    // Les messages sont partis : un échec du marquage est seulement signalé (la facture serait reproposée)
    private async Task MarquerAsync(UtilisateurConnecte u, IEnumerable<FactureAEnvoyer> factures, CancellationToken ct)
    {
        try { await donnees.MarquerAsync(u.Societe, factures.Select(f => (f.Numero, f.Date)), manuel: true, "M", FormatPdf, DateTime.Now, ct); }
        catch (Exception ex) { journalTechnique.LogWarning(ex, "Factures envoyées mais non marquées envoyées (FAC080)"); }
    }
}
