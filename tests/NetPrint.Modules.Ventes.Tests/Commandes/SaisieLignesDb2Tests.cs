using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Intégration DB2 de la saisie des lignes : création réelle d'une commande et de ses lignes (sur demande), nettoyage
public class SaisieLignesDb2Tests(ITestOutputHelper sortie)
{
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static readonly ConsultationCommandesDb2 Consultation = new(BaseDeTest.Configuration);
    private static readonly LignesCommandeDb2 Lignes = new(BaseDeTest.Configuration);

    [FactDb2Ecriture]
    public async Task Lignes_d_une_commande_de_bout_en_bout()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var article = await ArticleDuClientAsync(cnx);
        Assert.NotNull(article);
        var client = await ValeursAsync(cnx, "SELECT CDDTPC, CDDTDC, CDDDOS, CDDCLE FROM NP11F.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient);
        var adtcde = await ValeursAsync(cnx, "SELECT ADTCDE FROM NP11F.SPF010 WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?",
            BaseDeTest.Societe, article.Value.Article, article.Value.Indice);
        CleCommande? cle = null;
        try
        {
            // En-tête
            var entete = new SaisieCommandesService(new SaisieCommandesDb2(BaseDeTest.Configuration), Consultation, new Utilisateur());
            var e = await entete.PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            e.Designation1 = "TEST NETPRINT SAAS LIGNES - A SUPPRIMER";
            e.DateLivraison = DateOnly.FromDateTime(DateTime.Today.AddDays(20));
            e.Interlocuteur = "TEST SAAS";
            e.Fabricant = "LEN";
            cle = await entete.CreerAsync(e);
            sortie.WriteLine($"Commande {cle.Libelle}, article {article.Value.Article} {article.Value.Indice}");

            // Ligne : valeurs par défaut, compléments, avertissements acceptés
            var service = new SaisieLignesService(Lignes, Consultation, new Utilisateur());
            var l = await service.NouvelleAsync(cle);
            l.Article = article.Value.Article;
            l.Indice = article.Value.Indice;
            l.Quantite = 2500;
            l.PrixBrut = 49;
            l.PrixSaisi = true;
            l.DateLivraison = NextWorkingDay(DateOnly.FromDateTime(DateTime.Today.AddDays(15)));
            l.AdresseSiege = true;
            l = await service.CompleterAsync(cle, l);
            sortie.WriteLine($"Complétée : « {l.Designation} » {l.Unite} montant {l.Montant} cession {l.PrixCession} commission {l.TauxCommission}");
            l.AvertissementsAcceptes = [.. typeof(AvertissementsLigne).GetFields().Select(f => (string)f.GetValue(null)!)];
            var numero = await service.EnregistrerAsync(cle, l);
            Assert.Equal(1, numero);

            var fiche = await Consultation.LireFicheAsync(BaseDeTest.Societe, cle, null, default);
            Assert.NotNull(fiche);
            var ligne = Assert.Single(fiche.Lignes);
            Assert.Equal(2500, ligne.Ligne.Dnqtec);
            Assert.Equal(49m, ligne.Ligne.Dnpus);
            var total = await ValeursAsync(cnx, "SELECT CCPXVE, CCUNIT FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                BaseDeTest.Societe, cle.Numero, cle.Cle);
            sortie.WriteLine($"En-tête : total {total[0]}, unité {total[1]}");
            Assert.Equal("LT", Convert.ToString(total[1])!.Trim());

            // Modification de la quantité
            var lue = (await service.LireAsync(cle, 1))!;
            lue.Quantite = 5000;
            lue.AvertissementsAcceptes = l.AvertissementsAcceptes;
            await service.EnregistrerAsync(cle, lue);
            Assert.Equal(5000, (await service.LireAsync(cle, 1))!.Quantite);

            // Solde puis réactivation, puis suppression
            await service.SolderAsync(cle, 1);
            Assert.True((await service.LireAsync(cle, 1))!.Soldee);
            await service.ReactiverAsync(cle, 1);
            Assert.False((await service.LireAsync(cle, 1))!.Soldee);
            await service.SupprimerAsync(cle, 1);
            Assert.Null(await service.LireAsync(cle, 1));

            // Actions sur la commande : nouvelle ligne, solde, réactivation, suppression
            var l2 = l.Avec(x => { x.NumeroLigne = 0; x.Empreinte = ""; });
            Assert.Equal(1, await service.EnregistrerAsync(cle, l2));
            IActionsCommande actions = service;
            await actions.SolderAsync(cle);
            Assert.True((await service.LireAsync(cle, 1))!.Soldee);
            Assert.Equal("D", Convert.ToString((await ValeursAsync(cnx, "SELECT CCTAGS FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                BaseDeTest.Societe, cle.Numero, cle.Cle))[0])!.Trim());
            await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() => actions.SolderAsync(cle));
            await actions.ReactiverAsync(cle);
            await actions.SupprimerAsync(cle);
            Assert.Empty(await ValeursAsync(cnx, "SELECT 1 FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle));
            Assert.Empty(await ValeursAsync(cnx, "SELECT 1 FROM NP11F.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle));

            var journal = await ValeursAsync(cnx, "SELECT LISTAGG(DETYMO, '') WITHIN GROUP (ORDER BY DEDTHE) FROM NP11F.NEG100E WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?",
                BaseDeTest.Societe, cle.Numero, cle.Cle);
            sortie.WriteLine($"Journal NEG100E : {journal[0]}");
        }
        finally
        {
            if (cle is not null) await NettoyerAsync(cnx, cle, client, article!.Value, adtcde);
        }
    }

    // Lecture seule : une commande livrée et facturée du client d'essai serait refusée à la suppression
    [FactDb2]
    public async Task Commande_livree_non_supprimable()
    {
        var pieces = await Lignes.PiecesAsync(BaseDeTest.Societe, new CleCommande(68537, "9"), default);
        Assert.Equal((true, true), pieces);
        Assert.NotNull(SaisieLignesService.RefusSuppressionCommande(pieces.Bl, pieces.Facture));
    }

    [FactDb2Ecriture]
    public async Task Recalcul_des_reservations_par_STOCK03()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var avant = await ValeursAsync(cnx, "SELECT PRQRES FROM NP11F.AK00F WHERE RSOC = 8 AND PRTSTO = 'P1' AND PRDEP = 1 AND PRREF = 'PPG01' AND PRIND = ' '");
        await Lignes.RecalculerStockAsync(8, "P1", 1, "PPG01", "", default);
        var apres = await ValeursAsync(cnx, "SELECT PRQRES FROM NP11F.AK00F WHERE RSOC = 8 AND PRTSTO = 'P1' AND PRDEP = 1 AND PRREF = 'PPG01' AND PRIND = ' '");
        sortie.WriteLine($"PRQRES avant {avant.FirstOrDefault()}, après {apres.FirstOrDefault()}");
    }

    private static DateOnly NextWorkingDay(DateOnly d) =>
        d.DayOfWeek switch { DayOfWeek.Saturday => d.AddDays(2), DayOfWeek.Sunday => d.AddDays(1), _ => d };

    private static async Task<(string Article, string Indice)?> ArticleDuClientAsync(OdbcConnection cnx)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT a.AARTIC, a.AINDIC FROM NP11F.SPF010 a
            WHERE a.RSOC = ? AND a.ATYPEA = '2' AND a.ATYFAC = '1' AND a.AGEN = ? AND a.ACLI = ? AND a.AREMPL NOT IN ('C', 'O')
              AND a.AOPT4 <> 'O' AND a.AUNITE <> ' ' AND a.AARTIC NOT LIKE '999%'
              AND NOT EXISTS (SELECT 1 FROM NP11F.SPFNOM n WHERE n.RSOC = a.RSOC AND n.AARTIC = a.AARTIC AND n.AINDIC = a.AINDIC)
              AND EXISTS (SELECT 1 FROM NP11F.AA00083 u WHERE u.RSOC = a.RSOC AND u.RTABLE = 'UV' AND u.RARG = a.AUNITE)
            FETCH FIRST 1 ROW ONLY
            """;
        cmd.Parameters.AddWithValue("a", BaseDeTest.Societe);
        cmd.Parameters.AddWithValue("b", BaseDeTest.CompteGeneral);
        cmd.Parameters.AddWithValue("c", BaseDeTest.CompteClient);
        await using var rd = await cmd.ExecuteReaderAsync();
        return await rd.ReadAsync() ? (Convert.ToString(rd.GetValue(0))!.Trim(), Convert.ToString(rd.GetValue(1))!.Trim()) : null;
    }

    private static async Task<object[]> ValeursAsync(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.AddWithValue("p", v);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return [];
        var r = new object[rd.FieldCount];
        rd.GetValues(r);
        return r;
    }

    private async Task NettoyerAsync(OdbcConnection cnx, CleCommande cle, object[] client, (string Article, string Indice) article, object[] adtcde)
    {
        async Task Executer(string sql, params object[] valeurs)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = sql;
            foreach (var v in valeurs) cmd.Parameters.AddWithValue("p", v);
            sortie.WriteLine($"{await cmd.ExecuteNonQueryAsync()} ligne(s) : {sql[..Math.Min(40, sql.Length)]}");
        }
        object[] k = [BaseDeTest.Societe, cle.Numero, cle.Cle];
        await Executer("DELETE FROM NP11F.NEG100T WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?", k);
        await Executer("DELETE FROM NP11F.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?", k);
        await Executer("DELETE FROM NP11F.NEG100E WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?", k);
        await Executer("DELETE FROM NP11F.DOS040 WHERE RSOC = ? AND RDOSS = ? AND HICLE = ?", k);
        await Executer("DELETE FROM NP11F.EVE200 WHERE RSOC = ? AND AMDOSS = ? AND AMDCLE = ?", k);
        await Executer("DELETE FROM NP11F.AB0016 WHERE RSOC = ? AND RDOSS = ? AND F16CLE = ?", k);
        await Executer("DELETE FROM NP11F.AB001FA WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", k);
        await Executer("DELETE FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", k);
        await Executer("UPDATE NP11F.AA0003 SET CDDTPC = ?, CDDTDC = ?, CDDDOS = ?, CDDCLE = ? WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            client[0], client[1], client[2], client[3], BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient);
        if (adtcde.Length > 0)
            await Executer("UPDATE NP11F.SPF010 SET ADTCDE = ? WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?",
                adtcde[0], BaseDeTest.Societe, article.Article, article.Indice == "" ? " " : article.Indice);
    }
}
