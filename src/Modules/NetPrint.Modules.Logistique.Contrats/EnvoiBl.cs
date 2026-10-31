using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Envoi d'un BL par e-mail (EXP010S option EE → EXP030 en PDF → SNDEMAIL), comme l'ARC : brouillon Outlook (.eml, sans
// serveur) ou envoi par le serveur SMTP de la société (garde-fou Messagerie:Mode) ; chaque envoi tracé dans le journal ENV100
// avec le PDF ; le BL envoyé est marqué édité, comme le faisait le RPG. Refusé à qui ne voit qu'une partie du BL.
// =====================================================================
public interface IEnvoiBl
{
    Task<OptionsEnvoiBl> OptionsAsync(int numeroBl, CancellationToken ct = default);
    Task<DocumentGenere> PreparerMessageAsync(int numeroBl, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, CancellationToken ct = default);
    Task EnvoyerAsync(int numeroBl, IReadOnlyList<string> destinataires, IReadOnlyList<string> copies, CancellationToken ct = default);
    Task<DocumentGenere?> PdfEnvoyeAsync(int numeroBl, long id, CancellationToken ct = default);
}

// Propose : contact coché d'office (contacts « par défaut » du client pour les BL)
public sealed record ContactBl(string Nom, string Email, bool ParDefaut, bool Propose);

public sealed record EnvoiBlJournalise(long Id, DateTime Date, string Profil, string Canal, string Statut, string Destinataires, string Copies,
    string Redirection, string Erreur, bool AvecPdf);

public sealed record OptionsEnvoiBl(
    IReadOnlyList<ContactBl> Contacts, bool MessagerieDisponible, string RaisonIndisponible, string Expediteur, string? Redirection,
    IReadOnlyList<EnvoiBlJournalise> Historique);
