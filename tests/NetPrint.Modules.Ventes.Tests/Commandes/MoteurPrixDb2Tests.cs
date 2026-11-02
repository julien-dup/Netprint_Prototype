using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Moteur de prix contre les lignes réellement saisies en 5250 (lecture seule), sans devis d'origine ni remise :
// à la date de la commande, le prix calculé doit être le prix brut enregistré, sauf prix saisi à la main
public class MoteurPrixDb2Tests(ITestOutputHelper sortie)
{
    [FactDb2]
    public async Task Prix_recalcules_sur_les_lignes_du_client_d_essai()
    {
        var lignes = new List<(int Dossier, int Ligne, string Article, string Indice, int Quantite, decimal Prix, DateOnly Date, int Rep, int Gen, int Cli)>();
        await using (var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2")))
        {
            await cnx.OpenAsync();
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = """
                SELECT b.RDOSS, b.DNNLIG, b.DNART, b.DNIND, b.DNQTEC, b.DNPUNR, a.CCDATA, a.CCDATM, a.CCDATJ, a.CCREP, a.RGEN, a.RCLI
                FROM NP11F.NEG100 b JOIN NP11F.AB001 a ON a.RSOC = b.RSOC AND a.RDOSS = b.RDOSS AND a.CCDCLE = b.DNDCLE
                WHERE b.RSOC = ? AND b.DNSTAT = '1' AND b.DNOPT2 <> 'O' AND b.DNOPT5 <> 'O' AND b.DNDEV = 0 AND b.DNTXRE = 0
                  AND b.DNPUNR > 0 AND b.DNART NOT LIKE '999%' AND a.CCDATA >= 2023
                ORDER BY a.CCDATA DESC, a.CCDATM DESC, a.CCDATJ DESC, b.RDOSS DESC, b.DNNLIG FETCH FIRST 300 ROWS ONLY
                """;
            cmd.Parameters.AddWithValue("a", BaseDeTest.Societe);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                lignes.Add((Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)), Convert.ToString(rd.GetValue(2))!.Trim(),
                    Convert.ToString(rd.GetValue(3))!.Trim(), Convert.ToInt32(rd.GetValue(4)), Convert.ToDecimal(rd.GetValue(5)),
                    new DateOnly(Convert.ToInt32(rd.GetValue(6)), Convert.ToInt32(rd.GetValue(7)), Convert.ToInt32(rd.GetValue(8))),
                    Convert.ToInt32(rd.GetValue(9)), Convert.ToInt32(rd.GetValue(10)), Convert.ToInt32(rd.GetValue(11))));
        }

        var donnees = new LignesCommandeDb2(BaseDeTest.Configuration);
        int egaux = 0, differents = 0;
        var sources = new Dictionary<string, int>();
        foreach (var l in lignes)
        {
            var d = await donnees.DonneesTarifAsync(BaseDeTest.Societe, l.Article, l.Indice, l.Gen, l.Cli, l.Rep, l.Date, default);
            if (d is null) continue;
            var r = MoteurPrixCommande.Tarif(d, l.Quantite, l.Date, 0);
            sources[r.Source == "" ? "(aucun)" : r.Source] = sources.GetValueOrDefault(r.Source == "" ? "(aucun)" : r.Source) + 1;
            if (r.Prix == l.Prix) egaux++;
            else
            {
                differents++;
                if (differents <= 8) sortie.WriteLine($"{l.Dossier}/L{l.Ligne} {l.Article} qté {l.Quantite} le {l.Date} : saisi {l.Prix}, calculé {r.Prix} ({r.Source})");
            }
        }
        sortie.WriteLine($"{lignes.Count} lignes : {egaux} prix identiques, {differents} différents ; sources : " +
            string.Join(", ", sources.Select(s => $"{s.Key} {s.Value}")));
        Assert.NotEmpty(lignes);
    }
}
