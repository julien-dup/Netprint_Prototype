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
internal sealed class EnvoiFacturesService(EnvoiFacturesDb2 donnees, ConsultationFacturesDb2 consultation, DroitsFacturationDb2 droits,
    IEditionFactures edition, IContactsTiers contacts, IMessagerie messagerie,
    IJournalEnvois journal, IUtilisateurs utilisateurs, ISocietes societes, IUtilisateurCourant utilisateur,
    ILogger<EnvoiFacturesService> journalTechnique) : IEnvoiFactures
{
    public const string TypeEnvoi = "FAC";   // ENV100.ENTYPE (CHAR 3) : envoi d'une facture
    public const int Maxi = LimitesRecherche.Maximum;   // factures listées pour un client (garde-fou de la charte)
    public const string FormatPdf = "PDF";
    public const string ProgrammeEnvoiAuto = "PCFACAUTO";   // PRT000P1 : AOPT01 = « ** » interdit l'envoi automatique (WinDev)

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
        var factures = new List<(FactureAEnvoyer Facture, DocumentGenere Pdf)>();
        foreach (var lue in await RelireAsync(u, envoi, ct)) factures.Add((lue, await edition.EditerAsync(lue.Numero, lue.Date, ct)));
        var societe = await societes.LireNomAsync(u.Societe, ct) ?? "";
        return (Message(factures, nom ?? "", p?.Objet ?? "", envoi.Destinataires, envoi.Copies, societe), factures);
    }

    // Message : objet (modèle PFOBJM sinon « Facture n° … »), texte, un PDF par facture
    private static MessageBrouillon Message(IReadOnlyList<(FactureAEnvoyer Facture, DocumentGenere Pdf)> factures, string nom, string modeleObjet,
        IEnumerable<string> destinataires, IEnumerable<string> copies, string societe)
    {
        var a = Adresses(destinataires);
        var cc = Adresses(copies).Except(a, StringComparer.OrdinalIgnoreCase).ToList();
        var objet = Objet(modeleObjet, [.. factures.Select(f => f.Facture)], nom);
        var quoi = factures.Count == 1
            ? $"{(factures[0].Facture.Avoir ? "l'avoir" : "la facture")} n° {factures[0].Facture.Numero} du {factures[0].Facture.Date:dd/MM/yyyy}"
            : $"nos factures et avoirs n° {string.Join(", ", factures.Select(f => f.Facture.Numero))}";
        var texte = $"Bonjour,\r\n\r\nVeuillez trouver ci-joint {quoi}.\r\n\r\nCordialement,\r\n{societe}".TrimEnd();
        return new MessageBrouillon(a, cc, objet, texte, [.. factures.Select(f => new PieceJointe(f.Pdf.Nom, f.Pdf.Contenu))]);
    }

    // ---------- Envoi automatique ----------
    private async Task<UtilisateurConnecte> ExigerDroitAutoAsync(CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        if (await droits.OptionsProgrammeAsync(ProgrammeEnvoiAuto, u.Profil, ct) is [var total, ..] && total == "**")
            throw new RegleMetierException($"Vous n'avez pas le droit de lancer l'envoi automatique des factures ({ProgrammeEnvoiAuto}).");
        return u;
    }

    // Clients du lot, avec leur paramétrage d'envoi (objet du message)
    private async Task<(List<(ClientEnvoiAuto Client, ParametresEnvoiClient Parametres)> Clients, bool Tronque)> ClientsAutoAsync(UtilisateurConnecte u,
        CriteresEnvoiAuto c, CancellationToken ct)
    {
        var (lignes, tronque) = await donnees.FacturesAutoAsync(u.Societe, c, await consultation.RepresentantImposeAsync(u.Societe, u.Profil, ct), Maxi, ct);
        return ([.. lignes.GroupBy(l => (l.General, l.Client)).Select(g =>
        {
            var (gen, cli, nom, p, _) = g.First();
            return (new ClientEnvoiAuto(gen, cli, nom, p.Canal, p.Format, PrisEnCharge(p), p.Destinataires, p.UnParFacture, [.. g.Select(x => x.Facture)]), p);
        })], tronque);
    }

    private static ErreurLancement? ControlerAuto(CriteresEnvoiAuto c)
    {
        if (c.Du > c.Au) return new ErreurLancement(nameof(c.Du), "Période : le début est après la fin.");
        if (c.NumeroDu > c.NumeroAu) return new ErreurLancement(nameof(c.NumeroDu), "N° de facture : le début est après la fin.");
        if ((c.CompteGeneral is null) != (c.CompteClient is null))
            return new ErreurLancement(nameof(c.CompteGeneral), "Client : indiquez le compte général et le compte client, ou aucun des deux.");
        return null;
    }

    public async Task<LotEnvoiAuto> PreparerAutoAsync(CriteresEnvoiAuto c, CancellationToken ct = default)
    {
        var u = await ExigerDroitAutoAsync(ct);
        if (ControlerAuto(c) is { } e) return new LotEnvoiAuto([e], [], false, false, "", "", null);
        var etat = await messagerie.EtatAsync(u.Societe, null, ct);   // expéditeur : la société (FAC010.PEXPEN)
        var (clients, tronque) = await ClientsAutoAsync(u, c, ct);
        return new LotEnvoiAuto([], [.. clients.Select(x => x.Client)], tronque, etat.Disponible, etat.Raison, etat.Expediteur, etat.Redirection);
    }

    public async Task<IReadOnlyList<ClientEnvoiAuto>> EnvoyerAutoAsync(CriteresEnvoiAuto c, CancellationToken ct = default)
    {
        var u = await ExigerDroitAutoAsync(ct);
        if (ControlerAuto(c) is { } e) throw new RegleMetierException(e.Message);
        var etat = await messagerie.EtatAsync(u.Societe, null, ct);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);   // rien n'est tenté
        var (clients, tronque) = await ClientsAutoAsync(u, c, ct);
        if (tronque) throw new RegleMetierException($"Plus de {Maxi} factures à envoyer : affinez les critères (période, n°, client) avant d'envoyer.");
        var societe = await societes.LireNomAsync(u.Societe, ct) ?? "";
        var resultats = new List<ClientEnvoiAuto>();
        foreach (var (client, p) in clients)
        {
            ct.ThrowIfCancellationRequested();
            try { resultats.Add(await EnvoyerClientAsync(u, client, p, etat, societe, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)   // un client en erreur n'arrête pas le lot
            {
                resultats.Add(client with { Resultat = $"KO : {ex.Message}" });
            }
        }
        return resultats;
    }

    private async Task<ClientEnvoiAuto> EnvoyerClientAsync(UtilisateurConnecte u, ClientEnvoiAuto client, ParametresEnvoiClient p, EtatMessagerie etat,
        string societe, CancellationToken ct)
    {
        {
            if (!client.PrisEnCharge)
                return client with { Resultat = $"Non envoyé : {CanalLisible(client.Canal, client.Format)}, pas encore pris en charge" };
            if (Adresses(client.Destinataires).Count == 0)
                return client with { Resultat = "Non envoyé : aucun destinataire dans le paramétrage d'envoi du client (PRT057F)" };
            var envoyees = 0;
            var erreurs = new List<string>();
            foreach (var groupe in client.UnParFacture ? client.Factures.Select(f => (IReadOnlyList<FactureAEnvoyer>)[f]) : [client.Factures])
            {
                var envoi = new EnvoiFactures(client.CompteGeneral, client.CompteClient, groupe, client.Destinataires, []);
                List<(FactureAEnvoyer Facture, DocumentGenere Pdf)> factures = [];
                MessageBrouillon? message = null;
                try
                {
                    foreach (var f in groupe) factures.Add((f, await edition.EditerAsync(f.Numero, f.Date, ct)));
                    message = Message(factures, client.Nom, p.Objet, client.Destinataires, [], societe);
                    await messagerie.EnvoyerAsync(u.Societe, message, null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (message is not null)
                        await TracerAsync(u, envoi, message, factures, CanauxEnvoi.Smtp, StatutsEnvoi.Echec, etat.Expediteur, etat.Redirection ?? "", ex.Message);
                    erreurs.Add($"{string.Join(", ", groupe.Select(f => f.Numero))} : {ex.Message}");
                    continue;
                }
                await TracerAsync(u, envoi, message, factures, CanauxEnvoi.Smtp, etat.Redirection is null ? StatutsEnvoi.Envoye : StatutsEnvoi.Test,
                    etat.Expediteur, etat.Redirection ?? "", "");
                if (etat.Redirection is null)
                    try { await donnees.MarquerAsync(u.Societe, groupe.Select(f => (f.Numero, f.Date)), manuel: false, "M", FormatPdf, DateTime.Now, CancellationToken.None); }
                    catch (Exception ex) { journalTechnique.LogWarning(ex, "Factures envoyées mais non marquées envoyées (FAC080)"); }
                envoyees += groupe.Count;
            }
            return client with
            {
                Envoye = erreurs.Count == 0,
                Resultat = erreurs.Count == 0 ? $"OK : {envoyees} facture{(envoyees > 1 ? "s" : "")} envoyée{(envoyees > 1 ? "s" : "")}{(etat.Redirection is { } r ? $" (test, reçu par {r})" : "")}"
                    : $"KO ({envoyees} envoyée{(envoyees > 1 ? "s" : "")}) : {string.Join(" ; ", erreurs)}",
            };
        }
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
