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
    // Valeurs proposées : lignes soldées comprises ou non (DOS005.PLGARC), adresse e-mail du client
    Task<OptionsArc> LireOptionsAsync(CleCommande commande, CancellationToken ct = default);

    // PDF de l'ARC. avecLignesSoldees null = paramètre société. Droit « Édition » (PRT000P1 AOPT07) requis, sauf aperçu.
    Task<DocumentGenere> EditerAsync(CleCommande commande, bool? avecLignesSoldees = null, bool apercu = false,
        CancellationToken ct = default);
}

public sealed record OptionsArc(bool AvecLignesSoldees, string EmailClient, bool PeutEditer);
