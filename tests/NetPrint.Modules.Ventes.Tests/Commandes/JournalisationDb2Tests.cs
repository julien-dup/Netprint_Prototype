using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Les écritures de la saisie de commande sont transactionnelles : toutes les tables écrites doivent être
// journalisées dans NP11F (sinon SQL7008 à la première écriture sous transaction).
// EVE200 (audit) n'en fait pas partie : non journalisée, elle est écrite après la validation.
public class JournalisationDb2Tests(ITestOutputHelper sortie)
{
    public static readonly string[] TablesEcrites =
    [
        "AB001", "AB001FA", "AB0016", "NEG100", "NEG100E", "NEG100T", "NEG100S", "EXP020", "EVE100", "EVE105", "SYN100",
        "DOS040", "ABREPE", "ABREPD", "ABCOUTE", "ABCOUTD", "ABCOUTB", "AFC020", "AB0020", "ABDIRL", "ABDIRP", "DIR100",
    ];

    [FactDb2]
    public async Task Les_tables_ecrites_par_la_saisie_sont_journalisees()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        await using var cmd = new OdbcCommand(
            "SELECT TRIM(OBJECT_NAME) FROM QSYS2.JOURNALED_OBJECTS WHERE OBJECT_LIBRARY = 'NP11F' AND OBJECT_TYPE = '*FILE'", cnx);
        var journalisees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var lecteur = await cmd.ExecuteReaderAsync())
            while (await lecteur.ReadAsync()) journalisees.Add(lecteur.GetString(0));
        var manquantes = TablesEcrites.Where(t => !journalisees.Contains(t)).ToList();
        sortie.WriteLine(manquantes.Count == 0 ? "Toutes journalisées" : "Non journalisées : " + string.Join(", ", manquantes));
        Assert.Empty(manquantes);
    }
}
