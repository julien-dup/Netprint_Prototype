using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Production.Donnees;
using NetPrint.Modules.Production.Services;

namespace NetPrint.Modules.Ventes.Tests.Production;

// Demandes d'OF (NEG515C2) : contrôles, numérotation, colonnes de l'en-tête, sans base
public class InitialisationProductionTests
{
    [Fact]
    public void Demande_complete_acceptee() =>
        Assert.Empty(InitialisationProductionService.Controler(Demande(), creation: true, true, true, true));

    [Fact]
    public void Controles_de_la_fenetre_de_lancement()
    {
        var d = Demande() with { Designation1 = "", Categorie = "$2", Prix = 12, Unite = "", Articles = [new("A", "", 0, 5, new(2026, 12, 1))] };
        var zones = InitialisationProductionService.Controler(d, true, true, true, true).ToDictionary(e => e.Zone, e => e.Message);
        Assert.Equal("Cette zone doit être renseignée.", zones["Designation1"]);
        Assert.Equal("Catégorie réservée aux dossiers de négoce et PFS.", zones["Categorie"]);
        Assert.Equal("Cette zone doit être renseignée.", zones["Unite"]);           // prix sans unité
        Assert.Equal("Cette zone doit être renseignée.", zones["Articles[0].AFabriquer"]);
        Assert.Equal("Quantité à affecter > quantité à fabriquer.", zones["Articles[0].AAffecter"]);
        // Catégorie et sous-catégorie ne se contrôlent qu'en création (protégées en modification)
        Assert.DoesNotContain(InitialisationProductionService.Controler(Demande() with { Categorie = "" }, false, true, true, true),
            e => e.Zone == "Categorie");
        Assert.Contains(InitialisationProductionService.Controler(Demande(), true, false, true, true), e => e.Message == "Cette catégorie n'existe pas.");
    }

    [Fact]
    public void Cle_de_l_OF_premier_chiffre_libre()
    {
        Assert.Equal("1", NetPrint.Noyau.Compteurs.NumerosDossier.PremiereCleLibre(new HashSet<string> { "9" }));          // la commande 68537/9 → OF 68537/1
        Assert.Equal("2", NetPrint.Noyau.Compteurs.NumerosDossier.PremiereCleLibre(new HashSet<string> { "1", "9" }));
        Assert.Equal("0", NetPrint.Noyau.Compteurs.NumerosDossier.PremiereCleLibre(new HashSet<string>("123456789".Select(c => c.ToString()))));
        Assert.Null(NetPrint.Noyau.Compteurs.NumerosDossier.PremiereCleLibre(new HashSet<string>("1234567890".Select(c => c.ToString()))));
        // Clé de GETDOSSIER gardée si elle est libre (POPT02 = 'N')
        Assert.Equal("B", NetPrint.Noyau.Compteurs.NumerosDossier.PremiereCleLibre(new HashSet<string> { "1" }, "B"));
        Assert.Equal((12345, "B"), NetPrint.Noyau.Compteurs.NumerosDossier.Decouper("12345 B"));
        Assert.Null(NetPrint.Noyau.Compteurs.NumerosDossier.Decouper("-1"));
        // GETCLE (= PRTCLEF) : commande 68537, client 376 → « D »
        Assert.Equal("D", NetPrint.Noyau.Compteurs.NumerosDossier.CleCalculee(68537, 376));
        Assert.Equal("A", NetPrint.Noyau.Compteurs.NumerosDossier.CleCalculee(26, 0));
    }

    [Fact]
    public void Ligne_sans_quantite_ignoree_mais_au_moins_une_a_fabriquer()
    {
        var d = Demande() with { Articles = [new("A", "", 10, 5, new(2026, 12, 1)), new("B", "", 0, 0, new(2026, 12, 1))] };
        Assert.Empty(InitialisationProductionService.Controler(d, true, true, true, true));
        var vide = Demande() with { Articles = [new("B", "", 0, 0, new(2026, 12, 1))] };
        Assert.Contains(InitialisationProductionService.Controler(vide, true, true, true, true), e => e.Zone == "Articles");
    }

    [Fact]
    public void En_tete_de_l_OF()
    {
        var c = DemandesOfDb2.ColonnesEntete(Demande(), "dlp").ToDictionary(x => x.Colonne, x => x.Valeur);
        Assert.Equal(("1", 2026, 12, 1, 20261215, "N", "DLP"), (c["CCENSE"], c["CCDTLA"], c["CCDTLM"], c["CCDTLJ"], c["CCDMADC"], c["CCOP10"], c["CCPROF"]));
        Assert.Equal((12647, 0, 2), (c["CCDEV"], c["CCVERS"], c["CCHYP"]));
    }

    [Fact]
    public void Saisie_normalisee() =>
        Assert.Equal(("DT", "393228", "  abc"[2..]), InitialisationProductionService.Normaliser(Demande() with
        {
            Categorie = " dt ", Designation1 = "  abc ", Articles = [new(" 393228 ", "", 10, 0, new(2026, 12, 1))],
        }) is var d ? (d.Categorie, d.Articles[0].Article, d.Designation1) : default);

    internal static DemandeOf Demande() => new(
        new CommandeOrigine(24, "1"), 411100, 376, "1", "PO-12", "SANDRA", "DT", "01", "ETIQUETTES CLARINS", "", new(2026, 12, 1),
        new(2026, 10, 30), new(2026, 10, 2), new(2026, 12, 15), 10000, 0, "", new DevisOrigine(12647, 0, 2), null, 0, "", "NF1", "",
        false, [new("393228", "", 10000, 10000, new(2026, 12, 1))]);
}
