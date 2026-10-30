using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetPrint.Socle.Donnees;

namespace NetPrint.Socle.Session;

public enum ResultatAuthentification { Ok, Refuse, Indisponible }

// Vérification du profil / mot de passe. Prototype : contrôle sur l'IBM i ; cible : Entra ID (le reste ne change pas).
public interface IAuthentificateur
{
    Task<ResultatAuthentification> VerifierAsync(string profil, string motDePasse, CancellationToken ct = default);
}

// Ex-SQLConnecte du composant Connexion WinDev : on tente une connexion ODBC avec le profil et le mot de passe saisis.
// Le mot de passe n'est ni stocké ni gardé en mémoire.
internal sealed class AuthentificateurIbmi(IConfiguration config, IHostEnvironment env, ILogger<AuthentificateurIbmi> log)
    : IAuthentificateur
{
    public async Task<ResultatAuthentification> VerifierAsync(string profil, string motDePasse, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(profil) || string.IsNullOrEmpty(motDePasse)) return ResultatAuthentification.Refuse;

        if (ModeDemo.Actif(config, env))
        {
            log.LogWarning("Mode démo : connexion de {Profil} acceptée sans vérification IBM i", profil);
            return ResultatAuthentification.Ok;
        }

        try
        {
            await BaseDb2.VerifierProfilAsync(config, profil, motDePasse, ct);
            return ResultatAuthentification.Ok;
        }
        catch (OdbcException ex)
        {
            // SQLSTATE 28xxx = autorisation refusée (profil ou mot de passe) ; le reste = IBM i injoignable, pilote absent...
            var etat = ex.Errors.Count > 0 ? ex.Errors[0].SQLState : "";
            if (etat.StartsWith("28")) return ResultatAuthentification.Refuse;
            log.LogError(ex, "Connexion IBM i impossible pour {Profil}", profil);
            return ResultatAuthentification.Indisponible;
        }
    }
}

// Mode démo : DÉVELOPPEMENT UNIQUEMENT (appsettings.Development.json, Authentification:ModeDemo = true).
// Accepte n'importe quel profil / mot de passe, pour travailler sur les écrans sans IBM i.
public static class ModeDemo
{
    public static bool Actif(IConfiguration config, IHostEnvironment env) =>
        env.IsDevelopment() && config.GetValue<bool>("Authentification:ModeDemo");
}
