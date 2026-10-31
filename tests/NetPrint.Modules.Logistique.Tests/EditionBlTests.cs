using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Noyau.Societes;
using NetPrint.Socle;
using NetPrint.Socle.Session;
using Xunit.Abstractions;

namespace NetPrint.Modules.Logistique.Tests;

// Édition du BL (EXP035P2) : règles du modèle sans base, puis PDF d'un BL réel du client d'essai
[Collection(EcrituresNp11f.Collection)]
public class EditionBlTests(ITestOutputHelper sortie)
{
    // ---------- Jeux de données ----------
    private static EnteteEditionBl Entete(bool adresseClient = false, bool refClientDabord = false, string regime = "F") => new(
        165139, "", new DateOnly(2023, 11, 10), "CLARINS", "12 AVENUE DE LA PORTE", "", "95000", "PONTOISE", "", ["", "Quai 3", ""],
        "F", "", "M", "TRANSPORTS X", "95000 PONTOISE", "0102030405", "MENTION", 34, 5, 1, 376,
        "CLARINS SIEGE", "4 RUE BERTEAUX", "", "75008", "PARIS", "FOUR12", "FR123", regime, "FR999", true, adresseClient, refClientDabord,
        false, "1", "0102", "0103", "achats@clarins.test", "", false, false, false);

    private static ElementEditionBl Element(int numero = 1, string etat = "C", int type = 2, string norme = "", IReadOnlyList<string>? textes = null) => new(
        numero, etat, type, 12345, "A", 5000, "T", 12, "IMP1", "PO-77", new DateOnly(2023, 10, 2), 10, 500, "C", 0, 0, "Q", 28, "DUPONT",
        false, 0, "1", norme, textes ?? [], "PO-77", new DateOnly(2023, 10, 1));

    private static LigneEditionBl Ligne(int ligne = 1, string article = "ART1", bool imprimee = true, string refClient = "",
        int quantite = 5000, bool enUc = false, int emballage = 0, string appel = "", string norme = "") => new(
        1, ligne, article, "01", quantite, ["ETUI 50 ML"], "T", 1.5m, 10, 500, 0, 0, "C", imprimee, enUc, emballage, "IMP1", refClient,
        "", "", appel, appel == "" ? null : new DateOnly(2023, 11, 1), norme);

    private static DonneesEditionBl Donnees(EnteteEditionBl? e = null, IReadOnlyList<ElementEditionBl>? elements = null,
        IReadOnlyList<LigneEditionBl>? lignes = null, IReadOnlyList<LotEditionBl>? lots = null,
        Dictionary<(string, string), IReadOnlyList<string>>? normes = null, bool partiel = false) =>
        new(partiel, e ?? Entete(), elements ?? [Element()], lignes ?? [Ligne()], lots ?? [], ["Merci"], normes ?? [], LibellesBl.EnFrancais);

    // ---------- Règles ----------
    [Fact]
    public void En_tete_destinataire_transport_et_totaux()
    {
        var m = BonLivraison.Construire(Donnees());
        Assert.False(m.Retour);
        Assert.Equal(["CLARINS", "12 AVENUE DE LA PORTE", "95000 PONTOISE"], m.Destinataire);
        Assert.Equal(["Quai 3"], m.Observations);
        Assert.Contains(("Port", "Franco"), m.Transport);
        Assert.Contains(("Livraison", "Messagerie"), m.Transport);
        Assert.Contains(("Notre n° CEE", "FR999"), m.Transport);
        Assert.DoesNotContain(m.Transport, t => t.Libelle == "Votre n° CEE");   // client France
        Assert.Equal("28 DUPONT", m.Representant);
        Assert.Equal(["MENTION"], m.Introduction);
        Assert.Equal((34, 5, 1), (m.PoidsTotal, m.ColisTotal, m.PalettesTotal));
    }

    [Fact]
    public void Retour_client_a_l_export_et_adresse_du_client()
    {
        var m = BonLivraison.Construire(Donnees(Entete(adresseClient: true, regime: "C"), [Element(etat: "R")]));
        Assert.True(m.Retour);
        Assert.Contains(("Votre n° CEE", "FR123"), m.Transport);
        Assert.Empty(m.Introduction.Where(l => l.StartsWith("Commande du client")));   // seulement pour un BL client ou vierge

        m = BonLivraison.Construire(Donnees(Entete(adresseClient: true)));
        Assert.Equal(["Commande du client : CLARINS SIEGE", "4 RUE BERTEAUX", "75008 PARIS", "MENTION"], m.Introduction);
    }

    [Fact]
    public void Ligne_article_reference_quantite_colis_poids_appel()
    {
        var g = Assert.Single(BonLivraison.Construire(Donnees(lignes: [Ligne(refClient: "CLA-9", appel: "AP42")])).Groupes);
        Assert.Equal("Commande 12345/A · 5 000 ex.", g.Titre);
        var l = Assert.Single(g.Lignes);
        Assert.Equal("ART1/01", l.Reference);
        Assert.Equal(["V/Réf. : CLA-9"], l.Complements);
        Assert.Equal("5 000", l.Quantite);
        Assert.Equal("Totalité", l.Code);
        Assert.Equal(["10 Carton(s)", "de 500 ex."], l.Colis);
        Assert.Equal("15", l.Poids);
        Assert.Equal(["N° d'appel : AP42 du 01/11/2023"], l.Details);
    }

    [Fact]
    public void Reference_client_d_abord_et_article_masque()
    {
        var g = BonLivraison.Construire(Donnees(Entete(refClientDabord: true),
            lignes: [Ligne(refClient: "CLA-9"), Ligne(2, imprimee: false), Ligne(3, article: "")])).Groupes[0];
        Assert.Equal(2, g.Lignes.Count);                       // ligne 2 : article à ne pas imprimer (SPF010.AOPT1 = 'N')
        Assert.Equal(("CLA-9", "N/Réf. : ART1/01"), (g.Lignes[0].Reference, g.Lignes[0].Complements[0]));
        Assert.Equal(("", ""), (g.Lignes[1].Reference, g.Lignes[1].Quantite));   // texte : désignations seules
        Assert.Equal(["ETUI 50 ML"], g.Lignes[1].Designations);
    }

    [Theory]
    [InlineData(5000, true, 500, "10", "Carton(s) de 500 ex.")]
    [InlineData(5100, true, 500, "5 100", "")]                 // reste : quantité en exemplaires (le RPG perdait le reste)
    [InlineData(5000, false, 500, "5 000", "")]
    public void Quantite_en_unites_de_conditionnement(int quantite, bool enUc, int emballage, string attendue, string uc)
    {
        var l = BonLivraison.Construire(Donnees(lignes: [Ligne(quantite: quantite, enUc: enUc, emballage: emballage)])).Groupes[0].Lignes[0];
        Assert.Equal(attendue, l.Quantite);
        Assert.Equal(uc, l.Designations.Count > 1 ? l.Designations[0] : "");
    }

    [Fact]
    public void BL_revendeur_expediteur_sans_societe_ni_representant()
    {
        var m = BonLivraison.Construire(Donnees(Entete(adresseClient: true) with { Revendeur = true },
            elements: [Element(type: 1, norme: "FSC")], normes: new() { [("1", "FSC")] = ["Certifié FSC"] }));
        Assert.Equal(["CLARINS SIEGE", "4 RUE BERTEAUX", "75008 PARIS", "Tél. 0102", "Fax 0103", "achats@clarins.test"], m.Expediteur);
        Assert.Equal("", m.Representant);
        Assert.DoesNotContain(m.Transport, t => t.Libelle == "Notre n° CEE");
        Assert.Equal(["MENTION"], m.Introduction);                 // pas d'adresse du client donneur d'ordre
        Assert.Empty(m.Groupes[0].Lignes[0].Details);              // pas de mention de norme

        // Sous-traitance : la société reste l'expéditeur
        Assert.Null(BonLivraison.Construire(Donnees(Entete() with { Revendeur = true }, [Element(etat: "S")])).Expediteur);
        Assert.Null(BonLivraison.Construire(Donnees()).Expediteur);
    }

    [Fact]
    public void Presentation_PRINTOR_par_article()
    {
        var e2 = Element(2) with { Dossier = 12300, Cle = "B", RefCommandeClient = "", DateCommandeClient = null };
        var lignes = new[]
        {
            Ligne(1, article: "ZZZ", appel: "A2"),
            Ligne(2, article: "AAA", refClient: "CLA-1", appel: "A9", norme: "FSC"),
            Ligne(1, article: "AAA", refClient: "CLA-1", appel: "A1") with { Element = 2 },
            Ligne(3, article: "") ,                                 // texte d'un BL client : non imprimé
        };
        var m = BonLivraison.Construire(Donnees(Entete() with { Presentation = "2" }, [Element(), e2], lignes,
            normes: new() { [("1", "FSC")] = ["Certifié FSC"] }));
        Assert.Equal(["AAA/01 · V/Réf. : CLA-1", "ZZZ/01 · V/Réf. : IMP1"], m.Groupes.Select(g => g.Titre));
        var aaa = m.Groupes[0];
        Assert.Equal(2, aaa.Lignes.Count);
        Assert.Equal(["N/cde 12300/B", "N° d'appel : A1 du 01/11/2023"], aaa.Lignes[0].Details);     // appel A1 avant A9
        Assert.Equal("V/cde : PO-77 du 01/10/2023 - N/cde 12345/A", aaa.Lignes[1].Details[0]);
        Assert.All(aaa.Lignes, l => Assert.Equal("", l.Reference));
        Assert.Equal(["Certifié FSC"], aaa.Textes);                                                   // normes en fin d'article
    }

    [Fact]
    public void BL_partiel_sans_totaux()
    {
        var m = BonLivraison.Construire(Donnees(partiel: true));
        Assert.Equal((0, 0, 0), (m.PoidsTotal, m.ColisTotal, m.PalettesTotal));
    }

    [Fact]
    public void Deux_conditionnements_d_un_produit_fabrique()
    {
        var el = Element(type: 1) with { NbUc2 = 2, QteUc2 = 6000 };
        Assert.Equal(["10 Carton(s)", "de 500 ex.", "2 Palette(s)", "de 6 000 ex."], BonLivraison.ColisElement(el));
    }

    [Fact]
    public void Colis_avec_mecompte()
    {
        var l = Ligne() with { ColisMecompte = 1, ExemplairesMecompte = 120 };
        Assert.Equal(["10 Carton(s)", "de 500 ex.", "et 120 ex.", "en 1 Carton(s)"], BonLivraison.ColisLigne(l));
        Assert.Equal("15,36", BonLivraison.Poids(l));   // 1,5 × 10 + 1,5 × 120 / 500
    }

    [Fact]
    public void Lots_consecutifs_regroupes_lot_fournisseur_et_unite()
    {
        var lots = new LotEditionBl[] { new(1, 1, "12345A", 1000, ""), new(1, 1, "12345A", 500, ""), new(1, 1, "12399B", 200, "F-77"),
            new(1, 1, "12400C", 50, "F-77") };   // même lot fournisseur, lots NetPrint différents : deux lignes
        Assert.Equal(["N° lot 12345A  1 500 ex.", "N° lot F-77  200 ex.", "N° lot F-77  50 ex."], BonLivraison.Lots(lots, "").Select(x => x.Texte));
        Assert.Equal(["N° lot 12345A  1 000 KG"], BonLivraison.Lots(lots[..1], "KG").Select(x => x.Texte));

        var l = BonLivraison.Construire(Donnees(lots: lots)).Groupes[0].Lignes[0];
        Assert.Equal(3, l.Details.Count);
        l = BonLivraison.Construire(Donnees(Entete() with { ImprimerLots = false }, lots: lots)).Groupes[0].Lignes[0];
        Assert.Empty(l.Details);
    }

    [Fact]
    public void Codes_barres_des_lots_selon_les_options_et_date_de_sortie_de_fabrication()
    {
        var lots = new LotEditionBl[] { new(1, 1, "12345A", 1000, "F-77", 25000, new DateOnly(2023, 10, 5)) };
        // Société sans l'option : pas de code-barres
        var l = BonLivraison.Construire(Donnees(Entete() with { CodeBarreClient = true }, lots: lots)).Groupes[0].Lignes[0];
        Assert.Empty(l.CodesBarres!);
        // Option société + client : code-barres du n° de lot NetPrint (pas du lot fournisseur) et quantité fabriquée
        l = BonLivraison.Construire(Donnees(Entete() with { CodeBarreClient = true, CodesBarresSociete = true }, lots: lots)).Groupes[0].Lignes[0];
        var cb = Assert.Single(l.CodesBarres!);
        Assert.Equal(("12345A", "Qté totale fab. : 25 000"), (cb.Lot, cb.QuantiteFabriquee));
        // Option société + article
        l = BonLivraison.Construire(Donnees(Entete() with { CodesBarresSociete = true }, lignes: [Ligne() with { CodeBarreArticle = true }],
            lots: lots)).Groupes[0].Lignes[0];
        Assert.Single(l.CodesBarres!);
        // Date de sortie de fabrication demandée par le client
        l = BonLivraison.Construire(Donnees(Entete() with { DateFabSurLots = true }, lots: lots)).Groupes[0].Lignes[0];
        Assert.Contains("N° lot F-77  1 000 ex. 05/10/2023", l.Details);

        // Lot non encodable : pas de code-barres, pas d'erreur
        l = BonLivraison.Construire(Donnees(Entete() with { CodeBarreClient = true, CodesBarresSociete = true },
            lots: [new(1, 1, "12é45A", 10, "")])).Groupes[0].Lignes[0];
        Assert.Empty(l.CodesBarres!);
    }

    [Fact]
    public void Code_39_relu_par_un_lecteur()
    {
        var svg = NetPrint.Modules.Logistique.Impressions.CodeBarres.Code39Svg("66426A");
        // Barres du SVG → ligne de pixels → lecture Code 39
        var barres = System.Text.RegularExpressions.Regex.Matches(svg, "x=\"([0-9]+)\" y=\"0\" width=\"([0-9]+)\"")
            .Select(x => (int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value))).ToList();
        var largeur = int.Parse(System.Text.RegularExpressions.Regex.Match(svg, "viewBox=\"0 0 ([0-9]+)").Groups[1].Value);
        var ligne = new ZXing.Common.BitArray(largeur + 20);
        foreach (var (x, w) in barres) for (var i = 0; i < w; i++) ligne[x + i + 10] = true;
        Assert.Equal("66426A", new ZXing.OneD.Code39Reader().decodeRow(0, ligne, null).Text);
    }

    [Fact]
    public void Libelles_traduits_dans_la_langue_du_client()
    {
        var traductions = Enumerable.Repeat("", 50).ToArray();
        (traductions[0], traductions[18], traductions[44], traductions[30]) = ("Delivery note", "Reference", "Delivery call", "Order");
        var listes = new Dictionary<(int, string), string> { [(32, "T")] = "Total", [(28, "C")] = "Parcel(s)", [(31, "F")] = "EXW" };
        var L = LibellesBl.Pour(traductions, listes);
        Assert.Equal(("Delivery note", "Reference", "Quantité"), (L.Titre, L.Reference, L.Quantite));   // non traduit : français

        var m = BonLivraison.Construire(Donnees(lignes: [Ligne(appel: "A1")]) with { Libelles = L });
        var l = m.Groupes[0].Lignes[0];
        Assert.StartsWith("Order 12345/A", m.Groupes[0].Titre);
        Assert.Equal("Total", l.Code);
        Assert.Equal("10 Parcel(s)", l.Colis[0]);
        Assert.Contains(("Port", "EXW"), m.Transport);
        Assert.Equal("Delivery call : A1 du 01/11/2023", l.Details[0]);
        Assert.Equal("10/11/2023", L.DateLongue(new DateOnly(2023, 11, 10)));
        Assert.Same(LibellesBl.EnFrancais, LibellesBl.Pour(Enumerable.Repeat("", 50).ToList(), new Dictionary<(int, string), string>()));
    }

    [Fact]
    public void Produit_fabrique_normes_fournisseur_et_textes()
    {
        var normes = new Dictionary<(string, string), IReadOnlyList<string>> { [("1", "FSC")] = ["Certifié FSC"] };
        var g = BonLivraison.Construire(Donnees(elements: [Element(etat: "S", type: 1, norme: "FSC", textes: ["Palette filmée"])],
            normes: normes)).Groupes[0];
        Assert.Equal("", g.Titre);
        var l = Assert.Single(g.Lignes);
        Assert.Equal(("12345/A", "5 000", "12"), (l.Reference, l.Quantite, l.Poids));
        Assert.Equal(["Réf. imp. : IMP1", "Réf. cde : PO-77 du 02/10/2023", "ETUI 50 ML"], l.Designations);
        Assert.Equal(["10 Carton(s)", "de 500 ex."], l.Colis);
        Assert.Equal(["Certifié FSC"], l.Details);
        Assert.Equal(["Fournisseur n° 376", "Palette filmée"], g.Textes);
    }

    // ---------- Base de test ----------
    private sealed class Utilisateur : IUtilisateurCourant
    {
        public Task<UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new UtilisateurConnecte("NP11", BaseDeTest.Societe, "TEST", BaseDeTest.CompteGeneral));
    }

    private sealed class SansSociete : ISocietes
    {
        public Task<string?> LireNomAsync(int societe, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IdentiteSociete?> LireIdentiteAsync(int societe, string enseigne = "", string typeLogo = "", CancellationToken ct = default) =>
            Task.FromResult<IdentiteSociete?>(null);
    }

    private static EditionBlService Service() => new(new EditionBlDb2(BaseDeTest.Configuration), new SansSociete(), new Utilisateur());

    private static async Task<(int Bl, string Edite, int Date)> BlDuClientAsync(int? numero = null)
    {
        await using var cnx = new OdbcConnection(BaseDeTest.Configuration.GetConnectionString("Db2"));
        await cnx.OpenAsync();
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"""
            SELECT HNOBL, HEDBL, HDEDBA * 10000 + HDEDBM * 100 + HDEDBJ FROM NP11F.EXP009
            WHERE RSOC = ? AND HGENE = ? AND HCLIE = ? {(numero is null ? "" : "AND HNOBL = ?")}
              AND (SELECT COUNT(*) FROM NP11F.EXP010 e WHERE e.RSOC = EXP009.RSOC AND e.HNOBL = EXP009.HNOBL) > 1
            ORDER BY HNOBL DESC FETCH FIRST 1 ROW ONLY
            """;
        foreach (var v in new object?[] { BaseDeTest.Societe, BaseDeTest.CompteGeneral, BaseDeTest.CompteClient, numero }.Where(v => v is not null))
            cmd.Parameters.AddWithValue("", v);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        return (Convert.ToInt32(rd.GetValue(0)), Convert.ToString(rd.GetValue(1))!.Trim(), Convert.ToInt32(rd.GetValue(2)));
    }

    [FactDb2]
    public async Task Apercu_d_un_BL_du_client_d_essai_sans_ecriture()
    {
        var avant = await BlDuClientAsync();
        var lues = await new EditionBlDb2(BaseDeTest.Configuration).LireAsync(BaseDeTest.Societe, "NP11", avant.Bl, default);
        Assert.NotNull(lues);
        var m = BonLivraison.Construire(lues);
        sortie.WriteLine($"BL {m.Numero} : {m.Groupes.Count} élément(s), {m.Groupes.Sum(g => g.Lignes.Count)} ligne(s), " +
            $"{m.PoidsTotal} kg, {m.ColisTotal} colis ; {string.Join(" | ", m.Transport.Select(t => $"{t.Libelle} {t.Valeur}"))}");

        Assert.False(lues.Partiel);
        var pdf = await Service().EditerAsync(avant.Bl, apercu: true);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Contenu, 0, 4));
        var fichier = Path.Combine(Path.GetTempPath(), pdf.Nom);
        await File.WriteAllBytesAsync(fichier, pdf.Contenu);
        sortie.WriteLine(fichier);
        Assert.Equal(avant, await BlDuClientAsync(avant.Bl));   // produire le PDF n'écrit rien
        await Service().EditerAsync(avant.Bl, apercu: false);
        Assert.Equal(avant, await BlDuClientAsync(avant.Bl));

        await Assert.ThrowsAsync<RegleMetierException>(() => Service().EditerAsync(999999, apercu: true));
    }

    [FactDb2Ecriture]
    public async Task Impression_marque_le_BL_edite()
    {
        var avant = await BlDuClientAsync();
        await Service().MarquerEditeAsync(avant.Bl);
        var apres = await BlDuClientAsync(avant.Bl);
        Assert.Equal("N", apres.Edite);
        var t = DateTime.Today;
        Assert.Equal(t.Year * 10000 + t.Month * 100 + t.Day, apres.Date);
    }
}
