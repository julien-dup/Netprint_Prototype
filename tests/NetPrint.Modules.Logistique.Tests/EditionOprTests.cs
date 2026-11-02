using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Édition d'un OPR (OPR150) : règles du conditionnement et du stock, puis PDF d'OPR réels de la base de test (lecture seule)
public class EditionOprTests(ITestOutputHelper sortie)
{
    [Fact]
    public void Conditionnement_comme_OPR150_sans_superposition()
    {
        // 3 500 ex en paquets de 100, 6 paquets par carton, 40 cartons par palette : 35 paquets → 6 cartons → 1 palette
        Assert.Equal(["paquet de 100 ex", "carton de 6 paquets", "Mise sur 1 palette", "Soit 6 colis"], ModeleOpr.Conditionnement(3500, "P", 100, 6, 40));
        // Cartons de 500 ex, un carton = une UC : pas de « carton de 1 cartons »
        Assert.Equal(["carton de 500 ex", "Soit 3 colis"], ModeleOpr.Conditionnement(1200, "C", 500, 1, 0));
        // Palettes sans emballage : le RPG imprimait « Mise sur    palette(s) »
        Assert.Equal(["Mise sur palette"], ModeleOpr.Conditionnement(1200, "", 0, 0, 20));
        Assert.Equal(["Soit 13 colis", "Mise sur 2 palettes"], ModeleOpr.Conditionnement(1250, "X", 100, 0, 10).OrderByDescending(x => x).ToList());
    }

    [Fact]
    public void Emplacement_unite_et_poids()
    {
        Assert.Equal("A1.02.3", OprDb2.Emplacement("A1023", (2, 2, 1)));
        Assert.Equal("PREPA", OprDb2.Emplacement("PREPA", (0, 0, 0)));
        Assert.Equal("UN", OprDb2.Unite(1, "UN"));
        Assert.Equal("ex", OprDb2.Unite(1000, "LM"));
        Assert.Equal("ex", OprDb2.Unite(0, ""));
        var l = Ligne() with { Quantite = 3500, EmballagePar = 100, PoidsUc = 2.35m };
        Assert.Equal(82, ModeleOpr.Poids(l));   // 35 × 2,35 = 82,25 → 82 kg
        Assert.Null(ModeleOpr.Poids(l with { PoidsUc = 0 }));
    }

    private static LigneEditionOpr Ligne(int dossier = 1, string cle = "9", AdresseBl? adresse = null) => new(
        1, dossier, cle, new DateOnly(2026, 11, 2), 411100, 20, "DIOR", "", "", null, adresse ?? new AdresseBl(0, "", "DIOR", "", "", "45000", "ORLEANS", "45", ""),
        "", [], "ART", "", "ETUI", 100, "ex", "", "", 0, 0, 0, 0, "", "", [], 0, 0, [], 0, "", 0, 0, 0, 0, false, false, false, false, false, "", [], [], [], [], 0, []);

    [Fact]
    public void Pages_par_date_et_adresse_ou_par_commande()
    {
        var autre = new AdresseBl(2, "", "DIOR", "", "", "75000", "PARIS", "75", "");
        var d = new DonneesEditionOpr(500, "AB", ["", "TEXTE"], true, false, false, [Ligne(1), Ligne(2), Ligne(3, adresse: autre), Ligne(1) with { NumeroLigne = 2 }]);
        var (entete, pages) = ModeleOpr.Construire(d, false, DateTime.Now);
        Assert.Equal(["TEXTE"], entete.Textes);
        Assert.Equal(2, pages.Count);
        Assert.Equal([1, 2], pages[0].Commandes.Select(c => c.Dossier));
        Assert.Equal(2, pages[0].Commandes[0].Lignes.Count);
        Assert.Equal(3, ModeleOpr.Construire(d with { PageParCommande = true }, false, DateTime.Now).Pages.Count);
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    [FactDb2]
    public async Task PDF_d_OPR_de_la_base_de_test()
    {
        var service = new EditionOprService(new OprDb2(BaseDeTest.Configuration), new Utilisateur());
        foreach (var numero in new[] { 65980, 65973, 65969 })
        {
            var pdf = await service.EditerAsync(numero, ajoutsSeulement: false);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Contenu, 0, 4));
            var fichier = Path.Combine(Path.GetTempPath(), pdf.Nom);
            await File.WriteAllBytesAsync(fichier, pdf.Contenu);
            sortie.WriteLine(fichier);
        }
        var ajouts = await service.EditerAsync(65980, ajoutsSeulement: true);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(ajouts.Contenu, 0, 4));
    }
}
