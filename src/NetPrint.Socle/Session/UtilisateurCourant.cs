using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace NetPrint.Socle.Session;

// Point d'accès UNIQUE des modules à l'utilisateur connecté (ex-SessionFilter des essais) :
// les services métier prennent la société ici, jamais dans un paramètre venu de l'écran ou de l'API.
// Quand on passera à Entra ID, seule cette implémentation changera.
public interface IUtilisateurCourant
{
    Task<UtilisateurConnecte> LireAsync();

    // Comme LireAsync, mais lève une exception si personne n'est connecté
    async Task<UtilisateurConnecte> ExigerAsync()
    {
        var u = await LireAsync();
        return u.EstConnecte ? u : throw new UnauthorizedAccessException("Aucune session NetPrint ouverte.");
    }
}

internal sealed class UtilisateurCourant(IServiceProvider services, IHttpContextAccessor http) : IUtilisateurCourant
{
    public async Task<UtilisateurConnecte> LireAsync()
    {
        // Écrans Blazor (interactifs ou statiques) : l'état d'authentification du circuit fait foi
        var etat = services.GetService<AuthenticationStateProvider>();
        if (etat is not null)
        {
            try
            {
                return (await etat.GetAuthenticationStateAsync()).User.Utilisateur();
            }
            catch (InvalidOperationException)
            {
                // Appel d'API (hors Blazor) : l'état Blazor n'est pas initialisé, on lit la requête HTTP
            }
        }
        return http.HttpContext?.User.Utilisateur() ?? UtilisateurConnecte.Anonyme;
    }
}
