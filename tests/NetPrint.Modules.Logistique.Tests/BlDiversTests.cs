using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// BL divers (cahier des charges § 11) : contrôles sans base, puis création, modification et suppression sur le client d'essai
[Collection(EcrituresNp11f.Collection)]
public class BlDiversTests(ITestOutputHelper sortie)
{
    private static SaisieBlDivers Saisie() => new()
    {
        Tiers = new TiersBlDivers(411100, 376, 0, "", 0), Adresse = new AdresseBl(0, "", "CLARINS", "12 AV", "", "95000", "PONTOISE", "95", "FR"),
        TypeLivraison = "M", Transporteur = 5, TypePort = "F", Quantite = 10, Designations = ["Échantillons"],
    };

    [Fact]
    public void Controles()
    {
        Assert.Empty(BlDiversService.Controler(Saisie()));
        Assert.Equal(2, new TiersBlDivers(411100, 376, 12345, "A", 0).Renseignes);
        var s = Saisie();
        (s.Quantite, s.Designations) = (0, ["", ""]);
        Assert.Contains(BlDiversService.Controler(s), e => e.Contains("Indiquez ce qui est livré"));
        s.Designations = [.. Enumerable.Repeat("x", 31)];
        Assert.Contains(BlDiversService.Controler(s), e => e.Contains("30 lignes"));
        s = Saisie();
        (s.TypePort, s.Transporteur, s.TypeLivraison) = ("E", 0, "");
        Assert.Empty(BlDiversService.Controler(s));
        s = Saisie();
        (s.Quantite, s.Designations, s.Palettes) = (0, [], 2);
        Assert.Empty(BlDiversService.Controler(s));                                   // palettes seules : livré
        s.Designations = [new string('x', 41)];
        Assert.Contains(BlDiversService.Controler(s), e => e.Contains("40 caractères au plus (41 saisis)"));
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    [FactDb2]
    public async Task Un_seul_destinataire()
    {
        var service = new BlDiversService(new SaisieBlDb2(BaseDeTest.Configuration), new Utilisateur());
        await Assert.ThrowsAsync<RegleMetierException>(() => service.NouveauAsync(new TiersBlDivers(0, 0, 0, "", 0)));
        var s = await service.NouveauAsync(new TiersBlDivers(BaseDeTest.CompteGeneral, BaseDeTest.CompteClient, 0, "", 0));
        sortie.WriteLine($"{s.NomTiers} : {s.Adresses.Count} adresse(s), enseigne {s.Enseigne}");
        Assert.NotEmpty(s.Adresses);
    }

    [FactDb2Ecriture]
    public async Task Creation_modification_suppression()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var donnees = new SaisieBlDb2(BaseDeTest.Configuration);
        var service = new BlDiversService(donnees, new Utilisateur());
        var s = await service.NouveauAsync(new TiersBlDivers(BaseDeTest.CompteGeneral, BaseDeTest.CompteClient, 0, "", 0));
        (s.Quantite, s.Colis, s.Poids, s.Palettes) = (25, 2, 14, 1);
        s.Designations = ["TEST NETPRINT SAAS - A SUPPRIMER", "Échantillons", "Notice", "Présentoir"];
        if (s.Transporteur == 0) (s.TypePort, s.TypeLivraison) = ("E", "");
        var numero = 0;
        try
        {
            numero = await service.ValiderAsync(s);
            sortie.WriteLine($"BL divers {numero}");
            Assert.Equal(1, await Compter(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNOBL = ? AND HETABL = 'D' AND HFACTU = 'N'", numero));
            Assert.Equal(2, await Compter(cnx, "SELECT COUNT(*) FROM NP11F.EXP015 WHERE RSOC = 8 AND DNBL = ? AND DART = ' '", numero));   // 4 désignations
            Assert.Equal(2, await Compter(cnx, "SELECT HNCOLT FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", numero));

            var m = await service.OuvrirAsync(numero);
            Assert.Equal(["TEST NETPRINT SAAS - A SUPPRIMER", "Échantillons", "Notice", "Présentoir"], m.Designations);
            (m.Colis, m.Designations) = (3, ["TEST NETPRINT SAAS - A SUPPRIMER"]);
            Assert.Equal(numero, await service.ValiderAsync(m));
            Assert.Equal(3, await Compter(cnx, "SELECT HNCOLT FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", numero));
            Assert.Equal(1, await Compter(cnx, "SELECT COUNT(*) FROM NP11F.EXP015 WHERE RSOC = 8 AND DNBL = ?", numero));

            Assert.True(await new SaisieBlService(donnees, new Utilisateur()).SupprimerAsync(numero, 1));
            Assert.Equal(0, await Compter(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", numero));
        }
        finally
        {
            if (numero > 0)
                foreach (var (table, colonne) in new[] { ("EXP015", "DNBL"), ("EXP012", "HNOBL"), ("EXP010", "HNOBL"), ("EXP009", "HNOBL"), ("EVE200", "AMBL") })
                {
                    await using var cmd = cnx.CreateCommand();
                    cmd.CommandText = $"DELETE FROM NP11F.{table} WHERE RSOC = 8 AND {colonne} = ?";
                    cmd.Parameters.AddWithValue("", numero);
                    await cmd.ExecuteNonQueryAsync();
                }
        }
    }

    private static async Task<int> Compter(OdbcConnection cnx, string sql, int numero)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("", numero);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? 0 : Convert.ToInt32(r);
    }
}
