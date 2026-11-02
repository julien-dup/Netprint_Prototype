using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using Xunit.Abstractions;

namespace NetPrint.Modules.Facturation.Tests;

// Calcul d'une facture (reprise de FAC201 / FAC199) : règles sans base, puis comparaison avec les factures déjà éditées en
// définitif par l'AS400 (totaux posés dans FAC080)
public class CalculFactureTests(ITestOutputHelper sortie)
{
    private static readonly DateOnly Jour = new(2026, 3, 15);

    private static ConditionsFacture C(decimal escompte = 0, decimal acompte = 0, bool avoir = false, int le = 0, int jours = 0, int arrete = 0) =>
        new(avoir, Jour, escompte, acompte, le, jours, arrete, null);

    [Fact]
    public void Facture_a_un_taux_comme_le_rpg()
    {
        // Facture 50001 du 01/05/2026 (Dior) : 415 HT, TVA 20 % = 83, TTC 498
        var t = CalculFacture.Calculer([new("V", 415m, 20m)], C());
        Assert.Equal((415m, 83m, 498m, 498m), (t.Ht, t.TotalTva, t.Ttc, t.NetAPayer));
        Assert.Equal([new TvaParTaux(20m, 415m, 83m)], t.Tva);

        // Remise, port taxable et exonéré, escompte 2 %, acompte
        t = CalculFacture.Calculer([new("V", 1000m, 20m), new("R", 100m, 20m), new("P", 25m, 20m), new("E", 10m, 0m), new("T", 0m, 0m)],
            C(escompte: 2m, acompte: 50m));
        Assert.Equal(900m, t.Ht);
        Assert.Equal(18m, t.Escompte);
        Assert.Equal(Math.Round(882m * 0.2m, 2) + 5m, t.TotalTva);               // TVA marchandises (HT − escompte) + TVA du port
        Assert.Equal(900m - 18m + 25m + 10m + t.TotalTva, t.Ttc);
        Assert.Equal(t.Ttc - 50m, t.NetAPayer);
    }

    [Fact]
    public void TVA_ventilee_par_taux()
    {
        var t = CalculFacture.Calculer([new("V", 100m, 20m), new("V", 200m, 5.5m), new("V", 50m, 0m)], C());
        Assert.Equal([(0m, 50m, 0m), (5.5m, 200m, 11m), (20m, 100m, 20m)], t.Tva.Select(x => (x.Taux, x.Base, x.Montant)));
        Assert.Equal(31m, t.TotalTva);
    }

    [Fact]
    public void Echeances_de_getechean()
    {
        DateOnly E(int le = 0, int jours = 0, int arrete = 0, bool avoir = false) => CalculFacture.Echeance(C(le: le, jours: jours, arrete: arrete, avoir: avoir));
        Assert.Equal(new DateOnly(2026, 4, 15), E(jours: 30));
        Assert.Equal(new DateOnly(2026, 5, 15), E(jours: 60));
        Assert.Equal(new DateOnly(2026, 4, 30), E(jours: 45));                    // 1 mois et 15 jours
        Assert.Equal(new DateOnly(2026, 4, 4), E(jours: 20));
        Assert.Equal(Jour, E(jours: 75));                                         // délai non prévu : aucun
        Assert.Equal(new DateOnly(2026, 4, 30), E(jours: 30, le: 31));            // 30 jours fin de mois
        Assert.Equal(new DateOnly(2026, 5, 10), E(jours: 30, le: 10));            // 30 jours le 10 (mois suivant)
        Assert.Equal(new DateOnly(2026, 5, 15), E(jours: 30, arrete: 10));        // facture après l'arrêté du 10 : un mois de plus
        Assert.Equal(Jour, E(jours: 30, avoir: true));
    }

    // Les factures éditées en définitif de 2025-2026 : le calcul natif doit retrouver HT, port, escompte et TVA de FAC080
    [FactDb2]
    public async Task Memes_totaux_que_les_factures_editees_par_l_as400()
    {
        var factures = new List<(int Societe, int Numero, DateOnly Date)>();
        await using (var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2")))
        {
            await cnx.OpenAsync();
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = """
                SELECT RSOC, RNFAC, FDANNE, FDMOIS, FDJOUR FROM NP11F.FAC080
                WHERE RSOC IN (8, 29) AND FDEDIT = 'E' AND FDANNE >= 2022 ORDER BY RSOC, FDANNE DESC, FDMOIS DESC, RNFAC DESC FETCH FIRST 300 ROWS ONLY
                """;
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                factures.Add((Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)),
                    new DateOnly(Convert.ToInt32(rd.GetValue(2)), Convert.ToInt32(rd.GetValue(3)), Convert.ToInt32(rd.GetValue(4)))));
        }
        var donnees = new CalculFacturesDb2(BaseDeTest.Configuration);
        var ecarts = new List<string>();
        var multiTaux = 0;
        var sansLignes = 0;
        foreach (var (societe, numero, date) in factures)
        {
            var f = (await donnees.LireAsync(societe, numero, date, default))!;
            // Données incohérentes de la base de test : date de l'en-tête changée sans celle des lignes (avoir 20501 : en-tête au
            // 14/04/2026, lignes au 14/02)
            if (f.Lignes.Count == 0 && f.Enregistre.Ht != 0) { sansLignes++; continue; }
            var t = CalculFacture.Calculer(f.Lignes, f.Conditions);
            if (t.Tva.Count(x => x.Base != 0) > 1) { multiTaux++; continue; }   // le RPG n'appliquait qu'un taux : écart voulu
            var e = f.Enregistre;
            if ((t.Ht, t.PortTaxable, t.PortExonere, t.Escompte, t.TotalTva) != (e.Ht, e.PortTaxable, e.PortExonere, e.Escompte, e.Tva))
                ecarts.Add($"soc {societe} facture {numero} du {date:dd/MM/yyyy} : calcul {t.Ht}/{t.PortTaxable}/{t.PortExonere}/{t.Escompte}/{t.TotalTva}" +
                           $", AS400 {e.Ht}/{e.PortTaxable}/{e.PortExonere}/{e.Escompte}/{e.Tva}");
        }
        sortie.WriteLine($"{factures.Count} factures comparées, {multiTaux} à plusieurs taux et {sansLignes} sans lignes écartées, {ecarts.Count} écart(s)");
        foreach (var e in ecarts.Take(30)) sortie.WriteLine(e);
        Assert.True(factures.Count > 50);
        Assert.Empty(ecarts);
    }
}
