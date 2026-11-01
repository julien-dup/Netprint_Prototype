using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Transformation d'un pré-BL en BL (EXP540) : règles sans base, puis transformation réelle et suppression du BL sur NP11F de test
[Collection(EcrituresNp11f.Collection)]
public class TransformationPreBlTests(ITestOutputHelper sortie)
{
    private static readonly AdresseBl Adresse = new(0, "", "DIOR", "", "", "45000", "ORLEANS", "45", "");

    private static PaletteSourcePreBl P(string sscc, int quantite, int depot = 1, string emplacement = "E1", string lot = "123451", string statut = "4",
        bool picking = false) => new(sscc, quantite, picking, statut, depot, emplacement, lot);

    private static LigneSourcePreBl L(int numero, int dossier, int quantite, bool stocke, params LotSourcePreBl[] lots) => new(
        numero, dossier, "9", numero, "ART", "", quantite, ["OBS"], false, "T", 2, 0, 500, 0, "C", stocke, "P1", "F", "SF", 12.5m, "ETUI", " ",
        ["TEXTE 1", "TEXTE 2", "TEXTE 3"], 0, 0, lots);

    private static CommandeSourcePreBl C(int dossier, int genF = 0, int cliF = 0) =>
        new(dossier, "9", "1", 411100, 20, "REF", null, 1, "N", true, genF, cliF);

    private static SourcePreBl S(params LigneSourcePreBl[] lignes) => new(
        1, "", new DateOnly(2026, 11, 2), 411100, 20, 0, Adresse, ["", "", ""], "", 2, 0, 1, "M", "F", 0, 0, false, false, 25, 1, lignes,
        lignes.Select(l => l.Dossier).Distinct().ToDictionary(d => (d, "9"), d => C(d)), []);

    [Fact]
    public void Refus_sous_verrou()
    {
        var lot = new LotSourcePreBl(1, false, "N° 1", [P("A", 100)]);
        Assert.Equal("", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot))));
        Assert.Contains("déjà transformé", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot)) with { Henbl = "O" }));
        Assert.Contains("aucune ligne", ReglesTransformationPreBl.Refus(S()));
        Assert.Contains("encore à produire", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot with { AProduire = true }))));
        Assert.Contains("aucune palette", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot with { Palettes = [] }))));
        Assert.Equal("", ReglesTransformationPreBl.Refus(S(L(1, 1, 5, false))));   // frais : ni lot ni palette
        Assert.Contains("pas géré en stock", ReglesTransformationPreBl.Refus(S(L(1, 1, 5, false, lot))));
        Assert.Contains("statut 5", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot with { Palettes = [P("A", 100, statut: "5")] }))));
        Assert.Contains("n'existe plus", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot with { Palettes = [P("A", 100, statut: "")] }))));
        Assert.Contains("picking", ReglesTransformationPreBl.Refus(S(L(1, 1, 100, true, lot with { Palettes = [P("A", 100, picking: true)] }))));
        var sansCommande = S(L(1, 1, 100, true, lot), L(2, 2, 100, true, lot));
        Assert.Contains("commande 2/9 n'existe plus", ReglesTransformationPreBl.Refus(sansCommande with
            { Commandes = new Dictionary<(int, string), CommandeSourcePreBl> { [(1, "9")] = C(1) } }));
    }

    [Fact]
    public void Periode_de_stock_ouverte()
    {
        var debut = new DateOnly(2026, 10, 1);
        Assert.Null(ReglesStock.HorsPeriode(new DateOnly(2026, 10, 1), debut));
        Assert.Null(ReglesStock.HorsPeriode(new DateOnly(2026, 11, 30), debut));
        Assert.Equal(debut, ReglesStock.HorsPeriode(new DateOnly(2026, 12, 1), debut));
        Assert.Equal(debut, ReglesStock.HorsPeriode(new DateOnly(2026, 9, 30), debut));
        Assert.Null(ReglesStock.HorsPeriode(new DateOnly(2020, 1, 1), null));
    }

    [Fact]
    public void Quantites_sorties_textes_poids_et_elements()
    {
        var ligne = L(1, 2, 1000, true,
            new LotSourcePreBl(1, false, "N° 1 à 2", [P("A", 400), P("B", 300, depot: 2, emplacement: "Z9"), P("C", 200)]),
            new LotSourcePreBl(2, false, "", [P("D", 50, lot: "999991")]));
        Assert.Equal(950, ReglesTransformationPreBl.Quantite(ligne));
        Assert.Equal(5, ReglesTransformationPreBl.Quantite(L(1, 1, 5, false)));
        Assert.Equal([(1, "123451", "E1", 600, 2), (1, "999991", "E1", 50, 1), (2, "123451", "Z9", 300, 1)],
            ReglesTransformationPreBl.Sorties(ligne).Select(x => (x.Depot, x.Lot, x.Emplacement, x.Quantite, x.Palettes.Count)));

        Assert.Equal(["TEXTE 1", "TEXTE 2", "TEXTE 3", "OBS", "Palette(s) N° 1 à 2"], ReglesTransformationPreBl.Textes(ligne, false));
        Assert.Equal(["TEXTE 1", "TEXTE 2", "TEXTE 3", "OBS"], ReglesTransformationPreBl.Textes(ligne, true));

        Assert.Equal((2, 25m), ReglesTransformationPreBl.ColisPoids(ligne));
        Assert.Equal((3, 25m), ReglesTransformationPreBl.ColisPoids(ligne with { ExMecompte = 10 }));
        var s = S(ligne, L(2, 1, 10, false));
        Assert.Equal(25 + 25 + 2 * 25, ReglesTransformationPreBl.PoidsTotal(s));
        Assert.Equal(300, ReglesTransformationPreBl.PoidsTotal(s with { Poids = 300 }));

        Assert.Equal([1, 2], ReglesTransformationPreBl.Elements(s).Select(e => e.Key.Dossier));
        Assert.Contains("Ligne 1 (ART) : 950 livrés (palettes) pour 1", Assert.Single(ReglesTransformationPreBl.Avertissements(s)));

        Assert.Equal((411100, 20), ReglesTransformationPreBl.ClientFacture([ligne], C(2)));
        Assert.Equal((411100, 55), ReglesTransformationPreBl.ClientFacture([ligne], C(2, 411100, 55)));
        Assert.Equal((411200, 7), ReglesTransformationPreBl.ClientFacture([ligne, ligne with { CompteGeneralFacture = 411200, CompteClientFacture = 7 }],
            C(2, 411100, 55)));
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    // Pré-BL d'essai 165268 (société 8, une palette au dépôt 2 : transfert puis sortie). Ses palettes sont passées au statut 4 (pointées) le temps du
    // test ; le BL est supprimé comme depuis sa fiche : la base doit revenir à l'identique. Les palettes de l'ancien BL 165268
    // supprimé (16 palettes de l'OPR 65885, données de test) sont mises de côté pendant le test (n° de BL à 0) puis remises.
    private const int PreBlEssai = 165268;
    private static readonly DateOnly DepartEssai = new(2025, 2, 10);

    // Pré-BL 165268 : son n° est celui d'un ancien BL supprimé dont 16 palettes (OPR 65885) portent encore le n° : refusé
    [FactDb2Ecriture]
    public async Task Refus_si_des_palettes_portent_deja_le_numero()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        if (await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_BL = 165268 AND NUMERO_OPR = 65885") == 0)
        {
            sortie.WriteLine("Plus de palettes de l'ancien BL 165268 : test sans objet.");
            return;
        }
        var statut = await TexteAsync(cnx, "SELECT STATUT FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_SSCC = 'P00000084'");
        try
        {
            await Executer(cnx, "UPDATE NP11F.PROD_ENTITE SET STATUT = '4' WHERE SOCIETE = 8 AND NUMERO_SSCC = 'P00000084'");
            var bl = new SaisieBlDb2(BaseDeTest.Configuration);
            var ex = await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(async () =>
                await bl.TransformerPreBlAsync(8, "TEST", 165268, await bl.ParametresAsync(8, default), new DateOnly(2025, 2, 10), default));
            sortie.WriteLine(ex.Message);
            Assert.Contains("ancien BL n° 165268", ex.Message);
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = 165268"));
        }
        finally
        {
            await Executer(cnx, "UPDATE NP11F.PROD_ENTITE SET STATUT = ? WHERE SOCIETE = 8 AND NUMERO_SSCC = 'P00000084'", statut);
        }
    }

    [FactDb2Ecriture]
    public async Task Transformation_puis_suppression_du_BL()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var palettes = new List<(string Sscc, string Statut, string Sortie)>();
        await using (var cmd = Commande(cnx, """
            SELECT e.NUMERO_SSCC, e.STATUT, COALESCE(CHAR(e.DATE_SOR_STOCK), '') FROM NP11F.EXP517 p
            JOIN NP11F.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC WHERE p.RSOC = 8 AND p.DNBL = ?
            """, PreBlEssai))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) palettes.Add((rd.GetString(0).Trim(), rd.GetString(1).Trim(), rd.GetString(2).Trim()));
        Assert.NotEmpty(palettes);
        var henbl = await TexteAsync(cnx, "SELECT HENBL FROM NP11F.EXP509 WHERE RSOC = 8 AND HNOBL = ?", PreBlEssai);
        Assert.NotEqual("O", henbl);
        Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", PreBlEssai));
        var anciennes = new List<string>();
        await using (var cmd = Commande(cnx, """
            SELECT NUMERO_SSCC FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_BL = ? AND ELEMENT_BL = 1 AND NUMERO_LIGNE_BL = 1 AND STATUT = '5'
              AND NUMERO_SSCC NOT IN (SELECT DPSSCC FROM NP11F.EXP517 WHERE RSOC = 8 AND DNBL = ?)
            """, PreBlEssai, PreBlEssai))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) anciennes.Add(rd.GetString(0).Trim());
        Assert.Equal(anciennes.Count, await CompterAsync(cnx, """
            SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_BL = ?
              AND NUMERO_SSCC NOT IN (SELECT DPSSCC FROM NP11F.EXP517 WHERE RSOC = 8 AND DNBL = ?)
            """, PreBlEssai, PreBlEssai));
        var avant = await PhotoAsync(cnx);
        var dernierAudit = await CompterAsync(cnx, "SELECT COALESCE(MAX(AMLIG), 0) FROM NP11F.EVE200");
        var debut = DateTime.Now.AddMinutes(-1);
        var bl = new SaisieBlDb2(BaseDeTest.Configuration);
        var transforme = false;
        try
        {
            foreach (var a in anciennes) await Executer(cnx, "UPDATE NP11F.PROD_ENTITE SET NUMERO_BL = 0 WHERE SOCIETE = 8 AND NUMERO_SSCC = ?", a);
            foreach (var p in palettes) await Executer(cnx, "UPDATE NP11F.PROD_ENTITE SET STATUT = '4' WHERE SOCIETE = 8 AND NUMERO_SSCC = ?", p.Sscc);
            var avertissements = await bl.TransformerPreBlAsync(8, "TEST", PreBlEssai, await bl.ParametresAsync(8, default), DepartEssai, default);
            transforme = true;
            foreach (var a in avertissements) sortie.WriteLine(a);
            sortie.WriteLine($"BL {PreBlEssai} : {await TexteAsync(cnx, "SELECT CHAR(COUNT(*)) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ?", PreBlEssai)} mouvements");
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", PreBlEssai));
            Assert.Equal("O", await TexteAsync(cnx, "SELECT HENBL FROM NP11F.EXP509 WHERE RSOC = 8 AND HNOBL = ?", PreBlEssai));
            Assert.Equal(palettes.Count, await CompterAsync(cnx,
                "SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_BL = ? AND STATUT = '5' AND ELEMENT_BL = 1", PreBlEssai));
            Assert.True(await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'EX'", PreBlEssai) > 0);
            Assert.Equal(2, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'TR'", PreBlEssai));
            await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() =>
                bl.TransformerPreBlAsync(8, "TEST", PreBlEssai, bl.ParametresAsync(8, default).Result, DepartEssai, default));

            var saisie = new SaisieBlService(bl, new Utilisateur());
            var elements = await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNOBL = ?", PreBlEssai);
            for (var e = elements; e >= 1; e--) await saisie.SupprimerAsync(PreBlEssai, e);
            transforme = false;
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", PreBlEssai));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ?", PreBlEssai));
        }
        finally
        {
            if (transforme) sortie.WriteLine($"ATTENTION : BL {PreBlEssai} à supprimer à la main, puis remettre NUMERO_BL = {PreBlEssai} aux palettes " +
                                             string.Join(", ", anciennes));
            else
            {
                foreach (var a in anciennes)
                    await Executer(cnx, "UPDATE NP11F.PROD_ENTITE SET NUMERO_BL = ? WHERE SOCIETE = 8 AND NUMERO_SSCC = ?", PreBlEssai, a);
                foreach (var p in palettes)
                    await Executer(cnx, """
                        UPDATE NP11F.PROD_ENTITE SET STATUT = ?,
                               DATE_SOR_STOCK = CASE WHEN CAST(? AS VARCHAR(30)) = '' THEN NULL ELSE TIMESTAMP(CAST(? AS VARCHAR(30))) END
                        WHERE SOCIETE = 8 AND NUMERO_SSCC = ?
                        """, p.Statut, p.Sortie, p.Sortie, p.Sscc);
                await Executer(cnx, "UPDATE NP11F.EXP509 SET HENBL = ? WHERE RSOC = 8 AND HNOBL = ?", henbl == "" ? " " : henbl, PreBlEssai);
                await Executer(cnx, "DELETE FROM NP11F.EVE200 WHERE AMLIG > ? AND RSOC = 8 AND AMBL = ?", dernierAudit, PreBlEssai);
                await Executer(cnx, """
                    DELETE FROM NP11F.DOS040 WHERE RSOC = 8 AND HITYPM = 'SO' AND HIDTMA = ? AND HIDTMM = ? AND HIDTMJ = ? AND HIHEUR >= ?
                      AND (RDOSS, HICLE) IN (SELECT HNODOS, HCLEF FROM NP11F.EXP510 WHERE RSOC = 8 AND HNOBL = ?)
                    """, debut.Year, debut.Month, debut.Day, debut.Hour * 10000 + debut.Minute * 100 + debut.Second, PreBlEssai);
            }
        }
        Assert.Equal(avant, await PhotoAsync(cnx));
    }

    private sealed record Photo(string PreBl, string Palettes, string Commande, string Dossier, string Mouvements);

    private static async Task<Photo> PhotoAsync(OdbcConnection cnx)
    {
        async Task<string> Lire(string sql)
        {
            await using var cmd = Commande(cnx, sql, PreBlEssai);
            await using var rd = await cmd.ExecuteReaderAsync();
            var r = new List<string>();
            while (await rd.ReadAsync()) r.Add(string.Join("/", Enumerable.Range(0, rd.FieldCount).Select(i => Convert.ToString(rd.GetValue(i))!.Trim())));
            return string.Join(";", r);
        }
        return new Photo(
            await Lire("SELECT HENBL, HPROF, HNCOLT, HPDSTT FROM NP11F.EXP509 WHERE RSOC = 8 AND HNOBL = ?"),
            await Lire("""
                SELECT e.NUMERO_SSCC, e.STATUT, e.NUMERO_BL, e.ELEMENT_BL, e.NUMERO_LIGNE_BL, e.DEPOT, e.EMPLACEMENT, e.PALETTE_GROUP, e.DATE_SOR_STOCK
                FROM NP11F.EXP517 p JOIN NP11F.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC WHERE p.RSOC = 8 AND p.DNBL = ?
                ORDER BY e.NUMERO_SSCC
                """),
            await Lire("""
                SELECT n.DNNLIG, n.DNQTEL, n.DNQTER, n.DNSOLD FROM NP11F.NEG100 n JOIN NP11F.EXP510 l ON l.RSOC = n.RSOC AND l.HNODOS = n.RDOSS
                AND l.HCLEF = n.DNDCLE AND l.HNUMLC = n.DNNLIG WHERE l.RSOC = 8 AND l.HNOBL = ? ORDER BY n.DNNLIG
                """),
            await Lire("""
                SELECT a.CCTAGS, a.CCQTEL, a.CCSLDA FROM NP11F.AB001 a WHERE a.RSOC = 8 AND (a.RDOSS, a.CCDCLE) IN
                (SELECT HNODOS, HCLEF FROM NP11F.EXP510 WHERE RSOC = 8 AND HNOBL = ?)
                """),
            await Lire("""
                SELECT COUNT(*), COALESCE(SUM(m.MVQTUS), 0) FROM NP11F.A700F m WHERE m.RSOC = 8 AND (m.MVREF, m.MVIND) IN
                (SELECT DART, DIND FROM NP11F.EXP510 WHERE RSOC = 8 AND HNOBL = ?)
                """));
    }

    private static OdbcCommand Commande(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.AddWithValue("", v);
        return cmd;
    }

    private static async Task<int> CompterAsync(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = Commande(cnx, sql, valeurs);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? 0 : Convert.ToInt32(r);
    }

    private static async Task<string> TexteAsync(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = Commande(cnx, sql, valeurs);
        return Convert.ToString(await cmd.ExecuteScalarAsync())?.Trim() ?? "";
    }

    private static async Task Executer(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = Commande(cnx, sql, valeurs);
        await cmd.ExecuteNonQueryAsync();
    }
}
