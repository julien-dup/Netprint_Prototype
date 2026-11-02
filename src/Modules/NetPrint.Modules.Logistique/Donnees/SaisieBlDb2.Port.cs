using System.Data.Odbc;
using NetPrint.Modules.Logistique.Services;

namespace NetPrint.Modules.Logistique.Donnees;

// Port d'un BL (cahier des charges § 7, SPCPOR d'EXP010) : tranches de poids (B300F), tarif du transporteur (B400F, dépôt 1,
// département du destinataire), frais de timbre (B200F) ; imputation au prix de revient de chaque dossier (A20005)
internal sealed partial class SaisieBlDb2
{
    // Port d'un poids (plafonné à 99 999 kg comme le RPG) ; null si incalculable (pas de transporteur, de tranche ou de tarif)
    public async Task<decimal?> PortAsync(int societe, int transporteur, string departement, int bareme, int poids, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return (await PortAsync(cnx, null, societe, transporteur, departement, bareme, poids, ct)).Port;
    }

    private async Task<(decimal? Port, string Transporteur)> PortAsync(OdbcConnection cnx, OdbcTransaction? tx, int societe, int transporteur,
        string departement, int bareme, int poids, CancellationToken ct)
    {
        if (transporteur <= 0) return (null, "");
        poids = Math.Min(poids, 99_999);
        decimal? timbre = null;
        var nom = "";
        await using (var rd = await Lecteur.ExecuterAsync(cnx, tx, $"SELECT MFRIMB, MNOM FROM {Bib}.B200F WHERE RSOC = ? AND RTRANS = ?", ct,
                         societe, transporteur))
            if (await rd.ReadAsync(ct)) (timbre, nom) = (rd.Nombre(0), rd.Texte(1));
        if (timbre is null) return (null, "");
        var tranches = new List<int>();
        var colonnes = string.Join(", ", Enumerable.Range(1, 20).Select(i => $"TPCH{i}"));
        await using (var rd = await Lecteur.ExecuterAsync(cnx, tx, $"SELECT {colonnes} FROM {Bib}.B300F WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct)) tranches.AddRange(Enumerable.Range(0, 20).Select(rd.Entier));
        var tranche = ReglesSaisieBl.Tranche(poids, tranches);
        if (tranche == 0) return (null, nom);
        await using (var rd = await Lecteur.ExecuterAsync(cnx, tx, $"""
            SELECT BTBAR1, BTBAR2, BTBAR3, BTUNPX FROM {Bib}.B400F WHERE RSOC = ? AND RDEP = 1 AND RTRANS = ? AND RDPT = ? AND RTRANC = ?
            """, ct, societe, transporteur, departement, tranche))
            if (await rd.ReadAsync(ct))
                return (ReglesSaisieBl.Port(poids, new TarifPort(rd.Nombre(0), rd.Nombre(1), rd.Nombre(2), rd.Texte(3)), bareme, timbre.Value), nom);
        return (null, nom);
    }

    // Poids des autres éléments du BL (pour le port de tout le BL affiché au contrôle d'un ajout ou d'une modification)
    public async Task<int> PoidsAutresElementsAsync(int societe, int numeroBl, int element, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT COALESCE(SUM(HPDST), 0) FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND HNUM <> ?", ct,
            societe, numeroBl, element);
        return await rd.ReadAsync(ct) ? rd.Entier(0) : 0;
    }

    // Port franco calculé (AA0007.SOP09 = 2) : coût de tout le BL (poids total EXP009, plafonné) réparti au poids entre ses
    // dossiers (un dossier présent sur plusieurs éléments : une seule imputation), imputé dans A20005 (imputation du dossier
    // pour ce BL supprimée puis réécrite). Autre port : imputations du BL retirées. Appelé après chaque validation et après
    // la suppression d'un élément quand le BL reste.
    private async Task ImputerPortAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, bool calculPort, CancellationToken ct)
    {
        if (!calculPort) return;
        string typePort, departement;
        int transporteur, bareme, poidsTotal;
        DateOnly date;
        await using (var rd = await Lire(cnx, tx, "SELECT HTYPP, HTRAN, HDPT, HBART, HPDSTT, HDEXAA, HDEXMM, HDEXJJ FROM {b}.EXP009 WHERE RSOC = ? AND HNOBL = ?",
                         ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) return;
            (typePort, transporteur, departement, bareme, poidsTotal) = (rd.Texte(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Entier(4));
            date = new DateOnly(rd.Entier(5), Math.Max(1, rd.Entier(6)), Math.Max(1, rd.Entier(7)));
        }
        var dossiers = new List<(int Dossier, string Cle, int Poids)>();
        await using (var rd = await Lire(cnx, tx, """
            SELECT HNODOS, HCLEF, SUM(HPDST) FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND HNODOS <> 0
            GROUP BY HNODOS, HCLEF ORDER BY MIN(HNUM)
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct)) dossiers.Add((rd.Entier(0), rd.Texte(1), rd.Entier(2)));
        foreach (var (dossier, cle, _) in dossiers)
            await Executer(cnx, tx, "DELETE FROM {b}.A20005 WHERE RSOC = ? AND RDOSS = ? AND S5DCLE = ? AND S5NOBL = ?", ct, societe, dossier, cle, numero);
        if (typePort != "F" || dossiers.Count == 0) return;

        var (port, nom) = await PortAsync(cnx, tx, societe, transporteur, departement, bareme, poidsTotal, ct);
        if (port is not > 0) return;
        // N° de ligne SLIG : dernier n° de la société + 1, comme EXP010 (SETLL / READPE sur A20005F2), sans verrou de table : un
        // LOCK TABLE attendrait tout travail qui a A20005 ouvert en mise à jour (programmes RPG compris)
        var parts = ReglesSaisieBl.RepartirAuPoids(port.Value, [.. dossiers.Select(e => e.Poids)]);
        for (var i = 0; i < dossiers.Count; i++)
        {
            var (dossier, cle, _) = dossiers[i];
            await Executer(cnx, tx, """
                INSERT INTO {b}.A20005 (RSOC, RDOSS, S5DCLE, S5REF, S5AA, S5MM, S5JJ, S5NOM, S5MTN, SLIG, S5NOBL, S5PROV)
                SELECT CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(5, 0)), CAST(? AS CHAR(1)), CAST(? AS CHAR(6)), CAST(? AS NUMERIC(4, 0)),
                       CAST(? AS NUMERIC(2, 0)), CAST(? AS NUMERIC(2, 0)), CAST(? AS CHAR(40)), CAST(? AS NUMERIC(9, 2)),
                       COALESCE(MAX(SLIG), 0) + 1, CAST(? AS NUMERIC(6, 0)), 'E'
                FROM {b}.A20005 WHERE RSOC = ?
                """, ct, societe, dossier, cle, $"{transporteur:00}".PadRight(6, '0'), date.Year, date.Month, date.Day,
                $"{nom,-34}{numero:000000}", parts[i], numero, societe);
        }
    }
}
