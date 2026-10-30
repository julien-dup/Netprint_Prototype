using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using NetPrint.Noyau.Societes;
using NetPrint.Socle.Session;

namespace NetPrint.Web.Compte;

// Connexion / déconnexion / paramètres : le cookie de session ne peut être posé que par une vraie requête HTTP,
// d'où ces points d'entrée appelés par des formulaires (page /connexion, page /parametres, bouton « Se déconnecter »).
public static class EndpointsCompte
{
    public static IEndpointRouteBuilder MapEndpointsCompte(this IEndpointRouteBuilder app)
    {
        var compte = app.MapGroup("/compte");

        // Le jeton anti-falsification du formulaire est contrôlé automatiquement (UseAntiforgery + [FromForm])
        compte.MapPost("/connexion", ConnexionAsync).AllowAnonymous();

        compte.MapPost("/deconnexion", async (HttpContext http) =>
        {
            await SessionNetPrint.FermerAsync(http);
            return Results.LocalRedirect("~" + AuthentificationNetPrint.PageConnexion);
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());

        // Changement de société / compte général / compte client sans ressaisir le mot de passe
        compte.MapPost("/parametres", ParametresAsync).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> ConnexionAsync(
        HttpContext http,
        [FromForm] FormulaireConnexion f,
        IAuthentificateur authentificateur,
        ISocietes societes,
        ILogger<FormulaireConnexion> log,
        CancellationToken ct)
    {
        var profil = (f.Profil ?? "").Trim().ToUpperInvariant();
        var retour = RetourLocal(f.Retour);

        // En cas d'erreur : retour sur la page de connexion avec le message et la saisie (sauf le mot de passe)
        IResult Echec(string message) => Results.LocalRedirect(
            $"~{AuthentificationNetPrint.PageConnexion}?erreur={Uri.EscapeDataString(message)}" +
            $"&profil={Uri.EscapeDataString(profil)}&societe={Uri.EscapeDataString(f.Societe ?? "")}" +
            $"&compte={Uri.EscapeDataString(f.CompteGeneral ?? "")}&retour={Uri.EscapeDataString(retour)}");

        if (profil == "" || profil.Length > 10) return Echec("Le profil est obligatoire (10 caractères maximum).");
        if (string.IsNullOrEmpty(f.MotDePasse)) return Echec("Le mot de passe est obligatoire.");
        if (!int.TryParse(f.Societe, out var societe) || societe is <= 0 or > 9999) return Echec("Le code société est obligatoire.");
        var compteGeneral = 0;
        if (!string.IsNullOrWhiteSpace(f.CompteGeneral)
            && (!int.TryParse(f.CompteGeneral, out compteGeneral) || compteGeneral is < 0 or > 999999))
            return Echec("Le compte général est invalide.");

        // 1. Profil / mot de passe
        switch (await authentificateur.VerifierAsync(profil, f.MotDePasse, ct))
        {
            case ResultatAuthentification.Refuse:
                log.LogWarning("Échec de connexion du profil {Profil}", profil);
                return Echec("Profil ou mot de passe incorrect.");
            case ResultatAuthentification.Indisponible:
                return Echec("Le serveur IBM i ne répond pas. Réessayez dans quelques instants.");
        }

        // 2. La société doit exister (AA0007).
        //    À faire : contrôler que le profil a accès à cette société (options PRT000P1, code STE).
        string? nomSociete;
        try
        {
            nomSociete = await societes.LireNomAsync(societe, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Lecture de la société {Societe} impossible", societe);
            return Echec("Lecture de la société impossible : " + ex.Message);
        }
        if (nomSociete is null) return Echec($"La société {societe} n'existe pas.");

        // 3. Ouverture de la session
        await SessionNetPrint.OuvrirAsync(http, new UtilisateurConnecte(profil, societe, nomSociete, compteGeneral), f.ResterConnecte);
        log.LogInformation("Connexion du profil {Profil}, société {Societe}", profil, societe);
        return Results.LocalRedirect("~" + retour);
    }

    private static async Task<IResult> ParametresAsync(
        HttpContext http,
        [FromForm] FormulaireParametres f,
        ISocietes societes,
        ILogger<FormulaireParametres> log,
        CancellationToken ct)
    {
        var actuel = http.User.Utilisateur();
        var retour = RetourLocal(f.Retour);
        if (retour.StartsWith(PageParametres, StringComparison.OrdinalIgnoreCase)) retour = "/";

        IResult Echec(string message) => Results.LocalRedirect(
            $"~{PageParametres}?erreur={Uri.EscapeDataString(message)}" +
            $"&societe={Uri.EscapeDataString(f.Societe ?? "")}&compteGeneral={Uri.EscapeDataString(f.CompteGeneral ?? "")}" +
            $"&compteClient={Uri.EscapeDataString(f.CompteClient ?? "")}&retour={Uri.EscapeDataString(retour)}");

        if (!int.TryParse(f.Societe, out var societe) || societe is <= 0 or > 9999) return Echec("Le code société est obligatoire.");
        var compteGeneral = 0;
        if (!string.IsNullOrWhiteSpace(f.CompteGeneral)
            && (!int.TryParse(f.CompteGeneral, out compteGeneral) || compteGeneral is < 0 or > 999999))
            return Echec("Le compte général est invalide.");
        var compteClient = 0;
        if (!string.IsNullOrWhiteSpace(f.CompteClient)
            && (!int.TryParse(f.CompteClient, out compteClient) || compteClient is < 0 or > 9999999))
            return Echec("Le compte client est invalide.");

        // La société doit exister (AA0007). À faire, comme à la connexion : droit du profil sur la société (PRT000P1).
        // À faire : contrôler que le compte client existe dans la société.
        string? nomSociete;
        try
        {
            nomSociete = await societes.LireNomAsync(societe, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Lecture de la société {Societe} impossible", societe);
            return Echec("Lecture de la société impossible : " + ex.Message);
        }
        if (nomSociete is null) return Echec($"La société {societe} n'existe pas.");

        // Nouvelle session pour le même profil, en gardant le choix « Rester connecté »
        var authentification = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var resterConnecte = authentification.Properties?.IsPersistent ?? false;
        await SessionNetPrint.OuvrirAsync(http,
            new UtilisateurConnecte(actuel.Profil, societe, nomSociete, compteGeneral, compteClient), resterConnecte);
        log.LogInformation("Profil {Profil} : société {Ancienne} → {Societe}, compte général {CompteGeneral}, compte client {CompteClient}",
            actuel.Profil, actuel.Societe, societe, compteGeneral, compteClient);

        // Changement de société : les écrans de l'ancienne société n'ont plus de sens, retour à l'accueil
        return Results.LocalRedirect("~" + (societe == actuel.Societe ? retour : "/"));
    }

    public const string PageParametres = "/parametres";

    // N'accepte qu'une adresse de l'application (« /ventes/devis »), jamais un autre site
    private static string RetourLocal(string? retour) =>
        !string.IsNullOrEmpty(retour) && retour.StartsWith('/') && !retour.StartsWith("//") && !retour.StartsWith("/\\")
            ? retour : "/";
}

// Champs du formulaire de connexion (noms = attributs « name » de Connexion.razor).
// Tout en texte pour pouvoir renvoyer un message clair si la saisie est invalide.
public sealed class FormulaireConnexion
{
    public string? Profil { get; set; }
    public string? MotDePasse { get; set; }
    public string? Societe { get; set; }
    public string? CompteGeneral { get; set; }
    public bool ResterConnecte { get; set; }
    public string? Retour { get; set; }
}

// Champs du formulaire de la page Paramètres (noms = attributs « name » de Parametres.razor)
public sealed class FormulaireParametres
{
    public string? Societe { get; set; }
    public string? CompteGeneral { get; set; }
    public string? CompteClient { get; set; }
    public string? Retour { get; set; }
}
