using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// ARC d'une commande réelle du client d'essai (lecture seule) : PDF écrit dans le dossier temporaire pour être regardé
public class EditionArcDb2Tests(ITestOutputHelper sortie)
{
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private sealed class Societes : ISocietes
    {
        public Task<string?> LireNomAsync(int societe, CancellationToken ct = default) => Task.FromResult<string?>("TEST");
        public Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", CancellationToken ct = default) =>
            Task.FromResult<IdentiteSociete?>(new(societe, "TEST PRINTOR PACKAGING", "SAS", "Route de Jargeau B.P. 558", "", "45305",
                "PITHIVIERS CEDEX", "02.38.33.00.04", "dlp@axilog.net", "", "000000002", "00024", "222C", "160000", null));
    }

    [FactDb2]
    public async Task Arc_d_une_commande_livree_du_client_d_essai()
    {
        var service = new EditionArcService(new ArcCommandeDb2(BaseDeTest.Configuration), new ConsultationCommandesDb2(BaseDeTest.Configuration),
            new Societes(), new Utilisateur());
        var cle = new CleCommande(68537, "9");
        var options = await service.LireOptionsAsync(cle);
        sortie.WriteLine($"Options : lignes soldées {options.AvecLignesSoldees}, e-mail « {options.EmailClient} »");

        foreach (var (soldees, apercu) in new[] { (true, false), (false, true) })
        {
            var doc = await service.EditerAsync(cle, soldees, apercu);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(doc.Contenu, 0, 4));
            var chemin = Path.Combine(Path.GetTempPath(), doc.Nom);
            await File.WriteAllBytesAsync(chemin, doc.Contenu);
            sortie.WriteLine($"{doc.Nom} : {doc.Contenu.Length:N0} octets → {chemin}");
        }
    }
}
