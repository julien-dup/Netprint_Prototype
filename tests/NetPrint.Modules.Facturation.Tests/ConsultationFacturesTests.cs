using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Facturation.Tests;

// Consultation des factures : règles sans base, puis lecture sur NP11F de test (société 8 : factures de Dior 411100 20,
// facture 110463 du 13/11/2023 à 9 lignes ; avoir 20501 du 14/04/2026)
public class ConsultationFacturesTests(ITestOutputHelper sortie)
{
    private static LigneFacture L(string code, string type, string des, decimal qte = 0, decimal pu = 0, decimal montant = 0) =>
        new(1, 1, code, type, "ART", "", qte, des, [], pu, "UN", montant, 0, null, "", "");

    [Fact]
    public void Montants_statuts_envoi_et_lignes()
    {
        Assert.Equal(1214.60m, ReglesFactures.Ttc(1000m, 10m, 5m, 0.40m, 200m));
        Assert.Equal(StatutsFacture.AEditer, ReglesFactures.Statut("O", "N"));
        Assert.Equal(StatutsFacture.Editee, ReglesFactures.Statut("E", "N"));
        Assert.Equal(StatutsFacture.Comptabilisee, ReglesFactures.Statut("E", "O"));
        Assert.Equal(StatutsFacture.Attente, ReglesFactures.Statut("A", ""));
        Assert.Equal("", ReglesFactures.Envoi("", "", 0, "", "", ""));
        Assert.Equal("Ignorée", ReglesFactures.Envoi("O", "O", 20261101, "A", "M", "PDF"));
        Assert.Equal("Envoyée le 01/11/2026 (automatique, e-mail, PDF)", ReglesFactures.Envoi("O", "", 20261101, "A", "M", "PDF "));

        // Texte rattaché à la ligne précédente ; avoir : quantité et montants négatifs ; remise : montant négatif
        var lignes = ReglesFactures.Lignes(false, [L("1", "V", "ETUIS", 1000, 0.5m, 500), L("9", "T", "TEXTE"), L("2", "R", "REMISE", 1, 50, 50)]);
        Assert.Equal(2, lignes.Count);
        Assert.Equal(["TEXTE"], lignes[0].Textes);
        Assert.Equal(-50, lignes[1].Montant);
        var avoir = ReglesFactures.Lignes(true, [L("1", "V", "ETUIS", 1000, 0.5m, 500), L("2", "R", "REMISE", 1, 50, 50)]);
        Assert.Equal([(-1000m, -500m), (-1m, 50m)], avoir.Select(l => (l.Quantite, l.Montant)));

        Assert.Contains("postérieure", string.Join("", ConsultationFacturesService.Controler(new CriteresFactures
            { Du = new DateOnly(2026, 2, 1), Au = new DateOnly(2026, 1, 1) })));
        Assert.Contains("clé", string.Join("", ConsultationFacturesService.Controler(new CriteresFactures { Cle = "9" })));
        Assert.Empty(ConsultationFacturesService.Controler(new CriteresFactures { Commande = 1, Cle = "9" }));
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(new UtilisateurConnecte("NP11", 8, "TEST", 411100));
    }

    private static ConsultationFacturesService Service() => new(new ConsultationFacturesDb2(BaseDeTest.Configuration), new Utilisateur());

    [FactDb2]
    public async Task Recherche_et_fiche()
    {
        var r = await Service().RechercherAsync(new CriteresFactures { CompteGeneralFacture = 411100, CompteClientFacture = 20, Du = new DateOnly(2023, 11, 13),
            Au = new DateOnly(2023, 11, 14) });
        foreach (var f in r.Lignes) sortie.WriteLine($"{f.Numero} {f.Date:dd/MM/yyyy} {f.NomFacture} HT {f.Ht} TTC {f.Ttc} {f.Statut} {f.Envoi}");
        Assert.Contains(r.Lignes, f => f.Numero == 110463 && f.Ht == 1294.90m);
        Assert.Equal(r.Lignes.OrderByDescending(f => f.Date).Select(f => f.Date), r.Lignes.Select(f => f.Date));

        var fiche = await Service().LireAsync(110463, new DateOnly(2023, 11, 13));
        Assert.NotNull(fiche);
        foreach (var e in fiche.Elements) sortie.WriteLine($"élément {e.Numero} commande {e.Dossier}/{e.Cle} {e.ReferenceCommande}");
        foreach (var l in fiche.Lignes) sortie.WriteLine($"  {l.Element}.{l.Numero} {l.Article} {l.Quantite} × {l.PrixUnitaire} = {l.Montant} BL {l.Bl} · {l.Designation} {string.Join(" / ", l.Textes)}");
        Assert.NotEmpty(fiche.Elements);
        Assert.Equal(1294.90m, fiche.Lignes.Sum(l => l.Montant));
        Assert.Null(await Service().LireAsync(110463, new DateOnly(2023, 11, 14)));   // n° + date

        var avoir = (await Service().RechercherAsync(new CriteresFactures { Numero = 20501, Du = new DateOnly(2026, 1, 1), AvoirsSeulement = true })).Lignes.Single();
        Assert.True(avoir.Avoir);
        Assert.Equal(-2800m, avoir.Ht);
        Assert.Empty((await Service().RechercherAsync(new CriteresFactures { Numero = 20501, Du = new DateOnly(2026, 1, 1), MontantHtDe = 0 })).Lignes);   // HT signé
    }
}
