using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;

namespace NetPrint.Modules.Facturation.Tests;

// Saisie d'une facture à partir de BL (FAC085 → FAC150) : regroupement, recalcul des lignes, report des corrections
public class SaisieFactureTests
{
    private static readonly ParametresGeneration P = new(
        new Dictionary<string, (string, string)> { ["V"] = ("V", "Vente"), ["W"] = ("V", "Vente 2"), ["R"] = ("R", "Remise"), ["P"] = ("P", "Port"), ["9"] = ("T", "Texte") },
        new Dictionary<string, decimal> { ["EX"] = 1, ["MI"] = 1000 });

    private static readonly AdresseEnvoi Adresse = new("", "CLIENT", "1 RUE", "", "75001", "PARIS", "");

    private static DossierAFacturer Dossier(int n) => new(n, "A", "4", "V", "EX", "Dossier " + n, "", 0, 0, "REF" + n, null, "", "", 0, 0, 0, "E", 0,
        "M. X", "", "", Adresse, true, "", "", []);

    private static ElementBlAFacturer Bl(int bl, DossierAFacturer d, bool retour = false, int mode = 1, int client = 20) =>
        new(bl, 1, 411100, client, 0, 0, mode, new ConditionsReglement("56", 30, 0, 0, 0), AdresseEnvoi.Aucune, 101, retour, 0, d,
            [new LigneBl(1, "ART", "", 10, "Article", "", "", new LigneCommandeNegoce(1, "V", "ART", "", 2, "EX", 0, false, false))], []);

    private static FactureSaisie Saisie(FacturePrevue f) =>
        SaisieFacture.VersSaisie(f, [], "CLIENT", P, new DateOnly(2026, 11, 1), [new CodeLibelle("56", "Virement")], [new CodeLibelle("101", "Rep")], true);

    [Fact]
    public void Une_facture_un_element_par_commande_quel_que_soit_le_mode()
    {
        var (d1, d2) = (Dossier(100), Dossier(200));
        var f = GenerationFactures.Regrouper([Bl(3, d2), Bl(1, d1, mode: 4), Bl(2, d1, mode: 0)], P);
        Assert.Equal([(100, 2), (200, 1)], f.Elements.Select(e => (e.Dossier.Numero, e.Bls.Count)));
        Assert.Equal(2, f.Elements[0].Lignes.Count);   // une ligne par BL
        Assert.Equal(60m, f.Ventes);
        Assert.Throws<RegleMetierException>(() => GenerationFactures.Regrouper([Bl(1, d1), Bl(2, d1, retour: true)], P));
        Assert.Throws<RegleMetierException>(() => GenerationFactures.Regrouper([Bl(1, d1), Bl(2, d1, client: 21)], P));
        Assert.Throws<RegleMetierException>(() => GenerationFactures.Regrouper([], P));
        Assert.True(GenerationFactures.Regrouper([Bl(1, d1, retour: true)], P).Avoir);
    }

    [Fact]
    public void Recalcul_des_lignes()
    {
        var s = Saisie(GenerationFactures.Regrouper([Bl(1, Dossier(100))], P));
        Assert.Equal((20m, "56", 101, "REF100"), (s.Elements[0].Ventes, s.ModeReglement, s.Representant!.Value, s.Reference));
        var lignes = s.Elements[0].Lignes;
        lignes[0].Quantite = 2500;
        lignes[0].Unite = "mi";
        lignes[0].Pu = 12.34m;
        lignes.Add(new LigneSaisie { CodeLigne = "P", Pu = 15 });
        lignes.Add(new LigneSaisie { CodeLigne = "9", Designation = "Texte", Quantite = 3, Pu = 4 });
        lignes.Add(new LigneSaisie { CodeLigne = "R", Remise = 10 });
        lignes.Add(new LigneSaisie { CodeLigne = "R", Pu = 1 });
        var c = SaisieFacture.Recalculer(s);
        Assert.Empty(c.Erreurs);
        Assert.Empty(c.Anomalies);
        // 2 500 au mille × 12,34 = 30,85 ; port 15 hors CA ; texte 0 ; remise 10 % du CA tronquée ; remise d'un montant
        Assert.Equal([30.85m, 15m, 0m, 3.08m, 1m], lignes.Select(l => l.Montant));
        Assert.Equal("MI", lignes[0].Unite);
        Assert.Equal(41.77m, c.Ventes);

        lignes.Add(new LigneSaisie { CodeLigne = "Z" });
        lignes.Add(new LigneSaisie { CodeLigne = "V", Quantite = 1, Unite = "ZZ", Pu = 1 });
        lignes.Add(new LigneSaisie { CodeLigne = "W", Quantite = 1, Unite = "EX" });
        s.Representant = null;
        s.NombreJours = 35;
        s.CompteGeneralFacture = 411100;
        c = SaisieFacture.Recalculer(s);
        Assert.Equal([nameof(s.CompteGeneralFacture), nameof(s.NombreJours), nameof(s.Representant), "", ""], c.Erreurs.Select(e => e.Champ));
        Assert.Contains("ligne 6 : type de ligne « Z » inconnu.", c.Erreurs[3].Message);
        Assert.Equal(["P.U.", "Code vente différent"], c.Anomalies);
    }

    [Fact]
    public void Corrections_reportees_sur_la_facture_a_ecrire()
    {
        var f = GenerationFactures.Regrouper([Bl(1, Dossier(100)), Bl(2, Dossier(200))], P);
        var s = Saisie(f);
        s.Elements[0].Lignes[0].Pu = 3;
        s.Elements[0].Lignes.Add(new LigneSaisie { CodeLigne = "9", Designation = " Texte ajouté ", Quantite = 5 });
        s.Elements[1].Lignes.Clear();
        s.Elements[1].Lignes.Add(new LigneSaisie { CodeLigne = "W", Designation = "Forfait", Pu = 50 });
        (s.CompteGeneralFacture, s.CompteClientFacture, s.Reference, s.Escompte) = (411100, 99, "MA REF", 1.5m);
        var c = SaisieFacture.Recalculer(s);
        Assert.Equal(["Unité", "Code vente différent"], c.Anomalies);   // forfait sans unité : à vérifier

        var e = SaisieFacture.Appliquer(f, s, P, c.Anomalies);
        Assert.Equal([(30m, 30m, 2), (50m, 50m, 1)], e.Elements.Select(x => (x.ChiffreAffaires, x.Ventes, x.Lignes.Count)));
        var texte = e.Elements[0].Lignes[1];
        Assert.Equal((2, "Texte ajouté", 0m, ""), (texte.Numero, texte.Designation, texte.Quantite, texte.Monnaie));
        Assert.Equal((1, "ART"), (e.Elements[0].Lignes[0].Bl, e.Elements[0].Lignes[0].Article));   // origine gardée
        Assert.Equal((411100, 99, "MA REF", 1.5m), (e.CompteGeneralFacture, e.CompteClientFacture, e.DernierDossier.Reference, e.Conditions.Escompte));
        Assert.Equal(AdresseEnvoi.Aucune, e.Adresse);   // autre client facturé : pas l'adresse de la commande
        Assert.Equal(("V", true), (e.CodeVente, e.AControler));
        Assert.Equal((411100, 20), e.Pied);   // textes de pied du donneur d'ordre (choix de la commande)
    }
}

public class SaisieFactureRevueTests
{
    private static readonly ParametresGeneration P = new(
        new Dictionary<string, (string, string)> { ["V"] = ("V", "Vente"), ["R"] = ("R", "Remise"), ["9"] = ("T", "Texte") },
        new Dictionary<string, decimal> { ["EX"] = 1 });

    // Commande de fabrication avec deux compléments AB004 (une vente, une remise de 5 €) et un élément en plus EXP011
    private static ElementBlAFacturer Bl(int clientFacture = 0) =>
        new(1, 1, 411100, 20, clientFacture == 0 ? 0 : 411100, clientFacture, 6, new ConditionsReglement("56", 30, 0, 0, 0), AdresseEnvoi.Aucune,
            101, false, 10,
            new DossierAFacturer(100, "A", "1", "V", "EX", "Étuis", "", 2, 0, "REF", null, "", "", 0, 0, 0, "E", 0, "", "", "", AdresseEnvoi.Aucune,
                true, "", "", [new ComplementDossier(1, "V", 1, "Cliché", "EX", 0, 30), new ComplementDossier(2, "R", 0, "Remise", "", 0, 5)]),
            [], [new LigneEnPlus(1, "V", "", "", 1, "Palette", "EX", 12, 0)]);

    [Fact]
    public void Complement_ou_element_en_plus_supprime_reste_a_facturer()
    {
        var f = GenerationFactures.Regrouper([Bl()], P);
        Assert.Equal([1, 2], f.Elements[0].Complements);
        Assert.Single(f.Elements[0].EnPlus);
        var s = SaisieFacture.VersSaisie(f, [], "", P, new DateOnly(2026, 11, 1), [], [], false);
        var lignes = s.Elements[0].Lignes;
        Assert.Equal(5m, lignes.Single(l => l.CodeLigne == "R").Pu);   // remise en montant : saisie dans le prix
        lignes.RemoveAll(l => l.Designation is "Cliché" or "Palette");
        var c = SaisieFacture.Recalculer(s);
        var e = SaisieFacture.Appliquer(f, s, P, c.Anomalies).Elements[0];
        Assert.Equal([2], e.Complements);   // la remise reste facturée, le cliché non
        Assert.Empty(e.EnPlus);
        var remise = e.Lignes.Single(l => l.CodeLigne == "R");
        Assert.Equal((0m, 5m), (remise.Pu, remise.Montant));   // comme la facturation automatique
        Assert.Equal(15m, e.Ventes);   // 10 × 2 − 5
    }

    [Fact]
    public void Premier_numero_propose_pour_un_nouveau_compteur()
    {
        var date = new DateOnly(2026, 11, 1);
        Assert.Equal(110001, NumerotationFactures.PremierPropose(date, new NumerotationFactures(2, false, "").Mois(date)));   // mensuelle
        Assert.Equal(260001, NumerotationFactures.PremierPropose(date, new NumerotationFactures(1, false, "").Mois(date)));   // annuelle
        Assert.True(new NumerotationFactures(3, true, "1").ParEnseigne);
    }

    [Fact]
    public void Nouvelle_date_d_une_facture_modifiee()
    {
        var origine = new DateOnly(2026, 11, 1);
        Assert.Null(SaisieFacture.ControlerNouvelleDate(1, origine, new DateOnly(2026, 3, 5)));      // annuelle : même année
        Assert.NotNull(SaisieFacture.ControlerNouvelleDate(1, origine, new DateOnly(2027, 1, 2)));
        Assert.Null(SaisieFacture.ControlerNouvelleDate(2, origine, new DateOnly(2026, 11, 30)));    // mensuelle : même mois
        Assert.NotNull(SaisieFacture.ControlerNouvelleDate(2, origine, new DateOnly(2026, 12, 1)));
        Assert.Null(SaisieFacture.ControlerNouvelleDate(3, origine, new DateOnly(2027, 1, 2)));      // par enseigne : libre
    }

    [Fact]
    public void Un_seul_client_facture()
    {
        Assert.Throws<RegleMetierException>(() => GenerationFactures.Regrouper([Bl(), Bl(30) with { Bl = 2 }], P));
        Assert.Single(GenerationFactures.Regrouper([Bl(20), Bl() with { Bl = 2 }], P).Elements);   // 411100 20 = le donneur d'ordre
    }
}

// Saisie sur la base de test (société 29) : BL hors facturation automatique facturé à la main, base remise à l'identique
[Collection(EcrituresNp11f.Nom)]   // une seule classe d'écriture à la fois : compteurs et BL partagés
public class SaisieFactureBaseTests(Xunit.Abstractions.ITestOutputHelper sortie)
{
    private static SaisieFacturesService Service(int societe)
    {
        var config = BaseDeTest.Configuration;
        return new SaisieFacturesService(new FacturationAutoDb2(config), new SaisieFacturesDb2(config),
            new EditionDefinitiveDb2(config, new CalculFacturesDb2(config)), new DroitsFacturationDb2(config),
            new FacturationAutoBaseTests.Utilisateur(societe));
    }

    [FactDb2]
    public async Task Cles_de_facturation()
    {
        var service = Service(29);
        Assert.Contains("n'est pas facturable", (await service.ChercherBlsAsync(new CleFacturation { Bl = 29404 })).Erreurs.Single().Message);
        Assert.Contains("n'existe pas", (await service.ChercherBlsAsync(new CleFacturation { Bl = 999999 })).Erreurs.Single().Message);
        Assert.Single((await service.ChercherBlsAsync(new CleFacturation { Bl = 1, Commande = 2 })).Erreurs);
        // BL hors facturation automatique (HFACTO = N) : proposé, coché, avec les autres BL du client
        var r = await service.ChercherBlsAsync(new CleFacturation { Bl = 29379 });
        Assert.Empty(r.Erreurs);
        Assert.Equal((411029, 1), (r.CompteGeneral, r.CompteClient));
        Assert.Equal([29379], r.Bls.Where(b => b.Propose).Select(b => b.Bl));
        Assert.True(r.Bls.Count > 1);
        sortie.WriteLine($"{r.NomClient} : {r.Bls.Count} éléments de BL, {r.Ecartes.Count} écartés");

        var f = await service.PreparerAsync([new ElementDeBl(29379, 1)]);
        var c = service.Recalculer(f);
        sortie.WriteLine($"Facture préparée : {f.Elements.Count} élément(s), {f.Elements.Sum(e => e.Lignes.Count)} lignes, {c.Ventes:N2} €, "
            + $"anomalies {string.Join(", ", c.Anomalies)} ; erreurs {string.Join(" / ", c.Erreurs.Select(e => e.Message))}");
        Assert.Equal(26748, f.Elements.Single().Commande);
        Assert.NotEmpty(f.CodesLigne);
        Assert.NotEmpty(f.Representants);
    }

    [FactDb2Ecriture]
    public async Task Creation_puis_remise_a_l_identique()
    {
        const int societe = 29, bl = 29379;
        var date = DateOnly.FromDateTime(DateTime.Today);
        var service = Service(societe);
        var f = await service.PreparerAsync([new ElementDeBl(bl, 1)]);
        f.Elements[0].Lignes.Add(new LigneSaisie { CodeLigne = f.CodesLigne.First(x => x.Type == "T").Code, Designation = "Ligne ajoutée à la saisie" });
        var controle = service.Recalculer(f);
        Assert.Empty(controle.Erreurs);

        await using var cnx = NetPrint.Socle.Donnees.BaseDb2.Connexion(BaseDeTest.Configuration);
        await cnx.OpenAsync();
        var avant = await FacturationAutoBaseTests.ReleverAsync(cnx, societe, bl, date);
        int? numero = null;
        try
        {
            var creee = await service.CreerAsync(f, editerDefinitif: controle.Anomalies.Count == 0);
            numero = creee.Numero;
            sortie.WriteLine($"BL {bl} → facture {numero} du {date:dd/MM/yyyy}, {controle.Ventes:N2} €, éditée {creee.EditeeDefinitif}, {creee.Avertissement} "
                + $"(anomalies : {string.Join(", ", controle.Anomalies)})");
            Assert.Null(creee.Avertissement);
            Assert.Equal(controle.Anomalies.Count == 0, creee.EditeeDefinitif);
            var fiche = await new ConsultationFacturesService(new ConsultationFacturesDb2(BaseDeTest.Configuration),
                new FacturationAutoBaseTests.Utilisateur(societe)).LireAsync(numero.Value, date);
            Assert.Contains(fiche!.Lignes, l => l.Textes.Contains("Ligne ajoutée à la saisie"));   // texte rattaché à la ligne qui précède
            if (creee.EditeeDefinitif) Assert.Equal(controle.Ventes, fiche.Facture.Ht + fiche.Facture.Port);   // totaux posés par l'édition
            // Le BL est facturé : il n'est plus proposé, et une deuxième création est refusée
            Assert.Contains("déjà facturé", (await service.ChercherBlsAsync(new CleFacturation { Bl = bl })).Erreurs.Single().Message);
            await Assert.ThrowsAsync<RegleMetierException>(() => service.CreerAsync(f, false));
        }
        finally
        {
            if (numero is { } n)
                foreach (var table in new[] { ("FAC100", "FG"), ("FAC090", "FE"), ("FAC081", "FD"), ("FAC080", "FD") })
                    await FacturationAutoBaseTests.ExecuterAsync(cnx, $"DELETE FROM {{b}}.{table.Item1} WHERE RSOC = ? AND RNFAC = ? AND {table.Item2}ANNE = ? AND {table.Item2}MOIS = ? AND {table.Item2}JOUR = ?",
                        societe, n, date.Year, date.Month, date.Day);
            foreach (var (table, rrn, colonnes, valeurs) in avant)
                await FacturationAutoBaseTests.ExecuterAsync(cnx, $"UPDATE {{b}}.{table} t SET {string.Join(", ", colonnes.Split(',').Select(x => $"{x.Trim()} = ?"))} WHERE RRN(t) = ?",
                    [.. valeurs, rrn]);
        }
        var remis = await FacturationAutoBaseTests.ReleverAsync(cnx, societe, bl, date);
        Assert.Equal(avant.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))), remis.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))));
    }

    // Comptes des lignes d'une facture par fichier, à une date
    private static async Task<string> CompterAsync(System.Data.Odbc.OdbcConnection cnx, int societe, int numero, DateOnly d)
    {
        var bib = NetPrint.Socle.Donnees.BaseDb2.Bibliotheque(BaseDeTest.Configuration);
        var comptes = new List<string>();
        foreach (var (table, n, a, m, j) in new[] { ("FAC080", "RNFAC", "FDANNE", "FDMOIS", "FDJOUR"), ("FAC081", "RNFAC", "FDANNE", "FDMOIS", "FDJOUR"),
                     ("FAC090", "RNFAC", "FEANNE", "FEMOIS", "FEJOUR"), ("FAC100", "RNFAC", "FGANNE", "FGMOIS", "FGJOUR"),
                     ("EXP010", "HNOFAC", "HDAFAA", "HDAFAM", "HDAFAJ") })
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {bib}.{table} WHERE RSOC = ? AND {n} = ? AND {a} = ? AND {m} = ? AND {j} = ?";
            foreach (var v in new object[] { societe, numero, d.Year, d.Month, d.Day }) NetPrint.Socle.Donnees.BaseDb2.Valeur(cmd.Parameters, v);
            comptes.Add($"{table}={Convert.ToInt32(await cmd.ExecuteScalarAsync())}");
        }
        return string.Join(" ", comptes);
    }

    [FactDb2Ecriture]
    public async Task Modification_avec_changement_de_date_puis_remise_a_l_identique()
    {
        const int societe = 29, bl = 29379;
        var date = DateOnly.FromDateTime(DateTime.Today);
        var autre = date.Day < 28 ? date.AddDays(1) : date.AddDays(-1);   // même mois : valable quelle que soit la numérotation
        var service = Service(societe);
        var f = await service.PreparerAsync([new ElementDeBl(bl, 1)]);
        Assert.Empty(service.Recalculer(f).Erreurs);

        await using var cnx = NetPrint.Socle.Donnees.BaseDb2.Connexion(BaseDeTest.Configuration);
        await cnx.OpenAsync();
        var avant = await FacturationAutoBaseTests.ReleverAsync(cnx, societe, bl, date);
        int? numero = null;
        try
        {
            numero = (await service.CreerAsync(f, editerDefinitif: false)).Numero;
            var avantModif = await CompterAsync(cnx, societe, numero.Value, date);

            // Modification : nouvelle date, prix de la première ligne, texte ajouté
            var m = await service.OuvrirAsync(numero.Value, date);
            Assert.Equal((numero, date), (m.Numero, m.DateOrigine));
            m.Date = autre;
            var vente = m.Elements[0].Lignes.First(l => m.CodesLigne.Any(c => c.Code == l.CodeLigne && c.Type == "V") && l.Pu != 0);
            vente.Pu += 1;
            m.Elements[0].Lignes.Add(new LigneSaisie { CodeLigne = m.CodesLigne.First(c => c.Type == "T").Code, Designation = "Texte de la modification" });
            var controle = service.Recalculer(m);
            Assert.Empty(controle.Erreurs);
            var r = await service.ModifierAsync(m, editerDefinitif: false);
            Assert.Equal(autre, r.Date);

            // Plus rien à l'ancienne date, tout à la nouvelle (BL compris)
            Assert.Equal("FAC080=0 FAC081=0 FAC090=0 FAC100=0 EXP010=0", await CompterAsync(cnx, societe, numero.Value, date));
            var apres = await CompterAsync(cnx, societe, numero.Value, autre);
            sortie.WriteLine($"Facture {numero} : avant {avantModif} ; après, au {autre:dd/MM/yyyy} : {apres}");
            Assert.Equal(avantModif.Replace("FAC100=4", "FAC100=5"), apres);
            var relue = await service.OuvrirAsync(numero.Value, autre);
            Assert.Contains(relue.Elements[0].Lignes, l => l.Designation == "Texte de la modification");
            Assert.Equal(controle.Ventes, service.Recalculer(relue).Ventes);

            // Changement d'année refusé en numérotation annuelle (société 29)
            relue.Date = new DateOnly(autre.Year + 1, 1, 15);
            await Assert.ThrowsAsync<RegleMetierException>(() => service.ModifierAsync(relue, false));

            // Éditée en définitif : plus modifiable
            relue.Date = autre;
            Assert.True((await service.ModifierAsync(relue, editerDefinitif: true)).EditeeDefinitif);
            await Assert.ThrowsAsync<RegleMetierException>(() => service.OuvrirAsync(numero.Value, autre));
        }
        finally
        {
            if (numero is { } n)
                foreach (var d in new[] { date, autre })
                    foreach (var table in new[] { ("FAC100", "FG"), ("FAC090", "FE"), ("FAC081", "FD"), ("FAC080", "FD") })
                        await FacturationAutoBaseTests.ExecuterAsync(cnx, $"DELETE FROM {{b}}.{table.Item1} WHERE RSOC = ? AND RNFAC = ? AND {table.Item2}ANNE = ? AND {table.Item2}MOIS = ? AND {table.Item2}JOUR = ?",
                            societe, n, d.Year, d.Month, d.Day);
            foreach (var (table, rrn, colonnes, valeurs) in avant)
                await FacturationAutoBaseTests.ExecuterAsync(cnx, $"UPDATE {{b}}.{table} t SET {string.Join(", ", colonnes.Split(',').Select(x => $"{x.Trim()} = ?"))} WHERE RRN(t) = ?",
                    [.. valeurs, rrn]);
        }
        var remis = await FacturationAutoBaseTests.ReleverAsync(cnx, societe, bl, date);
        Assert.Equal(avant.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))), remis.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))));
    }
}
