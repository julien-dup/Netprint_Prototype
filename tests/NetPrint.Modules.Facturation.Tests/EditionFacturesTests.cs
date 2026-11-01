using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Facturation.Tests;

// Édition d'une facture (FAC202P5) : règles sans base, puis PDF de factures de la base de test (société 8) : 50001 du 01/05/2026
// (Dior, en anglais, modèle K:\Factures_pdf\DIOR\PRINTOR_2026_050001.PDF), 110463 du 13/11/2023 (5 commandes), avoir 100476
public class EditionFacturesTests(ITestOutputHelper sortie)
{
    [Fact]
    public void Numero_echeance_et_libelles()
    {
        Assert.Equal("26.050001", ReglesEditionFacture.NumeroImprime(50001, new DateOnly(2026, 5, 1), true));
        Assert.Equal("50001", ReglesEditionFacture.NumeroImprime(50001, new DateOnly(2026, 5, 1), false));
        var l = LibellesFacture.Francais;
        var jour = new DateOnly(2026, 5, 1);
        Assert.Equal("", ReglesEditionFacture.Echeance(true, jour, jour, false, l));
        Assert.Equal(l.AuComptant, ReglesEditionFacture.Echeance(false, jour, jour, false, l));
        Assert.Equal("Échéance : 21/05/2026", ReglesEditionFacture.Echeance(false, jour, new DateOnly(2026, 5, 21), false, l));
        var anglais = LibellesFacture.Pour([.. Enumerable.Range(1, 50).Select(i => i == 15 ? "Invoice N°" : "")]);
        Assert.Equal(("Invoice N°", "Quantité"), (anglais.NumeroFacture, anglais.Quantite));
    }

    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() => Task.FromResult(new UtilisateurConnecte("NP11", 8, "TEST", 411100));
    }

    private sealed class SansSociete : ISocietes
    {
        public Task<string?> LireNomAsync(int societe, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", string typeLogo = "", CancellationToken ct = default) =>
            Task.FromResult<IdentiteSociete?>(null);
    }

    [FactDb2]
    public async Task PDF_de_factures_de_la_base_de_test()
    {
        var u = new Utilisateur();
        var service = new EditionFacturesService(new ConsultationFacturesService(new ConsultationFacturesDb2(BaseDeTest.Configuration), u),
            new CalculFacturesDb2(BaseDeTest.Configuration), new EditionFacturesDb2(BaseDeTest.Configuration), new SansSociete(), u);
        foreach (var (numero, date) in new[] { (50001, new DateOnly(2026, 5, 1)), (110463, new DateOnly(2023, 11, 13)), (100476, new DateOnly(2026, 5, 12)) })
        {
            var pdf = await service.EditerAsync(numero, date);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Contenu, 0, 4));
            var fichier = Path.Combine(Path.GetTempPath(), pdf.Nom);
            await File.WriteAllBytesAsync(fichier, pdf.Contenu);
            sortie.WriteLine(fichier);
        }
        await Assert.ThrowsAsync<RegleMetierException>(() => service.EditerAsync(50001, new DateOnly(2026, 5, 2)));
    }
}
