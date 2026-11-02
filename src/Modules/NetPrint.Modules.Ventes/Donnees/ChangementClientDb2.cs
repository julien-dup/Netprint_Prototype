using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Changement de client, date ou enseigne d'une commande (DOS505 F17)
// =====================================================================
internal sealed class ChangementClientDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Conditions SQL « pas de BL (EXP010) ni de facture (FAC070) de la commande pour son client » (contrôles 69 et 68 de DOS505)
    private static string SansBl(string b) =>
        $"NOT EXISTS (SELECT 1 FROM {b}.EXP010 x WHERE x.RSOC = ? AND x.HGENE = ? AND x.HCLIE = ? AND x.HNODOS = ? AND x.HCLEF = ?)";
    private static string SansFacture(string b) =>
        $"NOT EXISTS (SELECT 1 FROM {b}.FAC070 f WHERE f.RSOC = ? AND f.HFGEN = ? AND f.HFCLI = ? AND f.RDOSS = ? AND f.RDCLE = ?)";

    // BL et factures de la commande pour un client ; références de la commande qui dépendent du client : n° d'adresse
    // (lignes, facture automatique, ARC) et client facturé (en-tête, lignes)
    public async Task<(bool Bl, bool Facture, int NumerosAdresse, int ClientsFactures)> PiecesAsync(int societe, CleCommande cle,
        int compteGeneral, int compteClient, CancellationToken ct)
    {
        var b = Bib;
        object?[] k = [societe, compteGeneral, compteClient, cle.Numero, cle.Cle];
        object?[] c = [societe, cle.Numero, cle.Cle];
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT
              (SELECT COUNT(*) FROM {b}.EXP010 x WHERE x.RSOC = ? AND x.HGENE = ? AND x.HCLIE = ? AND x.HNODOS = ? AND x.HCLEF = ?),
              (SELECT COUNT(*) FROM {b}.FAC070 f WHERE f.RSOC = ? AND f.HFGEN = ? AND f.HFCLI = ? AND f.RDOSS = ? AND f.RDCLE = ?),
              (SELECT COUNT(*) FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNLIGA <> 0)
              + (SELECT COUNT(*) FROM {b}.AB001FA WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND FANAL <> 0)
              + (SELECT COUNT(*) FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCNAEA <> 0),
              (SELECT COUNT(*) FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNCLIF <> 0)
              + (SELECT COUNT(*) FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCLIF <> 0)
            FROM SYSIBM.SYSDUMMY1
            """, ct, [.. k, .. k, .. c, .. c, .. c, .. c, .. c]);
        await rd.ReadAsync(ct);
        return (rd.Entier(0) > 0, rd.Entier(1) > 0, rd.Entier(2), rd.Entier(3));
    }

    // Une instruction (atomique) : refusée si le client, l'enseigne ou la date ont changé depuis la lecture, ou si un BL ou une
    // facture est apparu entre-temps pour l'ancien client (client changé). Audit EVE200 « MO » ensuite (table non journalisée).
    public async Task ChangerAsync(int societe, string profil, CleCommande cle, EnteteSaisie avant, int compteGeneral, int compteClient,
        string enseigne, DateOnly date, CancellationToken ct)
    {
        var b = Bib;
        var clientChange = compteGeneral != avant.CompteGeneral || compteClient != avant.CompteClient;
        object?[] k = [societe, avant.CompteGeneral, avant.CompteClient, cle.Numero, cle.Cle];
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using (var cmd = Commande(cnx, $"""
            UPDATE {b}.AB001 SET RGEN = ?, RCLI = ?, CCENSE = ?, CCDATA = ?, CCDATM = ?, CCDATJ = ?
            WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC IN ('3', '4')
              AND RGEN = ? AND RCLI = ? AND CCENSE = ? AND CCDATA = ? AND CCDATM = ? AND CCDATJ = ?
              {(clientChange ? $"AND {SansBl(b)} AND {SansFacture(b)}" : "")}
            """, [compteGeneral, compteClient, enseigne, date.Year, date.Month, date.Day, societe, cle.Numero, cle.Cle,
            avant.CompteGeneral, avant.CompteClient, avant.Enseigne, avant.DateDossier.Year, avant.DateDossier.Month, avant.DateDossier.Day,
            .. clientChange ? [.. k, .. k] : Array.Empty<object?>()]))
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La commande a été modifiée entre-temps (en-tête, BL ou facture) : rechargez-la.");
        await using (var cmd = Commande(cnx, $"""
            INSERT INTO {b}.EVE200 (AMLIG, RSOC, AMTYPE, AMDATE, AMPROF, AMDOSS, AMDCLE)
            SELECT COALESCE(MAX(AMLIG), 0) + 1, CAST(? AS NUMERIC(4, 0)), CAST('MO' AS CHAR(2)), CURRENT TIMESTAMP,
                   CAST(? AS CHAR(10)), CAST(? AS NUMERIC(5, 0)), CAST(? AS CHAR(1)) FROM {b}.EVE200
            """, [societe, profil.ToUpperInvariant(), cle.Numero, cle.Cle]))
        {
            try { await cmd.ExecuteNonQueryAsync(CancellationToken.None); }
            catch (OdbcException) { /* audit non bloquant, comme la modification d'en-tête */ }
        }
    }

    private static OdbcCommand Commande(OdbcConnection cnx, string sql, object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, sql, valeurs).ExecuteReaderAsync(ct);
}
