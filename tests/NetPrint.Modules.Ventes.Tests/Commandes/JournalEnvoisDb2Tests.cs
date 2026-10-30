using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Noyau.Messagerie;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Journal des envois (ENV100) sur NP11F de test. Table absente (script db/db2/20261030-2230 pas encore appliqué) :
// l'écriture est ignorée et la lecture vide. Table présente : écriture, lecture, PDF, puis nettoyage.
public class JournalEnvoisDb2Tests(ITestOutputHelper sortie)
{
    [FactDb2Ecriture]
    public async Task Ecriture_et_lecture_du_journal()
    {
        var journal = new JournalEnvoisDb2(BaseDeTest.Configuration);
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        await using (var cmd = new OdbcCommand("SELECT 1 FROM QSYS2.SYSTABLES WHERE TABLE_SCHEMA = 'NP11F' AND TABLE_NAME = 'ENV100'", cnx))
            if (await cmd.ExecuteScalarAsync() is null)
            {
                await journal.EcrireAsync([Ligne()]);   // ne doit pas échouer
                Assert.Empty(await journal.LireAsync(BaseDeTest.Societe, TypesEnvoi.Arc, 99999, "Z"));
                sortie.WriteLine("ENV100 absente : journal ignoré (appliquer db/db2/20261030-2230_ENV100_journal_envois.sql)");
                return;
            }

        try
        {
            await journal.EcrireAsync([Ligne()]);
            var lu = Assert.Single(await journal.LireAsync(BaseDeTest.Societe, TypesEnvoi.Arc, 99999, "Z"));
            Assert.Equal(("E", "a@exemple.fr; b@exemple.fr", "TEST.pdf", true), (lu.Statut, lu.Destinataires, lu.NomPdf, lu.AvecPdf));
            var pdf = await journal.LirePdfAsync(BaseDeTest.Societe, lu.Id, TypesEnvoi.Arc, 99999, "Z");
            Assert.Equal(new byte[] { 37, 80, 68, 70 }, pdf!.Value.Contenu);
            Assert.Null(await journal.LirePdfAsync(BaseDeTest.Societe, lu.Id, TypesEnvoi.Arc, 99998, "Z"));   // autre dossier
        }
        finally
        {
            await using var suppr = new OdbcCommand("DELETE FROM NP11F.ENV100 WHERE RSOC = ? AND RDOSS = 99999 AND ENDCLE = 'Z'", cnx);
            suppr.Parameters.AddWithValue("s", BaseDeTest.Societe);
            await suppr.ExecuteNonQueryAsync();
        }
    }

    private static EnvoiDocument Ligne() => new(BaseDeTest.Societe, Guid.NewGuid().ToString("N"), "TEST", TypesEnvoi.Arc, 99999, "Z",
        BaseDeTest.CompteGeneral, BaseDeTest.CompteClient, CanauxEnvoi.Smtp, StatutsEnvoi.Envoye, "test@exemple.fr",
        ["a@exemple.fr", "b@exemple.fr"], [], "", "TEST NETPRINT SAAS", "", "TEST.pdf", [37, 80, 68, 70]);
}
