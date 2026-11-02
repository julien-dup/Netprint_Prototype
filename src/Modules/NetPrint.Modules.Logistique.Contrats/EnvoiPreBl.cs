using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Envoi groupé des pré-BL par e-mail (WinDev PreBL, FEN_EnvoideBL : PDF par EXPEMPNAV puis Outlook) : les pré-BL non livrés
// d'un même client, chacun en PDF (« Bon de livraison », envoyé au client avant la livraison), dans un seul message ;
// brouillon Outlook (.eml) ou envoi par le serveur de la société, comme l'envoi des BL ; chaque pré-BL tracé dans le journal
// ENV100 sous son n° (celui du BL qu'il deviendra).
// =====================================================================
public interface IEnvoiPreBl
{
    Task<OptionsEnvoiBl> OptionsAsync(IReadOnlyList<int> numeros, CancellationToken ct = default);
    Task<DocumentGenere> PreparerMessageAsync(IReadOnlyList<int> numeros, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies,
        CancellationToken ct = default);
    Task EnvoyerAsync(IReadOnlyList<int> numeros, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, CancellationToken ct = default);
}
