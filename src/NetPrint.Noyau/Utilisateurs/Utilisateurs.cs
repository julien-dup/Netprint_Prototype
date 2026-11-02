using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;

namespace NetPrint.Noyau.Utilisateurs;

// Profils utilisateurs NetPrint (AI00F)
public interface IUtilisateurs
{
    // Adresse e-mail externe du profil (AI00F.ACMELE « BAL EMAIL EXTERNE ») ; "" si non renseignée.
    // Avec la connexion Entra ID, l'adresse viendra du compte de l'utilisateur.
    Task<string> EmailAsync(string profil, CancellationToken ct = default);
}

internal sealed class UtilisateursDb2(IConfiguration config) : IUtilisateurs
{
    public async Task<string> EmailAsync(string profil, CancellationToken ct = default)
    {
        var bib = BaseDb2.Bibliotheque(config);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT ACMELE FROM {bib}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY";
        cmd.Parameters.Valeur(profil.Trim().ToUpperInvariant());
        var res = await cmd.ExecuteScalarAsync(ct);
        var email = res is null or DBNull ? "" : Convert.ToString(res)!.Trim();
        return email.Contains('@') ? email : "";
    }
}
