using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Édition du pré-BL (EXP535P2) : données lues dans le modèle du BL, puis PDF de pré-BL de la base de test (société 8 : 165260
// Dior, 4 appels dont des frais ; société 29 : 29406)
public class EditionPreBlTests(ITestOutputHelper sortie)
{
    private sealed class Utilisateur(int societe) : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(new UtilisateurConnecte("NP11", societe, "TEST", 411000));
    }

    private sealed class SansSociete : ISocietes
    {
        public Task<string?> LireNomAsync(int societe, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", string typeLogo = "", CancellationToken ct = default) =>
            Task.FromResult<IdentiteSociete?>(null);
    }

    [FactDb2]
    public async Task Donnees_du_pre_bl_dans_le_modele_du_bl()
    {
        var d = await new EditionBlDb2(BaseDeTest.Configuration).LirePreBlAsync(8, "TEST", 165260, default);
        Assert.NotNull(d);
        sortie.WriteLine($"{d.Entete.RaisonSociale} · {d.Entete.Ville} · colis {d.Entete.ColisTotal} poids {d.Entete.PoidsTotal} palettes {d.Entete.PalettesTotal}");
        foreach (var l in d.Lignes) sortie.WriteLine($"  él. {l.Element} l. {l.Ligne} {l.Article} {l.Quantite} : {string.Join(" | ", l.Designations)}");
        foreach (var lot in d.Lots) sortie.WriteLine($"  lot él. {lot.Element} l. {lot.Ligne} {lot.Lot} {lot.Quantite}");
        Assert.Equal(2, d.Elements.Count);   // commandes 69320/9 et 69323/9
        Assert.Equal(4, d.Lignes.Count(l => l.Article != ""));
        Assert.Equal(5000, d.Lignes.Where(l => l.Article == "PQA002").Sum(l => l.Quantite));
        Assert.Equal(3, d.Lots.Count);
        var modele = BonLivraison.Construire(d);
        Assert.Equal(165260, modele.Numero);
        Assert.Equal(2, modele.Groupes.Count);
        Assert.Null(await new EditionBlDb2(BaseDeTest.Configuration).LirePreBlAsync(8, "TEST", 999999, default));
    }

    private static EditionPreBlService Service(int societe) => new(new EditionBlDb2(BaseDeTest.Configuration), new PreBlDb2(BaseDeTest.Configuration),
        new OprDb2(BaseDeTest.Configuration), new SansSociete(), new Utilisateur(societe));

    private async Task EcrireAsync(NetPrint.Socle.Documents.DocumentGenere pdf)
    {
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Contenu, 0, 4));
        var fichier = Path.Combine(Path.GetTempPath(), pdf.Nom);
        await File.WriteAllBytesAsync(fichier, pdf.Contenu);
        sortie.WriteLine(fichier);
    }

    [FactDb2]
    public async Task PDF_de_pre_bl_de_la_base_de_test()
    {
        foreach (var (societe, numero) in new[] { (8, 165260), (29, 29406) }) await EcrireAsync(await Service(societe).EditerAsync(numero, false));
        await Assert.ThrowsAsync<RegleMetierException>(() => Service(8).EditerAsync(999999, false));
    }

    [FactDb2]
    public async Task Feuilles_de_preparation_et_camion()
    {
        await EcrireAsync(await Service(8).FeuillePreparationAsync(165260, null, true));
        await EcrireAsync(await Service(29).FeuillePreparationAsync(29406, null, true));
        await EcrireAsync(await Service(29).FeuilleCamionAsync([29406], true));
        // 29405 : autre client (411029 4) que 29406 (411029 1)
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Service(29).FeuilleCamionAsync([29405, 29406], false));
        Assert.Contains("même client", ex.Message);
    }

    [Fact]
    public void Regles_des_feuilles()
    {
        Assert.Equal("A1.B2.C3", ReglesFeuillesPreBl.Emplacement("A1B2C3", 2, 2, 2));
        Assert.Equal("PREPA", ReglesFeuillesPreBl.Emplacement("PREPA", 0, 0, 0));
        var p = new PalettePreparation(1, "", "S1", "A", "", "", "", "", 10, "", 0, "", "");
        Assert.Equal(2, ReglesFeuillesPreBl.Palettes([p, p with { Sscc = "S2" }]));
        Assert.Equal(1, ReglesFeuillesPreBl.Palettes([p with { Groupe = "G" }, p with { Sscc = "S2", Groupe = "G" }]));

        // 2 palettes complètes de 1 000 (10 colis de 100) + 300 sur la palette mixte 5, partagée avec un autre lot
        LotCamion Lot(string lib, int qte, int pal, int mixte, string article = "A") => new(1, article, "", "", "", 1, "DEP1", false, lib, mixte, qte, pal,
            100, 10, "C", 2m, 1, "9", "", false, 0, 0);
        var (lignes, totaux, depots) = ReglesFeuillesPreBl.Lignes(
            [Lot("N° 1 à 2 et n° 5", 2300, 2, 5), Lot("N° 5", 150, 0, 5, "B"),
             new LotCamion(1, "FRAIS", "", "", "", 0, "", false, "", 0, 3, 0, 0, 0, "", 0, 1, "9", "", true, 30m, 10m)],
            new Dictionary<int, int>(), 25);
        Assert.Equal(["N° 1 à 2", "N° 5", "N° 5", ""], lignes.Select(l => l.NumerosPalettes));
        Assert.Equal([2000, 300, 150, 3], lignes.Select(l => l.Quantite));
        Assert.Equal([20 * 2 + 2 * 25, 3 * 2 + 25, 2 * 2, 0], lignes.Select(l => l.Poids));   // palette mixte pesée une fois
        Assert.Equal(3, totaux.Single().Palettes);
        Assert.Equal(30m, totaux.Single().Montant);
        Assert.Equal([("DEP1", 3)], depots);
    }
}
