using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Intégration DB2 de la saisie d'en-tête : lectures (toujours), création / modification réelles (sur demande)
public class SaisieCommandesDb2Tests(ITestOutputHelper sortie)
{
    private static readonly SaisieCommandesDb2 Donnees = new(BaseDeTest.Configuration);

    private static SaisieCommandesService Service() => new(Donnees, new ConsultationCommandesDb2(BaseDeTest.Configuration),
        new Utilisateur());

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    [FactDb2]
    public async Task Contexte_et_preparation_du_client_d_essai()
    {
        var contexte = await Service().LireContexteAsync();
        Assert.True(contexte.GereEnseignes);
        Assert.NotEmpty(contexte.Enseignes);
        var e = await Service().PreparerAsync(new DebutCommande
        {
            CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
        });
        Assert.Equal("$2", e.Categorie);
        Assert.Equal("LABORATOIRES CLARINS", e.NomClient);
        sortie.WriteLine($"rep {e.Representant} taux {e.TauxCommission} fab {e.Fabricant} code vente {e.CodeVente} " +
            $"interlocuteur « {e.Interlocuteur} » adresse ARC {e.AdresseArc} port {e.Port}");
    }

    [FactDb2]
    public async Task Controles_de_reference_s_executent()
    {
        var s = SaisieCommandesServiceTests.Valide();
        s.AdresseArc = 1;
        s.ModeReglement = "VI";
        s.CompteGeneralFacture = BaseDeTest.CompteGeneral;
        s.CompteClientFacture = BaseDeTest.CompteClient;
        var r = await Donnees.VerifierAsync(BaseDeTest.Societe, s, default);
        Assert.NotNull(r.SemaineDeLaDate);
        Assert.True(r.RepresentantExiste);
        Assert.True(r.ClientFactureExiste);
        sortie.WriteLine(r.ToString());
    }

    // Changement de client, date, enseigne (F17 de DOS505) d'une commande d'essai, passée un moment à un autre client puis supprimée.
    // Une commande livrée et facturée du client d'essai est refusée (contrôle fait avant toute écriture).
    [FactDb2Ecriture]
    public async Task Changement_de_client_d_une_commande()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var client = await LireDatesClientAsync(cnx);
        CleCommande? cle = null;
        try
        {
            var service = Service();
            var changement = new ChangementClientService(new ChangementClientDb2(BaseDeTest.Configuration), Donnees, service,
                new ConsultationCommandesDb2(BaseDeTest.Configuration), new Utilisateur());
            int autreGen, autreCli;
            await using (var cmd = new OdbcCommand("""
                SELECT RGEN, RCLI FROM NP11F.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI <> ? AND CDFERM = 0 AND CTAG <> '*' ORDER BY RCLI FETCH FIRST 1 ROW ONLY
                """, cnx))
            {
                cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                cmd.Parameters.AddWithValue("g", BaseDeTest.CompteGeneral);
                cmd.Parameters.AddWithValue("c", BaseDeTest.CompteClient);
                await using var rd = await cmd.ExecuteReaderAsync();
                Assert.True(await rd.ReadAsync());
                (autreGen, autreCli) = (Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)));
            }

            // Commande en cours du client d'essai qui a un BL : refus (si le jeu d'essai en a une)
            CleCommande? avecBl = null;
            await using (var cmd = new OdbcCommand("""
                SELECT a.RDOSS, a.CCDCLE FROM NP11F.AB001 a WHERE a.RSOC = ? AND a.RGEN = ? AND a.RCLI = ? AND a.CCTYPC IN ('3', '4')
                  AND a.CCTAGS = ' ' AND a.CCOP10 <> 'O' AND a.CCOP11 <> 'O'
                  AND EXISTS (SELECT 1 FROM NP11F.EXP010 x WHERE x.RSOC = a.RSOC AND x.HGENE = a.RGEN AND x.HCLIE = a.RCLI
                                AND x.HNODOS = a.RDOSS AND x.HCLEF = a.CCDCLE)
                FETCH FIRST 1 ROW ONLY
                """, cnx))
            {
                cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                cmd.Parameters.AddWithValue("g", BaseDeTest.CompteGeneral);
                cmd.Parameters.AddWithValue("c", BaseDeTest.CompteClient);
                await using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync()) avecBl = new CleCommande(Convert.ToInt32(rd.GetValue(0)), Convert.ToString(rd.GetValue(1))!.Trim());
            }
            if (avecBl is null) sortie.WriteLine("Aucune commande en cours avec BL pour le client d'essai : refus BL non essayé.");
            else
            {
                var avant = (await service.LireAsync(avecBl))!;
                var livree = await Assert.ThrowsAsync<SaisieInvalideException>(() => changement.ChangerAsync(avecBl,
                    new ChangementClient { CompteGeneral = autreGen, CompteClient = autreCli, Enseigne = avant.Enseigne, DateDossier = avant.DateDossier }));
                sortie.WriteLine($"{avecBl.Libelle} : {string.Join(" / ", livree.Erreurs.Select(x => x.Message))}");
                Assert.Contains(livree.Erreurs, x => x.Message == MessagesChangementClient.BlExistant);
            }

            var e = await service.PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            (e.Designation1, e.DateLivraison, e.Interlocuteur, e.Fabricant) =
                ("TEST NETPRINT SAAS CHANGEMENT CLIENT - A SUPPRIMER", DateOnly.FromDateTime(DateTime.Today.AddDays(15)), "TEST SAAS", "LEN");
            cle = await service.CreerAsync(e);
            var lue = (await service.LireAsync(cle))!;

            // Client inexistant (n° vérifié absent : 9999999 existe dans le jeu d'essai)
            int inexistant;
            await using (var cmd = new OdbcCommand("""
                SELECT MIN(c.RCLI + 1) FROM NP11F.AA0003 c WHERE c.RSOC = ? AND c.RGEN = ?
                  AND NOT EXISTS (SELECT 1 FROM NP11F.AA0003 d WHERE d.RSOC = c.RSOC AND d.RGEN = c.RGEN AND d.RCLI = c.RCLI + 1)
                """, cnx))
            {
                cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                cmd.Parameters.AddWithValue("g", BaseDeTest.CompteGeneral);
                inexistant = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            var ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => changement.ChangerAsync(cle,
                new ChangementClient { CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = inexistant, Enseigne = lue.Enseigne, DateDossier = lue.DateDossier }));
            Assert.Equal(nameof(ChangementClient.CompteClient), Assert.Single(ex.Erreurs).Zone);

            // Autre client : avertissement éventuel sur les n° d'adresse, puis changement accepté
            var vers = new ChangementClient { CompteGeneral = autreGen, CompteClient = autreCli, Enseigne = lue.Enseigne, DateDossier = lue.DateDossier };
            try { await changement.ChangerAsync(cle, vers); }
            catch (SaisieInvalideException avert) when (avert.Forcable)
            {
                sortie.WriteLine($"Avertissement : {avert.Erreurs[0].Message}");
                vers.AvertissementAccepte = true;
                await changement.ChangerAsync(cle, vers);
            }
            Assert.Equal((autreGen, autreCli), ((await service.LireAsync(cle))!.CompteGeneral, (await service.LireAsync(cle))!.CompteClient));

            // Retour au client d'essai avec une autre date
            var date = lue.DateDossier.AddDays(-1);
            await changement.ChangerAsync(cle, new ChangementClient
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, Enseigne = lue.Enseigne, DateDossier = date,
                AvertissementAccepte = true,
            });
            var relue = (await service.LireAsync(cle))!;
            Assert.Equal((BaseDeTest.CompteClient, date), (relue.CompteClient, relue.DateDossier));
        }
        finally
        {
            if (cle is not null) await NettoyerAsync(cnx, cle, client);
        }
    }

    // Paramètres de facturation automatique (F13 de DOS505, AB001FA) d'une commande d'essai : lecture, contrôles, écriture
    [FactDb2Ecriture]
    public async Task Parametres_de_facturation_automatique()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var client = await LireDatesClientAsync(cnx);
        CleCommande? cle = null;
        try
        {
            var service = Service();
            var e = await service.PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            (e.Designation1, e.DateLivraison, e.Interlocuteur, e.Fabricant) =
                ("TEST NETPRINT SAAS FACTURATION - A SUPPRIMER", DateOnly.FromDateTime(DateTime.Today.AddDays(15)), "TEST SAAS", "LEN");
            cle = await service.CreerAsync(e);
            var facturation = new FacturationAutomatiqueService(new FacturationAutomatiqueDb2(BaseDeTest.Configuration), service,
                new ConsultationCommandesDb2(BaseDeTest.Configuration), new Utilisateur());

            var f = (await facturation.LireAsync(cle))!;
            sortie.WriteLine($"Commande {cle.Libelle} : auto {f.Automatique}, adresse {f.NumeroAdresse}, règlement {f.ConditionsReglement}, texte {f.TextePied}");

            // N° d'adresse inexistant, n° et adresse ensemble, origine inconnue : refusés
            f.NumeroAdresse = 9999;
            var ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => facturation.EnregistrerAsync(cle, f));
            Assert.Contains(ex.Erreurs, x => x.Message == MessagesFacturation.AdresseInexistante);
            f.Nom = "CLARINS COMPTABILITE";
            ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => facturation.EnregistrerAsync(cle, f));
            Assert.Contains(ex.Erreurs, x => x.Message == MessagesFacturation.AdresseOuNumero);
            (f.NumeroAdresse, f.OrigineTechnique) = (null, "ZZZ");
            ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => facturation.EnregistrerAsync(cle, f));
            Assert.Equal(nameof(FacturationAutomatique.OrigineTechnique), Assert.Single(ex.Erreurs).Zone);

            // Adresse saisie, acompte, conditions du donneur d'ordre : écrits puis relus
            (f.OrigineTechnique, f.Automatique, f.Rue1, f.CodePostal, f.Ville, f.Attention, f.Acompte, f.ConditionsReglement, f.EditionDetailBl) =
                ("", false, "1 rue des Tests", "76000", "ROUEN", "Service comptable", 150.5m, ConditionsFacturation.DonneurOrdre, true);
            await facturation.EnregistrerAsync(cle, f);
            var lue = (await facturation.LireAsync(cle))!;
            Assert.Equal((false, (int?)null, "CLARINS COMPTABILITE", "ROUEN", "Service comptable", (decimal?)150.5m, "1", true),
                (lue.Automatique, lue.NumeroAdresse, lue.Nom, lue.Ville, lue.Attention, lue.Acompte, lue.ConditionsReglement, lue.EditionDetailBl));

            // Fenêtre restée ouverte pendant une autre modification : refus
            f.Attention = "AUTRE";
            await Assert.ThrowsAsync<RegleMetierException>(() => facturation.EnregistrerAsync(cle, f));
        }
        finally
        {
            if (cle is not null) await NettoyerAsync(cnx, cle, client);
        }
    }

    [FactDb2Ecriture]
    public async Task Creation_puis_modification_d_un_en_tete()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var client = await LireDatesClientAsync(cnx);
        CleCommande? cle = null;
        try
        {
            var service = Service();
            var e = await service.PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            e.Designation1 = "TEST NETPRINT SAAS - A SUPPRIMER";
            e.DateLivraison = DateOnly.FromDateTime(DateTime.Today.AddDays(15));
            e.ReferenceCommandeClient = "TEST-SAAS";
            e.Interlocuteur = "TEST SAAS";      // obligatoire pour ce client (CDOPT6 = 'O')
            e.Fabricant = "LEN";                // le fabricant du client (LE) n'existe plus dans AI00F
            cle = await service.CreerAsync(e);
            sortie.WriteLine($"Commande créée : {cle.Libelle}");

            var lue = await service.LireAsync(cle);
            Assert.NotNull(lue);
            Assert.Equal("TEST NETPRINT SAAS - A SUPPRIMER", lue.Designation1);
            Assert.Equal("TEST-SAAS", lue.ReferenceCommandeClient);
            Assert.Equal("$2", lue.Categorie);
            Assert.False(lue.AvecLignes);

            lue.Designation2 = "MODIFIEE";
            await service.ModifierAsync(cle, lue);
            Assert.Equal("MODIFIEE", (await service.LireAsync(cle))!.Designation2);

            // Lecture périmée : refus
            await Assert.ThrowsAsync<RegleMetierException>(() => service.ModifierAsync(cle, lue));

            // Texte complémentaire (AB0011) : créé, imprimé sur l'ARC seulement si la case est cochée
            var avecTexte = (await service.LireAsync(cle))!;
            avecTexte.TexteComplementaire = "Livraison sur rendez-vous\nQuai n° 3";
            avecTexte.TexteSurArc = true;
            await service.ModifierAsync(cle, avecTexte);
            var relue = (await service.LireAsync(cle))!;
            Assert.Equal(("Livraison sur rendez-vous\nQuai n° 3", true, false), (relue.TexteComplementaire, relue.TexteSurArc, relue.TexteSurBl));
            var arc = await new ArcCommandeDb2(BaseDeTest.Configuration).LireAsync(BaseDeTest.Societe, cle, null, default);
            Assert.Equal(["Livraison sur rendez-vous", "Quai n° 3"], arc!.TexteComplementaire);
            relue.TexteSurArc = false;
            await service.ModifierAsync(cle, relue);
            Assert.Empty((await new ArcCommandeDb2(BaseDeTest.Configuration).LireAsync(BaseDeTest.Societe, cle, null, default))!.TexteComplementaire);

            // La commande apparaît dans la consultation
            var fiche = await new ConsultationCommandesService(new ConsultationCommandesDb2(BaseDeTest.Configuration), new Utilisateur())
                .LireAsync(cle);
            Assert.NotNull(fiche);
            Assert.Equal(EtatCommande.EnCours, fiche.Entete.Etat);
        }
        finally
        {
            if (cle is not null) await NettoyerAsync(cnx, cle, client);
        }
    }

    // Demande d'OF (module Production) pour une commande d'essai : n° de la commande + clé libre, en-tête AB001T, lien
    // AB0020, modèle attribué ; modification, lecture, suppression. Le compteur des modèles (NK) consomme un n°.
    [FactDb2Ecriture]
    public async Task Demande_d_OF_pour_une_commande()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var client = await LireDatesClientAsync(cnx);
        CleCommande? cle = null;
        var production = new NetPrint.Modules.Production.Services.InitialisationProductionService(
            new NetPrint.Modules.Production.Donnees.DemandesOfDb2(BaseDeTest.Configuration),
            new NetPrint.Modules.Production.Donnees.ReferencesProductionDb2(BaseDeTest.Configuration), new Utilisateur(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NetPrint.Modules.Production.Services.InitialisationProductionService>.Instance);
        var crees = new List<NetPrint.Modules.Production.Contrats.NumeroOf>();
        try
        {
            var e = await Service().PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            (e.Designation1, e.DateLivraison, e.Interlocuteur, e.Fabricant) = ("TEST NETPRINT SAAS OF - A SUPPRIMER",
                DateOnly.FromDateTime(DateTime.Today.AddDays(30)), "TEST SAAS", "LEN");
            cle = await Service().CreerAsync(e);
            var origine = new NetPrint.Modules.Production.Contrats.CommandeOrigine(cle.Numero, cle.Cle);
            var demande = Production.InitialisationProductionTests.Demande() with
            {
                Commande = origine, Categorie = "01", SousCategorie = "55", Designation1 = "TEST NETPRINT SAAS OF",
                DateDossier = DateOnly.FromDateTime(DateTime.Today), Devis = null,
            };

            // Clé calculée par GETCLE (les tests n'ont pas Production:CleOf = PremierChiffreLibre), sauf si un dossier de ce n°
            // l'a déjà (les n° d'essai repassent par d'anciens n°) : premier chiffre libre, comme GETDOSSIER
            var calculee = NetPrint.Noyau.Compteurs.NumerosDossier.CleCalculee(cle.Numero, BaseDeTest.CompteClient);
            var prise = false;
            await using (var cmd = new OdbcCommand("""
                SELECT 1 FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                UNION ALL SELECT 1 FROM NP11F.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                """, cnx))
            {
                foreach (var _ in new[] { 0, 1 })
                {
                    cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                    cmd.Parameters.AddWithValue("n", cle.Numero);
                    cmd.Parameters.AddWithValue("c", calculee);
                }
                prise = await cmd.ExecuteScalarAsync() is not null;
            }
            var of = await production.DemanderOfAsync(demande);
            crees.Add(of);
            sortie.WriteLine($"Commande {cle.Libelle} → OF {of.Libelle} (clé calculée {calculee}{(prise ? " déjà prise" : "")})");
            Assert.Equal(cle.Numero, of.Numero);
            if (prise) Assert.True(char.IsDigit(of.Cle[0]), of.Cle);
            else Assert.Equal(calculee, of.Cle);
            Assert.Equal(NetPrint.Modules.Production.Contrats.EtatOf.AOuvrir, await production.EtatAsync(of));

            var lue = (await production.LireDemandeOfAsync(of, origine))!;
            Assert.Equal(("TEST NETPRINT SAAS OF", "01", "1"), (lue.Demande.Designation1, lue.Demande.Categorie, lue.Demande.Enseigne));
            Assert.True(lue.Demande.Modele > 0);
            var lien = Assert.Single(lue.Demande.Articles);
            Assert.Equal(("393228", 10000, 10000), (lien.Article, lien.AFabriquer, lien.AAffecter));

            await production.ModifierDemandeOfAsync(of, lue.Demande with
            {
                Designation2 = "MODIFIEE", Articles = [lien with { AFabriquer = 12000, AAffecter = 8000 }],
            });
            var relue = (await production.LireDemandeOfAsync(of, origine))!;
            Assert.Equal(("MODIFIEE", 12000, 8000), (relue.Demande.Designation2, relue.Demande.Articles[0].AFabriquer, relue.Demande.Articles[0].AAffecter));

            // Une 2e demande pour la même commande prend la clé suivante
            var autre = await production.DemanderOfAsync(demande);
            crees.Add(autre);
            Assert.Equal(cle.Numero, autre.Numero);
            Assert.NotEqual(of.Cle, autre.Cle);

            await production.SupprimerDemandeOfAsync(of, origine);
            await production.SupprimerDemandeOfAsync(autre, origine);
            Assert.Equal(NetPrint.Modules.Production.Contrats.EtatOf.Inconnu, await production.EtatAsync(of));
            Assert.Null(await production.LireDemandeOfAsync(of, origine));
        }
        finally
        {
            if (cle is not null)
            {
                await using (var cmd = new OdbcCommand("DELETE FROM NP11F.AB0020 WHERE RSOC = ? AND LADOSC = ? AND LACLDC = ?", cnx))
                {
                    cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                    cmd.Parameters.AddWithValue("n", cle.Numero);
                    cmd.Parameters.AddWithValue("c", cle.Cle);
                    await cmd.ExecuteNonQueryAsync();
                }
                // Les OF créés, quel que soit leur n° (n° de la commande ou compteur)
                foreach (var of in crees)
                    await using (var cmd = new OdbcCommand("DELETE FROM NP11F.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", cnx))
                    {
                        cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                        cmd.Parameters.AddWithValue("n", of.Numero);
                        cmd.Parameters.AddWithValue("c", of.Cle);
                        await cmd.ExecuteNonQueryAsync();
                    }
                await NettoyerAsync(cnx, cle, client);
            }
        }
    }

    private static async Task<object[]> LireDatesClientAsync(OdbcConnection cnx)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = "SELECT CDDTPC, CDDTDC, CDDDOS, CDDCLE FROM NP11F.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?";
        cmd.Parameters.AddWithValue("a", BaseDeTest.Societe);
        cmd.Parameters.AddWithValue("b", BaseDeTest.CompteGeneral);
        cmd.Parameters.AddWithValue("c", BaseDeTest.CompteClient);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return [rd.GetValue(0), rd.GetValue(1), rd.GetValue(2), rd.GetValue(3)];
    }

    // Supprime tout ce que la création a écrit et remet la fiche client comme avant
    private async Task NettoyerAsync(OdbcConnection cnx, CleCommande cle, object[] client)
    {
        async Task Executer(string sql, params object[] valeurs)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = sql;
            foreach (var v in valeurs) cmd.Parameters.AddWithValue("p", v);
            sortie.WriteLine($"{await cmd.ExecuteNonQueryAsync()} ligne(s) : {sql.Split(' ')[0]} {sql.Split(' ')[2]}");
        }
        await Executer("DELETE FROM NP11F.NEG100E WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("DELETE FROM NP11F.DOS040 WHERE RSOC = ? AND RDOSS = ? AND HICLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("DELETE FROM NP11F.EVE200 WHERE RSOC = ? AND AMDOSS = ? AND AMDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("DELETE FROM NP11F.AB001FA WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("DELETE FROM NP11F.AB0011 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("DELETE FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("UPDATE NP11F.AA0003 SET CDDTPC = ?, CDDTDC = ?, CDDDOS = ?, CDDCLE = ? WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            client[0], client[1], client[2], client[3], BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient);
    }
}
