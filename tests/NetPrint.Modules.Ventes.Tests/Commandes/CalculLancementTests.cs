using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using L = NetPrint.Modules.Ventes.Services.CalculLancement;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Calculs de l'écran de lancement (NEG515 § 2), sans base
public class CalculLancementTests
{
    private static readonly CleCommande Cde = new(24, "9");
    private static readonly NumeroOf Of1 = new(24, "1");
    private static readonly NumeroOf Of2 = new(500, "2");

    [Fact]
    public void OF_de_la_commande_disponible_et_reste_a_affecter()
    {
        var a = Calculer([Lien(Of1, Cde, lance: 10000, fabrique: 4000, affecte: 6000)], lots: [new("000241", "M", 4000)])[0];
        Assert.Equal((10000, 6000, 4000), (a.Commande, a.Affecte, a.ResteAAffecter));
        var s = Assert.Single(a.Sources);
        // à fab = 10000 − 4000 ; dispo = 6000 + 4000 (stock) − 6000 (affecté)
        Assert.Equal((NatureSource.OfOuvert, 6000, 6000, 4000, 4000), (s.Nature, s.AffecteCommande, s.AFabriquer, s.Stock, s.Disponible));
    }

    [Fact]
    public void Livre_pour_la_commande_reintegre_dans_le_disponible()
    {
        // Fabriqué 6000, affecté 6000 dont 2000 déjà livrés à cette commande (le stock est passé à 4000) : sans la
        // correction, le disponible serait 4000 − 6000 < 0 ; les 2000 livrés ne doivent pas compter deux fois
        var s = Calculer([Lien(Of1, Cde, 6000, 6000, 6000)], lots: [new("000241", "M", 6000)],
            livraisons: [new("000241", 2000, 2000)])[0].Sources.Single();
        Assert.Equal(2000, s.Disponible);   // 0 + 6000 − 6000 + min(2000, 6000)
    }

    [Fact]
    public void Affectation_d_une_autre_commande_soldee_ignoree()
    {
        var autre = new CleCommande(300, "9");
        // Lien S soldé d'une commande qui a encore des lignes de l'article à livrer : compte dans l'affecté total
        var compte = Calculer([Lien(Of2, autre, 0, 0, 3000, type: "S", solde: true, encore: true)], lots: [new("005002", "M", 5000)])[0];
        Assert.Equal(2000, compte.Sources.Single().Disponible);
        // Même lien, mais la commande n'a plus rien à livrer de l'article : ignoré
        var ignore = Calculer([Lien(Of2, autre, 0, 0, 3000, type: "S", solde: true, encore: false)], lots: [new("005002", "M", 5000)])[0];
        Assert.Equal(5000, ignore.Sources.Single().Disponible);
    }

    [Fact]
    public void OF_sur_affecte_ailleurs_masque_et_son_lot_jamais_repropose()
    {
        var autre = new CleCommande(300, "9");
        var a = Calculer([Lien(Of2, autre, 0, 0, 8000, type: "S", solde: true, encore: true)], lots: [new("005002", "M", 5000)])[0];
        Assert.Empty(a.Sources);   // dispo −3000, rien pour cette commande : ni ligne d'OF, ni lot libre (arbitrage l. 1809)
    }

    [Fact]
    public void Lots_sans_lien_proposes_en_stock_libre()
    {
        var a = Calculer([], lots: [new("009991", "M", 1000), new("009991", "A", 300), new("009992", "Q", 50)])[0];
        var s = Assert.Single(a.Sources);   // dépôt Q ignoré
        Assert.Equal((NatureSource.Lot, "00999/1", 1000, 300, 1300), (s.Nature, s.Of.Libelle, s.Stock, s.Surplus, s.Disponible));
    }

    [Fact]
    public void OF_sans_rien_pour_la_commande_et_sans_disponible_masque() =>
        Assert.Empty(Calculer([Lien(Of2, new CleCommande(300, "9"), 1000, 1000, 1000)])[0].Sources);

    [Fact]
    public void OF_a_ouvrir_bloque_et_BAG()
    {
        var d = Donnees([Lien(Of1, Cde, 10000, 0, 10000)], [], []) with
        {
            OfAOuvrir = new HashSet<NumeroOf> { Of1 }, Bloques = new HashSet<(NumeroOf, string)> { (Of1, L.ToutesReferences) },
            Bags = new Dictionary<(NumeroOf, string), EtatBag>
            {
                [(Of1, L.Reference("393228", ""))] = new(1, null, null, new(2026, 10, 1), null, ""),
                [(Of1, L.Reference("393228", "B"))] = new(1, null, null, null, null, "O"),   // autre indice : ne s'applique pas
            },
        };
        var s = L.Calculer(d, Cde)[0].Sources.Single();
        Assert.Equal((NatureSource.OfAOuvrir, true, "BAG envoyé"), (s.Nature, s.LotBloque, s.Bag!.Libelle));
    }

    [Fact]
    public void Livre_borne_a_l_affecte_de_la_commande()
    {
        // Affecté 100 à cette commande, 900 à d'autres ; 600 livrés à cette commande : seuls 100 sont réintégrés
        var autre = new CleCommande(300, "9");
        var s = Calculer([Lien(Of1, Cde, 0, 0, 100, type: "S", solde: true, encore: true), Lien(Of1, autre, 0, 0, 900, type: "S", solde: true, encore: true)],
            lots: [new("000241", "M", 1500)], livraisons: [new("000241", 600, 600)])[0].Sources.Single();
        Assert.Equal(600, s.Disponible);   // 1500 − 1000 + min(600, 100) (et non + 600)
    }

    [Fact]
    public void Lot_d_un_produit_fini_et_OF() =>
        Assert.Equal(("685372", new NumeroOf(68537, "2")), (L.NumeroLot(new NumeroOf(68537, "2")), L.OfDuLot("685372")));

    // ---------- Jeux d'essai ----------
    private static L.Lien Lien(NumeroOf of, CleCommande cde, int lance, int fabrique, int affecte, string type = " ", bool solde = false,
        bool encore = false) => new(of, cde.Numero, cde.Cle, lance, fabrique, affecte, type.Trim(), solde, encore);

    private static L.Donnees Donnees(List<L.Lien> liens, List<L.Lot> lots, List<L.Livraison> livraisons) => new(
        [new L.ArticleCommande("393228", "", "ETIQUETTE", "", true, 10000)],
        new Dictionary<(string, string), IReadOnlyList<L.Lien>> { [("393228", "")] = liens },
        new Dictionary<(string, string), IReadOnlyList<L.Lot>> { [("393228", "")] = lots },
        new Dictionary<(string, string), IReadOnlyList<L.Livraison>> { [("393228", "")] = livraisons },
        new HashSet<NumeroOf>(), new HashSet<(NumeroOf, string)>(), new Dictionary<(NumeroOf, string), EtatBag>());

    private static List<ArticleALancer> Calculer(List<L.Lien> liens, List<L.Lot>? lots = null, List<L.Livraison>? livraisons = null) =>
        L.Calculer(Donnees(liens, lots ?? [], livraisons ?? []), Cde);
}
