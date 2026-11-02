using System.Data.Odbc;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Tests;

// Édition définitive (FAC201 en définitif) sur la base de test : facture d'essai 100544 du 23/10/2008 (société 8, « à éditer »).
// Tout ce que l'édition touche (FAC080, FAC090, EXP010, AB001, NEG100) est relevé avant, puis remis à l'identique ; l'historique
// commercial (FAC070, FAC072) est relevé avant (la facture d'essai en a un, resté d'une ancienne édition) et remis tel quel.
[Collection(EcrituresNp11f.Nom)]   // une seule classe d'écriture à la fois : compteurs et BL partagés
public class EditionDefinitiveTests
{
    private const int Societe = 8, Numero = 100544;
    private static readonly DateOnly Date = new(2008, 10, 23);
    private static string Bib => BaseDb2.Bibliotheque(BaseDeTest.Configuration);

    // Lignes touchées, relevées par numéro relatif (RRN) : table, colonnes, condition (paramètres : société, n°, date)
    private static readonly (string Table, string Colonnes, string Condition)[] Releves =
    [
        ("FAC080", "FDEDIT, FDEDT, FDEDA, FDEDM, FDEDJ, FDMTN, FDPOT, FDPOE, FDMES, FDTVA, FDECHA, FDECHM, FDECHJ",
            "RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?"),
        ("FAC090", "FEEDT", "RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?"),
        ("EXP010", "HBLFAC", "RSOC = ? AND HNOFAC = ? AND HDAFAA = ? AND HDAFAM = ? AND HDAFAJ = ?"),
        ("AB001", "CCNOFA, CCFCTA, CCFCTM, CCFCTJ, CCQTEP", """
            RSOC = ? AND (RDOSS, CCDCLE) IN (SELECT FEDOSS, FECLE FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ?
            AND FEJOUR = ? AND FEDOSS <> 0)
            """),
        ("NEG100", "DNQTEF", """
            RSOC = ? AND (RDOSS, DNDCLE) IN (SELECT FEDOSS, FECLE FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ?
            AND FEJOUR = ? AND FEDOSS <> 0)
            """),
    ];

    private static object[] Cle(string condition) => condition.Count(c => c == '?') == 6
        ? [Societe, Societe, Numero, Date.Year, Date.Month, Date.Day]
        : [Societe, Numero, Date.Year, Date.Month, Date.Day];

    private static async Task<List<(string Table, long Rrn, string Colonnes, object?[] Valeurs)>> ReleverAsync(OdbcConnection cnx)
    {
        var releve = new List<(string, long, string, object?[])>();
        foreach (var (table, colonnes, condition) in Releves)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT RRN(t), {colonnes} FROM {Bib}.{table} t WHERE {condition.Replace("{b}", Bib)}";
            foreach (var v in Cle(condition)) cmd.Parameters.Valeur(v);
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

    private static async Task<(int Fac070, int Fac072)> HistoriqueAsync(OdbcConnection cnx)
    {
        var comptes = new int[2];
        foreach (var (i, table, an, mois, jour) in new[] { (0, "FAC070", "HFDTFA", "HFDTFM", "HFDTFJ"), (1, "FAC072", "HNDTFA", "HNDTFM", "HNDTFJ") })
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {Bib}.{table} WHERE RSOC = ? AND RNFAC = ? AND {an} = ? AND {mois} = ? AND {jour} = ?";
            foreach (var v in new object[] { Societe, Numero, Date.Year, Date.Month, Date.Day }) cmd.Parameters.Valeur(v);
            comptes[i] = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        return (comptes[0], comptes[1]);
    }

    // Lignes FAC070 / FAC072 de la facture d'essai, toutes colonnes
    private static async Task<List<(string Table, List<Dictionary<string, object>> Lignes)>> LignesHistoriqueAsync(OdbcConnection cnx)
    {
        var resultat = new List<(string, List<Dictionary<string, object>>)>();
        foreach (var (table, an, mois, jour) in new[] { ("FAC070", "HFDTFA", "HFDTFM", "HFDTFJ"), ("FAC072", "HNDTFA", "HNDTFM", "HNDTFJ") })
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {Bib}.{table} WHERE RSOC = ? AND RNFAC = ? AND {an} = ? AND {mois} = ? AND {jour} = ?";
            foreach (var v in new object[] { Societe, Numero, Date.Year, Date.Month, Date.Day }) cmd.Parameters.Valeur(v);
            await using var rd = await cmd.ExecuteReaderAsync();
            var lignes = new List<Dictionary<string, object>>();
            while (await rd.ReadAsync())
            {
                var l = new Dictionary<string, object>();
                for (var i = 0; i < rd.FieldCount; i++) l[rd.GetName(i).Trim()] = rd.GetValue(i) is string t ? t.TrimEnd() : rd.GetValue(i);
                lignes.Add(l);
            }
            resultat.Add((table, lignes));
        }
        return resultat;
    }

    private static string Texte(Dictionary<string, object> l) => string.Join("|", l.Select(x => $"{x.Key}={x.Value}".TrimEnd()));

    private static async Task RemettreAsync(OdbcConnection cnx, List<(string Table, long Rrn, string Colonnes, object?[] Valeurs)> releve)
    {
        foreach (var (table, rrn, colonnes, valeurs) in releve)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"UPDATE {Bib}.{table} t SET {string.Join(", ", colonnes.Split(',').Select(c => $"{c.Trim()} = ?"))} WHERE RRN(t) = ?";
            foreach (var v in valeurs) cmd.Parameters.Valeur(v);
            cmd.Parameters.Valeur(rrn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Interdiction_FAC200()
    {
        Assert.True(EditionFacturesService.EditionInterdite(["**", "", "", "", "", "", "", ""]));
        Assert.False(EditionFacturesService.EditionInterdite(["", "", "", "", "", "", "", ""]));
        Assert.False(EditionFacturesService.EditionInterdite(null));
    }

    [FactDb2Ecriture]
    public async Task Edition_definitive_puis_remise_a_l_identique()
    {
        await using var cnx = BaseDb2.Connexion(BaseDeTest.Configuration);
        await cnx.OpenAsync();
        var avant = await ReleverAsync(cnx);
        var historique = await LignesHistoriqueAsync(cnx);
        var entete = avant.Single(x => x.Table == "FAC080");
        Assert.Equal("O", entete.Valeurs[0]!.ToString()!.Trim());   // facture d'essai encore « à éditer »
        var calcul = await new CalculFacturesDb2(BaseDeTest.Configuration).LireAsync(Societe, Numero, Date, default);
        var attendu = CalculFacture.Calculer(calcul!.Lignes, calcul.Conditions);
        var aujourdhui = new DateOnly(2026, 11, 1);
        try
        {
            var t = await new EditionDefinitiveDb2(BaseDeTest.Configuration, new CalculFacturesDb2(BaseDeTest.Configuration)).EditerAsync(Societe, Numero, Date, aujourdhui, default);
            Assert.Equal(attendu with { Tva = [] }, t with { Tva = [] });
            Assert.Equal(attendu.Tva, t.Tva);

            var apres = await ReleverAsync(cnx);
            var e = apres.Single(x => x.Table == "FAC080").Valeurs;
            Assert.Equal(["E", "O"], e[..2].Select(x => x!.ToString()!.Trim()));
            Assert.Equal([2026m, 11m, 1m, t.Ht, t.PortTaxable, t.PortExonere, t.Escompte, t.TotalTva, t.Echeance.Year, t.Echeance.Month, t.Echeance.Day],
                e[2..].Select(Convert.ToDecimal));
            Assert.All(apres.Where(x => x.Table is "FAC090" or "EXP010"), x => Assert.Equal("O", x.Valeurs[0]!.ToString()!.Trim()));
            foreach (var ab in apres.Where(x => x.Table == "AB001"))
                Assert.Equal([Numero, Date.Year, Date.Month, Date.Day], ab.Valeurs[..4].Select(Convert.ToInt32));
            // Historique commercial remplacé : une ligne FAC070 par élément, les lignes FAC072 calculées
            var (h070, h072) = await HistoriqueAsync(cnx);
            Assert.Equal(apres.Count(x => x.Table == "FAC090"), h070);
            var lu = await HistoriqueCommercialDb2.LireAsync(cnx, null, Bib, Societe, Numero, Date, default);
            Assert.Equal(HistoriqueCommercial.Calculer(lu!.Value.Entete, lu.Value.Parametres, lu.Value.Elements).Fac072.Count, h072);

            // Deuxième édition refusée : la facture est éditée
            var refus = await Assert.ThrowsAsync<RegleMetierException>(() =>
                new EditionDefinitiveDb2(BaseDeTest.Configuration, new CalculFacturesDb2(BaseDeTest.Configuration)).EditerAsync(Societe, Numero, Date, aujourdhui, default));
            Assert.Contains("déjà éditée", refus.Message);
        }
        finally
        {
            await RemettreAsync(cnx, avant);
            foreach (var (table, an, mois, jour) in new[] { ("FAC070", "HFDTFA", "HFDTFM", "HFDTFJ"), ("FAC072", "HNDTFA", "HNDTFM", "HNDTFJ") })
            {
                await using var cmd = cnx.CreateCommand();
                cmd.CommandText = $"DELETE FROM {Bib}.{table} WHERE RSOC = ? AND RNFAC = ? AND {an} = ? AND {mois} = ? AND {jour} = ?";
                foreach (var v in new object[] { Societe, Numero, Date.Year, Date.Month, Date.Day }) cmd.Parameters.Valeur(v);
                await cmd.ExecuteNonQueryAsync();
            }
            await using var tx = cnx.BeginTransaction();
            foreach (var (table, lignes) in historique) await HistoriqueCommercialDb2.EcrireAsync(cnx, tx, Bib, table, lignes, default);
            tx.Commit();
        }
        var remisHistorique = await LignesHistoriqueAsync(cnx);
        Assert.Equal(historique.Select(x => (x.Table, string.Join(";", x.Lignes.Select(Texte).Order()))),
            remisHistorique.Select(x => (x.Table, string.Join(";", x.Lignes.Select(Texte).Order()))));
        var remis = await ReleverAsync(cnx);
        Assert.Equal(avant.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))), remis.Select(x => (x.Table, x.Rrn, string.Join("|", x.Valeurs))));
    }
}
