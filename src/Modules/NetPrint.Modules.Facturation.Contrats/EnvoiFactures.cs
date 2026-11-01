using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Envoi des factures par e-mail (EnvoideFactures, onglet « manuel ») : factures éditées, pas encore envoyées, pas exclues de
// l'envoi (FDNONE), d'un client facturé sur une période ; destinataires du paramétrage d'envoi du client (PRT057F.PFDEST1-5),
// sinon ses contacts « facture » (PRT051F) ; brouillon Outlook (.eml) ou envoi par le serveur SMTP de la société (garde-fou
// Messagerie:Mode) ; chaque facture tracée dans le journal ENV100 (type FAC) avec son PDF, et marquée envoyée (FAC080 FDTAGE,
// date, heure, manuel, mail, PDF) seulement après l'envoi réel (le WinDev la marquait avant).
// =====================================================================
public interface IEnvoiFactures
{
    // Client facturé : ses paramètres d'envoi, ses contacts, l'état de la messagerie, ses factures à envoyer sur la période
    Task<FacturesAEnvoyer> ChercherAsync(CriteresEnvoiFactures criteres, CancellationToken ct = default);

    // Brouillon Outlook : rien n'est marqué envoyé (rien ne dit qu'il partira) ; la trace ENV100 est « préparée »
    Task<DocumentGenere> PreparerMessageAsync(EnvoiFactures envoi, CancellationToken ct = default);

    // Envoi par le serveur ; factures marquées envoyées et tracées
    Task EnvoyerAsync(EnvoiFactures envoi, CancellationToken ct = default);

    // Après un brouillon parti depuis Outlook : marquer les factures envoyées (manuel, mail), relues comme pour l'envoi
    Task MarquerEnvoyeesAsync(EnvoiFactures envoi, CancellationToken ct = default);
}

public sealed class CriteresEnvoiFactures
{
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public DateOnly? Du { get; set; }
    public DateOnly? Au { get; set; }
    public bool AvecDejaEnvoyees { get; set; }   // renvoyer une facture déjà envoyée
}

public sealed record FactureAEnvoyer(int Numero, DateOnly Date, bool Avoir, string DonneurOrdre, decimal Ht, string Envoi);

public sealed record ContactFacture(string Nom, string Email, bool ParDefaut, bool Propose);

// Canal / Format : PRT057F (PFFTP N mail, O FTP, X plateforme ; PFFMT PDF, FACX…) ; Pris en charge : mail en PDF seulement pour
// l'instant ; Destinataires : PFDEST1-5 (prioritaires sur les contacts, comme le WinDev)
public sealed record FacturesAEnvoyer(
    IReadOnlyList<ErreurLancement> Erreurs, int CompteGeneral, int CompteClient, string NomClient, string Canal, string Format,
    bool PrisEnCharge, IReadOnlyList<string> Destinataires, IReadOnlyList<ContactFacture> Contacts, bool MessagerieDisponible,
    string RaisonIndisponible, string Expediteur, string? Redirection, IReadOnlyList<FactureAEnvoyer> Factures, bool Tronque);

public sealed record EnvoiFactures(int CompteGeneral, int CompteClient, IReadOnlyList<FactureAEnvoyer> Factures, IReadOnlyList<string> Destinataires,
    IReadOnlyList<string> Copies);
