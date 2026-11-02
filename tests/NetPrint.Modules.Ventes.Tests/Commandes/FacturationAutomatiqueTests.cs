using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Contrôles de la fenêtre de facturation automatique (DOS50505)
public class FacturationAutomatiqueTests
{
    private static FacturationAutomatique Valide() => new() { Automatique = true, NumeroAdresse = 2 };

    [Fact]
    public void Parametres_valides_acceptes()
    {
        Assert.Empty(FacturationAutomatiqueService.Controler(Valide(), true, true));
        Assert.Empty(FacturationAutomatiqueService.Controler(new FacturationAutomatique { Nom = "X", Ville = "ROUEN" }, true, true));
    }

    [Fact]
    public void Numero_et_adresse_ensemble_refuses()
    {
        var f = Valide();
        f.Ville = "ROUEN";
        Assert.Equal(MessagesFacturation.AdresseOuNumero, Assert.Single(FacturationAutomatiqueService.Controler(f, true, true)).Message);
    }

    [Fact]
    public void Numero_inexistant_et_origine_inconnue()
    {
        var erreurs = FacturationAutomatiqueService.Controler(Valide(), adresseExiste: false, origineExiste: false);
        Assert.Equal([MessagesFacturation.AdresseInexistante, MessagesFacturation.OrigineInexistante], erreurs.Select(e => e.Message));
    }

    [Fact]
    public void Client_a_adresse_speciale_sans_adresse_refuse()
    {
        var f = new FacturationAutomatique { AdresseSpecialeClient = true };
        Assert.Equal(MessagesFacturation.AdresseSpeciale, Assert.Single(FacturationAutomatiqueService.Controler(f, true, true)).Message);
        f.NumeroAdresse = 1;
        Assert.Empty(FacturationAutomatiqueService.Controler(f, true, true));
    }

    [Fact]
    public void Acompte_negatif_et_valeurs_hors_liste()
    {
        var f = Valide();
        (f.Acompte, f.ConditionsReglement, f.TextePied) = (-1, "3", "");
        Assert.Equal([nameof(f.Acompte), nameof(f.ConditionsReglement), nameof(f.TextePied)],
            FacturationAutomatiqueService.Controler(f, true, true).Select(e => e.Zone));
    }

    [Fact]
    public void Tailles_des_zones_et_code_deja_enregistre()
    {
        var f = Valide();
        (f.NumeroAdresse, f.Acompte) = (10000, 10.123m);
        Assert.Equal([nameof(f.NumeroAdresse), nameof(f.Acompte)], FacturationAutomatiqueService.Controler(f, true, true).Select(e => e.Zone));
        // Une valeur posée par le 5250 (autre que 1 / 2) reste acceptée telle quelle
        var g = Valide();
        g.TextePied = "3";
        Assert.Single(FacturationAutomatiqueService.Controler(g, true, true));
        Assert.Empty(FacturationAutomatiqueService.Controler(g, true, true, new FacturationAutomatique { TextePied = "3" }));
    }

    [Fact]
    public void Normalisation_aux_longueurs_de_la_table()
    {
        var n = FacturationAutomatiqueService.Normaliser(new FacturationAutomatique
        {
            NumeroAdresse = 0, Nom = "  " + new string('N', 40), OrigineTechnique = " abx ", Acompte = 0, CodePostal = "f-75001",
        });
        Assert.Equal(((int?)null, 34, "ABX", (decimal?)null, "F-75001"), (n.NumeroAdresse, n.Nom.Length, n.OrigineTechnique, n.Acompte, n.CodePostal));
    }
}
