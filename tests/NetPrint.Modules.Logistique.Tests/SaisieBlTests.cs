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
    public void Modification_disponible_et_variation_de_stock()
    {
        // Ligne enregistrée : 400 pris sur L1 ; l'utilisateur passe à 300 sur L1 et 200 sur L2
        var l = Ligne(500);
        l.PrisesEnregistrees = [new PriseLot { Depot = 1, Lot = "L1", Emplacement = "E1", Quantite = 400 }];
        l.Prises = [new PriseLot { Depot = 1, Lot = "L1", Emplacement = "E1", Quantite = 300 }, new PriseLot { Depot = 1, Lot = "L2", Emplacement = "E1", Quantite = 200 }];
        var s = Saisie(l);
        // L1 n'a plus de stock (tout est sur le BL) : la position reste proposée, avec la quantité enregistrée
        var dispo = ReglesSaisieBl.Disponibles(s, l, [new PositionStock(1, "L2", "E1", null, null, 500)]);
        Assert.Equal([("L2", 500), ("L1", 400)], dispo.Select(x => (x.Lot, x.Stock)));
        var v = ReglesSaisieBl.Variations(s);
        Assert.Equal(100, v[("P1", "ART", "", 1, "L1", "E1")]);    // rendu au stock
        Assert.Equal(-200, v[("P1", "ART", "", 1, "L2", "E1")]);   // à contrôler
        l.Quantite = 0;                                            // ligne retirée : tout est rendu
        Assert.Equal(400, Assert.Single(ReglesSaisieBl.Variations(s)).Value);
    }

    [Fact]
    public void Ajout_a_un_BL_meme_destinataire_et_meme_client_facture()
    {
        Assert.True(ReglesSaisieBl.VaAuBl(Proposee(1), Clarins with { Numero = 9 }, 411100, 376));   // le n° d'adresse ne compte pas
        Assert.False(ReglesSaisieBl.VaAuBl(Proposee(1), Clarins with { Ville = "PARIS" }, 411100, 376));
        Assert.False(ReglesSaisieBl.VaAuBl(Proposee(1), Clarins, 411100, 20));
    }

    [Fact]
    public void Port_tranche_tarif_et_repartition()
    {
        int[] tranches = [9, 19, 29, 100, 9999, 0];
        Assert.Equal(1, ReglesSaisieBl.Tranche(5, tranches));
        Assert.Equal(2, ReglesSaisieBl.Tranche(10, tranches));
        Assert.Equal(4, ReglesSaisieBl.Tranche(100, tranches));
        Assert.Equal(0, ReglesSaisieBl.Tranche(150_000, tranches));   // plafonné à 99 999, au-delà de la dernière tranche

        Assert.Equal(560.40m, ReglesSaisieBl.Port(10, new TarifPort(555, 10, 0, "F"), 1, 5.40m));    // forfait + timbre
        Assert.Equal(232.50m, ReglesSaisieBl.Port(250, new TarifPort(93, 0, 0, "C"), 1, 0));         // aux 100 kg
        Assert.Equal(25.00m, ReglesSaisieBl.Port(250, new TarifPort(93, 100, 0, "T"), 2, 0));        // barème 2, à la tonne
        Assert.Equal(0, ReglesSaisieBl.Port(250, new TarifPort(93, 0, 0, "K"), 2, 5));               // prix nul : pas de port

        Assert.Equal([33.33m, 66.67m], ReglesSaisieBl.RepartirAuPoids(100, [1, 2]));
        Assert.Equal([50m, 50m], ReglesSaisieBl.RepartirAuPoids(100, [0, 0]));
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
            numero = await service.ValiderAsync(s);
            sortie.WriteLine($"BL {numero}");
            sortie.WriteLine($"Port contrôlé : {controle.Port}");
            Assert.Equal(controle.Port is > 0 ? 1 : 0,
                await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A20005 WHERE RSOC = 8 AND RDOSS = 69320 AND S5DCLE = '9' AND S5NOBL = ?", numero));
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
            var depassement = await Assert.ThrowsAsync<DepassementResteException>(() => service.ValiderAsync(concurrent));
            sortie.WriteLine(depassement.Message);
            Assert.Equal(1, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNODOS = 69320 AND HCLEF = '9' AND HNOBL >= ?", numero));
            var apres = await LigneCommandeAsync(cnx);
            Assert.Equal(avant.Livre + 1500, apres.Livre);
            Assert.Equal(apres.Commande - apres.Livre, apres.Reste);
            const string Emplacement =
                "SELECT PEQTE FROM NP11F.AP00F WHERE RSOC = 8 AND PETSTO = 'P1' AND PEREF = 'PQA002' AND PELOT = ? AND PEEMP = ?";

            // Modification : 1 500 → 1 800 ; le disponible de la position compte la quantité déjà livrée par l'élément
            var m = await service.OuvrirElementAsync(numero, 1);
            Assert.True(m.Modification);
            var lm = m.Lignes.Single(l => !l.Texte);
            Assert.Equal((1500, avant.Reste), (lm.Quantite, lm.Reste));
            Assert.Equal(positions[0].Stock, (await service.PositionsAsync(m, lm)).Single(x => x.Lot == positions[0].Lot && x.Emplacement == positions[0].Emplacement).Stock);
            lm.Quantite = 1800;
            lm.Prises[0].Quantite = 1800;
            Assert.Equal(numero, await service.ValiderAsync(m));
            Assert.Equal(avant.Livre + 1800, (await LigneCommandeAsync(cnx)).Livre);
            Assert.Equal(1800, await CompterAsync(cnx, "SELECT SUM(MVQTUS) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ? AND MVCMVT = 'EX'", numero));
            Assert.Equal(positions[0].Stock - 1800, await CompterAsync(cnx, Emplacement, positions[0].Lot, positions[0].Emplacement));

            // Ajout d'un dossier au BL : le reste de la ligne (200) en second élément ; la ligne de commande est soldée
            var ajout = await service.PreparerAsync(69320, "9", numero);
            Assert.Contains(ajout.Lignes, l => l.NumeroLigne == 4 && l.Reste == avant.Reste - 1800);
            var a = await service.OuvrirAsync(69320, "9", [4], numero);
            Assert.True(a.Ajout);
            Assert.Equal(s.DateExpedition, a.DateExpedition);   // en-tête du BL repris
            Assert.Equal(avant.Reste - 1800, a.Lignes.Single(l => !l.Texte).Reparti);   // lot unique pris d'office
            Assert.Equal(numero, await service.ValiderAsync(a));
            Assert.Equal(2, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP010 WHERE RSOC = 8 AND HNOBL = ?", numero));
            var complete = await LigneCommandeAsync(cnx);
            Assert.Equal((complete.Commande, "O"), (complete.Livre, complete.Solde));

            // Suppression du second élément : le BL reste, ses totaux sont recalculés
            var imputations = controle.Port is > 0 ? 1 : 0;   // un même dossier sur deux éléments : une seule imputation
            Assert.Equal(imputations, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A20005 WHERE RSOC = 8 AND RDOSS = 69320 AND S5DCLE = '9' AND S5NOBL = ?", numero));
            Assert.False(await service.SupprimerAsync(numero, 2));
            Assert.Equal(imputations, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A20005 WHERE RSOC = 8 AND RDOSS = 69320 AND S5DCLE = '9' AND S5NOBL = ?", numero));   // port des dossiers restants recalculé
            Assert.Equal(avant.Livre + 1800, (await LigneCommandeAsync(cnx)).Livre);

            // Suppression du BL (dernier élément) : tout ce que le BL avait mis à jour est rétabli
            Assert.True(await service.SupprimerAsync(numero, 1));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP009 WHERE RSOC = 8 AND HNOBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.EXP015 WHERE RSOC = 8 AND DNBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A700F WHERE RSOC = 8 AND MVBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.A20005 WHERE RSOC = 8 AND S5NOBL = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PRTLCK WHERE PLPROF = 'NP11'"));
            Assert.Equal(avant, await LigneCommandeAsync(cnx) with { Solde = avant.Solde });
            Assert.Equal(positions[0].Stock, await CompterAsync(cnx, Emplacement, positions[0].Lot, positions[0].Emplacement));
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
        foreach (var (table, colonne) in new[] { ("A20005", "S5NOBL"), ("A700F", "MVBL"), ("EXP016", "DNBL"), ("EXP015", "DNBL"), ("EXP012", "HNOBL"),
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
