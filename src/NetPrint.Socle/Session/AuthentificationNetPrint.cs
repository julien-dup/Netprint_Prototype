using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NetPrint.Socle.Session;

public static class AuthentificationNetPrint
{
    public const string PageConnexion = "/connexion";

    // À appeler une fois dans le Program.cs de NetPrint.Web : builder.AjouterSocle();
    public static WebApplicationBuilder AjouterSocle(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = SessionNetPrint.NomCookie;
                o.Cookie.HttpOnly = true;                              // illisible depuis JavaScript
                o.Cookie.SameSite = SameSiteMode.Strict;
                // HTTPS obligatoire en production ; en développement (http://localhost) le cookie passe aussi en HTTP
                o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromHours(10);             // session de travail, renouvelée tant qu'on l'utilise
                o.SlidingExpiration = true;
                o.LoginPath = PageConnexion;
                o.AccessDeniedPath = PageConnexion;
                o.ReturnUrlParameter = "retour";
                // Écrans : redirection vers la page de connexion ; API (/api/...) : 401 / 403, sans redirection
                o.Events.OnRedirectToLogin = c => Rediriger(c, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = c => Rediriger(c, StatusCodes.Status403Forbidden);
            });

        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IUtilisateurCourant, UtilisateurCourant>();
        builder.Services.AddSingleton<IAuthentificateur, AuthentificateurIbmi>();

        // Clés de chiffrement du cookie : à fixer sur un dossier en production (sinon les sessions sont perdues
        // à chaque redémarrage du pool IIS). En développement, l'emplacement par défaut de .NET suffit.
        var dossierCles = builder.Configuration["DataProtection:Dossier"];
        if (!string.IsNullOrWhiteSpace(dossierCles))
            builder.Services.AddDataProtection()
                .SetApplicationName("NetPrint")
                .PersistKeysToFileSystem(new DirectoryInfo(dossierCles));

        return builder;
    }

    private static Task Rediriger(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> c, int codeApi)
    {
        if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = codeApi;
        else c.Response.Redirect(c.RedirectUri);
        return Task.CompletedTask;
    }
}
