using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;
using Xunit.Abstractions;

namespace NetPrint.Modules.Facturation.Tests;

public class EnvoiFacturesTests
{
    private static FactureAEnvoyer F(int n, bool avoir = false) => new(n, new DateOnly(2026, 11, 1), avoir, "", 0, "");

    [Fact]
    public void Objet_du_message()
    {
        Assert.Equal("Facture n° 12", EnvoiFacturesService.Objet("", [F(12)], "DIOR"));
        Assert.Equal("Avoir n° 13", EnvoiFacturesService.Objet(" ", [F(13, avoir: true)], "DIOR"));
        Assert.Equal("Factures n° 12, 13", EnvoiFacturesService.Objet("", [F(12), F(13)], "DIOR"));
        // Variables du paramétrage PRT057F.PFOBJM (WinDev) : &NOFAC, &NOM, &NBFAC ; sauts de ligne retirés
        Assert.Equal("DIOR - 2 factures : 12, 13", EnvoiFacturesService.Objet("&NOM - &NBFAC factures :\r\n&NOFAC", [F(12), F(13)], "DIOR"));
    }

    [Fact]
    public void Canaux_pris_en_charge()
    {
        static ParametresEnvoiClient P(string canal, string format) => new(canal, format, [], "", false, false, false);
        Assert.True(EnvoiFacturesService.PrisEnCharge(null));   // pas de paramétrage : mail, PDF
        Assert.True(EnvoiFacturesService.PrisEnCharge(P("N", "PDF1")));
        Assert.True(EnvoiFacturesService.PrisEnCharge(P("", "")));
        Assert.False(EnvoiFacturesService.PrisEnCharge(P("N", "FACX")));
        Assert.False(EnvoiFacturesService.PrisEnCharge(P("O", "PDF")));   // FTP
        Assert.False(EnvoiFacturesService.PrisEnCharge(P("X", "FACX")));  // plateforme
        Assert.Equal(["a@b.fr", "c@d.fr"], EnvoiFacturesService.Adresses(["a@b.fr; c@d.fr", "A@B.FR", "pas une adresse"]));
    }
}

// Lecture et marque d'envoi sur la base de test (société 29)
[Collection(EcrituresNp11f.Nom)]
public class EnvoiFacturesBaseTests(ITestOutputHelper sortie)
{
    private static EnvoiFacturesDb2 Donnees => new(BaseDeTest.Configuration);

    [FactDb2]
    public async Task Parametres_et_factures_d_un_client()
    {
        // Client de la société 8 avec paramétrage d'envoi (PRT057F : mail, PDF1, envoi automatique, destinataire)
        var (nom, p) = await Donnees.ClientAsync(8, 411018, 123, default);
        Assert.NotNull(nom);
        Assert.Equal(("N", "PDF1", true), (p!.Canal, p.Format, p.Automatique));
        Assert.NotEmpty(p.Destinataires);
        Assert.True(EnvoiFacturesService.PrisEnCharge(p));

        var (factures, _) = await Donnees.FacturesAsync(29, 411029, 1, null, null, avecDejaEnvoyees: true, 50, default);
        sortie.WriteLine($"411029 1 : {factures.Count} factures éditées (déjà envoyées comprises)");
        Assert.NotEmpty(factures);
        // Relecture par clé (n°, date) : exactement les factures demandées ; une clé d'un autre client n'est pas rendue
        var cles = factures.Take(2).Select(f => (f.Numero, f.Date)).ToList();
        var (relues, _) = await Donnees.FacturesAsync(29, 411029, 1, null, null, true, 10, default, cles: [.. cles, (999999, new DateOnly(2024, 1, 1))]);
        Assert.Equal(cles.Order(), relues.Select(f => (f.Numero, f.Date)).Order());
    }

    [FactDb2Ecriture]
    public async Task Marque_d_envoi_puis_remise_a_l_identique()
    {
        var (factures, _) = await Donnees.FacturesAsync(29, 411029, 1, null, null, avecDejaEnvoyees: true, 1, default);
        var f = Assert.Single(factures);
        await using var cnx = BaseDb2.Connexion(BaseDeTest.Configuration);
        await cnx.OpenAsync();
        var avant = await LireAsync(cnx, f);
        try
        {
            var quand = new DateTime(2026, 11, 1, 14, 5, 9);
            await Donnees.MarquerAsync(29, [(f.Numero, f.Date)], manuel: true, "M", "PDF", quand, default);
            Assert.Equal("O|20261101|140509|M|M|PDF", await LireAsync(cnx, f));
            var (relue, _) = await Donnees.FacturesAsync(29, 411029, 1, f.Date, f.Date, avecDejaEnvoyees: false, 50, default);
            Assert.DoesNotContain(relue, x => x.Numero == f.Numero);   // envoyée : plus proposée
        }
        finally
        {
            var v = avant.Split('|');
            await FacturationAutoBaseTests.ExecuterAsync(cnx, """
                UPDATE {b}.FAC080 SET FDTAGE = ?, FDDATEE = ?, FDHEURE = ?, FDMANAU = ?, FDMETHE = ?, FDTYPEE = ?
                WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                """, v[0] == "" ? " " : v[0], int.Parse(v[1]), int.Parse(v[2]), v[3] == "" ? " " : v[3], v[4] == "" ? " " : v[4], v[5] == "" ? " " : v[5],
                29, f.Numero, f.Date.Year, f.Date.Month, f.Date.Day);
        }
        Assert.Equal(avant, await LireAsync(cnx, f));
    }

    private static async Task<string> LireAsync(System.Data.Odbc.OdbcConnection cnx, FactureAEnvoyer f)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT FDTAGE, FDDATEE, FDHEURE, FDMANAU, FDMETHE, FDTYPEE FROM {BaseDb2.Bibliotheque(BaseDeTest.Configuration)}.FAC080 "
            + "WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?";
        foreach (var v in new object[] { 29, f.Numero, f.Date.Year, f.Date.Month, f.Date.Day }) BaseDb2.Valeur(cmd.Parameters, v);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return string.Join("|", Enumerable.Range(0, 6).Select(i => $"{rd.GetValue(i)}".Trim()));
    }
}
