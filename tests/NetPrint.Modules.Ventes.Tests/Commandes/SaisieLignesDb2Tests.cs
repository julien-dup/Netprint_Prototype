using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Production.Contrats;
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

    // Appels de livraison : ligne « à la demande » de 10 000, appels créés, modifiés, supprimés ; reste de l'origine, solde,
    // report de prix
    [FactDb2Ecriture]
    public async Task Appels_de_livraison_de_bout_en_bout()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var article = await ArticleDuClientAsync(cnx);
        Assert.NotNull(article);
        var client = await ValeursAsync(cnx, "SELECT CDDTPC, CDDTDC, CDDDOS, CDDCLE FROM NP11F.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient);
        var adtcde = await ValeursAsync(cnx, "SELECT ADTCDE FROM NP11F.SPF010 WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?",
            BaseDeTest.Societe, article.Value.Article, article.Value.Indice);
        HashSet<string> tous = [.. typeof(AvertissementsLigne).GetFields().Select(f => (string)f.GetValue(null)!)];
        var date = NextWorkingDay(DateOnly.FromDateTime(DateTime.Today.AddDays(15)));
        CleCommande? cle = null;
        try
        {
            var entete = new SaisieCommandesService(new SaisieCommandesDb2(BaseDeTest.Configuration), Consultation, new Utilisateur());
            var e = await entete.PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            e.Designation1 = "TEST NETPRINT SAAS APPELS - A SUPPRIMER";
            e.DateLivraison = DateOnly.FromDateTime(DateTime.Today.AddDays(20));
            e.Interlocuteur = "TEST SAAS";
            e.Fabricant = "LEN";
            cle = await entete.CreerAsync(e);
            sortie.WriteLine($"Commande {cle.Libelle}");

            var service = new SaisieLignesService(Lignes, Consultation, new Utilisateur());
            IAppelsLivraison appels = service;
            async Task<(int Qteo, int Qtea, int Qtec, string Sold)> Origine()
            {
                var v = await ValeursAsync(cnx, "SELECT DNQTEO, DNQTEA, DNQTEC, DNSOLD FROM NP11F.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = 1",
                    BaseDeTest.Societe, cle.Numero, cle.Cle);
                return (Convert.ToInt32(v[0]), Convert.ToInt32(v[1]), Convert.ToInt32(v[2]), Convert.ToString(v[3])!.Trim());
            }

            // Ligne « à la demande » de 10 000
            var o = await service.NouvelleAsync(cle);
            (o.Article, o.Indice, o.Statut, o.Quantite, o.PrixBrut, o.PrixSaisi) = (article.Value.Article, article.Value.Indice, StatutsLigne.ALaDemande, 10000, 49, true);
            o.DateLivraison = date;
            o.AdresseSiege = true;
            o = await service.CompleterAsync(cle, o);
            o.AvertissementsAcceptes = tous;
            Assert.Equal(1, await service.EnregistrerAsync(cle, o));
            Assert.Equal((10000, 0, 10000, ""), await Origine());

            // Appel de 3000 (canal navigateur)
            var a = await appels.NouvelAsync(cle, 1);
            Assert.Equal(10000, a.ResteOrigine);
            (a.Quantite, a.DateLivraison, a.AvertissementsAcceptes) = (3000, date, tous);
            Assert.Equal(2, await appels.EnregistrerAsync(cle, a, CanalAppel.Navigateur));
            Assert.Equal((10000, 3000, 7000, ""), await Origine());
            var appel = await ValeursAsync(cnx, "SELECT DNSTAT, DNLORI, DNPUS, DNQTEO, DNQTEC FROM NP11F.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = 2",
                BaseDeTest.Societe, cle.Numero, cle.Cle);
            Assert.Equal(("1", 1, 49m, 0, 3000), (Convert.ToString(appel[0])!.Trim(), Convert.ToInt32(appel[1]), Convert.ToDecimal(appel[2]),
                Convert.ToInt32(appel[3]), Convert.ToInt32(appel[4])));

            // Dépassement du reste : avertissement à accepter
            var trop = await appels.NouvelAsync(cle, 1);
            (trop.Quantite, trop.DateLivraison) = (8000, date);
            trop.AvertissementsAcceptes = [.. tous.Where(x => x != AvertissementsLigne.DepassementOrigine)];
            var ex = await Assert.ThrowsAsync<SaisieInvalideException>(() => appels.EnregistrerAsync(cle, trop, CanalAppel.Saisie));
            Assert.True(ex.Forcable);
            Assert.Contains(ex.Erreurs, x => x.Zone == AvertissementsLigne.DepassementOrigine);

            // Modification de l'appel par la saisie des lignes (même moteur) : 3000 → 4000
            var lue = (await service.LireAsync(cle, 2))!;
            Assert.True(lue.EstAppel);
            Assert.Equal(7000, lue.ResteOrigine);
            (lue.Quantite, lue.AvertissementsAcceptes) = (4000, tous);
            await service.EnregistrerAsync(cle, lue);
            Assert.Equal((10000, 4000, 6000, ""), await Origine());

            // Nouveau prix de l'origine reporté sur l'appel
            var lo = (await service.LireAsync(cle, 1))!;
            (lo.PrixBrut, lo.AvertissementsAcceptes) = (55, tous);
            await service.EnregistrerAsync(cle, lo);
            Assert.Equal([2], await appels.ReporterPrixAsync(cle, 1));
            Assert.Equal(55m, Convert.ToDecimal((await ValeursAsync(cnx, "SELECT DNPUS FROM NP11F.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = 2",
                BaseDeTest.Societe, cle.Numero, cle.Cle))[0]));
            Assert.Empty(await appels.ReporterPrixAsync(cle, 1));
            sortie.WriteLine($"Solde du maxi : {await appels.CalculerMaxiAsync(cle, 1)}");

            // Appel du reste : origine soldée ; sa suppression la remet en cours
            var fin = await appels.NouvelAsync(cle, 1);
            (fin.Quantite, fin.DateLivraison, fin.AvertissementsAcceptes) = (6000, date, tous);
            Assert.Equal(3, await appels.EnregistrerAsync(cle, fin, CanalAppel.Edi));
            Assert.Equal((10000, 10000, 0, "O"), await Origine());
            await service.SupprimerAsync(cle, 3);
            Assert.Equal((10000, 4000, 6000, ""), await Origine());

            // L'origine ne se supprime pas tant qu'elle a des appels ; l'appel, si
            await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() => service.SupprimerAsync(cle, 1));
            await appels.SupprimerAsync(cle, 2, CanalAppel.Saisie);
            Assert.Equal((10000, 0, 10000, ""), await Origine());

            var journal = await ValeursAsync(cnx, "SELECT LISTAGG(DNNLIG || DETYMO, ' ') WITHIN GROUP (ORDER BY DEDTHE, DNNLIG) FROM NP11F.NEG100E WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?",
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

    // Lancement en fabrication (NEG515, NEG510 F10) d'une commande d'essai d'une ligne : valeurs proposées, contrôles,
    // lancement, modification, F10 refusé puis accepté, rattachement, affectation de stock s'il y en a, suppression
    [FactDb2Ecriture]
    public async Task Lancement_en_fabrication_de_bout_en_bout()
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var article = await ArticleDuClientAsync(cnx, avecStock: true);
        Assert.NotNull(article);
        var client = await ValeursAsync(cnx, "SELECT CDDTPC, CDDTDC, CDDDOS, CDDCLE FROM NP11F.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient);
        var adtcde = await ValeursAsync(cnx, "SELECT ADTCDE FROM NP11F.SPF010 WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?",
            BaseDeTest.Societe, article.Value.Article, article.Value.Indice);
        var production = new NetPrint.Modules.Production.Services.InitialisationProductionService(
            new NetPrint.Modules.Production.Donnees.DemandesOfDb2(BaseDeTest.Configuration),
            new NetPrint.Modules.Production.Donnees.ReferencesProductionDb2(BaseDeTest.Configuration), new Utilisateur(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NetPrint.Modules.Production.Services.InitialisationProductionService>.Instance);
        var lancement = new LancementCommandeService(new LancementCommandeDb2(BaseDeTest.Configuration), Consultation, production, new Utilisateur());
        var crees = new List<NumeroOf>();
        var commandes = new List<CleCommande>();
        var livraison = DateOnly.FromDateTime(DateTime.Today.AddDays(25));
        // Commande d'essai d'une ligne de l'article
        async Task<CleCommande> CommandeAsync(int quantite)
        {
            var entete = new SaisieCommandesService(new SaisieCommandesDb2(BaseDeTest.Configuration), Consultation, new Utilisateur());
            var e = await entete.PreparerAsync(new DebutCommande
            {
                CompteGeneral = BaseDeTest.CompteGeneral, CompteClient = BaseDeTest.CompteClient, TypeCommande = "4", Enseigne = "1",
            });
            (e.Designation1, e.DateLivraison, e.Interlocuteur, e.Fabricant) = ("TEST NETPRINT SAAS LANCEMENT - A SUPPRIMER", livraison, "TEST SAAS", "LEN");
            var c = await entete.CreerAsync(e);
            commandes.Add(c);
            var service = new SaisieLignesService(Lignes, Consultation, new Utilisateur());
            var l = await service.NouvelleAsync(c);
            (l.Article, l.Indice, l.Quantite, l.PrixBrut, l.PrixSaisi, l.AdresseSiege) = (article.Value.Article, article.Value.Indice, quantite, 49, true, true);
            l.DateLivraison = NextWorkingDay(DateOnly.FromDateTime(DateTime.Today.AddDays(15)));
            l = await service.CompleterAsync(c, l);
            l.AvertissementsAcceptes = [.. typeof(AvertissementsLigne).GetFields().Select(f => (string)f.GetValue(null)!)];
            await service.EnregistrerAsync(c, l);
            return c;
        }
        try
        {
            var cle = await CommandeAsync(2500);
            var reference = new ReferenceArticle(article.Value.Article, article.Value.Indice);
            sortie.WriteLine($"Commande {cle.Libelle}, article {reference.Article} {reference.Indice}");

            async Task<ArticleALancer> ArticleAsync() => (await lancement.LireAsync(cle))!.Articles.Single();
            Assert.Equal(2500, (await ArticleAsync()).ResteAAffecter);

            // Valeurs proposées
            var demande = await lancement.PreparerAsync(cle, [reference]);
            sortie.WriteLine($"Proposé : « {demande.Designation1} » cat. {demande.Categorie}/{demande.SousCategorie} MAD {demande.DateMiseADisposition} " +
                $"modèle {demande.Modele}{demande.IndiceComposition} préc. {demande.DossierPrecedent?.Libelle} devis {demande.Devis?.Numero} norme {demande.Norme}");
            var besoin = Assert.Single(demande.Articles);
            Assert.Equal((2500, 2500, livraison), (besoin.AFabriquer, besoin.AAffecter, demande.DateMiseADisposition));
            Assert.Equal((BaseDeTest.CompteClient, "1"), (demande.CompteClient, demande.Enseigne));
            if (demande.Designation1 == "") demande = demande with { Designation1 = "TEST NETPRINT SAAS LANCEMENT" };

            // Affecté au-delà du commandé : refusé
            var trop = await Assert.ThrowsAsync<DemandeOfInvalideException>(() =>
                lancement.LancerAsync(cle, demande with { Articles = [besoin with { AFabriquer = 3000, AAffecter = 3000 }] }));
            Assert.Contains(trop.Erreurs, x => x.Message == PreparationLancement.Messages.AffecteSuperieurCommande);

            var of = await lancement.LancerAsync(cle, demande);
            crees.Add(of);
            sortie.WriteLine($"OF {of.Libelle}");
            var a = await ArticleAsync();
            Assert.Equal(0, a.ResteAAffecter);
            Assert.Contains(a.Sources, s => s.Of == of && s.Nature == NatureSource.OfAOuvrir && s.AffecteCommande == 2500);

            // Modification : 3000 à fabriquer, 2000 affectés
            var lue = (await lancement.LireLancementAsync(cle, of))!;
            await lancement.ModifierLancementAsync(cle, of, lue with { Articles = [lue.Articles[0] with { AFabriquer = 3000, AAffecter = 2000 }] });
            Assert.Equal(500, (await ArticleAsync()).ResteAAffecter);

            // F10 refusé tant qu'un OF existe pour la commande
            var refus = await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() => lancement.CreerOfAutomatiqueAsync(cle));
            Assert.Equal("Un OF a déjà été créé pour cette commande.", refus.Message);

            // Rattachement (AL) refusé : l'OF sert déjà la commande
            await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() => lancement.RattacherAsync(cle, of, reference));

            await lancement.SupprimerLancementAsync(cle, of);
            Assert.Equal(EtatOf.Inconnu, await production.EtatAsync(of));
            Assert.Equal(2500, (await ArticleAsync()).ResteAAffecter);

            // F10 : un OF, tout le commandé fabriqué et affecté
            var auto = await lancement.CreerOfAutomatiqueAsync(cle);
            crees.Add(auto);
            var d = (await lancement.LireLancementAsync(cle, auto))!;
            sortie.WriteLine($"F10 : OF {auto.Libelle} « {d.Designation1} » cat. {d.Categorie} lancé {d.QuantiteLancee} dossier du {d.DateDossier}");
            Assert.Equal((2500, 2500), (d.Articles.Single().AFabriquer, d.Articles.Single().AAffecter));

            // Rattachement (AL) d'une 2e commande à l'OF à ouvrir : reste à affecter fabriqué et affecté, quantité lancée augmentée
            var autre = await CommandeAsync(1000);
            await lancement.RattacherAsync(autre, auto, reference);
            Assert.Equal(0, (await lancement.LireAsync(autre))!.Articles.Single().ResteAAffecter);
            Assert.Equal(d.QuantiteLancee + 1000, (await lancement.LireLancementAsync(autre, auto))!.QuantiteLancee);
            await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() => lancement.RattacherAsync(autre, auto, reference));
            // Un lien sans affectation reste modifiable et supprimable (pas de rattachement par-dessus)
            var lueAutre = (await lancement.LireLancementAsync(autre, auto))!;
            await lancement.ModifierLancementAsync(autre, auto, lueAutre with { Articles = [lueAutre.Articles[0] with { AFabriquer = 500, AAffecter = 0 }] });
            Assert.Contains((await lancement.LireAsync(autre))!.Articles.Single().Sources, s => s.Of == auto && s.LieALaCommande && s.AffecteCommande == 0);
            await lancement.SupprimerLancementAsync(autre, auto);
            Assert.Equal(EtatOf.AOuvrir, await production.EtatAsync(auto));   // sert encore la 1re commande
            await lancement.SupprimerLancementAsync(cle, auto);
            Assert.Equal(EtatOf.Inconnu, await production.EtatAsync(auto));

            // Affectation du stock d'un OF ouvert (ou d'un lot) disponible, puis sa suppression
            a = await ArticleAsync();
            var stock = a.Sources.FirstOrDefault(s => s.Nature != NatureSource.OfAOuvrir && s.Disponible > 0);
            if (stock is null)
                sortie.WriteLine("Aucun stock disponible pour cet article : affectation non essayée.");
            else
            {
                var q = Math.Min(stock.Disponible, a.ResteAAffecter);
                sortie.WriteLine($"Affectation de {q} sur {stock.Of.Libelle} ({stock.Nature}, dispo {stock.Disponible})");
                await Assert.ThrowsAsync<DemandeOfInvalideException>(() => lancement.AffecterStockAsync(cle, stock.Of, reference, stock.Disponible + 1));
                await lancement.AffecterStockAsync(cle, stock.Of, reference, q);
                var s = (await ArticleAsync()).Sources.Single(x => x.Of == stock.Of);
                Assert.Equal((q, true), (s.AffecteCommande, s.AffectationStock));
                await lancement.SupprimerAffectationAsync(cle, stock.Of, reference);
                Assert.Equal(2500, (await ArticleAsync()).ResteAAffecter);
            }
        }
        finally
        {
            foreach (var cle in commandes)
            {
                await using (var cmd = new OdbcCommand("DELETE FROM NP11F.AB0020 WHERE RSOC = ? AND LADOSC = ? AND LACLDC = ?", cnx))
                {
                    cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                    cmd.Parameters.AddWithValue("n", cle.Numero);
                    cmd.Parameters.AddWithValue("c", cle.Cle);
                    sortie.WriteLine($"{await cmd.ExecuteNonQueryAsync()} lien(s) AB0020 restant(s) supprimé(s)");
                }
                await NettoyerAsync(cnx, cle, client, article!.Value, adtcde);
            }
            foreach (var of in crees)
                await using (var cmd = new OdbcCommand("DELETE FROM NP11F.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", cnx))
                {
                    cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                    cmd.Parameters.AddWithValue("n", of.Numero);
                    cmd.Parameters.AddWithValue("c", of.Cle);
                    await cmd.ExecuteNonQueryAsync();
                }
        }
    }

    // Article commandable du client d'essai ; avecStock : géré en stock, avec des lots en stock (de préférence)
    private static async Task<(string Article, string Indice)?> ArticleDuClientAsync(OdbcConnection cnx, bool avecStock = false)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.AARTIC, a.AINDIC FROM NP11F.SPF010 a
            WHERE a.RSOC = ? AND a.ATYPEA = '2' AND a.ATYFAC = '1' AND a.AGEN = ? AND a.ACLI = ? AND a.AREMPL NOT IN ('C', 'O')
              AND a.AOPT4 <> 'O' AND a.AUNITE <> ' ' AND a.AARTIC NOT LIKE '999%'
              AND NOT EXISTS (SELECT 1 FROM NP11F.SPFNOM n WHERE n.RSOC = a.RSOC AND n.AARTIC = a.AARTIC AND n.AINDIC = a.AINDIC)
              AND EXISTS (SELECT 1 FROM NP11F.AA00083 u WHERE u.RSOC = a.RSOC AND u.RTABLE = 'UV' AND u.RARG = a.AUNITE)
            {(avecStock ? "AND a.ASTK = 'O'" : "")}
            {(avecStock ? "ORDER BY (SELECT COUNT(*) FROM NP11F.AL00F p WHERE p.RSOC = a.RSOC AND p.PLREF = a.AARTIC AND p.PLIND = a.AINDIC AND p.PLQENT > 0) DESC, a.AARTIC" : "")}
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
