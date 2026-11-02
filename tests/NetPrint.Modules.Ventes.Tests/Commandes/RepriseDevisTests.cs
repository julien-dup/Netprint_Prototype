using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Reprise d'un devis dans une ligne de commande (NEG520 F10, SPDEV)
public class RepriseDevisTests
{
    // Unité « le tout » reconnue à son diviseur (999999999), quel que soit son code
    private static RepriseDevis.Devis Devis(string unite = "LM", int quantite = 10000, decimal prix = 67.50m, decimal revient = 528.88m,
        bool articleDuDevis = false) =>
        new(new ReferenceDevis(99841, 0, 1), "etq 33.5x75 contrat SK3", "REF-IMP", unite, quantite, prix, revient,
            unite switch { "LT" or "TT" => 999999999, "LM" => 1000, "UN" => 1, _ => null }, 1000, articleDuDevis);

    private static AdresseDevis Adresse(int priorite = 0, string rs = "", int quantite = 4000) =>
        new(priorite, rs, rs == "" ? "" : "1 rue des Tests", "", rs == "" ? "" : "76000", rs == "" ? "" : "ROUEN", quantite, "Com 1", "Com 2", "");

    private static LigneSaisie Nouvelle() => new() { CodeVente = "2", NumeroAdresse = 3, Commentaire1 = "" };

    [Fact]
    public void Prix_au_mille_repris_tel_quel()
    {
        Assert.Equal(("LM", 67.50m), RepriseDevis.PrixDeVente(Devis()));
    }

    [Fact]
    public void Prix_le_tout_ramene_au_mille()
    {
        // 600 € le tout pour 2 500 → 240 € le mille (tronqué à 2 décimales comme le RPG)
        Assert.Equal(("LM", 240m), RepriseDevis.PrixDeVente(Devis("LT", 2500, 600m)));
        Assert.Equal(("LM", 222.22m), RepriseDevis.PrixDeVente(Devis("LT", 4500, 1000m)));
        Assert.Equal(("LM", 0m), RepriseDevis.PrixDeVente(Devis("LT", 0, 600m)));
        Assert.Equal(("LM", 240m), RepriseDevis.PrixDeVente(Devis("TT", 2500, 600m)));
    }

    [Fact]
    public void Prix_de_cession_d_apres_le_prix_de_revient()
    {
        // 528,88 / 10 000 = 0,05288 × 1 000 = 52,88
        Assert.Equal(52.88m, RepriseDevis.PrixCession(Devis()));
        // Le tout : cession au mille, comme le prix (337,46 / 2 500 × 1 000)
        Assert.Equal(134.98m, RepriseDevis.PrixCession(Devis("LT", 2500, 600m, 337.46m)));
        Assert.Equal(0.13m, RepriseDevis.PrixCession(Devis("UN", 2500, 600m, 337.46m)));
        Assert.Equal(0m, RepriseDevis.PrixCession(Devis("XX")));
    }

    [Fact]
    public void Sans_adresse_quantite_devisee_et_adresse_par_defaut()
    {
        var l = Nouvelle();
        RepriseDevis.Appliquer(l, Devis(), null);
        Assert.Equal(("", "etq 33.5x75 contrat SK3", "LM", 67.50m, true, 52.88m), (l.Article, l.Designation, l.Unite, l.PrixBrut, l.PrixSaisi, l.PrixCession));
        Assert.Equal((StatutsLigne.ALaDemande, 10000, 3, new ReferenceDevis(99841, 0, 1)), (l.Statut, l.Quantite, l.NumeroAdresse, l.Devis));
    }

    [Fact]
    public void Article_repris_si_option_societe()
    {
        var l = Nouvelle();
        RepriseDevis.Appliquer(l, Devis(articleDuDevis: true), null);
        Assert.Equal("REF-IMP", l.Article);
    }

    [Fact]
    public void Adresse_du_client_par_son_numero()
    {
        var l = Nouvelle();
        RepriseDevis.Appliquer(l, Devis(), Adresse(priorite: 7));
        Assert.Equal((7, false, 4000, ""), (l.NumeroAdresse, l.AdresseSiege, l.Quantite, l.Commentaire1));
    }

    [Fact]
    public void Adresse_vide_siege_du_client()
    {
        var l = Nouvelle();
        RepriseDevis.Appliquer(l, Devis(), Adresse());
        Assert.Equal(((int?)null, true, 4000), (l.NumeroAdresse, l.AdresseSiege, l.Quantite));
    }

    [Fact]
    public void Adresse_saisie_dans_le_devis_avec_ses_commentaires()
    {
        var l = Nouvelle();
        RepriseDevis.Appliquer(l, Devis(), Adresse(rs: "CLARINS LOGISTIQUE"));
        Assert.Equal(((int?)null, false, "CLARINS LOGISTIQUE", "ROUEN", "Com 1", "Com 2"),
            (l.NumeroAdresse, l.AdresseSiege, l.AdresseRaisonSociale, l.AdresseVille, l.Commentaire1, l.Commentaire2));
    }

    [Fact]
    public void Article_saisi_ensuite_garde_designation_et_unite_du_devis()
    {
        var l = Nouvelle();
        RepriseDevis.Appliquer(l, Devis("LT", 2500, 600m), null);
        l.Article = "393229";
        var c = SaisieLignesService.Completer(SaisieLignesServiceTests.Contexte(), l, SaisieLignesServiceTests.Article() with { Unite = "UN", Designation = "ARTICLE" }, null, 1000,
            new DateOnly(2026, 10, 31));
        Assert.Equal(("etq 33.5x75 contrat SK3", "LM", 240m, "NF1"), (c.Designation, c.Unite, c.PrixBrut, c.Norme));
    }
}
