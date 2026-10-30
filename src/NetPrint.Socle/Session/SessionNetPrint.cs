using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace NetPrint.Socle.Session;

// Session NetPrint : profil, société, compte général et compte client de l'utilisateur, stockés dans le cookie chiffré « NetPrint.Session ».
// Avec une seule application, plus de cookie à partager entre applis ni de dossier de clés commun.
public static class SessionNetPrint
{
    public const string NomCookie = "NetPrint.Session";

    public const string ClaimSociete = "societe";
    public const string ClaimNomSociete = "nomSociete";
    public const string ClaimCompteGeneral = "compteGeneral";
    public const string ClaimCompteClient = "compteClient";

    public const int CompteGeneralParDefaut = 411000;

    // ---------- Lecture : User.Societe(), User.Profil()... ----------
    public static int Societe(this ClaimsPrincipal u) =>
        int.TryParse(u.FindFirstValue(ClaimSociete), out var s) ? s : 0;

    public static string Profil(this ClaimsPrincipal u) => u.Identity?.IsAuthenticated == true ? u.Identity.Name ?? "" : "";

    public static string NomSociete(this ClaimsPrincipal u) => u.FindFirstValue(ClaimNomSociete) ?? "";

    public static int CompteGeneral(this ClaimsPrincipal u) =>
        int.TryParse(u.FindFirstValue(ClaimCompteGeneral), out var c) ? c : 0;

    public static int CompteClient(this ClaimsPrincipal u) =>
        int.TryParse(u.FindFirstValue(ClaimCompteClient), out var c) ? c : 0;

    public static UtilisateurConnecte Utilisateur(this ClaimsPrincipal u) =>
        u.Identity?.IsAuthenticated == true
            ? new UtilisateurConnecte(u.Profil(), u.Societe(), u.NomSociete(), u.CompteGeneral(), u.CompteClient())
            : UtilisateurConnecte.Anonyme;

    // ---------- Ouverture / fermeture ----------
    public static Task OuvrirAsync(HttpContext http, UtilisateurConnecte u, bool resterConnecte)
    {
        var compteGeneral = u.CompteGeneral > 0 ? u.CompteGeneral : CompteGeneralParDefaut;
        var identite = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, u.Profil),
            new Claim(ClaimSociete, u.Societe.ToString()),
            new Claim(ClaimNomSociete, u.NomSociete),
            new Claim(ClaimCompteGeneral, compteGeneral.ToString()),
            new Claim(ClaimCompteClient, Math.Max(0, u.CompteClient).ToString()),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        var proprietes = new AuthenticationProperties
        {
            // « Rester connecté » : cookie conservé 30 jours ; sinon il disparaît à la fermeture du navigateur
            IsPersistent = resterConnecte,
            ExpiresUtc = resterConnecte ? DateTimeOffset.UtcNow.AddDays(30) : null,
        };
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identite), proprietes);
    }

    public static Task FermerAsync(HttpContext http) =>
        http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}

// Utilisateur connecté, tel que les modules le voient (jamais modifiable depuis le navigateur).
// CompteClient : compte client (auxiliaire) par défaut, 0 = aucun ; modifiable dans « Paramètres ».
public sealed record UtilisateurConnecte(string Profil, int Societe, string NomSociete, int CompteGeneral, int CompteClient = 0)
{
    public static readonly UtilisateurConnecte Anonyme = new("", 0, "", 0);

    public bool EstConnecte => Profil != "" && Societe > 0;

    public string Initiales => Profil.Length >= 2 ? Profil[..2] : Profil;
}
