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

            var of = await production.DemanderOfAsync(demande);
            crees.Add(of);
            sortie.WriteLine($"Commande {cle.Libelle} → OF {of.Libelle}");
            // n° de la commande ; clé calculée par GETCLE (les tests n'ont pas Production:CleOf = PremierChiffreLibre)
            Assert.Equal(cle.Numero, of.Numero);
            Assert.Equal(NetPrint.Noyau.Compteurs.NumerosDossier.CleCalculee(cle.Numero, BaseDeTest.CompteClient), of.Cle);
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
