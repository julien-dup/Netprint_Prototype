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

    // PDF de l'ARC. avecLignesSoldees null = paramètre société. Droit « Édition » (PRT000P1 AOPT07) requis, sauf aperçu.
    Task<DocumentGenere> EditerAsync(CleCommande commande, bool? avecLignesSoldees = null, bool apercu = false,
        CancellationToken ct = default);
}

public sealed record OptionsArc(bool AvecLignesSoldees, IReadOnlyList<ContactArc> Contacts, bool PeutEditer);

// Contact du client destinataire des ARC ; ParDefaut = proposé d'office (CNARCD)
public sealed record ContactArc(string Nom, string Email, bool ParDefaut);
