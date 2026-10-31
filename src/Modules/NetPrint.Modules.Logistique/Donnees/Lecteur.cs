using System.Data.Common;
using System.Data.Odbc;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Lecteur d'une requête paramétrée qui libère aussi sa commande (chaîne vide passée en blanc, comme les zones CHAR)
internal sealed class Lecteur(OdbcCommand commande, DbDataReader lecteur) : IAsyncDisposable
{
    public static async Task<Lecteur> ExecuterAsync(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
            return new Lecteur(cmd, await cmd.ExecuteReaderAsync(ct));
        }
        catch
        {
            await cmd.DisposeAsync();
            throw;
        }
    }

    public Task<bool> ReadAsync(CancellationToken ct) => lecteur.ReadAsync(ct);
    public string Texte(int i) => lecteur.Texte(i);
    public int Entier(int i) => lecteur.Entier(i);
    public decimal Nombre(int i) => lecteur.Nombre(i);

    public async ValueTask DisposeAsync()
    {
        await lecteur.DisposeAsync();
        await commande.DisposeAsync();
    }
}
