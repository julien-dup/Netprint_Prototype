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
        await Executer("DELETE FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", BaseDeTest.Societe, cle.Numero, cle.Cle);
        await Executer("UPDATE NP11F.AA0003 SET CDDTPC = ?, CDDTDC = ?, CDDDOS = ?, CDDCLE = ? WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            client[0], client[1], client[2], client[3], BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient);
    }
}
