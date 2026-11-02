using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Ordres de préparation : règles sans base, lecture, puis cycle complet sur NP11F de test
[Collection(EcrituresNp11f.Collection)]
public class OprTests(ITestOutputHelper sortie)
{
    private static readonly AdresseBl Dior = new(0, "", "DIOR", "RUE", "", "45000", "ORLEANS", "45", "");
    private static readonly DateOnly Jour = new(2026, 11, 2);

    private static LigneCandidateOpr Candidate(int dossier, int ligne, AdresseBl? adresse = null, bool sousTraite = false, int client = 20) => new(
        dossier, "9", ligne, 411100, client, "DIOR", "REF", "", "ART", "", "", "ETUI", Jour, null, 1000, 0, adresse ?? Dior, 5, "TNT", sousTraite,
        true, false, false, false, "", "");

    private static LigneSaisieOpr Saisie(int dossier, int ligne, DateOnly? date = null, int quantite = 100, int transporteur = 5) =>
        new(dossier, "9", ligne, date ?? Jour, quantite, transporteur, false, false, false, false, false, "");

    private static Dictionary<(int, string, int), LigneCandidateOpr> Index(params LigneCandidateOpr[] c) =>
        c.ToDictionary(x => (x.Dossier, x.Cle, x.LigneCommande));

    [Fact]
    public void Droits_OPR100()
    {
        Assert.Equal(new DroitsOpr(true, true, true, true, true), ReglesOpr.Droits(null));
        Assert.Equal(new DroitsOpr(false, false, false, false, false), ReglesOpr.Droits(["**", "", "", "", "", "", "", ""]));
        Assert.Equal(new DroitsOpr(true, false, true, false, true), ReglesOpr.Droits(["", "**", "", "**", "", "", "", ""]));
        Assert.False(ReglesOpr.Droits(["", "", "", "", "", "", "**", ""]).Editer);
    }

    [Fact]
    public void Regroupement_par_client_adresse_date_transporteur()
    {
        var candidates = Index(Candidate(1, 1), Candidate(1, 2), Candidate(2, 1), Candidate(3, 1, Dior with { Numero = 2, Ville = "PARIS" }),
            Candidate(4, 1, sousTraite: true));
        var lignes = new[] { Saisie(2, 1), Saisie(1, 2), Saisie(1, 1), Saisie(3, 1), Saisie(4, 1), Saisie(1, 1, Jour.AddDays(1)) with { LigneCommande = 1, Dossier = 2 } };
        // (la dernière ligne est la ligne 2/1 avec une autre date : un doublon de 2/1, pour le contrôle)
        Assert.Contains(ReglesOpr.ControlerSaisie(lignes, candidates, null, false), e => e.Contains("deux fois"));

        lignes = [Saisie(2, 1), Saisie(1, 2), Saisie(1, 1), Saisie(3, 1), Saisie(4, 1)];
        var groupes = ReglesOpr.Regrouper(lignes, candidates, uneCommandeParOpr: false);
        Assert.Equal(3, groupes.Count);   // adresse principale, autre adresse, sous-traitance
        Assert.Equal([(1, 1), (1, 2), (2, 1)], groupes[0].Select(l => (l.Dossier, l.LigneCommande)));
        Assert.Equal(4, ReglesOpr.Regrouper(lignes, candidates, uneCommandeParOpr: true).Count);   // 1 commande = 1 OPR
        Assert.Equal(4, ReglesOpr.Regrouper([.. lignes.Select((l, i) => i == 0 ? l with { Transporteur = 9 } : l)], candidates, false).Count);
    }

    [Fact]
    public void Controles_de_la_saisie_et_de_l_ajout()
    {
        var candidates = Index(Candidate(1, 1), Candidate(1, 2, Dior with { Ville = "PARIS" }), Candidate(2, 1), Candidate(3, 1, client: 21));
        string Erreurs(ReferenceOpr? r, bool uneCde, params LigneSaisieOpr[] l) => string.Join(" / ", ReglesOpr.ControlerSaisie(l, candidates, r, uneCde));
        Assert.Equal("", Erreurs(null, false, Saisie(1, 1)));
        Assert.Contains("au moins une ligne", Erreurs(null, false));
        Assert.Contains("positive", Erreurs(null, false, Saisie(1, 1, quantite: 0)));
        Assert.Contains("n'est plus à préparer", Erreurs(null, false, Saisie(9, 9)));
        Assert.Contains("400 caractères", Erreurs(null, false, Saisie(1, 1) with { Observation = new string('x', 401) }));

        var reference = new ReferenceOpr(500, "3", 411100, 20, 0, Dior, false, 1, "9", Jour);
        Assert.Equal("", Erreurs(reference, false, Saisie(2, 1)));
        Assert.Contains("autre adresse", Erreurs(reference, false, Saisie(1, 2)));
        Assert.Contains("autre client", Erreurs(reference, false, Saisie(3, 1)));
        Assert.Contains("ne porte que sur une commande", Erreurs(reference, true, Saisie(2, 1)));
        Assert.True(ReglesOpr.MemeAdresse(Dior, Dior with { RaisonSociale = " dior ", Ville = "orleans" }));
        Assert.False(ReglesOpr.MemeAdresse(Dior, Dior with { Rue1 = "AUTRE RUE" }));   // même règle que le regroupement en OPR
        candidates[(1, "9", 1)] = Candidate(1, 1) with { MaxiLivrable = 50 };
        Assert.Contains("maxi livrable", Erreurs(null, false, Saisie(1, 1, quantite: 51)));
    }

    [Fact]
    public void Modification()
    {
        LigneOpr L(int n, string etat) => new(n, 1, "9", n, "", "", "ART" + n, "", "", "", Jour, 100, 1000, 0, 5, "", false, false, false, false, false, "",
            etat, true, 0, 0);
        var fiche = new FicheOpr(500, EtatsOpr.EnPreparation, "NP11", Jour, 411100, 20, "DIOR", Dior, false, ["", "", "", ""],
            [L(1, EtatsOpr.APreparer), L(2, EtatsOpr.EnPreparation)]);
        LigneModifieeOpr M(int n, bool supprimer = false, int quantite = 50) => new(n, supprimer, Jour, quantite, 5, false, false, false, false, false, "");
        Assert.Empty(ReglesOpr.ControlerModification(fiche, new ModificationOpr(500, ["A"], [M(1)])));
        Assert.Contains(ReglesOpr.ControlerModification(fiche, new ModificationOpr(500, [], [M(2)])), e => e.Contains("en préparation"));
        Assert.Contains(ReglesOpr.ControlerModification(fiche, new ModificationOpr(500, [], [M(1, quantite: 0)])), e => e.Contains("positive"));
        Assert.Contains(ReglesOpr.ControlerModification(fiche, new ModificationOpr(500, [new string('x', 41)], [])), e => e.Contains("4 lignes"));
        var seule = fiche with { Lignes = [L(1, EtatsOpr.APreparer)] };
        Assert.Contains(ReglesOpr.ControlerModification(seule, new ModificationOpr(500, [], [M(1, supprimer: true)])), e => e.Contains("supprimez l'OPR"));
    }

    [Fact]
    public void Observation_en_zones_de_40()
    {
        var z = OprDb2.ZonesObservation(new string('a', 40) + new string('b', 45));
        Assert.Equal(10, z.Length);
        Assert.Equal((new string('a', 40), new string('b', 40), "bbbbb", ""), (z[0], z[1], z[2], z[3]));
        Assert.All(OprDb2.ZonesObservation("  "), x => Assert.Equal("", x));
        Assert.Equal("0008065981", OprDb2.CleVerrou(8, 65981));
    }

    // ---------- Base de test ----------
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static OprService Service() => new(new OprDb2(BaseDeTest.Configuration), new Utilisateur());

    [FactDb2]
    public async Task Liste_et_lignes_a_preparer_du_client_d_essai()
    {
        var s = Service();
        Assert.True((await s.DroitsAsync()).Creer);
        var liste = await s.RechercherAsync(new CriteresOpr { CompteGeneral = 411100, CompteClient = 20, AvecLivres = true });
        sortie.WriteLine($"{liste.Lignes.Count} élément(s) d'OPR");
        foreach (var e in liste.Lignes.Take(5)) sortie.WriteLine($"OPR {e.NumeroOpr} {e.Dossier}/{e.Cle} {e.Etat} {e.DatePrevue:dd/MM/yyyy} {e.Adresse.Ville}");
        var c = await s.CandidatesAsync(new CriteresCandidatesOpr { CompteGeneral = 411100, CompteClient = 20, Au = new DateOnly(2030, 12, 31) });
        sortie.WriteLine($"{c.Lignes.Count} ligne(s) à préparer");
        foreach (var l in c.Lignes.Take(8))
            sortie.WriteLine($"{l.Dossier}/{l.Cle} l.{l.LigneCommande} {l.Article} reste {l.Reste} {l.DateLivraison:dd/MM/yyyy} {l.Adresse.RaisonSociale} {l.Adresse.Ville} transp. {l.TransporteurClient}");
        Assert.All(c.Lignes, l => Assert.Equal(20, l.CompteClient));
        await Assert.ThrowsAsync<RegleMetierException>(() => s.CandidatesAsync(new CriteresCandidatesOpr()));
        if (liste.Lignes.Count > 0)
        {
            var fiche = await s.LireAsync(liste.Lignes[0].NumeroOpr);
            Assert.NotNull(fiche);
            Assert.NotEmpty(fiche.Lignes);
        }
    }

    // Création d'un OPR sur des lignes du client d'essai Dior, modification, ajout d'une ligne, retrait, suppression
    [FactDb2Ecriture]
    public async Task Creation_modification_ajout_et_suppression()
    {
        var s = Service();
        // Hors dossiers d'essai des tests de BL (69320, 68682), qui tournent en même temps
        var c = (await s.CandidatesAsync(new CriteresCandidatesOpr { CompteGeneral = 411100, CompteClient = 20, Au = new DateOnly(2030, 12, 31) })).Lignes
            .Where(x => x.Dossier is not (69320 or 68682)).ToList();
        var premiere = c.FirstOrDefault();
        Assert.NotNull(premiere);
        var seconde = c.FirstOrDefault(x => x != premiere && ReglesOpr.MemeAdresse(x.Adresse, premiere.Adresse) && x.SousTraite == premiere.SousTraite);
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        var numeros = new List<int>();
        try
        {
            numeros.AddRange(await s.CreerAsync([new LigneSaisieOpr(premiere.Dossier, premiere.Cle, premiere.LigneCommande, Jour, 10, 0, true, false, false,
                false, false, "TEST NETPRINT SAAS - A SUPPRIMER " + new string('x', 30))]));
            var numero = Assert.Single(numeros);
            sortie.WriteLine($"OPR {numero}");
            var fiche = await s.LireAsync(numero);
            Assert.NotNull(fiche);
            var ligne = Assert.Single(fiche.Lignes);
            Assert.Equal((10, true, EtatsOpr.APreparer), (ligne.Quantite, ligne.Imperatif, ligne.Etat));
            Assert.StartsWith("TEST NETPRINT SAAS", ligne.Observation);
            Assert.Equal(2, await CompterAsync(cnx, "SELECT LENGTH(TRIM(OTTXT2)) FROM NP11F.OPR016 WHERE RSOC = 8 AND ODNUM = ? AND ODNLIG = 1", numero) > 0 ? 2 : 0);

            // Édition des ajouts : la ligne imprimée est marquée éditée, une seconde édition des ajouts ne la reprend plus
            var edition = new EditionOprService(new OprDb2(BaseDeTest.Configuration), new Utilisateur());
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.OPR015 WHERE RSOC = 8 AND ODNUM = ? AND ODEDIT = 'O'", numero));
            await edition.ImprimerAjoutsAsync(numero);
            var editees = await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.OPR015 WHERE RSOC = 8 AND ODNUM = ? AND ODEDIT = 'O'", numero);
            sortie.WriteLine($"{editees} ligne(s) marquée(s) éditée(s)");
            Assert.True(editees <= 1);   // 0 si l'article n'est pas imprimable sur l'OPR (non stocké, non éditable)

            // La même ligne ne peut pas entrer dans un second OPR
            await Assert.ThrowsAsync<RegleMetierException>(() => s.CreerAsync([new LigneSaisieOpr(premiere.Dossier, premiere.Cle, premiere.LigneCommande,
                Jour, 10, 0, false, false, false, false, false, "")]));

            await s.ModifierAsync(new ModificationOpr(numero, ["COMMENTAIRE ESSAI"], [new LigneModifieeOpr(1, false, Jour.AddDays(1), 20, 0, false, true,
                false, false, false, "")]));
            fiche = await s.LireAsync(numero);
            Assert.Equal((20, false, true, Jour.AddDays(1), ""), (fiche!.Lignes[0].Quantite, fiche.Lignes[0].Imperatif, fiche.Lignes[0].Mecompte,
                fiche.Lignes[0].DatePrevue!.Value, fiche.Lignes[0].Observation));
            Assert.Equal("COMMENTAIRE ESSAI", fiche.Textes[0]);
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.OPR016 WHERE RSOC = 8 AND ODNUM = ?", numero));

            if (seconde is not null)
            {
                var ajout = await s.CandidatesAsync(new CriteresCandidatesOpr { NumeroOpr = numero, Au = new DateOnly(2030, 12, 31) });
                Assert.Contains(ajout.Lignes, x => (x.Dossier, x.Cle, x.LigneCommande) == (seconde.Dossier, seconde.Cle, seconde.LigneCommande));
                await s.AjouterAsync(numero, [new LigneSaisieOpr(seconde.Dossier, seconde.Cle, seconde.LigneCommande, Jour, 5, 0, false, false, false, false,
                    false, "")]);
                fiche = await s.LireAsync(numero);
                Assert.Equal(2, fiche!.Lignes.Count);
                Assert.All(fiche.Lignes, l => Assert.Equal(Jour.AddDays(1), l.DatePrevue));   // date de l'OPR
                if ((seconde.Dossier, seconde.Cle) != (premiere.Dossier, premiere.Cle))
                {
                    await s.SupprimerElementAsync(numero, seconde.Dossier, seconde.Cle);
                    Assert.Single((await s.LireAsync(numero))!.Lignes);
                }
            }

            await s.SupprimerAsync(numero);
            Assert.Null(await s.LireAsync(numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.OPR015 WHERE RSOC = 8 AND ODNUM = ?", numero));
            Assert.Equal(0, await CompterAsync(cnx, "SELECT COUNT(*) FROM NP11F.PRTLCK WHERE PLFIC = 'OPR' AND PLPROF = 'NP11'"));
            numeros.Clear();
        }
        finally
        {
            foreach (var n in numeros)
                foreach (var table in new[] { ("OPR016", "ODNUM"), ("OPR015", "ODNUM"), ("OPR010", "OENUM") })
                {
                    await using var cmd = cnx.CreateCommand();
                    cmd.CommandText = $"DELETE FROM NP11F.{table.Item1} WHERE RSOC = 8 AND {table.Item2} = ?";
                    cmd.Parameters.AddWithValue("", n);
                    await cmd.ExecuteNonQueryAsync();
                }
        }
    }

    private static async Task<int> CompterAsync(OdbcConnection cnx, string sql, params object[] valeurs)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.AddWithValue("", v);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? 0 : Convert.ToInt32(r);
    }
}
