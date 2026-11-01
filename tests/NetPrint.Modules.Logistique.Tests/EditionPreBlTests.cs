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

    [FactDb2]
    public async Task PDF_de_pre_bl_de_la_base_de_test()
    {
        foreach (var (societe, numero) in new[] { (8, 165260), (29, 29406) })
        {
            var service = new EditionPreBlService(new EditionBlDb2(BaseDeTest.Configuration), new OprDb2(BaseDeTest.Configuration), new SansSociete(),
                new Utilisateur(societe));
            var pdf = await service.EditerAsync(numero, false);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Contenu, 0, 4));
            var fichier = Path.Combine(Path.GetTempPath(), pdf.Nom);
            await File.WriteAllBytesAsync(fichier, pdf.Contenu);
            sortie.WriteLine(fichier);
        }
        await Assert.ThrowsAsync<RegleMetierException>(() => new EditionPreBlService(new EditionBlDb2(BaseDeTest.Configuration),
            new OprDb2(BaseDeTest.Configuration), new SansSociete(), new Utilisateur(8)).EditerAsync(999999, false));
    }
}
