using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;

namespace NetPrint.Modules.Ventes.Tests.Commandes;

// Requête de la liste des commandes : conditions ajoutées selon les critères, toujours paramétrées
public class RequeteListeCommandesTests
{
    private static RequeteListeCommandes.Requete Construire(CriteresCommandes c, int societe = 7) =>
        RequeteListeCommandes.Construire("NP11F", societe, c);

    private static int NbMarqueurs(string sql) => sql.Count(ch => ch == '?');

    // Clause WHERE principale (après les jointures et la sous-requête des documents)
    private static string Where(string sql) => sql[sql.IndexOf("WHERE a.RSOC = ?", StringComparison.Ordinal)..];

    [Fact]
    public void Societe_en_premier_parametre_et_types_3_et_4_par_defaut()
    {
        var r = Construire(new CriteresCommandes { Numero = 12345 });
        Assert.Equal(7, r.Parametres[0]);
        Assert.Contains("a.CCTYPC IN ('3', '4')", r.Sql);
        Assert.Contains("FROM NP11F.AB001 a", r.Sql);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    public void Type_choisi_passe_en_parametre(string type)
    {
        var r = Construire(new CriteresCommandes { Numero = 1, TypeCommande = type });
        Assert.Contains("a.CCTYPC = ?", r.Sql);
        Assert.Contains(type, r.Parametres);
    }

    [Fact]
    public void Sans_les_soldees_par_defaut()
    {
        Assert.Contains("a.CCTAGS = ' '", Construire(new CriteresCommandes { Numero = 1 }).Sql);
        Assert.DoesNotContain("CCTAGS = ' '", Construire(new CriteresCommandes { Numero = 1, AvecSoldees = true }).Sql);
    }

    [Fact]
    public void Autant_de_parametres_que_de_marqueurs_avec_tous_les_criteres()
    {
        var c = new CriteresCommandes
        {
            Numero = 12345, Cle = "a", CompteGeneral = 411000, CompteClient = 1234, Representant = 12, Devis = 555,
            Produit = 42, Article = "*ABC*", Indice = "01", ReferenceImprime = "REF", ReferenceCommandeClient = "PO-1",
            NumeroAppel = "APP1", Fabricant = "jd", BonLivraison = 99, Facture = 88, Categorie = "$1", SousCategorie = "01",
            Designation = "étiquette rouge", DateDu = new(2026, 1, 1), DateAu = new(2026, 12, 31),
            LivraisonDu = new(2026, 2, 1), LivraisonAu = new(2026, 3, 1), TypeCommande = "4", Enseigne = "e",
            FiniLargeur = 10.5m, FiniHauteur = 20m, FiniTroisiemeCote = 3m,
        };
        var r = Construire(c);
        Assert.Equal(NbMarqueurs(r.Sql), r.Parametres.Count);
    }

    [Fact]
    public void Aucune_valeur_saisie_dans_le_texte_SQL()
    {
        var piege = "X' OR '1'='1";
        var r = Construire(new CriteresCommandes
        {
            Article = piege, ReferenceCommandeClient = piege, NumeroAppel = piege, Designation = piege, Fabricant = piege,
        });
        Assert.DoesNotContain("OR '1'='1", r.Sql);
        Assert.DoesNotContain("X'", r.Sql);
    }

    [Fact]
    public void Numero_et_cle_en_majuscules()
    {
        var r = Construire(new CriteresCommandes { Numero = 12345, Cle = " b " });
        Assert.Contains("a.RDOSS = ?", r.Sql);
        Assert.Contains("a.CCDCLE = ?", r.Sql);
        Assert.Contains(12345, r.Parametres);
        Assert.Contains("B", r.Parametres);
    }

    [Fact]
    public void Client_compte_general_et_auxiliaire()
    {
        var r = Construire(new CriteresCommandes { CompteGeneral = 411000, CompteClient = 1234 });
        Assert.Contains("a.RGEN = ? AND a.RCLI = ?", r.Sql);
        Assert.Equal([7, 411000, 1234], r.Parametres.Where(p => p is int).Cast<int>().Take(3).ToArray());
    }

    [Fact]
    public void Compte_general_seul_ne_filtre_pas()
    {
        var r = Construire(new CriteresCommandes { CompteGeneral = 411000, Numero = 1 });
        Assert.DoesNotContain("a.RGEN", Where(r.Sql));
    }

    [Theory]
    [InlineData("ABC", "b.DNART = ?", "ABC")]
    [InlineData("ABC*", "b.DNART LIKE ?", "ABC%")]
    [InlineData("*ABC", "TRIM(b.DNART) LIKE ?", "%ABC")]
    [InlineData("*ABC*", "b.DNART LIKE ?", "%ABC%")]
    [InlineData("A_B*", "b.DNART LIKE ?", "A\\_B%")]
    public void Jokers_article(string saisie, string condition, string valeur)
    {
        var (c, v) = RequeteListeCommandes.Joker("b.DNART", saisie);
        Assert.StartsWith(condition, c);
        Assert.Equal(valeur, v);
    }

    [Fact]
    public void Article_saisi_en_minuscules_cherche_en_majuscules()
    {
        var r = Construire(new CriteresCommandes { Article = "abc*" });
        Assert.Contains("ABC%", r.Parametres);
    }

    [Fact]
    public void References_commencent_par_et_echappent_les_jokers_SQL()
    {
        var r = Construire(new CriteresCommandes { ReferenceCommandeClient = "po_10%" });
        Assert.Contains("UPPER(a.CCREFC) LIKE ? ESCAPE '\\'", r.Sql);
        Assert.Contains("PO\\_10\\%%", r.Parametres);
    }

    [Fact]
    public void Chaque_mot_de_la_designation_est_cherche()
    {
        var r = Construire(new CriteresCommandes { Designation = "  rouge   Étiquette rouge " });
        Assert.Equal(2, r.Sql.Split("UPPER(b.DNDESA) LIKE ?").Length - 1);
        Assert.Equal(3, r.Parametres.Count(p => Equals(p, "%ROUGE%")));
        Assert.Equal(3, r.Parametres.Count(p => Equals(p, "%ÉTIQUETTE%")));
    }

    [Fact]
    public void Dates_en_AAAAMMJJ()
    {
        var r = Construire(new CriteresCommandes
        {
            DateDu = new(2026, 1, 15), DateAu = new(2026, 2, 28), LivraisonDu = new(2026, 3, 1), LivraisonAu = new(2026, 3, 31),
        });
        Assert.Contains("(a.CCDATA * 10000 + a.CCDATM * 100 + a.CCDATJ) >= ?", r.Sql);
        Assert.Contains("b.DNDLIV <= ?", r.Sql);
        Assert.Equal([20260115, 20260228, 20260301, 20260331], r.Parametres.Skip(1).Cast<int>().ToArray());
    }

    [Fact]
    public void BL_et_facture_en_EXISTS_sans_doublon_de_ligne()
    {
        var r = Construire(new CriteresCommandes { BonLivraison = 99, Facture = 88 });
        Assert.Contains("EXISTS (SELECT 1 FROM NP11F.EXP010 x", r.Sql);
        Assert.Contains("EXISTS (SELECT 1 FROM NP11F.FAC090 y", r.Sql);
        Assert.DoesNotContain("JOIN NP11F.EXP010", r.Sql);
    }

    [Fact]
    public void Commande_sans_ligne_conservee_le_statut_est_dans_la_jointure()
    {
        var r = Construire(new CriteresCommandes { Numero = 1 });
        Assert.DoesNotContain("DNSTAT", Where(r.Sql));
        Assert.Contains("AND (b.DNSTAT = '1' OR (b.DNSTAT = '3'", r.Sql);
    }

    [Fact]
    public void Limite_a_une_ligne_de_plus_que_le_maximum()
    {
        var r = Construire(new CriteresCommandes { Numero = 1 });
        Assert.EndsWith($"FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY", r.Sql);
        Assert.Contains("ORDER BY a.RDOSS DESC, a.CCDCLE, b.DNNLIG", r.Sql);
    }

    [Fact]
    public void Bibliotheque_invalide_refusee() =>
        Assert.Throws<InvalidOperationException>(() =>
            RequeteListeCommandes.Construire("NP11F; DROP", 7, new CriteresCommandes { Numero = 1 }));
}
