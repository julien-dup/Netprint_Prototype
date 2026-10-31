using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Saisie des BL (cahier des charges lot 1) : règles sans base, puis création d'un BL d'essai de bout en bout
public class SaisieBlTests(ITestOutputHelper sortie)
{
    private static readonly AdresseBl Clarins = new(0, "", "CLARINS", "12 AV", "", "95000", "PONTOISE", "95", "FR");

    private static LigneProposee Proposee(int ligne, AdresseBl? adresse = null, int clientFacture = 376) =>
        new(ligne, "ART", "", "ETUI", 1000, 0, 1000, false, adresse ?? Clarins, 411100, clientFacture, null, null);

    private static LigneSaisieBl Ligne(int quantite = 1000, int emballage = 300, bool stock = true) => new()
    {
        NumeroLigneCommande = 1, Article = "ART", Quantite = quantite, Reste = 1000, TypeEmballage = "C", EmballagePar = emballage,
        PoidsUc = 2.5m, GereEnStock = stock, TypeStock = "P1",
    };

    private static SaisieBl Saisie(params LigneSaisieBl[] lignes) => new()
    {
        Dossier = 12345, Cle = "A", Adresse = Clarins, TypeLivraison = "M", Transporteur = 5, TypePort = "F", Bareme = 1,
        Lignes = [.. lignes],
    };

    [Fact]
    public void Selection_meme_adresse_et_meme_client_facture()
    {
        Assert.Equal("Choisissez au moins une ligne à livrer.", ReglesSaisieBl.ControlerSelection([]));
        Assert.Null(ReglesSaisieBl.ControlerSelection([Proposee(1), Proposee(2)]));
        Assert.Contains("même adresse", ReglesSaisieBl.ControlerSelection([Proposee(1), Proposee(2, Clarins with { Ville = "PARIS" })]));
        Assert.Contains("même client facturé", ReglesSaisieBl.ControlerSelection([Proposee(1), Proposee(2, clientFacture: 20)]));
    }

    [Fact]
    public void Lignes_cochees_par_defaut()
    {
        var demande = Proposee(3) with { ADemande = true };
        Assert.Equal([1, 2], ReglesSaisieBl.LignesCocheesParDefaut([Proposee(1), Proposee(2), demande]));
        // Même libellé mais autre n° d'adresse : aucune ligne cochée d'office
        Assert.Empty(ReglesSaisieBl.LignesCocheesParDefaut([Proposee(1), Proposee(2, Clarins with { Numero = 2 })]));
        Assert.Empty(ReglesSaisieBl.LignesCocheesParDefaut([demande]));
    }

    [Fact]
    public void Colisage_poids_et_totaux()
    {
        var l = Ligne();
        ReglesSaisieBl.Coliser(l);
        Assert.Equal((3, 1, 100), (l.Colis, l.ColisIncomplet, l.Mecompte));
        Assert.Equal(3 * 2.5m + 2.5m * 100 / 300, l.Poids);
        var s = Saisie(l);
        s.Palettes = 2;
        Assert.Equal((8, 4), ReglesSaisieBl.Totaux(s, false, 25));
        Assert.Equal((58, 4), ReglesSaisieBl.Totaux(s, true, 25));   // + 2 palettes × 25 kg, recalcul complet

        l.TypeEmballage = "S";
        ReglesSaisieBl.Coliser(l);
        Assert.Equal((0, 0, 0, 0), (l.Colis, l.ColisIncomplet, l.Mecompte, l.EmballagePar));
    }

    [Fact]
    public void Controles_de_saisie()
    {
        var l = Ligne();
        ReglesSaisieBl.Coliser(l);
        var (erreurs, avertissements) = ReglesSaisieBl.Controler(Saisie(l));
        Assert.Contains(erreurs, x => x.Contains("répartissez toute la quantité"));
        Assert.Empty(avertissements);

        l.Prises.Add(new PriseLot { Depot = 1, Lot = "12345A", Quantite = 1000 });
        Assert.Empty(ReglesSaisieBl.Controler(Saisie(l)).Erreurs);

        // Quantité en UC : multiple exact de l'emballage, jamais arrondie
        l.QuantiteEnUc = true;
        Assert.Contains(ReglesSaisieBl.Controler(Saisie(l)).Erreurs, x => x.Contains("multiple de l'emballage (300)"));
        l.QuantiteEnUc = false;

        // Non fractionnable : pas de colis incomplet
        l.Fractionnable = false;
        Assert.Contains(ReglesSaisieBl.Controler(Saisie(l)).Erreurs, x => x.Contains("non fractionnable"));
        l.Fractionnable = true;

        // Plus que le reste : avertissement seulement
        l.Quantite = 1200;
        l.Prises[0].Quantite = 1200;
        ReglesSaisieBl.Coliser(l);
        var c = ReglesSaisieBl.Controler(Saisie(l));
        Assert.Empty(c.Erreurs);
        Assert.Single(c.Avertissements);

        // En-tête : enlèvement sans transporteur ni mode ; sinon obligatoires
        var s = Saisie(l);
        (s.Transporteur, s.TypeLivraison) = (0, "");
        Assert.Equal(2, ReglesSaisieBl.Controler(s).Erreurs.Count);
        s.TypePort = "E";
        Assert.Empty(ReglesSaisieBl.Controler(s).Erreurs);

        // Aucune quantité
        Assert.Contains(ReglesSaisieBl.Controler(Saisie(Ligne(0))).Erreurs, x => x.Contains("Au moins une ligne"));
    }

    [Fact]
    public void Disponible_tient_compte_des_autres_lignes_du_BL()
    {
        var a = Ligne(400);
        a.Prises.Add(new PriseLot { Depot = 1, Lot = "L1", Emplacement = "E1", Quantite = 400 });
        var b = Ligne(500);
        var s = Saisie(a, b);
        var positions = new[] { new PositionStock(1, "L1", "E1", null, null, 1000), new PositionStock(1, "L2", "E1", null, null, 50) };
        var dispo = ReglesSaisieBl.Disponibles(s, b, positions);
        Assert.Equal([600, 50], dispo.Select(x => x.Stock));
        Assert.Equal([1000, 50], ReglesSaisieBl.Disponibles(s, a, positions).Select(x => x.Stock));   // sa propre prise ne compte pas

        // Un seul lot : pris d'office dans la limite du disponible
        var c = Ligne(700);
        ReglesSaisieBl.PrendreSiUnSeulLot(c, [positions[0] with { Stock = 600 }]);
        Assert.Equal(600, Assert.Single(c.Prises).Quantite);
    }

    [Fact]
    public void Textes_de_la_ligne_de_commande_par_trois()
    {
        var textes = ReglesSaisieBl.LignesDeTexte(["a", "b", "c", "d"]).ToList();
        Assert.Equal(2, textes.Count);
        Assert.All(textes, t => Assert.True(t.Texte));
        Assert.Equal(("a", "b", "c"), (textes[0].Designation, textes[0].Designation2, textes[0].Designation3));
        Assert.Equal(("d", ""), (textes[1].Designation, textes[1].Designation2));
    }

    [Fact]
    public void Message_de_stock_insuffisant()
    {
        Assert.Equal("Stock insuffisant : article PQA002, lot 693201, emplacement 888, dépôt 1 — le stock deviendrait négatif le 16/07/2024.",
            SaisieBlDb2.MessageStock("PQA002", "693201", "888", 1, 20240716));
    }

    // ---------- Base de test ----------
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static SaisieBlService Service() => new(new SaisieBlDb2(BaseDeTest.Configuration), new Utilisateur());

    [FactDb2]
    public async Task Dossier_inexistant_et_lignes_proposees()
    {
        await Assert.ThrowsAsync<RegleMetierException>(() => Service().PreparerAsync(99999, "Z"));
        var p = await Service().PreparerAsync(69320, "9");
        Assert.NotEmpty(p.Lignes);
        foreach (var l in p.Lignes)
            sortie.WriteLine($"{l.NumeroLigne} {l.Article} reste {l.Reste} {(l.ADemande ? "à la demande" : "")} {l.Adresse.Libelle} fact. {l.CompteClientFacture}");
        Assert.Contains(p.Lignes, l => l.ADemande);
    }

    // Création d'un BL sur la ligne 4 du dossier d'essai 69320/9 (PQA002, lot 693201), daté dans la période de stock ouverte,
    // puis remise en état de toutes les tables touchées
    [FactDb2Ecriture]
    public async Task Creation_d_un_BL_de_bout_en_bout()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var avant = await LigneCommandeAsync(cnx);
        var service = Service();
        var s = await service.OuvrirAsync(69320, "9", [4]);
        var concurrent = await service.OuvrirAsync(69320, "9", [4]);   // second poste, ouvert avant la validation du premier
        var ligne = Assert.Single(s.Lignes, l => !l.Texte);
        Assert.Equal("PQA002", ligne.Article);
        s.DateExpedition = new DateOnly(2024, 10, 15);   // période P1 ouverte : 09/2024 (+ mois suivant)
        ligne.Quantite = 1500;
        var positions = await service.PositionsAsync(s, ligne);
        sortie.WriteLine(string.Join(" | ", positions.Select(x => $"{x.Lot} {x.Emplacement} {x.Stock}")));
        ligne.Prises = [new PriseLot { Depot = positions[0].Depot, Lot = positions[0].Lot, Emplacement = positions[0].Emplacement, Quantite = 1500 }];
        s.TextePied = ["TEST NETPRINT SAAS - A SUPPRIMER"];
        var controle = await service.ControlerAsync(s);
        Assert.True(controle.Valide, string.Join(" / ", controle.Erreurs));

        var numero = 0;
        try
        {
            numero = await service.CreerAsync(s);
            sortie.WriteLine($"BL {numero}");
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", numero));
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNOBL = ? AND HNODOS = 69320", numero));
            Assert.Equal(1500, await CompterAsync(cnx, "SELECT SUM(DQTE) FROM NP11F.EXP015 WHERE RSOC = 8 AND DNBL = ?", numero));
            Assert.Equal(1500, await CompterAsync(cnx, "SELECT SUM(DNQTPL) FROM NP11F.EXP016 WHERE RSOC = 8 AND DNBL = ?", numero));
            Assert.Equal(1500, await CompterAsync(cnx, "SELECT SUM(MVQTUS) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'EX'", numero));
            // Stock recalculé par STOREVNAV après la validation : l'emplacement a perdu la quantité livrée
            Assert.Equal(positions[0].Stock - 1500, await CompterAsync(cnx,
                "SELECT PEQTE FROM NP11F.AP00F WHERE RSOC = 8 AND PETSTO = 'P1' AND PEREF = 'PQA002' AND PELOT = ? AND PEEMP = ?",
                positions[0].Lot, positions[0].Emplacement));
            // Le second poste livre 1 000 alors qu'il ne reste plus que 500 : avertissement, rien n'est écrit
            concurrent.DateExpedition = s.DateExpedition;
            var lc = concurrent.Lignes.Single(l => !l.Texte);
            lc.Quantite = 1000;
            lc.Prises = [new PriseLot { Depot = positions[0].Depot, Lot = positions[0].Lot, Emplacement = positions[0].Emplacement, Quantite = 1000 }];
            var depassement = await Assert.ThrowsAsync<DepassementResteException>(() => service.CreerAsync(concurrent));
            sortie.WriteLine(depassement.Message);
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNODOS = 69320 AND HCLEF = '9' AND HNOBL >= ?", numero));
            var apres = await LigneCommandeAsync(cnx);
            Assert.Equal(avant.Livre + 1500, apres.Livre);
            Assert.Equal(apres.Commande - apres.Livre, apres.Reste);

            // Suppression du BL (dernier élément) : tout ce que le BL avait mis à jour est rétabli
            Assert.True(await service.SupprimerAsync(numero, 1));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP015 WHERE RSOC = 8 AND DNBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PRTLCK WHERE PLPROF = 'NP11'"));
            Assert.Equal(avant, await LigneCommandeAsync(cnx) with { Solde = avant.Solde });
            Assert.Equal(positions[0].Stock, await CompterAsync(cnx,
                "SELECT PEQTE FROM NP11F.AP00F WHERE RSOC = 8 AND PETSTO = 'P1' AND PEREF = 'PQA002' AND PELOT = ? AND PEEMP = ?",
                positions[0].Lot, positions[0].Emplacement));
            await Executer(cnx, "DELETE FROM NP11F.EVE200 WHERE RSOC = 8 AND AMBL = ?", numero);   // traces d'audit de l'essai
            numero = 0;
        }
        finally
        {
            if (numero > 0) await NettoyerAsync(cnx, numero, avant);
        }
    }

    private static async Task<(int Commande, int Livre, int Reste, string Solde, int QteLivreeDossier)> LigneCommandeAsync(OdbcConnection cnx)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = """
            SELECT n.DNQTEC, n.DNQTEL, n.DNQTER, n.DNSOLD, a.CCQTEL FROM NP11F.NEG100 n
            JOIN NP11F.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            WHERE n.RSOC = 8 AND n.RDOSS = 69320 AND n.DNDCLE = '9' AND n.DNNLIG = 4
            """;
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        return (Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)), Convert.ToInt32(rd.GetValue(2)),
            Convert.ToString(rd.GetValue(3))!, Convert.ToInt32(rd.GetValue(4)));
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

    // Secours si la suppression échoue : remise en état directe
    private static async Task NettoyerAsync(OdbcConnection cnx, int numero,
        (int Commande, int Livre, int Reste, string Solde, int QteLivreeDossier) avant)
    {
        async Task Executer(string sql, params object[] valeurs)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = sql;
            foreach (var v in valeurs) cmd.Parameters.AddWithValue("", v);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (table, colonne) in new[] { ("A700F", "MVBL"), ("EXP016", "DNBL"), ("EXP015", "DNBL"), ("EXP012", "HNOBL"),
                     ("EXP010", "HNOBL"), ("EXP009", "HNOBL"), ("EVE200", "AMBL") })
            await Executer($"DELETE FROM NP11F.{table} WHERE RSOC = 8 AND {colonne} = ?", numero);
        await Executer("UPDATE NP11F.NEG100 SET DNQTEL = ?, DNQTER = ?, DNSOLD = ? WHERE RSOC = 8 AND RDOSS = 69320 AND DNDCLE = '9' AND DNNLIG = 4",
            avant.Livre, avant.Reste, avant.Solde == "" ? " " : avant.Solde);
        await Executer("UPDATE NP11F.AB001 SET CCQTEL = ? WHERE RSOC = 8 AND RDOSS = 69320 AND CCDCLE = '9'", avant.QteLivreeDossier);
        // Positions de stock recalculées sans le BL
        var parametre = "0008P1001PQA002".PadRight(26).PadRight(256);
        await Executer($"CALL QSYS2.QCMDEXC('CALL PGM(NP11P/STOREVNAV) PARM(''{parametre}'')')");
    }
}
