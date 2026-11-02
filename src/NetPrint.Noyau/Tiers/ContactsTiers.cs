using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;

namespace NetPrint.Noyau.Tiers;

// Contacts d'un client (PRT051F) destinataires d'un type de document : CNARC / CNARCD pour l'ARC, CNOFFRE / CNOFFRED
// pour l'offre, etc. « Par défaut » = destinataire proposé d'office (EnvoiARC FEN_Adrmail). Contacts supprimés exclus.
public interface IContactsTiers
{
    Task<IReadOnlyList<ContactEnvoi>> LireAsync(int societe, int compteGeneral, int compteClient, DocumentEnvoye document,
        CancellationToken ct = default);
}

public enum DocumentEnvoye { Arc, Offre, BonLivraison, Facture, PvControle, Bag }

public sealed record ContactEnvoi(string Nom, string Email, bool ParDefaut);

internal sealed class ContactsTiersDb2(IConfiguration config) : IContactsTiers
{
    public async Task<IReadOnlyList<ContactEnvoi>> LireAsync(int societe, int compteGeneral, int compteClient, DocumentEnvoye document,
        CancellationToken ct = default)
    {
        var (envoi, defaut) = document switch
        {
            DocumentEnvoye.Arc => ("CNARC", "CNARCD"),
            DocumentEnvoye.Offre => ("CNOFFRE", "CNOFFRED"),
            DocumentEnvoye.BonLivraison => ("CNBL", "CNBLD"),
            DocumentEnvoye.Facture => ("CNFAC", "CNFACD"),
            DocumentEnvoye.PvControle => ("CNPVC", "CNPVCD"),
            _ => ("CNBAGC", "CNBAGCD"),
        };
        var bib = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT CNCIV, CNNOM, CNEMAI, {defaut} FROM {bib}.PRT051F
            WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND COALESCE(CNTAG, ' ') <> '*' AND COALESCE(CNEMAI, ' ') <> ' ' AND ({envoi} = 'O' OR {defaut} = 'O')
            ORDER BY CASE WHEN {defaut} = 'O' THEN 0 ELSE 1 END, CNLIG
            """;
        cmd.Parameters.Valeur(societe);
        cmd.Parameters.Valeur(compteGeneral);
        cmd.Parameters.Valeur(compteClient);
        var contacts = new List<ContactEnvoi>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            contacts.Add(new ContactEnvoi($"{rd.Texte(0)} {rd.Texte(1)}".Trim(), rd.Texte(2), rd.Texte(3) == "O"));
        return contacts;
    }
}
