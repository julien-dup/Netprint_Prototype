using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Noyau.Societes;

// Sociétés (AA0007)
public interface ISocietes
{
    // Nom réduit de la société (AA0007.SRS) ; null si la société n'existe pas
    Task<string?> LireNomAsync(int societe, CancellationToken ct = default);
}

internal sealed class SocietesDb2(IConfiguration config, IHostEnvironment env) : ISocietes
{
    public async Task<string?> LireNomAsync(int societe, CancellationToken ct = default)
    {
        // Mode démo sans IBM i configuré : nom fictif
        if (ModeDemo.Actif(config, env) && string.IsNullOrWhiteSpace(config.GetConnectionString(BaseDb2.NomChaine)))
            return $"SOCIÉTÉ {societe} (démo)";

        var bib = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT SRS FROM {bib}.AA0007 WHERE RSOC = ?";
        cmd.Parameters.Valeur(societe);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is null ? null : res is DBNull ? "" : Convert.ToString(res)!.Trim();
    }
}
