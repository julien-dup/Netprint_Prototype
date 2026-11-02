using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Noyau.Evenements;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Noyau;

// Événements et commentaires (EVE300) : règles pures, puis écriture sur NP11F de test (commande fictive 99999/Z, nettoyée)
public class EvenementsTests(ITestOutputHelper sortie)
{
    private static TypeEvenement Type(string moment = "1", bool rappel = true) =>
        new("CO01", "Commentaire", rappel, moment, new TimeOnly(9, 0), false, true, ["CHEF"], true, false);

    private static SaisieEvenement Saisie() => new() { Type = "CO01", Textes = ["Bonjour", "", "", ""] };

    [Fact]
    public void Saisie_complete_acceptee()
    {
        var s = Saisie();
        (s.DatePrevue, s.HeurePrevue, s.Rappel, s.Profils) = (new DateOnly(2026, 11, 5), new TimeOnly(14, 30), true, ["CHEF", "dlp"]);
        Assert.Empty(ReglesEvenements.Controler(s, Type(), true, _ => true));
    }

    [Fact]
    public void Type_inexistant_ou_incompatible()
    {
        Assert.Equal(MessagesEvenements.TypeInexistant, Assert.Single(ReglesEvenements.Controler(Saisie(), null, false, _ => true)).Message);
        Assert.Equal(MessagesEvenements.TypeIncompatible, Assert.Single(ReglesEvenements.Controler(Saisie(), Type(), false, _ => true)).Message);
        var vide = Saisie();
        vide.Type = "";
        Assert.Equal(MessagesEvenements.Obligatoire, Assert.Single(ReglesEvenements.Controler(vide, null, false, _ => true)).Message);
    }

    [Fact]
    public void Date_et_heure_prevues_vont_ensemble()
    {
        var s = Saisie();
        s.DatePrevue = new DateOnly(2026, 11, 5);
        Assert.Equal(("datePrevue", MessagesEvenements.DateHeureIncompatibles),
            Assert.Single(ReglesEvenements.Controler(s, Type(), true, _ => true)) is var e ? (e.Zone, e.Message) : default);
        s = Saisie();
        s.HeurePrevue = new TimeOnly(10, 0);
        Assert.Single(ReglesEvenements.Controler(s, Type(), true, _ => true));
    }

    [Fact]
    public void Rappel_sans_date_prevue_a_saisir_et_agenda_exige_une_date()
    {
        var s = Saisie();
        s.Rappel = true;
        Assert.Equal(["dateRappel", "heureRappel"], ReglesEvenements.Controler(s, Type(), true, _ => true).Select(e => e.Zone));
        s = Saisie();
        s.Agenda = true;
        Assert.Equal(("datePrevue", MessagesEvenements.Obligatoire),
            Assert.Single(ReglesEvenements.Controler(s, Type(), true, _ => true)) is var e ? (e.Zone, e.Message) : default);
    }

    [Fact]
    public void Profils_inexistants_et_doublons()
    {
        var s = Saisie();
        s.Profils = ["CHEF", "inconnu", "chef", ""];
        var erreurs = ReglesEvenements.Controler(s, Type(), true, p => p == "CHEF");
        Assert.Equal([("profil1", MessagesEvenements.ProfilInexistant), ("profil2", MessagesEvenements.ProfilEnDouble)],
            erreurs.Select(e => (e.Zone, e.Message)));
    }

    [Fact]
    public void Texte_limite_a_50_caracteres()
    {
        var s = Saisie();
        s.Textes[2] = new string('x', 51);
        Assert.Equal("texte3", Assert.Single(ReglesEvenements.Controler(s, Type(), true, _ => true)).Zone);
    }

    [Theory]
    [InlineData("1", 4, true)]
    [InlineData("2", 3, true)]
    [InlineData("5", 6, true)]
    [InlineData("3", 5, false)]
    public void Rappel_par_defaut_selon_le_moment_du_type(string moment, int jour, bool avecHeure)
    {
        var (date, heure) = ReglesEvenements.RappelParDefaut(new DateOnly(2026, 11, 5), Type(moment));
        Assert.Equal((new DateOnly(2026, 11, jour), avecHeure ? new TimeOnly(9, 0) : (TimeOnly?)null), (date, heure));
    }

    [Fact]
    public void Type_applique_ses_valeurs_et_ajoute_ses_profils()
    {
        var s = Saisie();
        s.Profils = ["dlp", ""];
        ReglesEvenements.AppliquerType(s, Type(), ["FAB", "", "dlp"]);
        Assert.Equal((true, false, true), (s.Rappel, s.Agenda, s.Navigateur));
        Assert.Equal(["DLP", "CHEF", "FAB"], s.Profils);
    }

    [Theory]
    [InlineData(14.30, 14, 30)]
    [InlineData(9.05, 9, 5)]
    public void Heures_HH_MM(double valeur, int h, int m)
    {
        Assert.Equal(new TimeOnly(h, m), ReglesEvenements.Heure((decimal)valeur));
        Assert.Equal((decimal)valeur, ReglesEvenements.Heure(new TimeOnly(h, m)));
        Assert.Null(ReglesEvenements.Heure(0m));
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    [FactDb2Ecriture]
    public async Task Creation_modification_suppression_sur_NP11F()
    {
        var service = new EvenementsService(new EvenementsDb2(BaseDeTest.Configuration), new Utilisateur());
        var objet = ObjetEvenements.DeCommande(99999, "Z");
        var types = await service.TypesAsync(objet);
        sortie.WriteLine($"Types pour une commande client : {string.Join(", ", types.Select(t => $"{t.Code} {t.Libelle}"))}");
        if (types.Count == 0) return;

        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        long numero = 0;
        try
        {
            var s = await service.NouveauAsync(objet);
            s.Type = types[0].Code;
            s = await service.AppliquerTypeAsync(objet, s);
            (s.Textes[0], s.DatePrevue, s.HeurePrevue, s.Rappel, s.DateRappel, s.Agenda) =
                ("TEST NETPRINT SAAS - A SUPPRIMER", DateOnly.FromDateTime(DateTime.Today.AddDays(10)), new TimeOnly(14, 30), true, null, false);
            s.Profils = [];
            numero = await service.CreerAsync(objet, s);

            var lu = Assert.Single((await service.ListerAsync(objet)).Lignes);
            Assert.Equal((numero, "TEST NETPRINT SAAS - A SUPPRIMER", "NP11", false), (lu.Numero, lu.Textes[0], lu.Profil, lu.Automatique));
            Assert.Equal(new TimeOnly(14, 30), lu.HeurePrevue);
            Assert.NotNull(lu.DateRappel);   // calculé d'après la date prévue
            var detail = (await service.LireAsync(objet, numero))!;
            Assert.Equal(["NP11"], detail.Profils);   // le créateur est prévenu

            // Autre objet : l'événement n'y est pas
            Assert.Null(await service.LireAsync(ObjetEvenements.DeCommande(99998, "Z"), numero));

            // Modification : texte, rappel retiré, profil en double refusé
            detail.Textes[1] = "Deuxième ligne";
            detail.Rappel = false;
            await service.ModifierAsync(objet, numero, detail);
            lu = Assert.Single((await service.ListerAsync(objet)).Lignes);
            Assert.Equal(("Deuxième ligne", (DateOnly?)null), (lu.Textes[1], lu.DateRappel));
            detail.Profils = ["NP11", "np11"];
            var ex = await Assert.ThrowsAsync<EvenementInvalideException>(() => service.ModifierAsync(objet, numero, detail));
            Assert.Equal(MessagesEvenements.ProfilEnDouble, Assert.Single(ex.Erreurs).Message);

            await service.SupprimerAsync(objet, numero);
            Assert.Empty((await service.ListerAsync(objet)).Lignes);
            await Assert.ThrowsAsync<RegleMetierException>(() => service.SupprimerAsync(objet, numero));
            numero = 0;

            // Compteur de commentaires (AB001.CCTCOM) d'une commande du client d'essai : +1 si « navigateur », rendu à la suppression
            int? dossier = null;
            string cle = "";
            await using (var cmd = new OdbcCommand("""
                SELECT RDOSS, CCDCLE FROM NP11F.AB001 WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND CCTYPC IN ('3', '4')
                ORDER BY RDOSS DESC FETCH FIRST 1 ROW ONLY
                """, cnx))
            {
                cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                cmd.Parameters.AddWithValue("g", BaseDeTest.CompteGeneral);
                cmd.Parameters.AddWithValue("c", BaseDeTest.CompteClient);
                await using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync()) (dossier, cle) = (Convert.ToInt32(rd.GetValue(0)), rd.GetString(1).Trim());
            }
            if (dossier is null) return;
            async Task<int> CompteurAsync()
            {
                await using var cmd = new OdbcCommand("SELECT CCTCOM FROM NP11F.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", cnx);
                cmd.Parameters.AddWithValue("s", BaseDeTest.Societe);
                cmd.Parameters.AddWithValue("d", dossier);
                cmd.Parameters.AddWithValue("c", cle);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            objet = ObjetEvenements.DeCommande(dossier.Value, cle);
            var avant = await CompteurAsync();
            s = await service.NouveauAsync(objet);
            s.Type = types[0].Code;
            s = await service.AppliquerTypeAsync(objet, s);
            (s.Textes[0], s.Navigateur, s.Rappel, s.Agenda, s.Profils) = ("TEST NETPRINT SAAS - A SUPPRIMER", true, false, false, []);
            numero = await service.CreerAsync(objet, s);
            Assert.Equal(avant + 1, await CompteurAsync());
            detail = (await service.LireAsync(objet, numero))!;
            detail.Navigateur = false;
            await service.ModifierAsync(objet, numero, detail);
            Assert.Equal(avant, await CompteurAsync());
            detail.Navigateur = true;
            await service.ModifierAsync(objet, numero, detail);
            await service.SupprimerAsync(objet, numero);
            numero = 0;
            Assert.Equal(avant, await CompteurAsync());
            sortie.WriteLine($"Compteur vérifié sur la commande {dossier}/{cle} ({avant})");
        }
        finally
        {
            if (numero > 0)
            {
                await using (var cmd = new OdbcCommand("DELETE FROM NP11F.EVE105 WHERE EVLIG = ?", cnx))
                {
                    cmd.Parameters.AddWithValue("n", numero);
                    await cmd.ExecuteNonQueryAsync();
                }
                await using (var cmd = new OdbcCommand("DELETE FROM NP11F.EVE100 WHERE EVLIG = ? AND RSOC = 8", cnx))
                {
                    cmd.Parameters.AddWithValue("n", numero);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
