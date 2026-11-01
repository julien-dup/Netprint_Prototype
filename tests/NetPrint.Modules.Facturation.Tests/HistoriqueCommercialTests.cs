using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;
using Xunit.Abstractions;

namespace NetPrint.Modules.Facturation.Tests;

// Historique commercial recalculé sur des factures éditées par l'AS400 (FAC199) et comparé, colonne par colonne, aux lignes
// FAC070 / FAC072 écrites par le RPG (lecture seule)
public class HistoriqueCommercialTests(ITestOutputHelper sortie)
{
    [FactDb2]
    public async Task Identique_au_RPG_sur_des_factures_reelles()
    {
        var bib = BaseDb2.Bibliotheque(BaseDeTest.Configuration);
        await using var cnx = BaseDb2.Connexion(BaseDeTest.Configuration);
        await cnx.OpenAsync();
        var factures = new List<(int Societe, int Numero, DateOnly Date)>();
        foreach (var (societe, du, au) in new[] { (29, 20240101, 20241231), (8, 20220101, 20231231) })
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.RNFAC, f.FDANNE, f.FDMOIS, f.FDJOUR FROM {bib}.FAC080 f
                WHERE f.RSOC = ? AND f.FDEDIT = 'E' AND f.FDANNE * 10000 + f.FDMOIS * 100 + f.FDJOUR BETWEEN ? AND ?
                  AND EXISTS (SELECT 1 FROM {bib}.FAC070 h WHERE h.RSOC = f.RSOC AND h.RNFAC = f.RNFAC AND h.HFDTFA = f.FDANNE
                              AND h.HFDTFM = f.FDMOIS AND h.HFDTFJ = f.FDJOUR)
                ORDER BY f.FDANNE DESC, f.FDMOIS DESC, f.RNFAC DESC FETCH FIRST 150 ROWS ONLY
                """;
            foreach (var v in new object[] { societe, du, au }) BaseDb2.Valeur(cmd.Parameters, v);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                factures.Add((societe, Convert.ToInt32(rd.GetValue(0)), new DateOnly(Convert.ToInt32(rd.GetValue(1)), Convert.ToInt32(rd.GetValue(2)),
                    Convert.ToInt32(rd.GetValue(3)))));
        }

        var ecarts = new Dictionary<string, int>();
        var exemples = new List<string>();
        int identiques = 0, lignes070 = 0, lignes072 = 0;
        void Ecart(string colonne, string detail)
        {
            ecarts[colonne] = ecarts.GetValueOrDefault(colonne) + 1;
            if (exemples.Count(x => x.StartsWith(colonne + " ")) < 3) exemples.Add($"{colonne} {detail}");
        }
        foreach (var (societe, numero, date) in factures)
        {
            var lu = await HistoriqueCommercialDb2.LireAsync(cnx, null, bib, societe, numero, date, default);
            var (calc070, calc072) = HistoriqueCommercial.Calculer(lu!.Value.Entete, lu.Value.Parametres, lu.Value.Elements);
            var rpg070 = await LignesAsync(cnx, bib, "FAC070", "HFDTFA", "HFDTFM", "HFDTFJ", societe, numero, date);
            var rpg072 = await LignesAsync(cnx, bib, "FAC072", "HNDTFA", "HNDTFM", "HNDTFJ", societe, numero, date);
            lignes070 += rpg070.Count;
            lignes072 += rpg072.Count;
            var avant = ecarts.Values.Sum();
            Comparer("FAC070", calc070, rpg070, l => $"{l["HFNUM"]}", $"{societe}/{numero} du {date:dd/MM/yyyy}", Ecart);
            Comparer("FAC072", calc072, rpg072, l => string.Join("|", new[] { "HNELM", "HNBL", "HNBLEL", "HNBLLG", "HNART", "HNQTE" }.Select(c => Normaliser(l[c]))),
                $"{societe}/{numero} du {date:dd/MM/yyyy}", Ecart);
            if (ecarts.Values.Sum() == avant) identiques++;
        }
        sortie.WriteLine($"{factures.Count} factures, {lignes070} lignes FAC070, {lignes072} lignes FAC072 : {identiques} factures identiques");
        foreach (var (c, n) in ecarts.OrderByDescending(x => x.Value)) sortie.WriteLine($"  {c} : {n} écart(s)");
        foreach (var x in exemples) sortie.WriteLine($"    {x}");
        Assert.NotEmpty(factures);
        // Prix de la ligne de commande (NEG100) : le RPG reprend ceux de la ligne lue avant quand la ligne est introuvable (corrigé ici),
        // et les commandes ont pu changer de prix depuis l'édition : tolérés ; toute autre colonne doit être identique
        Assert.Empty(ecarts.Where(x => x.Key is not ("FAC072.HNPUS" or "FAC072.HNPUT")));
        Assert.True(ecarts.Where(x => x.Key is "FAC072.HNPUS" or "FAC072.HNPUT").Sum(x => x.Value) <= lignes072 / 10, "trop d'écarts de prix FAC072");
    }

    private static void Comparer(string table, List<Dictionary<string, object>> calcules, List<Dictionary<string, object>> rpg,
        Func<Dictionary<string, object>, string> cle, string facture, Action<string, string> ecart)
    {
        var parCle = rpg.GroupBy(cle).ToDictionary(g => g.Key, g => new Queue<Dictionary<string, object>>(g));
        foreach (var c in calcules)
        {
            if (!parCle.TryGetValue(cle(c), out var file) || file.Count == 0) { ecart($"{table}.ligne-en-trop", $"{facture} {cle(c)}"); continue; }
            var r = file.Dequeue();
            foreach (var (colonne, valeur) in c.Where(x => x.Key is not ("HFPRPF" or "HNPRPF")))   // prix de revient : posé plus tard par UTI100
                if (r.TryGetValue(colonne, out var attendu) && Normaliser(valeur) != Normaliser(attendu))
                    ecart($"{table}.{colonne}", $"{facture} {cle(c)} : calculé {Normaliser(valeur)}, RPG {Normaliser(attendu)}");
        }
        foreach (var reste in parCle.Values.SelectMany(q => q)) ecart($"{table}.ligne-manquante", $"{facture} {cle(reste)}");
    }

    private static string Normaliser(object? v) => v switch
    {
        null or DBNull => "",
        decimal d => d.ToString("0.######"),
        int or long or short => Convert.ToDecimal(v).ToString("0.######"),
        _ => $"{v}".Trim(),
    };

    private static async Task<List<Dictionary<string, object>>> LignesAsync(System.Data.Odbc.OdbcConnection cnx, string bib, string table, string an,
        string mois, string jour, int societe, int numero, DateOnly date)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {bib}.{table} WHERE RSOC = ? AND RNFAC = ? AND {an} = ? AND {mois} = ? AND {jour} = ?";
        foreach (var v in new object[] { societe, numero, date.Year, date.Month, date.Day }) BaseDb2.Valeur(cmd.Parameters, v);
        await using var rd = await cmd.ExecuteReaderAsync();
        var lignes = new List<Dictionary<string, object>>();
        while (await rd.ReadAsync())
        {
            var l = new Dictionary<string, object>();
            for (var i = 0; i < rd.FieldCount; i++) l[rd.GetName(i).Trim()] = rd.GetValue(i) is decimal d ? d : rd.GetValue(i);
            lignes.Add(l);
        }
        return lignes;
    }
}
