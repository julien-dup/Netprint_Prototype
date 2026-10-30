using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Donnees;

namespace NetPrint.Noyau.Messagerie;

// =====================================================================
// Envoi de messages par le serveur SMTP de la société, comme EnvoideFactures : paramètres FAC010 (PSMTPS serveur,
// PSMTPP port, PSMTPU / PSMTPM compte, PTLS, PEXPEN expéditeur), saisis par le programme Paramètres.exe ; accusé de
// lecture demandé.
// Garde-fou : la base de test contient les vrais serveurs et les vrais contacts des clients. Rien ne part tant que la
// configuration ne le dit pas (Messagerie:Mode) :
//   - « Desactive » (défaut) : envoi refusé ;
//   - « Redirection » : tout part vers Messagerie:Redirection, destinataires d'origine rappelés dans l'objet et le texte ;
//   - « Reel » : envoi aux vrais destinataires (production).
// =====================================================================
public interface IMessagerie
{
    Task<EtatMessagerie> EtatAsync(int societe, CancellationToken ct = default);

    // Envoie le message (destinataires, copies, pièces jointes) ; RegleMetierException si l'envoi est impossible
    Task EnvoyerAsync(int societe, MessageBrouillon message, CancellationToken ct = default);
}

// Disponible : un message peut partir ; sinon Raison dit pourquoi. Redirection : adresse qui reçoit tout (test).
public sealed record EtatMessagerie(bool Disponible, string Raison, string Expediteur, string? Redirection);

public sealed record ParametresSmtp(string Serveur, int Port, string Utilisateur, string MotDePasse, bool Tls, string Expediteur);

internal sealed class MessagerieSmtp(IConfiguration config) : IMessagerie
{
    private string Mode => config["Messagerie:Mode"] ?? "Desactive";
    private string? Redirection => config["Messagerie:Redirection"] is { Length: > 0 } r ? r.Trim() : null;

    public async Task<EtatMessagerie> EtatAsync(int societe, CancellationToken ct = default)
    {
        var p = await ParametresAsync(societe, ct);
        return ReglesMessagerie.Etat(p, Mode, Redirection);
    }

    public async Task EnvoyerAsync(int societe, MessageBrouillon message, CancellationToken ct = default)
    {
        var p = await ParametresAsync(societe, ct);
        var etat = ReglesMessagerie.Etat(p, Mode, Redirection);
        if (!etat.Disponible) throw new RegleMetierException(etat.Raison);
        using var mail = ReglesMessagerie.Preparer(p!, message, etat.Redirection);
        // SmtpClient ne connaît que STARTTLS (ports 25, 587) : le TLS implicite du port 465 bloquerait jusqu'au délai
        if (p!.Port == 465)
            throw new RegleMetierException("Le port 465 (TLS implicite) n'est pas pris en charge : utilisez le port 587 ou 25 avec TLS.");
        using var client = new SmtpClient(p.Serveur, p.Port > 0 ? p.Port : 25)
        {
            EnableSsl = p.Tls, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30_000,
        };
        if (p.Utilisateur != "") client.Credentials = new NetworkCredential(p.Utilisateur, p.MotDePasse);
        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (SmtpException ex)
        {
            throw new RegleMetierException($"Le serveur de messagerie a refusé le message : {ex.Message}");
        }
    }

    private async Task<ParametresSmtp?> ParametresAsync(int societe, CancellationToken ct)
    {
        var bib = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT PSMTPS, PSMTPP, PSMTPU, PSMTPM, PTLS, PEXPEN FROM {bib}.FAC010 WHERE RSOC = ?";
        cmd.Parameters.Valeur(societe);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new ParametresSmtp(rd.Texte(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4) == "O", rd.Texte(5));
    }
}

// Règles de l'envoi, sans réseau ni base (testées)
public static class ReglesMessagerie
{
    public static EtatMessagerie Etat(ParametresSmtp? p, string mode, string? redirection)
    {
        var expediteur = p?.Expediteur ?? "";
        if (p is null || p.Serveur == "")
            return new(false, "Le serveur SMTP de la société n'est pas renseigné (paramètres d'envoi des factures).", expediteur, null);
        if (expediteur == "")
            return new(false, "L'adresse d'expédition de la société n'est pas renseignée (paramètres d'envoi des factures).", expediteur, null);
        return mode switch
        {
            "Reel" => new(true, "", expediteur, null),
            "Redirection" when redirection is not null => new(true, "", expediteur, redirection),
            "Redirection" => new(false, "Envoi de test sans adresse de redirection (Messagerie:Redirection).", expediteur, null),
            _ => new(false, "L'envoi direct est désactivé sur ce poste (Messagerie:Mode) : préparez le message pour Outlook.", expediteur, null),
        };
    }

    // Message SMTP ; en redirection, seule l'adresse de test reçoit, les destinataires d'origine sont rappelés
    public static MailMessage Preparer(ParametresSmtp p, MessageBrouillon m, string? redirection)
    {
        var mail = new MailMessage { From = new MailAddress(p.Expediteur), Subject = m.Objet, Body = m.Texte, BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8 };
        if (redirection is not null)
        {
            var origine = $"À : {string.Join(", ", m.Destinataires)}" + (m.Copies.Count > 0 ? $" · copie : {string.Join(", ", m.Copies)}" : "");
            mail.To.Add(redirection);
            mail.Subject = $"[TEST] {m.Objet}";
            mail.Body = $"[Message de test redirigé — destinataires réels : {origine}]\n\n{m.Texte}";
        }
        else
        {
            foreach (var a in m.Destinataires) mail.To.Add(a);
            foreach (var a in m.Copies) mail.CC.Add(a);
        }
        // Accusé de lecture demandé (EnvoideFactures : AccuséRéception)
        mail.Headers.Add("Disposition-Notification-To", p.Expediteur);
        foreach (var pj in m.PiecesJointes)
            mail.Attachments.Add(new Attachment(new MemoryStream(pj.Contenu), pj.Nom, pj.Type));
        return mail;
    }
}
