using NetPrint.Modules.Ventes.Services;
using static NetPrint.Modules.Ventes.Services.MoteurPrixCommande;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Cascade de prix de CTL520 (SPRCPX / SPRCP1) et calculs de la ligne
public class MoteurPrixCommandeTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 10, 30);

    private static Article Art(bool modele = false, decimal pv = 0, int emballage = 0, bool parEmballage = false) =>
        new(modele, 42, pv, 1.5m, 0, "U", emballage, parEmballage, 100, 50);

    private static DonneesTarif D(Article? a = null, string categorie = "", IReadOnlyList<TarifDate>? tarifs = null,
        IReadOnlyList<TarifQuantite>? quantitatif = null, IReadOnlyList<TarifQuantite>? modeleClient = null,
        IReadOnlyList<TarifQuantite>? modeleGeneral = null, bool prorata = false, bool mandataire = false) => new(
        a ?? Art(), categorie, 411100, 376, tarifs ?? [],
        new Dictionary<string, long> { ["01"] = 1000, ["02"] = 5000, ["03"] = 999999 },
        quantitatif ?? [], modeleClient ?? [], modeleGeneral ?? [], prorata, 200, 50, mandataire);

    private static TarifDate T(string type, decimal prix, int gen = 0, int cli = 0, string tranche = "",
        DateOnly? date1 = null, decimal prix2 = 0, DateOnly? date2 = null) =>
        new(type, gen, cli, tranche, prix, date1 ?? new DateOnly(2026, 1, 1), prix / 2, prix2, date2, prix2 / 2);

    [Fact]
    public void Prix_date_le_plus_recent_sans_depasser_aujourd_hui()
    {
        var t = T("A", 10, date1: new(2025, 1, 1), prix2: 12, date2: new(2026, 6, 1));
        Assert.Equal(12, Prix(t, Aujourdhui, "x").Prix);
        Assert.Equal(10, Prix(t with { Date2 = new(2027, 1, 1) }, Aujourdhui, "x").Prix);   // date future ignorée
        Assert.Equal(0, Prix(t with { Date2 = t.Date1 }, Aujourdhui, "x").Prix);              // dates égales : 0
        Assert.Equal(0, Prix(t with { Date1 = null, Date2 = null }, Aujourdhui, "x").Prix);
    }

    [Fact]
    public void Tarif_de_categorie_client_prioritaire_et_definitif_meme_a_zero()
    {
        var d = D(Art(pv: 9), "C", [T("C", 0), T("A", 7)]);
        var r = Tarif(d, 100, Aujourdhui, 0);
        Assert.Equal(0, r.Prix);
        Assert.StartsWith("Tarif catégorie", r.Source);
    }

    [Fact]
    public void Tranche_client_la_plus_petite_qui_couvre_la_quantite()
    {
        var d = D(tarifs: [T("Q", 5, 411100, 376, "01"), T("Q", 4, 411100, 376, "02"), T("Q", 3, 0, 0, "01")]);
        Assert.Equal(5, Tarif(d, 800, Aujourdhui, 0).Prix);
        Assert.Equal(4, Tarif(d, 1500, Aujourdhui, 0).Prix);
        Assert.Equal("Tranche client", Tarif(d, 1500, Aujourdhui, 0).Source);
    }

    [Fact]
    public void Tranche_generale_si_le_client_n_a_pas_de_tranche()
    {
        var d = D(tarifs: [T("Q", 3, 0, 0, "01"), T("Q", 2, 0, 0, "03")]);
        Assert.Equal(3, Tarif(d, 1000, Aujourdhui, 0).Prix);
        Assert.Equal(2, Tarif(d, 1001, Aujourdhui, 0).Prix);
    }

    [Fact]
    public void Tranche_en_nombre_d_emballages()
    {
        // 6 000 exemplaires par cartons de 10 = 600 emballages : tranche 01 (≤ 1 000)
        var d = D(Art(emballage: 10, parEmballage: true), tarifs: [T("Q", 5, 0, 0, "01"), T("Q", 4, 0, 0, "02")]);
        Assert.Equal(5, Tarif(d, 6000, Aujourdhui, 0).Prix);
    }

    [Fact]
    public void Tarif_quantitatif_client_avec_son_unite()
    {
        var d = D(Art(pv: 9), quantitatif: [new TarifQuantite(1, 999, 20, 12, "LM"), new TarifQuantite(1000, 9999, 18, 11, "LM")]);
        var r = Tarif(d, 2500, Aujourdhui, 0);
        Assert.Equal((18m, 11m, "LM"), (r.Prix, r.Cession, r.Unite));
    }

    [Fact]
    public void Prix_de_la_fiche_puis_tarif_A()
    {
        Assert.Equal(9, Tarif(D(Art(pv: 9), tarifs: [T("A", 7)]), 100, Aujourdhui, 0).Prix);
        Assert.Equal(7, Tarif(D(Art(pv: 0), tarifs: [T("A", 7)]), 100, Aujourdhui, 0).Prix);
        Assert.False(Tarif(D(Art(pv: 0)), 100, Aujourdhui, 0).Trouve);
    }

    [Fact]
    public void Quantite_nulle_sans_tarif()
    {
        Assert.False(Tarif(D(Art(pv: 9)), 0, Aujourdhui, 0).Trouve);
    }

    [Fact]
    public void Article_a_tarif_modele_ignore_categorie_et_tranches()
    {
        var d = D(Art(modele: true, pv: 9), "C", [T("C", 1), T("Q", 1, 0, 0, "01")],
            modeleClient: [new TarifQuantite(1, 10000, 30, 20, "LM")]);
        var r = Tarif(d, 500, Aujourdhui, 0);
        Assert.Equal((30m, 20m, "LM", "Tarif modèle"), (r.Prix, r.Cession, r.Unite, r.Source));
    }

    [Fact]
    public void Tarif_modele_au_prorata_de_la_surface()
    {
        // Article 100 × 50, modèle 200 × 50 : moitié du prix
        var d = D(Art(modele: true), modeleGeneral: [new TarifQuantite(1, 10000, 30, 20, "")], prorata: true);
        var r = Tarif(d, 500, Aujourdhui, 0);
        Assert.Equal((15m, 10m), (r.Prix, r.Cession));
        Assert.Equal(0.5m, Ratio(d));
    }

    [Fact]
    public void Tarif_modele_sans_tranche_puis_prix_de_la_fiche()
    {
        var d = D(Art(modele: true, pv: 9), modeleClient: [new TarifQuantite(1, 100, 30, 20, "")]);
        Assert.Equal(9, Tarif(d, 500, Aujourdhui, 0).Prix);
    }

    [Fact]
    public void Representant_mandataire_prix_majore_et_arrondi_a_l_unite()
    {
        // 9 / (100 − 10) × 100 = 10
        Assert.Equal(10, Tarif(D(Art(pv: 9), mandataire: true), 100, Aujourdhui, 10).Prix);
        Assert.Equal(13, Tarif(D(Art(pv: 12.34m), mandataire: true), 100, Aujourdhui, 5).Prix);
    }

    [Theory]
    [InlineData(12.5, 0, 12.5)]
    [InlineData(12.5, 10, 11.25)]
    [InlineData(9.99, 15, 8.49)]     // 8,4915 tronqué
    public void Prix_net_tronque(decimal brut, decimal remise, decimal net) => Assert.Equal(net, PrixNet(brut, remise));

    [Theory]
    [InlineData(49, 2500, 1000, 122.5)]
    [InlineData(450, 5000, 999999999, 450)]
    [InlineData(2, 3, 0, 2)]
    [InlineData(1.33, 333, 1000, 0.44)]
    public void Montant_de_la_ligne(decimal prix, int quantite, long tnum, decimal montant) =>
        Assert.Equal(montant, Montant(prix, quantite, tnum));

    [Fact]
    public void Commission_par_rapport_prix_de_vente_sur_cession()
    {
        var (seuils, taux) = TableCommission("110120130140150160170180190200", "0100020003000400050006000700080009001000");
        Assert.Equal(1.10m, seuils[0]);
        Assert.Equal(0.0100m, taux[0]);
        Assert.Equal(1m, Commission(10, 10, "B", seuils, taux, 7));      // rapport 1 ≤ 1,10 : 1 %
        Assert.Equal(3m, Commission(13, 10, "B", seuils, taux, 7));      // 1,30 : 3 %
        Assert.Equal(10m, Commission(25, 10, "E", seuils, taux, 7));     // ≥ 2,00 : 10 %
        Assert.Equal(7m, Commission(25, 10, "", seuils, taux, 7));       // table inutilisée : taux de l'en-tête
        Assert.Equal(0m, Commission(25, 0, "B", seuils, taux, 7));       // pas de cession
    }

    [Fact]
    public void Maxi_livrable_selon_les_tranches()
    {
        var societe = new List<(int, decimal)> { (1000, 10), (10000, 5) };
        Assert.Equal(1100, MaxiLivrable(1000, [], societe));
        Assert.Equal(5250, MaxiLivrable(5000, [], societe));
        Assert.Equal(20000, MaxiLivrable(20000, [], societe));                           // au-delà : 0 %
        Assert.Equal(1020, MaxiLivrable(1000, [(5000, 2)], societe));                    // tranches du client prioritaires
    }
}
