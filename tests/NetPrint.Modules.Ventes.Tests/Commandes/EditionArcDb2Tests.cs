using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;
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
        public Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", string typeLogo = "", CancellationToken ct = default) =>
            Task.FromResult<IdentiteSociete?>(new(societe, "TEST PRINTOR PACKAGING", "SAS", "Route de Jargeau B.P. 558", "", "45305",
                "PITHIVIERS CEDEX", "02.38.33.00.04", "dlp@axilog.net", "", "000000002", "00024", "222C", "160000", null));
    }

    private sealed class Contacts : IContactsTiers
    {
        public Task<IReadOnlyList<ContactEnvoi>> LireAsync(int societe, int compteGeneral, int compteClient, DocumentEnvoye document,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContactEnvoi>>([new("Mme ACHATS", "achats@exemple.fr", true)]);
    }

    private static EditionArcService Service() =>
        new(new ArcCommandeDb2(BaseDeTest.Configuration), new ConsultationCommandesDb2(BaseDeTest.Configuration), new Societes(), new Contacts(),
            new Utilisateur());

    [FactDb2]
    public async Task Arc_d_une_commande_livree_du_client_d_essai()
    {
        var service = Service();
        var cle = new CleCommande(68537, "9");
        var options = await service.LireOptionsAsync([cle]);
        sortie.WriteLine($"Options : lignes soldées {options.AvecLignesSoldees}, contacts {string.Join(", ", options.Contacts.Select(c => c.Email))}");

        foreach (var (soldees, apercu) in new[] { (true, false), (false, true) })
        {
            var doc = await service.EditerAsync(cle, soldees, apercu);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(doc.Contenu, 0, 4));
            var chemin = Path.Combine(Path.GetTempPath(), doc.Nom);
            await File.WriteAllBytesAsync(chemin, doc.Contenu);
            sortie.WriteLine($"{doc.Nom} : {doc.Contenu.Length:N0} octets → {chemin}");
        }
    }

    // Brouillon Outlook avec l'ARC en pièce jointe : objet, texte du canevas (chapitre A), destinataires
    [FactDb2]
    public async Task Message_outlook_avec_l_arc_joint()
    {
        var eml = await Service().PreparerMessageAsync([new CleCommande(68537, "9")], ["achats@exemple.fr"], ["copie@exemple.fr"]);
        var texte = System.Text.Encoding.ASCII.GetString(eml.Contenu);
        Assert.Contains("X-Unsent: 1", texte);
        Assert.Contains("To: achats@exemple.fr", texte);
        Assert.Contains("Cc: copie@exemple.fr", texte);
        Assert.Contains("Content-Type: application/pdf", texte);
        var chemin = Path.Combine(Path.GetTempPath(), eml.Nom);
        await File.WriteAllBytesAsync(chemin, eml.Contenu);
        sortie.WriteLine($"{eml.Nom} : {eml.Contenu.Length:N0} octets → {chemin}");
        await Assert.ThrowsAsync<NetPrint.Socle.RegleMetierException>(() => Service().PreparerMessageAsync([], [], []));
    }
}
