using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// ARC (accusé de réception) d'une commande de négoce ou de produit fini : reprise d'ARC105P (NEG510 F18, navigateur,
// fiche client), produite en PDF par l'application, sans programme IBM i.
// L'ARC est recalculé à chaque édition sur l'état de la commande (comme le RPG : pas de version figée). L'aperçu
// est le même document, avec un filigrane, pour suivre la commande pendant sa saisie.
// =====================================================================
public interface IEditionArc
{
    // Valeurs proposées : lignes soldées comprises ou non (DOS005.PLGARC), contacts ARC du client (PRT051F CNARC / CNARCD)
    // Plusieurs commandes (envoi groupé) : options de la première commande accessible
    Task<OptionsArc> LireOptionsAsync(IReadOnlyList<CleCommande> commandes, CancellationToken ct = default);

    // Envoi par la messagerie de l'utilisateur (EnvoiARC, VisuArc, NEG510 F18), sans serveur SMTP : brouillon .eml avec
    // l'ARC de chaque commande en pièce jointe, à ouvrir dans Outlook. Commandes d'un même client ; droit « Édition ».
    // Objet : celui d'ARC105CLP (une commande) ou d'EnvoiARC (plusieurs) ; texte : chapitre A du canevas.
    Task<DocumentGenere> PreparerMessageAsync(IReadOnlyList<CleCommande> commandes, IReadOnlyList<string> destinataires,
        IReadOnlyList<string> copies, bool? avecLignesSoldees = null, CancellationToken ct = default);

    // Envoi direct du même message par le serveur SMTP de la société (comme EnvoideFactures), sans passer par Outlook
    Task EnvoyerMessageAsync(IReadOnlyList<CleCommande> commandes, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies,
        bool? avecLignesSoldees = null, CancellationToken ct = default);

    // Envoi groupé de commandes de plusieurs clients : un message par client, à ses contacts ARC (ceux « par défaut »,
    // sinon tous ; le premier en destinataire, les autres en copie). Planifier montre ce qui partira, sans rien envoyer ;
    // Envoyer envoie ce plan, tel que l'utilisateur l'a vu et confirmé, et renvoie le résultat client par client
    // (un échec n'arrête pas les autres).
    Task<IReadOnlyList<EnvoiArcClient>> PlanifierEnvoiAsync(IReadOnlyList<CleCommande> commandes, CancellationToken ct = default);
    Task<IReadOnlyList<EnvoiArcClient>> EnvoyerParClientAsync(IReadOnlyList<EnvoiArcClient> plan, bool? avecLignesSoldees = null,
        CancellationToken ct = default);

    // Journal des envois de l'ARC d'une commande (le plus récent d'abord) et PDF tel qu'il a été envoyé
    Task<IReadOnlyList<EnvoiArcTrace>> LireEnvoisAsync(CleCommande commande, CancellationToken ct = default);
    Task<DocumentGenere?> LirePdfEnvoyeAsync(CleCommande commande, long id, CancellationToken ct = default);

    // PDF de l'ARC. avecLignesSoldees null = paramètre société. Droit « Édition » (PRT000P1 AOPT07) requis, sauf aperçu.
    Task<DocumentGenere> EditerAsync(CleCommande commande, bool? avecLignesSoldees = null, bool apercu = false,
        CancellationToken ct = default);
}

// EnvoiDirect : le serveur SMTP de la société peut être utilisé (sinon RaisonEnvoiDirect) ; Redirection : en test, adresse
// qui reçoit tous les messages
public sealed record OptionsArc(bool AvecLignesSoldees, IReadOnlyList<ContactArc> Contacts, bool PeutEditer, bool EnvoiDirect = false,
    string RaisonEnvoiDirect = "", string Expediteur = "", string? Redirection = null);

// Une ligne du journal des envois. Canal : « Envoi direct » ou « Outlook » ; Statut : Envoyé, Test, Échec, Préparé
public sealed record EnvoiArcTrace(long Id, DateTime Date, string Profil, string Canal, string Statut, string Expediteur, string Destinataires,
    string Copies, string Redirection, string Objet, string Erreur, bool AvecPdf);

// Un message de l'envoi groupé : client, commandes, destinataires ; Erreur si rien ne peut partir ou si l'envoi a échoué
public sealed record EnvoiArcClient(string Client, string NomClient, IReadOnlyList<CleCommande> Commandes, IReadOnlyList<string> Destinataires,
    IReadOnlyList<string> Copies, string? Erreur, bool Envoye);

// Contact du client destinataire des ARC ; ParDefaut = CNARCD ; Propose = coché d'office (contacts par défaut, sinon tous)
public sealed record ContactArc(string Nom, string Email, bool ParDefaut, bool Propose = false);
