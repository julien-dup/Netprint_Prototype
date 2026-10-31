namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Changement du client, de la date ou de l'enseigne d'une commande existante (F17 de DOS505, « déprotection de
// l'en-tête »). Refusé si la commande a un BL ou une facture pour son client actuel. Les n° d'adresse (livraison des
// lignes, facture, ARC) et le client facturé restent ceux de l'ancien client : un avertissement à accepter le rappelle.
// =====================================================================
public interface IChangementClientCommande
{
    // SaisieInvalideException si une zone est incorrecte ; Forcable = true pour l'avertissement des références de l'ancien
    // client. Renvoie faux si rien n'a changé.
    Task<bool> ChangerAsync(CleCommande commande, ChangementClient changement, CancellationToken ct = default);
}

public sealed class ChangementClient
{
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public string Enseigne { get; set; } = "";
    public DateOnly? DateDossier { get; set; }
    public bool AvertissementAccepte { get; set; }
}
