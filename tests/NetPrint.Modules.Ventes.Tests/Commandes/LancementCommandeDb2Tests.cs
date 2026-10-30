using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Écran de lancement sur des commandes réelles (lecture seule)
public class LancementCommandeDb2Tests(ITestOutputHelper sortie)
{
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private static LancementCommandeService Service() =>
        new(new LancementCommandeDb2(BaseDeTest.Configuration), new ConsultationCommandesDb2(BaseDeTest.Configuration), new Utilisateur());

    [FactDb2]
    public async Task Commande_livree_du_client_d_essai()
    {
        var l = await Service().LireAsync(new CleCommande(68537, "9"));
        Assert.NotNull(l);
        Afficher(l);
        Assert.Equal(7, l.Articles.Count);
        Assert.All(l.Articles, a => Assert.Equal(a.Commande, a.Affecte));   // tout est affecté à l'OF 68537/2
        Assert.Contains(l.Articles.SelectMany(a => a.Sources), s => s.Of.Libelle == "68537/2" && s.AffecteCommande > 0);
        Assert.False(l.PeutModifier);   // commande soldée
    }

    private void Afficher(LancementCommande l)
    {
        sortie.WriteLine($"{l.Commande.Libelle} {l.NomClient} modifiable {l.PeutModifier} {l.RaisonNonModifiable}");
        foreach (var a in l.Articles)
        {
            sortie.WriteLine($"  {a.Article} {a.Indice} « {a.Designation} » commandé {a.Commande} affecté {a.Affecte} reste {a.ResteAAffecter} stocké {a.Stocke}");
            foreach (var s in a.Sources)
                sortie.WriteLine($"    {s.Nature} {s.Of.Libelle} aff.cde {s.AffecteCommande} à fab {s.AFabriquer} lancé {s.Lance} fab {s.Fabrique} " +
                    $"stock {s.Stock} surplus {s.Surplus} aff.total {s.AffecteTotal} dispo {s.Disponible} livré {s.Livre}/{s.LivreCommande} " +
                    $"S={s.AffectationStock} bloqué={s.LotBloque} {s.Bag?.Libelle}");
        }
    }

    // Une commande récente avec affectation de stock (lien S) : calculs affichés pour contrôle visuel
    [FactDb2]
    public async Task Commande_avec_affectation_de_stock()
    {
        await using var cnx = new System.Data.Odbc.OdbcConnection(Microsoft.Extensions.Configuration.ConfigurationExtensions.GetConnectionString(BaseDeTest.Configuration, "Db2"));
        await cnx.OpenAsync();
        await using var cmd = new System.Data.Odbc.OdbcCommand("""
            SELECT l.LADOSC, l.LACLDC FROM NP11F.AB0020 l JOIN NP11F.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.LADOSC AND a.CCDCLE = l.LACLDC
            WHERE l.RSOC = 8 AND l.LATYPE = 'S' AND a.CCTYPC IN ('3', '4') ORDER BY a.CCDATA DESC, a.CCDATM DESC, l.LADOSC DESC FETCH FIRST 1 ROW ONLY
            """, cnx);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        var cle = new CleCommande(Convert.ToInt32(rd.GetValue(0)), Convert.ToString(rd.GetValue(1))!.Trim());
        var l = await Service().LireAsync(cle);
        Assert.NotNull(l);
        Afficher(l);
        Assert.Contains(l.Articles.SelectMany(a => a.Sources), s => s.AffectationStock);
    }
}
