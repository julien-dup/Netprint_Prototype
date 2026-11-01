using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Services;

namespace NetPrint.Modules.Facturation.Tests;

// Facturation automatique (FAC400 → FAC402P) : contrôle des critères de lancement
public class FacturationAutoTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 1);
    private static readonly ContexteLancement Societe = new(false, false, [(202610, 202612)], 2);

    private static IReadOnlyList<ErreurLancement> Controler(CriteresFacturationAuto c, ContexteLancement? x = null) =>
        ReglesFacturationAuto.Controler(c, x ?? Societe, Aujourdhui);

    [Fact]
    public void Bornes_completees_comme_le_RPG()
    {
        var c = new CriteresFacturationAuto { ExpedieDu = new DateOnly(2026, 10, 1), DateFacture = new DateOnly(2026, 10, 31), Frequence = "m" };
        Assert.Empty(Controler(c));
        Assert.Equal((Aujourdhui, (int?)null, (int?)null), (c.ExpedieAu!.Value, c.BlDu, c.BlAu));
        Assert.Equal((0, 999999, 0, 9999999, "M"), (c.CompteGeneralDu!.Value, c.CompteGeneralAu!.Value, c.CompteClientDu!.Value, c.CompteClientAu!.Value, c.Frequence));

        var parBl = new CriteresFacturationAuto { BlDu = 1000, DateFacture = new DateOnly(2026, 11, 2) };
        Assert.Empty(Controler(parBl));
        Assert.Equal(999999, parBl.BlAu);
    }

    [Fact]
    public void Criteres_refuses()
    {
        Assert.Equal([nameof(CriteresFacturationAuto.BlDu), nameof(CriteresFacturationAuto.DateFacture)],
            Controler(new CriteresFacturationAuto()).Select(e => e.Champ));
        Assert.Equal([nameof(CriteresFacturationAuto.BlAu), nameof(CriteresFacturationAuto.CompteClientAu), nameof(CriteresFacturationAuto.Frequence)],
            Controler(new CriteresFacturationAuto { BlDu = 5, BlAu = 4, CompteClientDu = 10, CompteClientAu = 9, DateFacture = Aujourdhui, Frequence = "X" })
                .Select(e => e.Champ));
        // Date hors des périodes ouvertes ; numérotation par enseigne sans gestion des enseignes ; FAC010 absent
        Assert.Contains("période", Controler(new CriteresFacturationAuto { BlDu = 1, DateFacture = new DateOnly(2027, 1, 1) }).Single().Message);
        Assert.Single(Controler(new CriteresFacturationAuto { BlDu = 1, DateFacture = Aujourdhui }, Societe with { TypeNumerotation = 3 }));
        Assert.Single(Controler(new CriteresFacturationAuto { BlDu = 1, DateFacture = Aujourdhui }, Societe with { TypeNumerotation = null }));
    }

    [Fact]
    public void Enseigne()
    {
        var gere = Societe with { GereEnseignes = true };
        Assert.Equal(nameof(CriteresFacturationAuto.Enseigne), Controler(new CriteresFacturationAuto { BlDu = 1, DateFacture = Aujourdhui }, gere).Single().Champ);
        Assert.Contains("n'existe pas", Controler(new CriteresFacturationAuto { Enseigne = "z", BlDu = 1, DateFacture = Aujourdhui }, gere).Single().Message);
        var c = new CriteresFacturationAuto { Enseigne = "p", BlDu = 1, DateFacture = Aujourdhui };
        Assert.Empty(Controler(c, gere with { EnseigneExiste = true }));
        Assert.Equal("P", c.Enseigne);
        var sans = new CriteresFacturationAuto { Enseigne = "p", BlDu = 1, DateFacture = Aujourdhui };
        Assert.Empty(Controler(sans));
        Assert.Equal("", sans.Enseigne);
    }
}

// Moteur de génération (FAC402P) : regroupements, lignes, remise, montants, anomalies
public class GenerationFacturesTests
{
    private static readonly ParametresGeneration P = new(
        new Dictionary<string, (string, string)> { ["V"] = ("V", "Vente"), ["W"] = ("V", "Vente 2"), ["R"] = ("R", "Remise"), ["P"] = ("P", "Port"), ["9"] = ("T", "Texte") },
        new Dictionary<string, decimal> { ["EX"] = 1, ["MI"] = 1000, ["TT"] = ParametresGeneration.LeTout });

    private static readonly ConditionsReglement Cond = new("VI", 30, 0, 0, 0);

    private static DossierAFacturer Dossier(int n, decimal remise = 0, string type = "4") => new(n, "A", type, "V", "EX", "Dossier", "", 0, remise,
        "REF", null, "", "", 0, 0, 0, "E", 0, "", "", "", AdresseEnvoi.Aucune, true, "", "", []);

    private static LigneBl Ligne(int n, decimal qte, decimal pu, string code = "V", string unite = "EX") =>
        new(n, "ART" + n, "", qte, "Article " + n, "", "", new LigneCommandeNegoce(n, code, "ART" + n, "", pu, unite, 0, false, false));

    private static ElementBlAFacturer Bl(int bl, int mode, DossierAFacturer d, bool retour = false, int rep = 1, params LigneBl[] lignes) =>
        new(bl, 1, 411100, 20, 0, 0, mode, Cond, AdresseEnvoi.Aucune, rep, retour, 0, d, lignes.Length > 0 ? lignes : [Ligne(1, 10, 2)], []);

    [Fact]
    public void Regroupement_selon_le_mode()
    {
        var d1 = Dossier(100);
        var d2 = Dossier(200);
        // Mode 1 : une facture par élément de BL
        Assert.Equal(3, GenerationFactures.Generer([Bl(1, 1, d1), Bl(2, 1, d1), Bl(3, 1, d2)], P).Count);
        // Mode 3 : une facture par commande, un seul élément
        var m3 = GenerationFactures.Generer([Bl(1, 3, d1), Bl(2, 3, d1), Bl(3, 3, d2)], P);
        Assert.Equal([1, 1], m3.Select(f => f.Elements.Count));
        Assert.Equal([(1, 1), (2, 1)], m3[0].Elements[0].Bls);
        // Mode 4 : une facture par BL ; mode 6 : une facture par représentant et conditions, un élément par commande
        Assert.Equal(3, GenerationFactures.Generer([Bl(1, 4, d1), Bl(2, 4, d1), Bl(3, 4, d2)], P).Count);
        var m6 = GenerationFactures.Generer([Bl(1, 6, d1), Bl(2, 6, d1), Bl(3, 6, d2), Bl(4, 6, d2, rep: 2)], P);
        Assert.Equal([2, 1], m6.Select(f => f.Elements.Count));
        Assert.Equal([(2, 1), (1, 1)], m6.Select(f => (f.Elements[0].Lignes.Count, f.Elements[^1].Lignes.Count)));   // lignes de chaque BL
        // Retour : avoir à part, montants positifs, pas « à contrôler » pour autant
        var avec = GenerationFactures.Generer([Bl(1, 6, d1), Bl(2, 6, d1, retour: true)], P);
        Assert.Equal([false, true], avec.Select(f => f.Avoir));
        Assert.Equal(20m, avec[1].Ventes);
        Assert.False(avec[1].AControler);
        // Mode 5 traité (le RPG l'ignorait)
        Assert.Single(GenerationFactures.Generer([Bl(1, 5, d1), Bl(2, 5, d2)], P));
    }

    [Fact]
    public void Lignes_remise_et_montants()
    {
        var d = Dossier(100, remise: 10);
        var f = GenerationFactures.Generer([Bl(1, 1, d, lignes: [Ligne(1, 2500, 12.34m, unite: "MI"), Ligne(2, 3, 5, code: "P"),
            new LigneBl(3, "", "", 0, "Texte du BL", "Suite", "", null)])], P).Single();
        var e = f.Elements.Single();
        // 2 500 au mille × 12,34 = 30,85 ; port 3 × 5 hors CA ; textes ; remise 10 % du CA (tronquée), positive et retranchée
        Assert.Equal([30.85m, 15m, 0m, 0m, 3.08m], e.Lignes.Select(l => l.Montant));
        Assert.Equal(["V", "P", "9", "9", "R"], e.Lignes.Select(l => l.CodeLigne));
        Assert.Equal((27.77m, 42.77m), (e.ChiffreAffaires, e.Ventes));
        Assert.Equal("V", f.CodeVente);
        Assert.Equal(12.34m, GenerationFactures.Montant(7, 12.34m, "TT", P, out _));
        Assert.Equal(4m, GenerationFactures.Montant(7, 4m, "", P, out _));
        Assert.Equal(0m, GenerationFactures.Montant(7, 4m, "ZZ", P, out var inconnue));
        Assert.True(inconnue);
    }

    [Fact]
    public void Anomalies()
    {
        var d = Dossier(100);
        var f = GenerationFactures.Generer([Bl(1, 1, d, lignes: [Ligne(1, 1, 0), Ligne(2, 1, 3, code: "W"), Ligne(3, 1, 2, unite: "")])], P).Single();
        Assert.True(f.AControler);
        Assert.Equal(["P.U.", "Unité", "Code vente différent"], f.Anomalies);
        var vide = GenerationFactures.Generer([Bl(1, 1, d, lignes: [Ligne(1, 0, 0)])], P).Single();
        Assert.Contains("Vente à 0", vide.Anomalies);
        // Plus de 99 éléments : facture suivante
        Assert.Equal([99, 1], GenerationFactures.Generer(Enumerable.Range(1, 100).Select(i => Bl(i, 6, Dossier(i))), P).Select(x => x.Elements.Count));
    }
}

// Facturation automatique sur la base de test : simulation (lecture seule) puis génération d'une facture, base remise à l'identique
public class FacturationAutoBaseTests(Xunit.Abstractions.ITestOutputHelper sortie)
{
    internal sealed class Utilisateur(int societe) : NetPrint.Socle.Session.IUtilisateurCourant
    {
        public Task<NetPrint.Socle.Session.UtilisateurConnecte> LireAsync() =>
            Task.FromResult(new NetPrint.Socle.Session.UtilisateurConnecte("NP11", societe, "TEST", 411100));
    }

    internal static FacturationAutomatiqueService Service(int societe)
    {
        var config = BaseDeTest.Configuration;
        return new FacturationAutomatiqueService(new NetPrint.Modules.Facturation.Donnees.FacturationAutoDb2(config),
            new NetPrint.Modules.Facturation.Donnees.EditionDefinitiveDb2(config, new NetPrint.Modules.Facturation.Donnees.CalculFacturesDb2(config)),
            new NetPrint.Modules.Facturation.Donnees.DroitsFacturationDb2(config), new Utilisateur(societe));
    }

    [FactDb2]
    public async Task Simulation_societes_8_et_29()
    {
        foreach (var (societe, enseigne) in new[] { (8, "1"), (8, "3"), (29, "") })
        {
            var r = await Service(societe).SimulerAsync(new CriteresFacturationAuto { Enseigne = enseigne, ExpedieDu = new DateOnly(2000, 1, 1),
                DateFacture = DateOnly.FromDateTime(DateTime.Today) });
            foreach (var e in r.Erreurs) sortie.WriteLine($"{societe} erreur {e.Champ} : {e.Message}");
            sortie.WriteLine($"Société {societe} {enseigne} : {r.Factures.Count} factures ({r.Factures.Count(f => f.Avoir)} avoirs, {r.Factures.Count(f => f.AControler)} à contrôler), "
                + $"{r.Ecartes.Count} écartés, tronqué {r.Tronque}");
            foreach (var g in r.Ecartes.GroupBy(e => e.Raison)) sortie.WriteLine($"   écarté : {g.Key} × {g.Count()}");
            foreach (var g in r.Factures.SelectMany(f => f.Anomalies).GroupBy(x => x)) sortie.WriteLine($"   anomalie : {g.Key} × {g.Count()}");
            foreach (var g in r.Factures.GroupBy(f => f.Mode)) sortie.WriteLine($"   mode {g.Key} : {g.Count()} factures, {g.Sum(f => f.NombreBl)} BL, {g.Sum(f => f.Montant):N2} €");
            foreach (var f in r.Factures.Take(3))
                sortie.WriteLine($"   ex. {f.CompteGeneral} {f.CompteClient} {f.NomClient} : {f.Elements.Count} élt, {f.Elements.Sum(e => e.Lignes.Count)} lignes, {f.Montant:N2}");
            foreach (var f in r.Factures.Where(f => f.Anomalies.Contains("Vente à 0")).Take(3))
                foreach (var l in f.Elements.SelectMany(e => e.Lignes))
                    sortie.WriteLine($"      {f.Elements[0].Dossier}/{f.Elements[0].Cle} BL {l.Bl} [{l.Code}] {l.Designation} q={l.Quantite} u={l.Unite} pu={l.PrixUnitaire} m={l.Montant}");
        }
    }

    // Lignes touchées par la génération et l'édition définitive, relevées par RRN pour être remises telles quelles
    internal static async Task<List<(string Table, long Rrn, string Colonnes, object?[] Valeurs)>> ReleverAsync(System.Data.Odbc.OdbcConnection cnx,
        int societe, int bl, DateOnly date)
    {
        var bib = NetPrint.Socle.Donnees.BaseDb2.Bibliotheque(BaseDeTest.Configuration);
        var dossiers = $"(RDOSS, {{0}}) IN (SELECT HNODOS, HCLEF FROM {bib}.EXP010 WHERE RSOC = {societe} AND HNOBL = {bl})";
        var releves = new (string Table, string Colonnes, string Condition)[]
        {
            ("EXP010", "HBLFAC, HNOFAC, HDAFAA, HDAFAM, HDAFAJ, HNOELT", $"RSOC = {societe} AND HNOBL = {bl}"),
            ("EXP011", "HPFACT, HPNFAC, HPNELT, HPDFAA, HPDFAM, HPDFAJ", $"RSOC = {societe} AND HNOBL = {bl}"),
            ("AB004", "F4FACT, F4NFAC, F4NELT, F4DFAA, F4DFAM, F4DFAJ", $"RSOC = {societe} AND " + string.Format(dossiers, "F4DCLE")),
            ("AB001", "CCNOFA, CCFCTA, CCFCTM, CCFCTJ, CCQTEP", $"RSOC = {societe} AND " + string.Format(dossiers, "CCDCLE")),
            ("NEG100", "DNQTEF", $"RSOC = {societe} AND " + string.Format(dossiers, "DNDCLE")),
            // Compteurs : annuel (mois 0), mensuel (mois de la facture), par enseigne
            ("FAC040", "NFACT", $"RSOC = {societe} AND NAN = {date.Year} AND NMOIS IN (0, {date.Month})"),
            ("PRT320F", "ENDNFA", $"RSOC = {societe}"),
        };
        var releve = new List<(string, long, string, object?[])>();
        foreach (var (table, colonnes, condition) in releves)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT RRN(t), {colonnes} FROM {bib}.{table} t WHERE {condition}";
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var v = new object?[colonnes.Split(',').Length];
                for (var i = 0; i < v.Length; i++) v[i] = rd.GetValue(i + 1);
                releve.Add((table, Convert.ToInt64(rd.GetValue(0)), colonnes, v));
            }
        }
        return releve;
    }

    internal static async Task<int> ExecuterAsync(System.Data.Odbc.OdbcConnection cnx, string sql, params object?[] v)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = sql.Replace("{b}", NetPrint.Socle.Donnees.BaseDb2.Bibliotheque(BaseDeTest.Configuration));
        foreach (var x in v) NetPrint.Socle.Donnees.BaseDb2.Valeur(cmd.Parameters, x);
        return await cmd.ExecuteNonQueryAsync();
    }

    // Société 29 : numérotation annuelle, sans enseigne
    [FactDb2Ecriture]
    public Task Generation_et_edition_definitive_puis_remise_a_l_identique() => GenererPuisRemettreAsync(29, "");

    // Société 8 : celle qui utilise la facturation automatique (David, 01/11), enseignes, numérotation mensuelle
    [FactDb2Ecriture]
    public Task Generation_societe_8_enseigne_1_puis_remise_a_l_identique() => GenererPuisRemettreAsync(8, "1");

    private async Task GenererPuisRemettreAsync(int societe, string enseigne)
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        var service = Service(societe);
        var simulation = await service.SimulerAsync(new CriteresFacturationAuto { Enseigne = enseigne, ExpedieDu = new DateOnly(2000, 1, 1), DateFacture = date });
        var cible = simulation.Factures.First(f => f is { Avoir: false, AControler: false, NombreBl: 1 });
        var bl = cible.Elements[0].Bls[0];
        var criteres = new CriteresFacturationAuto { Enseigne = enseigne, BlDu = bl, BlAu = bl, DateFacture = date };
        Assert.Single((await service.SimulerAsync(criteres)).Factures);

        await using var cnx = NetPrint.Socle.Donnees.BaseDb2.Connexion(BaseDeTest.Configuration);
        await cnx.OpenAsync();
        // Compteur du mois absent (numérotation mensuelle : il se crée à la main, écran FAC040) : créé pour l'essai, supprimé après
        var compteurCree = false;
        await using (var rd = await new System.Data.Odbc.OdbcCommand($"SELECT PTYPEN FROM {NetPrint.Socle.Donnees.BaseDb2.Bibliotheque(BaseDeTest.Configuration)}.FAC010 WHERE RSOC = {societe}", cnx).ExecuteReaderAsync())
            if (await rd.ReadAsync() && Convert.ToInt32(rd.GetValue(0)) == 2)
                compteurCree = await ExecuterAsync(cnx, "INSERT INTO {b}.FAC040 (RSOC, NAN, NMOIS, NFACT) SELECT CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(2, 0)), CAST(? AS NUMERIC(6, 0)) FROM SYSIBM.SYSDUMMY1 "
                    + "WHERE NOT EXISTS (SELECT 1 FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ?)", societe, date.Year, date.Month, date.Month * 10000 + 1, societe, date.Year, date.Month) == 1;
        try
        {
            var avant = await ReleverAsync(cnx, societe, bl, date);
            int? numero = null;
            try
            {
                var r = await service.GenererAsync(criteres, editerDefinitif: true, (await service.SimulerAsync(criteres)).Empreinte);
                var f = Assert.Single(r.Factures);
                Assert.Null(f.Refus);
                numero = f.Numero;
                Assert.True(f.EditeeDefinitif);
                sortie.WriteLine($"BL {bl} → facture {numero} du {date:dd/MM/yyyy}, {f.Montant:N2} €");

                var apres = await ReleverAsync(cnx, societe, bl, date);
                Assert.All(apres.Where(x => x.Table == "EXP010"), x => Assert.Equal(("O", numero!.Value), (x.Valeurs[0]!.ToString()!.Trim(), Convert.ToInt32(x.Valeurs[1]))));
                // Un seul compteur a bougé, et il donne le n° suivant
                var compteurs = apres.Where(x => x.Table is "FAC040" or "PRT320F")
                    .Where(x => !avant.Single(y => y.Table == x.Table && y.Rrn == x.Rrn).Valeurs.SequenceEqual(x.Valeurs)).ToList();
                Assert.Equal(numero + 1, Convert.ToInt32(Assert.Single(compteurs).Valeurs[0]));
                // Facture lue par la consultation : éditée, même montant que la simulation
                var fiche = await new ConsultationFacturesService(new NetPrint.Modules.Facturation.Donnees.ConsultationFacturesDb2(BaseDeTest.Configuration),
                    new Utilisateur(societe)).LireAsync(numero!.Value, date);
                Assert.Equal((StatutsFacture.Editee, cible.Montant), (fiche!.Facture.Statut, fiche.Facture.Ht + fiche.Facture.Port));

                // Le BL est facturé : une nouvelle simulation ne le reprend plus
                Assert.Empty((await service.SimulerAsync(criteres)).Factures);
            }
            finally
            {
                if (numero is { } n)
                    foreach (var table in new[] { ("FAC100", "FG"), ("FAC090", "FE"), ("FAC081", "FD"), ("FAC080", "FD") })
                        await ExecuterAsync(cnx, $"DELETE FROM {{b}}.{table.Item1} WHERE RSOC = ? AND RNFAC = ? AND {table.Item2}ANNE = ? AND {table.Item2}MOIS = ? AND {table.Item2}JOUR = ?",
                            societe, n, date.Year, date.Month, date.Day);
                foreach (var (table, rrn, colonnes, valeurs) in avant)
                    await ExecuterAsync(cnx, $"UPDATE {{b}}.{table} t SET {string.Join(", ", colonnes.Split(',').Select(c => $"{c.Trim()} = ?"))} WHERE RRN(t) = ?",
                        [.. valeurs, rrn]);
            }
            var remis = await ReleverAsync(cnx, societe, bl, date);
            Assert.Equal(avant.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))), remis.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))));
        }
        finally
        {
            if (compteurCree)
                await ExecuterAsync(cnx, "DELETE FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ?", societe, date.Year, date.Month);
        }
    }
}

public class GenerationFacturesUneFoisTests
{
    [Fact]
    public void Complements_et_acompte_sur_la_premiere_facture_seulement()
    {
        var p = new ParametresGeneration(new Dictionary<string, (string, string)> { ["V"] = ("V", "Vente") }, new Dictionary<string, decimal> { ["EX"] = 1 });
        var d = new DossierAFacturer(100, "A", "1", "V", "EX", "Dossier", "", 2, 0, "", null, "", "", 0, 0, 0, "E", 50, "", "", "",
            AdresseEnvoi.Aucune, true, "", "", [new ComplementDossier(1, "V", 1, "Complément", "EX", 0, 10)]);
        ElementBlAFacturer Bl(int bl) => new(bl, 1, 411100, 20, 0, 0, 4, new("VI", 30, 0, 0, 0), AdresseEnvoi.Aucune, 1, false, 100, d, [], []);
        var f = GenerationFactures.Generer([Bl(1), Bl(2)], p);
        Assert.Equal([(210m, 50m, 1), (200m, 0m, 0)], f.Select(x => (x.Ventes, x.Acompte, x.Elements[0].Complements.Count)));
    }
}
