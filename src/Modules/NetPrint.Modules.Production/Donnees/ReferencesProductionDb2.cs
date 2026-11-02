using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Production.Donnees;

// Contrôles d'existence de la fenêtre de lancement (NEG515C2) : catégorie AA0009, sous-catégorie AE0003, unité AA00083 'UV'
internal sealed class ReferencesProductionDb2(IConfiguration config)
{
    public async Task<(bool Categorie, bool SousCategorie, bool Unite)> VerifierAsync(int societe, string categorie, string sousCategorie,
        string unite, CancellationToken ct)
    {
        var b = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        async Task<bool> Existe(string sql, params object?[] v)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.CommandText = sql;
            foreach (var x in v) cmd.Parameters.Valeur(x);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            return await rd.ReadAsync(ct);
        }
        var cat = categorie == "" || await Existe($"SELECT 1 FROM {b}.AA0009 WHERE RSOC = ? AND RCAT = ?", societe, categorie);
        var scat = sousCategorie == "" || await Existe($"SELECT 1 FROM {b}.AE0003 WHERE RSOC = ? AND RCAT = ? AND RSCAT = ?",
            societe, categorie == "" ? "  " : categorie, sousCategorie);
        var uv = unite == "" || await Existe($"SELECT 1 FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = 'UV' AND RARG = ? AND TTAG <> '*'",
            societe, unite);
        return (cat, scat, uv);
    }
}
