using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Livraison d'un OPR : règles (état des lignes, découpage en BL selon le mode du client), puis situation et génération réelles
[Collection(EcrituresNp11f.Collection)]
public class LivraisonOprTests(ITestOutputHelper sortie)
{
    [Fact]
    public void Etat_des_lignes()
    {
        Assert.Equal(("Frais", false), ReglesGenerationBlOpr.Etat(true, 10, 0, 0, false));
        Assert.Equal(("À sortir", true), ReglesGenerationBlOpr.Etat(false, 100, 0, 0, false));
        Assert.Equal(("En cours", true), ReglesGenerationBlOpr.Etat(false, 100, 100, 40, false));
        Assert.Equal(("Conditionnement en cours", true), ReglesGenerationBlOpr.Etat(false, 100, 100, 100, true));
        Assert.Equal(("OK", false), ReglesGenerationBlOpr.Etat(false, 100, 100, 100, false));
        Assert.Equal(("OK incomplet", false), ReglesGenerationBlOpr.Etat(false, 100, 80, 80, false));
        Assert.Equal(("OK qté supérieure", false), ReglesGenerationBlOpr.Etat(false, 100, 120, 120, false));
        LigneSituationOpr L(bool bloquante, bool frais = false, int cond = 10) => new(1, 1, "9", "ART", "", "", 10, 10, cond, "", bloquante, frais, []);
        Assert.Contains("pas paramétré", LivraisonOprService.Refus("", [L(false)]));
        Assert.Contains("préparation", LivraisonOprService.Refus("2", [L(false), L(true)]));
        Assert.Contains("frais", LivraisonOprService.Refus("1", [L(false), L(false, frais: true)]));
        Assert.Equal("", LivraisonOprService.Refus("2", [L(false), L(false, frais: true)]));
        Assert.Contains("transporteur", string.Join("", LivraisonOprService.Controler(new GenerationBlOpr(1, new DateOnly(2024, 9, 2), 0, "F", "", [], ""))));
        Assert.Empty(LivraisonOprService.Controler(new GenerationBlOpr(1, new DateOnly(2024, 9, 2), 0, "E", "", [], "")));
    }

    private static PaletteBlOpr P(int ligneOpr, int dossier, int ligneCde, string article, int quantite, int colis, string enseigne = "1", bool frais = false,
        string lot = "123451", int depot = 1, int clientFacture = 20) =>
        new(ligneOpr, dossier, "9", ligneCde, article, "", quantite, frais, frais ? "" : $"SSCC{ligneOpr}-{colis}-{quantite}", frais ? "" : lot, "E1", depot,
            frais ? 0 : colis, 10m, 7, 411100, clientFacture, enseigne);

    private static readonly PaletteBlOpr[] Palettes =
    [
        P(1, 100, 1, "A", 500, 1), P(1, 100, 1, "A", 500, 2), P(1, 100, 1, "A", 300, 2, lot: "999991"),
        P(2, 100, 2, "B", 200, 3), P(3, 200, 1, "A", 400, 3), P(4, 200, 2, "FRAIS", 1, 0, frais: true),
    ];

    [Fact]
    public void Decoupage_selon_le_mode_du_client()
    {
        var parColis = ReglesGenerationBlOpr.Planifier([.. Palettes.Where(p => !p.Frais)], "1");
        Assert.Equal(3, parColis.Count);                                   // colis 1, 2, 3
        Assert.Equal(2, parColis[2].Elements.Count);                       // le colis 3 contient deux commandes
        var parCommande = ReglesGenerationBlOpr.Planifier(Palettes, "2");
        Assert.Equal([100, 200], parCommande.Select(b => b.Elements.Single().Dossier));
        var ligneA = parCommande[0].Elements[0].Lignes.Single(l => l.Article == "A");
        Assert.Equal((1300, 2), (ligneA.Quantite, ligneA.Lots.Count));     // deux lots
        Assert.Equal(1, parCommande[1].Elements[0].Lignes.Single(l => l.Frais).Quantite);
        var parArticle = ReglesGenerationBlOpr.Planifier(Palettes, "3");
        Assert.Equal(3, parArticle.Count);                                 // A (deux commandes), B, FRAIS
        Assert.Equal(2, parArticle.Single(b => b.Elements.Any(e => e.Lignes.Any(l => l.Article == "A"))).Elements.Count);
        var total = ReglesGenerationBlOpr.Planifier(Palettes, "4");
        Assert.Equal(2, Assert.Single(total).Elements.Count);
        Assert.Equal((3, 30), (total[0].Colis, total[0].PoidsCartons));    // cartons distincts
        // Le carton 3 contient deux commandes : il n'est rattaché qu'au premier élément (une seule sortie de stock d'emballage)
        Assert.Equal(1, total[0].Elements.SelectMany(e => e.Cartons).Count(c => c.Colisage == 3));
        Assert.Equal(2, ReglesGenerationBlOpr.Planifier([.. Palettes, P(9, 300, 1, "C", 10, 9, enseigne: "2")], "4").Count);   // une enseigne de plus
    }

    [Fact]
    public void Conditionnement_et_etat_de_livraison()
    {
        Assert.Equal((2, 500, 1, 300), ReglesGenerationBlOpr.Conditionnement([500, 500, 300], "C"));
        Assert.Equal((0, 0, 0, 0), ReglesGenerationBlOpr.Conditionnement([500], "S"));
        Assert.Equal("T", ReglesGenerationBlOpr.EtatLivraison(1000, 2000, 3000, false));
        Assert.Equal("A", ReglesGenerationBlOpr.EtatLivraison(0, 1000, 3000, true));
        Assert.Equal("", ReglesGenerationBlOpr.EtatLivraison(0, 1000, 3000, false));
    }

    // ---------- Base de test ----------
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static LivraisonOprService Service() =>
        new(new OprDb2(BaseDeTest.Configuration), new SaisieBlDb2(BaseDeTest.Configuration), new Utilisateur());

    // OPR d'essai 65980 (Dior, dossier 68682/9, client en mode « un BL par colis ») : une palette conditionnée, carton clôturé
    private const int OprEssai = 65980;

    [FactDb2]
    public async Task Situation_de_l_OPR_d_essai()
    {
        var s = await Service().SituationAsync(OprEssai);
        sortie.WriteLine($"mode {s.Mode} ({s.LibelleMode}) · générer : {s.PeutGenerer} {s.Refus} · transporteur {s.Defauts.Transporteur} port {s.Defauts.TypePort}");
        foreach (var l in s.Lignes)
            sortie.WriteLine($"{l.NumeroLigne} {l.Dossier}/{l.Cle} {l.Article} {l.Quantite}/{l.Preparee}/{l.Conditionnee} {l.Etat} " +
                             string.Join(", ", l.Colis.Select(c => $"{c.Sscc} {c.Quantite} st.{c.Statut} carton {c.Carton} {(c.CartonClos ? "clos" : "")}")));
        Assert.Equal("1", s.Mode);
        Assert.NotEmpty(s.Lignes);
    }

    // Génération du BL de l'OPR d'essai puis suppression du BL : tout revient comme avant (palette, carton, commande, OPR, stock)
    [FactDb2Ecriture]
    public async Task Generation_du_BL_puis_suppression()
    {
        var s = await Service().SituationAsync(OprEssai);
        if (!s.PeutGenerer)
        {
            sortie.WriteLine($"OPR {OprEssai} non livrable : {s.Refus}");
            return;
        }
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var avant = await PhotoAsync(cnx);
        var bls = new List<int>();
        try
        {
            var r = await Service().GenererBlAsync(new GenerationBlOpr(OprEssai, new DateOnly(2024, 10, 15), 45, "F", "E", ["TEST NETPRINT SAAS"], ""));
            bls.AddRange(r.Bls.Select(b => b.NumeroBl));
            foreach (var a in r.Avertissements) sortie.WriteLine(a);
            sortie.WriteLine($"BL {string.Join(", ", bls)}");
            var bl = Assert.Single(bls);
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_OPR = ? AND STATUT = '5' AND NUMERO_BL = ?",
                OprEssai, bl));
            Assert.True(await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'EX'", bl) > 0);
            Assert.Equal("O", (await PhotoAsync(cnx)).LigneOpr.Split('/')[0]);
            await Service().FinaliserBlAsync(OprEssai, [new FinalisationBlOpr(bl, 2, 25, ["PIED ESSAI"])]);
            Assert.Equal(2, await CompterAsync(cnx, "SELECT HNPALT FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", bl));

            // Suppression du BL (comme depuis sa fiche) : tout revient
            var saisie = new SaisieBlService(new SaisieBlDb2(BaseDeTest.Configuration), new Utilisateur());
            var elements = await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNOBL = ?", bl);
            for (var e = elements; e >= 1; e--) await saisie.SupprimerAsync(bl, e);
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", bl));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ?", bl));
            Assert.Equal(avant with { LigneOpr = avant.LigneOpr }, await PhotoAsync(cnx));
            await Executer(cnx, "DELETE FROM NP11F.EVE200 WHERE RSOC = 8 AND AMBL = ?", bl);
            bls.Clear();
        }
        finally
        {
            foreach (var bl in bls) sortie.WriteLine($"ATTENTION : BL {bl} à supprimer à la main");
        }
    }

    private sealed record Photo(string LigneOpr, string StatutOpr, string Palettes, string Cartons, string Commande, string Dossier);

    private static async Task<Photo> PhotoAsync(OdbcConnection cnx)
    {
        async Task<string> Lire(string sql)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("", OprEssai);
            await using var rd = await cmd.ExecuteReaderAsync();
            var r = new List<string>();
            while (await rd.ReadAsync()) r.Add(string.Join("/", Enumerable.Range(0, rd.FieldCount).Select(i => Convert.ToString(rd.GetValue(i))!.Trim())));
            return string.Join(";", r);
        }
        return new Photo(
            await Lire("SELECT ODLIVR, ODBL, ODQTEX FROM NP11F.OPR015 WHERE RSOC = 8 AND ODNUM = ? ORDER BY ODNLIG"),
            await Lire("SELECT OESTAT FROM NP11F.OPR010 WHERE RSOC = 8 AND OENUM = ?"),
            await Lire("SELECT NUMERO_SSCC, STATUT, NUMERO_BL FROM NP11F.PROD_ENTITE WHERE SOCIETE = 8 AND NUMERO_OPR = ? ORDER BY NUMERO_SSCC"),
            await Lire("SELECT NUMERO_COLISAGE, NUMERO_BL, ELEMENT_BL FROM NP11F.PROD_COLISAGE WHERE SOCIETE = 8 AND NUMERO_OPR = ? ORDER BY NUMERO_COLISAGE"),
            await Lire("""
                SELECT n.DNNLIG, n.DNQTEL, n.DNQTER, n.DNSOLD FROM NP11F.NEG100 n JOIN NP11F.OPR015 l ON l.RSOC = n.RSOC AND l.ODNCDE = n.RDOSS
                AND l.ODNCLE = n.DNDCLE AND l.ODDLIG = n.DNNLIG WHERE l.RSOC = 8 AND l.ODNUM = ? ORDER BY n.DNNLIG
                """),
            await Lire("""
                SELECT a.CCTAGS, a.CCQTEL FROM NP11F.AB001 a WHERE a.RSOC = 8 AND (a.RDOSS, a.CCDCLE) IN
                (SELECT ODNCDE, ODNCLE FROM NP11F.OPR015 WHERE RSOC = 8 AND ODNUM = ?)
                """));
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
}
