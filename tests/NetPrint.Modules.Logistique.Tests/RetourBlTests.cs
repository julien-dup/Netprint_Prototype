using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Retour de marchandises : règles sans base, lecture, puis retour et suppression de bout en bout sur NP11F de test
[Collection(EcrituresNp11f.Collection)]
public class RetourBlTests(ITestOutputHelper sortie)
{
    private static readonly DateOnly Jour = new(2024, 10, 15);

    private static OrigineRetour Origine() => new(1000, new DateOnly(2024, 10, 1), "", false, 411100, 20, "DIOR", 5,
    [
        new ElementOrigine(1, "C", 69320, "9", "4", "PF", false, false, 0, 0, 0, 0, 0, 2,
        [
            new LigneOrigine(1, 2, "2", "PQA002", "", "ETUI", "", "", 0, "C",
                [new LotOrigine("658282", "", 5000, 0, true, "P1", 9m, "", "", [new PaletteOrigine("SSCC1", 3000, 65828, "2", false), new PaletteOrigine("SSCC2", 2000, 65828, "2", true)])]),
            new LigneOrigine(2, 3, "2", "PQA002", "", "ETUI", "", "", 0, "C", [new LotOrigine("693201", "PREPA", 2000, 500, true, "P1", 9m, "", "", [])]),
        ]),
        new ElementOrigine(2, "C", 70000, "9", "1", "FAB", true, false, 1000, 200, 1, 0, 0, 1, []),
        new ElementOrigine(3, "D", 0, "", "", "", false, false, 0, 0, 0, 0, 0, 0, []),
    ]);

    private static readonly HashSet<int> Tous = [1, 2, 3];

    private static SaisieRetour Saisie(params QuantiteRetournee[] q) => new(1000, Jour, 0, 0, 0, ["ABIME"], true, false, 0, 0, q);

    [Fact]
    public void Elements_retournables()
    {
        var o = Origine();
        Assert.Equal("", ReglesRetourBl.Refus(o.Elements[0]));
        Assert.Equal("", ReglesRetourBl.Refus(o.Elements[1]));
        Assert.Contains("divers", ReglesRetourBl.Refus(o.Elements[2]));
        Assert.Contains("sous-traité", ReglesRetourBl.Refus(o.Elements[0] with { SousTraite = true }));
        var p = ReglesRetourBl.Preparation(o, new HashSet<int> { 1, 2 });
        Assert.Equal([1, 2], p.Elements.Select(e => e.Element));   // représentant limité : éléments vus seulement
        Assert.Equal(800, p.Elements[1].Retournable);
        Assert.Equal(1500, p.Elements[0].Lots[1].Retournable);
    }

    [Fact]
    public void Controles_de_la_saisie()
    {
        var o = Origine();
        Assert.Empty(ReglesRetourBl.Controler(o, Saisie(new(1, 1, "658282", "", "SSCC1", 3000), new(1, 2, "693201", "PREPA", "", 1500),
            new(2, 0, "", "", "", 800)), Tous, Jour));
        string Erreurs(params QuantiteRetournee[] q) => string.Join(" / ", ReglesRetourBl.Controler(o, Saisie(q), Tous, Jour));
        Assert.Contains("au moins une quantité", Erreurs());
        Assert.Contains("dépasse", Erreurs(new QuantiteRetournee(2, 0, "", "", "", 801)));                  // fabrication : 1 000 − 200
        Assert.Contains("dépasse", Erreurs(new QuantiteRetournee(1, 2, "693201", "PREPA", "", 1501)));      // lot : 2 000 − 500
        Assert.Contains("déjà été retournée", Erreurs(new QuantiteRetournee(1, 1, "658282", "", "SSCC2", 10)));
        Assert.Contains("supérieure à la quantité livrée", Erreurs(new QuantiteRetournee(1, 1, "658282", "", "SSCC1", 3001)));
        Assert.Contains("choisissez les palettes", Erreurs(new QuantiteRetournee(1, 1, "658282", "", "", 10)));   // lot en palettes
        Assert.Contains("divers", Erreurs(new QuantiteRetournee(3, 0, "", "", "", 1)));
        Assert.Contains("n'existe pas", string.Join("", ReglesRetourBl.Controler(o, Saisie(new QuantiteRetournee(2, 0, "", "", "", 1)), new HashSet<int> { 1 }, Jour)));
        var s = Saisie(new QuantiteRetournee(2, 0, "", "", "", 1));
        Assert.Contains("postérieure", string.Join("", ReglesRetourBl.Controler(o, s, Tous, Jour.AddDays(-1))));
        Assert.Contains("antérieure", string.Join("", ReglesRetourBl.Controler(o, s with { Date = new DateOnly(2024, 9, 30) }, Tous, Jour)));
        Assert.Contains("client facturé", string.Join("", ReglesRetourBl.Controler(o, s with { FacturationAutomatique = true }, Tous, Jour)));
        Assert.Contains("40 caractères", string.Join("", ReglesRetourBl.Controler(o, s with { Motif = [new string('x', 41)] }, Tous, Jour)));
    }

    [Fact]
    public void Repartition_par_element_ligne_lot_et_palette()
    {
        var o = Origine();
        var e = ReglesRetourBl.Repartir(o, Saisie(new(1, 1, "658282", "", "SSCC1", 1200), new(1, 2, "693201", "PREPA", "", 100),
            new(2, 0, "", "", "", 50)));
        e = ReglesRetourBl.AvecEmplacement(e, _ => "NC");
        Assert.Equal(2, e.Count);
        Assert.Equal(1200, e[0].Lignes[0].Quantite);
        Assert.Equal(("SSCC1", 1200), (e[0].Lignes[0].Lots[0].Palettes[0].Palette.Sscc, e[0].Lignes[0].Lots[0].Palettes[0].Quantite));
        Assert.Equal(("693201", 100, "NC"), (e[0].Lignes[1].Lots[0].Origine.Lot, e[0].Lignes[1].Lots[0].Quantite, e[0].Lignes[1].Lots[0].Emplacement));
        Assert.Equal(50, e[1].QuantiteFabrication);
    }

    // ---------- Base de test ----------
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static RetourBlService Service() => new(new SaisieBlDb2(BaseDeTest.Configuration),
        new ConsultationBlService(new ConsultationBlDb2(BaseDeTest.Configuration), new Utilisateur()), new Utilisateur());

    // BL d'essai 165271 (Dior, dossier 69320/9) : ligne 1 livrée en une palette SSCC, ligne 2 par le lot 693201
    private const int BlEssai = 165271;

    [FactDb2]
    public async Task Preparation_du_BL_d_essai()
    {
        var p = await Service().PreparerAsync(BlEssai);
        foreach (var e in p.Elements)
            foreach (var l in e.Lots)
                sortie.WriteLine($"{e.Element} ligne {l.LigneBl} {l.Article} lot {l.Lot} {l.Emplacement} livré {l.QuantiteLivree} retourné {l.DejaRetourne} " +
                                 string.Join(", ", l.Palettes.Select(x => $"{x.Sscc} {x.Quantite}{(x.DejaRetournee ? " retournée" : "")}")));
        var el = Assert.Single(p.Elements);
        Assert.Equal("", el.Refus);
        Assert.Contains(el.Lots, l => l.Palettes.Count == 1);
        Assert.Contains(el.Lots, l => l.Palettes.Count == 0 && l.Lot == "693201");
        await Assert.ThrowsAsync<RegleMetierException>(() => Service().PreparerAsync(999999));
    }

    [FactDb2Ecriture]
    public async Task Retour_palette_et_lot_puis_suppression()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var avant = await PhotoAsync(cnx);
        var service = Service();
        var p = await service.PreparerAsync(BlEssai);
        var el = p.Elements.Single();
        var enPalette = el.Lots.Single(l => l.Palettes.Count > 0);
        var palette = enPalette.Palettes.Single(x => !x.DejaRetournee);
        var parLot = el.Lots.Single(l => l.Palettes.Count == 0);
        var saisie = new SaisieRetour(BlEssai, p.DateBl!.Value, 0, 1, 12, ["TEST NETPRINT SAAS", "A SUPPRIMER"], true, false, 0, 0,
            [new(el.Element, enPalette.LigneBl, enPalette.Lot, "", palette.Sscc, 100), new(el.Element, parLot.LigneBl, parLot.Lot, parLot.Emplacement, "", 50)]);
        var numero = 0;
        try
        {
            numero = await service.EnregistrerAsync(saisie);
            sortie.WriteLine($"Retour {numero}");
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNOBL = ? AND HETABL = 'R' AND HNOBLO = ?", numero, BlEssai));
            Assert.Equal(-150, await CompterAsync(cnx, "SELECT SUM(DQTE) FROM NP11F.EXP015 WHERE RSOC = 8 AND DNBL = ?", numero));
            Assert.Equal(-150, await CompterAsync(cnx, "SELECT SUM(DNQTPL) FROM NP11F.EXP016 WHERE RSOC = 8 AND DNBL = ?", numero));
            Assert.Equal(150, await CompterAsync(cnx, "SELECT SUM(MVQTUS) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'RE'", numero));
            Assert.Equal(2, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'TD' AND MVSENS = 'E' AND MVDEP = 13", numero));
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_SSCC = ? AND STATUT = '7' AND BL_RETOUR = ?",
                palette.Sscc, numero));
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND SSCC_ORIGINE = ? AND STATUT = '2' AND DEPOT = 13 AND QUANTITE_EX = 100",
                palette.Sscc));
            var pendant = await PhotoAsync(cnx);
            Assert.Equal(avant.Livre - 150, pendant.Livre);

            // Le même retour une seconde fois : palette déjà retournée
            await Assert.ThrowsAsync<RegleMetierException>(() => service.EnregistrerAsync(saisie));
            var apres = await service.PreparerAsync(BlEssai);
            Assert.Equal(50, apres.Elements.Single().Lots.Single(l => l.Palettes.Count == 0).DejaRetourne);
            Assert.True(apres.Elements.Single().Lots.Single(l => l.Palettes.Count > 0).Palettes.Single().DejaRetournee);

            // Suppression du retour : tout revient comme avant
            await service.SupprimerAsync(numero);
            foreach (var table in new[] { ("EXP009", "HNOBL"), ("EXP010", "HNOBL"), ("EXP015", "DNBL"), ("EXP016", "DNBL"), ("A700F", "MVBL") })
                Assert.Equal(0, await CompterAsync(cnx, $"SELECT COUNT(*) FROM NP11F.{table.Item1} WHERE RSOC = 8 AND {table.Item2} = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND SSCC_ORIGINE = ?", palette.Sscc));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PRTLCK WHERE PLPROF = 'NP11'"));
            Assert.Equal(avant, await PhotoAsync(cnx));
            await Executer(cnx, "DELETE FROM NP11F.EVE200 WHERE RSOC = 8 AND AMBL = ?", numero);
            numero = 0;
        }
        finally
        {
            if (numero > 0) await NettoyerAsync(cnx, numero, avant);
        }
    }

    private sealed record Photo(string Ligne2, string Ligne3, int QteLivreeDossier, string Tag, string Palette)
    {
        // Livré des deux lignes (DNQTEL), pour suivre l'effet du retour
        public int Livre => int.Parse(Ligne2.Split('/')[0]) + int.Parse(Ligne3.Split('/')[0]);
    }

    // Lignes 2 et 3 du dossier 69320/9 (livré / reste / soldé), dossier (quantité livrée, solde) et palette du BL d'essai
    private static async Task<Photo> PhotoAsync(OdbcConnection cnx)
    {
        async Task<string> Ligne(int n)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = "SELECT DNQTEL, DNQTER, DNSOLD FROM NP11F.NEG100 WHERE RSOC = 8 AND RDOSS = 69320 AND DNDCLE = '9' AND DNNLIG = ?";
            cmd.Parameters.AddWithValue("", n);
            await using var rd = await cmd.ExecuteReaderAsync();
            Assert.True(await rd.ReadAsync());
            return $"{Convert.ToInt32(rd.GetValue(0))}/{Convert.ToInt32(rd.GetValue(1))}/{Convert.ToString(rd.GetValue(2))!.Trim()}";
        }
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT a.CCQTEL, a.CCTAGS,
                   (SELECT MAX(STATUT || '/' || TRIM(SSCC_RETOUR) || '/' || BL_RETOUR) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_BL = 165271)
            FROM NP11F.AB001 a WHERE a.RSOC = 8 AND a.RDOSS = 69320 AND a.CCDCLE = '9'
            """;
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        return new Photo(await Ligne(2), await Ligne(3), Convert.ToInt32(rd.GetValue(0)), Convert.ToString(rd.GetValue(1))!.Trim(),
            Convert.ToString(rd.GetValue(2))!);
    }

    private static async Task<int> CompterAsync(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.AddWithValue("", v);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? 0 : Convert.ToInt32(r);
    }

    private static async Task Executer(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.AddWithValue("", v);
        await cmd.ExecuteNonQueryAsync();
    }

    // Secours si la suppression échoue : remise en état directe (les quantités sont reprises de la photo d'avant)
    private static async Task NettoyerAsync(OdbcConnection cnx, int numero, Photo avant)
    {
        await Executer(cnx, "DELETE FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_SSCC IN (SELECT SSCC_RETOUR FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND BL_RETOUR = ?)", numero);
        await Executer(cnx, "UPDATE NP11F.PROD_ENTITE SET STATUT = '5', SSCC_RETOUR = ' ', BL_RETOUR = 0 WHERE SOCIETE = 8 AND BL_RETOUR = ?", numero);
        foreach (var (table, colonne) in new[] { ("A700F", "MVBL"), ("EXP016", "DNBL"), ("EXP015", "DNBL"), ("EXP012", "HNOBL"), ("EXP010", "HNOBL"),
                     ("EXP009", "HNOBL"), ("EVE200", "AMBL") })
            await Executer(cnx, $"DELETE FROM NP11F.{table} WHERE RSOC = 8 AND {colonne} = ?", numero);
        foreach (var (ligne, valeurs) in new[] { (2, avant.Ligne2), (3, avant.Ligne3) })
        {
            var v = valeurs.Split('/');
            await Executer(cnx, "UPDATE NP11F.NEG100 SET DNQTEL = ?, DNQTER = ?, DNSOLD = ? WHERE RSOC = 8 AND RDOSS = 69320 AND DNDCLE = '9' AND DNNLIG = ?",
                int.Parse(v[0]), int.Parse(v[1]), v[2] == "" ? " " : v[2], ligne);
        }
        await Executer(cnx, "UPDATE NP11F.AB001 SET CCQTEL = ?, CCTAGS = ? WHERE RSOC = 8 AND RDOSS = 69320 AND CCDCLE = '9'", avant.QteLivreeDossier,
            avant.Tag == "" ? " " : avant.Tag);
        foreach (var depot in new[] { "001", "013" })
        {
            var parametre = $"0008P1{depot}PQA002".PadRight(26).PadRight(256);
            await Executer(cnx, $"CALL QSYS2.QCMDEXC('CALL PGM(NP11P/STOREVNAV) PARM(''{parametre}'')')");
        }
    }
}
