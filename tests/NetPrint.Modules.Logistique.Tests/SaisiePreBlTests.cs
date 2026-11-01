using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Saisie d'un pré-BL : règles sans base, lectures, puis création, modification et suppression sur NP11F de test : société 8
// sans palette (Dior), société 29 avec palettes (client 411029 1)
[Collection(EcrituresNp11f.Collection)]
public class SaisiePreBlTests(ITestOutputHelper sortie)
{
    private static LigneSaisiePreBl Ligne(int quantite, params LotSaisiePreBl[] lots) => new()
    {
        Dossier = 1, Cle = "9", LigneCommande = 1, Article = "ART", Stocke = true, Quantite = quantite, EmballagePar = 100, UcParPalette = 10,
        Lots = [.. lots],
    };

    private static LotSaisiePreBl Lot(int quantite, params int[] palettes) => new()
    {
        Lot = "123451", Depot = 1, Quantite = quantite, Palettes = [.. palettes.Select((q, i) => new PaletteSaisiePreBl { Sscc = $"S{i}", Quantite = q })],
    };

    private static SaisiePreBl Saisie(bool sansPalette = false, params LigneSaisiePreBl[] lignes) => new()
    {
        CompteGeneral = 411100, CompteClient = 20, Adresse = new AdresseBl(190, "", "DIOR", "", "", "", "", "", ""), DateDepart = new DateOnly(2026, 11, 2),
        Transporteur = 1, TypeLivraison = "M", TypePort = "F", SansPalette = sansPalette, Lignes = [.. lignes],
    };

    [Fact]
    public void Conditionnement_des_lots()
    {
        // 1 000 par palette complète (10 colis de 100)
        var l = Ligne(2550, Lot(2550, 1000, 1000, 550));
        Assert.Equal(new ConditionnementLotPreBl(2, 25, 50, true), ReglesSaisiePreBl.Conditionnement(l, l.Lots[0], false));
        Assert.Equal(new ConditionnementLotPreBl(2, 20, 0, false), ReglesSaisiePreBl.Conditionnement(l, Lot(2000, 1000, 1000), false));
        // Sans palette : palettes complètes = colis ÷ UC par palette
        Assert.Equal(new ConditionnementLotPreBl(2, 25, 50, true), ReglesSaisiePreBl.Conditionnement(l, Lot(2550), true));
        Assert.Equal(new ConditionnementLotPreBl(3, 30, 0, false), ReglesSaisiePreBl.Conditionnement(l, Lot(3000), true));
        // Conditionnement exceptionnel : 50 par colis, 4 colis par palette (200)
        l.EmballageParExceptionnel = 50;
        l.UcParPaletteExceptionnel = 4;
        Assert.Equal(new ConditionnementLotPreBl(2, 8, 0, false), ReglesSaisiePreBl.Conditionnement(l, Lot(400, 200, 200), false));
    }

    [Fact]
    public void Controles_de_la_saisie()
    {
        string E(SaisiePreBl s) => string.Join(" / ", ReglesSaisiePreBl.Controler(s));
        Assert.Equal("", E(Saisie(false, Ligne(1500, Lot(1500, 1000, 500)))));
        Assert.Contains("Au moins un appel", E(Saisie()));
        Assert.Contains("somme des quantités des lots", E(Saisie(false, Ligne(1600, Lot(1500, 1000, 500)))));
        Assert.Contains("somme des palettes", E(Saisie(false, Ligne(1500, Lot(1500, 1000, 400)))));
        Assert.Contains("au moins une palette", E(Saisie(false, Ligne(1500, Lot(1500)))));
        Assert.Equal("", E(Saisie(true, Ligne(1500, Lot(1500)))));   // sans palette : quantité seule
        Assert.Contains("au moins un lot", E(Saisie(false, Ligne(1500))));
        Assert.Contains("deux fois", E(Saisie(false, Ligne(1000, Lot(1000, 1000)), Ligne(1000, Lot(1000, 1000)))));
        Assert.Contains("La palette S0 est prise deux fois", E(Saisie(false, Ligne(1000, Lot(1000, 1000)), Ligne(1000, Lot(1000, 1000)))));
        var facture = Saisie(false, Ligne(1000, Lot(1000, 1000)), Ligne(1000, Lot(1000, 1000)));
        facture.Lignes[1].LigneCommande = 2;
        facture.Lignes[1].Lots[0].Palettes[0].Sscc = "AUTRE";
        Assert.Equal("", E(facture));
        (facture.Lignes[0].CompteGeneralFacture, facture.Lignes[0].CompteClientFacture) = (411100, 20);
        (facture.Lignes[1].CompteGeneralFacture, facture.Lignes[1].CompteClientFacture) = (411100, 55);
        Assert.Contains("même client facturé", E(facture));
        var exceptionnel = Ligne(1000, Lot(1000, 1000));
        exceptionnel.EmballageParExceptionnel = 50;
        Assert.Contains("vont ensemble", E(Saisie(false, exceptionnel)));
        var s = Saisie(false, Ligne(1000, Lot(1000, 1000)));
        (s.Transporteur, s.TypePort, s.TypeLivraison) = (0, "", "X");
        Assert.Contains("transporteur", E(s));
        Assert.Contains("type de port", E(s));
        Assert.Contains("mode de transport", E(s));
        var frais = Ligne(5);
        frais.Stocke = false;
        Assert.Equal("", E(Saisie(false, frais)));

        Assert.True(ReglesSaisiePreBl.LotInchange(Lot(1500, 1000, 500), Lot(1500, 1000, 500)));
        Assert.False(ReglesSaisiePreBl.LotInchange(Lot(1500, 1000, 500), Lot(1400, 1000, 400)));
    }

    private sealed class Utilisateur(int societe) : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(new UtilisateurConnecte("NP11", societe, "TEST", 411000));
    }

    private static SaisiePreBlService Service(int societe = 8) => new(new PreBlDb2(BaseDeTest.Configuration), new OprDb2(BaseDeTest.Configuration),
        new SaisieBlDb2(BaseDeTest.Configuration), new Utilisateur(societe));

    private static PreBlService Consultation(int societe) => new(new PreBlDb2(BaseDeTest.Configuration), new OprDb2(BaseDeTest.Configuration),
        new SaisieBlDb2(BaseDeTest.Configuration), new Utilisateur(societe));

    [FactDb2]
    public async Task Valeurs_par_defaut_lignes_et_lots()
    {
        var s = await Service().NouveauAsync(411100, 20, 190);
        sortie.WriteLine($"{s.NomClient} · {s.Adresse.Libelle} · transp. {s.Transporteur} {s.TypeLivraison}/{s.TypePort} sans palette {s.SansPalette}");
        Assert.Equal(190, s.Adresse.Numero);
        Assert.True(s.SansPalette);   // société 8 : EXP005.FEPREB = O
        await Assert.ThrowsAsync<RegleMetierException>(() => Service().NouveauAsync(411100, 20, 9999));
        var lignes = await Service().LignesCommandeAsync(s, 66414, "9");
        foreach (var l in lignes) sortie.WriteLine($"{l.LigneCommande} {l.Article} reste {l.Reste} {l.EmballagePar}/{l.UcParPalette} stocké {l.Stocke}");
        Assert.Contains(lignes, l => l.LigneCommande == 3 && l.Article == "349473" && l.Stocke);
        await Assert.ThrowsAsync<RegleMetierException>(() => Service().LignesCommandeAsync(s, 1, "9"));
        var lots = await Service().LotsAsync(s, "349473", "");
        foreach (var lot in lots.Take(5)) sortie.WriteLine($"lot {lot.Lot} dépôt {lot.Depot} stock {lot.Stock} : {lot.Palettes.Count} palettes");
        Assert.Contains(lots, x => x.Palettes.Count(p => p.Selectionnable) >= 2);
    }

    // Société 8 (sans palette) : une quantité par lot ; Dior, adresse 190, commande 66414/9 ligne 3
    [FactDb2Ecriture]
    public async Task Creation_modification_suppression_sans_palette()
    {
        var service = Service();
        var s = await service.NouveauAsync(411100, 20, 190);
        var ligne = (await service.LignesCommandeAsync(s, 66414, "9")).First(l => l.LigneCommande == 3);
        var lot = (await service.LotsAsync(s, ligne.Article, ligne.Indice)).First();
        s.Lignes.Add(Nouvelle(ligne, new LotSaisiePreBl { Lot = lot.Lot, Depot = lot.Depot, Quantite = 25000 }));
        var numero = 0;
        try
        {
            numero = await service.EnregistrerAsync(s);
            sortie.WriteLine($"Pré-BL {numero} : lot {lot.Lot} dépôt {lot.Depot}, 25 000");
            var fiche = await Consultation(8).LireAsync(numero);
            Assert.Equal(25000, fiche!.Lignes.Single().Lots.Single().Quantite);
            Assert.Empty(fiche.Lignes.Single().Lots.Single().PalettesSscc);

            var m = await service.OuvrirAsync(numero);
            m.Lignes.Single().Lots.Single().Quantite = m.Lignes.Single().Quantite = 30000;
            Assert.Equal(numero, await service.EnregistrerAsync(m));
            Assert.Equal(30000, (await service.OuvrirAsync(numero)).Lignes.Single().Lots.Single().Quantite);
        }
        finally
        {
            if (numero > 0) await service.SupprimerAsync(numero);
        }
        await AucuneTraceAsync(8, numero);
    }

    // Société 29 (avec palettes) : client 411029 1, adresse 22, commande 26297/9 ligne 41 ; une palette entière, une en picking
    [FactDb2Ecriture]
    public async Task Creation_modification_suppression_avec_palettes()
    {
        var service = Service(29);
        var s = await service.NouveauAsync(411029, 1, 22);
        Assert.False(s.SansPalette);
        var ligne = (await service.LignesCommandeAsync(s, 26297, "9")).First(l => l.LigneCommande == 41);
        var lot = (await service.LotsAsync(s, ligne.Article, ligne.Indice)).First(x => x.Palettes.Count(p => p.Selectionnable && p.Disponible > 1) >= 2);
        var libres = lot.Palettes.Where(p => p.Selectionnable && p.Disponible > 1).ToList();
        var (p1, p2) = (libres[0], libres[1]);
        var partiel = p2.Disponible / 2;
        s.Observations = ["TEST NETPRINT SAAS", "", ""];
        if (s.Transporteur == 0) s.Transporteur = 1;   // pas de transporteur sur la fiche du client : celui des pré-BL de la société
        s.Lignes.Add(Nouvelle(ligne, new LotSaisiePreBl
        {
            Lot = lot.Lot, Depot = lot.Depot, Quantite = p1.Disponible + partiel,
            Palettes = [new PaletteSaisiePreBl { Sscc = p1.Sscc, Quantite = p1.Disponible }, new PaletteSaisiePreBl { Sscc = p2.Sscc, Quantite = partiel }],
        }));
        var numero = 0;
        try
        {
            numero = await service.EnregistrerAsync(s);
            sortie.WriteLine($"Pré-BL {numero} : lot {lot.Lot} dépôt {lot.Depot}, palettes {p1.Sscc} ({p1.Disponible}) et {p2.Sscc} ({partiel} en picking)");
            var fiche = await Consultation(29).LireAsync(numero);
            Assert.NotNull(fiche);
            Assert.Equal([(p1.Sscc, false), (p2.Sscc, true)],
                fiche.Lignes.Single().Lots.Single().PalettesSscc.OrderBy(p => p.Sscc == p1.Sscc ? 0 : 1).Select(p => (p.Sscc, p.Picking)));
            Assert.Equal("TEST NETPRINT SAAS", fiche.Observations.Single());

            // La ligne de commande n'est plus proposée à un autre pré-BL, ni la quantité prise des palettes
            var autre = await service.NouveauAsync(411029, 1, 22);
            Assert.DoesNotContain(await service.LignesCommandeAsync(autre, 26297, "9"), l => l.LigneCommande == 41);
            var reste = (await service.LotsAsync(autre, ligne.Article, ligne.Indice)).First(x => x.Lot == lot.Lot && x.Depot == lot.Depot);
            Assert.DoesNotContain(reste.Palettes, p => p.Sscc == p1.Sscc);
            Assert.Equal(p2.Disponible - partiel, reste.Palettes.Single(p => p.Sscc == p2.Sscc).Disponible);

            // Modification : seconde palette prise en entier ; n° de ligne et de lot gardés
            var m = await service.OuvrirAsync(numero);
            var lm = m.Lignes.Single();
            lm.Lots.Single().Palettes.Single(p => p.Sscc == p2.Sscc).Quantite = p2.Disponible;
            lm.Lots.Single().Quantite = lm.Quantite = p1.Disponible + p2.Disponible;
            m.Observations[1] = "MODIFIE";
            Assert.Equal(numero, await service.EnregistrerAsync(m));
            var apres = await service.OuvrirAsync(numero);
            Assert.Equal((1, 1), (apres.Lignes.Single().Numero, apres.Lignes.Single().Lots.Single().Numero));
            Assert.Equal(p1.Disponible + p2.Disponible, apres.Lignes.Single().Quantite);
            Assert.All(apres.Lignes.Single().Lots.Single().Palettes, p => Assert.False(p.Picking));
            Assert.Equal("MODIFIE", apres.Observations[1]);
        }
        finally
        {
            if (numero > 0) await service.SupprimerAsync(numero);
        }
        await AucuneTraceAsync(29, numero);
    }

    private static LigneSaisiePreBl Nouvelle(LigneCommandePreBl l, LotSaisiePreBl lot) => new()
    {
        Dossier = l.Dossier, Cle = l.Cle, LigneCommande = l.LigneCommande, Article = l.Article, Indice = l.Indice, Stocke = true,
        EmballagePar = l.EmballagePar, UcParPalette = l.UcParPalette, TypeEmballage = l.TypeEmballage, Quantite = lot.Quantite, Lots = [lot],
        CompteGeneralFacture = l.CompteGeneralFacture, CompteClientFacture = l.CompteClientFacture,
    };

    private static async Task AucuneTraceAsync(int societe, int numero)
    {
        Assert.True(numero > 0);
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        foreach (var (table, colonne) in new[] { ("EXP509", "HNOBL"), ("EXP510", "HNOBL"), ("EXP516", "DNBL"), ("EXP517", "DNBL"), ("EXP512", "HNOBL") })
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM NP11F.{table} WHERE RSOC = ? AND {colonne} = ?";
            cmd.Parameters.AddWithValue("", societe);
            cmd.Parameters.AddWithValue("", numero);
            Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
    }
}
