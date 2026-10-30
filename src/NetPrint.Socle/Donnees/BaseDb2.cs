using System.Data.Common;
using System.Data.Odbc;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace NetPrint.Socle.Donnees;

// Accès à la base DB2 de l'IBM i (ODBC IBM i Access) pendant la cohabitation.
// Chaîne de connexion : ConnectionStrings:Db2 (compte de service)
//   - en dev   : user secrets (même UserSecretsId que les essais GestionOutils : la chaîne est déjà là)
//   - sur IIS  : variable d'environnement du pool  ConnectionStrings__Db2
// Bibliothèque des données : Db2:Bibliotheque (NP11F par défaut).
//
// Règles :
//   * SQL toujours paramétré (« ? » + Parameters.Valeur dans l'ordre) ; jamais de valeur saisie concaténée.
//   * Seul le nom de bibliothèque est inséré dans le texte SQL : il vient de la configuration et est contrôlé.
//   * Ces classes ne s'utilisent que dans le dossier Donnees/ d'un module, jamais dans un écran.
public static partial class BaseDb2
{
    public const string NomChaine = "Db2";

    public static OdbcConnection Connexion(IConfiguration config) =>
        new(config.GetConnectionString(NomChaine)
            ?? throw new InvalidOperationException(
                $"Chaîne de connexion « {NomChaine} » absente (user secrets ou variable ConnectionStrings__{NomChaine})."));

    public static string Bibliotheque(IConfiguration config) => Bibliotheque(config["Db2:Bibliotheque"]);

    public static string Bibliotheque(string? valeur)
    {
        var bib = string.IsNullOrWhiteSpace(valeur) ? "NP11F" : valeur.Trim().ToUpperInvariant();
        return NomObjetIbmi().IsMatch(bib)
            ? bib
            : throw new InvalidOperationException($"Nom de bibliothèque DB2 invalide : « {valeur} ».");
    }

    // Paramètre positionnel (« ? ») : à ajouter dans l'ordre d'apparition dans la requête
    public static OdbcParameter Valeur(this OdbcParameterCollection parametres, object? valeur) =>
        parametres.AddWithValue($"p{parametres.Count + 1}", valeur ?? DBNull.Value);

    // Lecture : les zones alpha AS400 sont complétées par des blancs, on les enlève
    public static string Texte(this DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim();

    // Les zones numériques AS400 (étendues / packées) arrivent en decimal
    public static int Entier(this DbDataReader rd, int i) => rd.IsDBNull(i) ? 0 : Convert.ToInt32(rd.GetValue(i));

    public static decimal Nombre(this DbDataReader rd, int i) => rd.IsDBNull(i) ? 0m : Convert.ToDecimal(rd.GetValue(i));

    // Date AS400 éclatée en trois zones (année, mois, jour) ; null si vide ou invalide
    public static DateOnly? Date(int annee, int mois, int jour) =>
        annee is >= 1 and <= 9999 && mois is >= 1 and <= 12 && jour >= 1 && jour <= DateTime.DaysInMonth(annee, mois)
            ? new DateOnly(annee, mois, jour) : null;

    // Date stockée en un seul nombre AAAAMMJJ (ex. F6DATEE, DEDDEM) ; null si vide ou invalide
    public static DateOnly? DateAAAAMMJJ(int valeur) => Date(valeur / 10000, valeur / 100 % 100, valeur % 100);

    // Date au format AAAAMMJJ, pour comparer avec (ANNEE * 10000 + MOIS * 100 + JOUR) dans le SQL
    public static int AAAAMMJJ(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;

    // Vérifie le profil / mot de passe auprès de l'IBM i par une connexion ODBC d'essai.
    // Lève OdbcException si le profil ou le mot de passe est refusé (SQLSTATE 28xxx) ou si l'IBM i est injoignable.
    public static async Task VerifierProfilAsync(IConfiguration config, string profil, string motDePasse, CancellationToken ct = default)
    {
        var cs = new OdbcConnectionStringBuilder(config.GetConnectionString(NomChaine)
            ?? throw new InvalidOperationException($"Chaîne de connexion « {NomChaine} » absente."));
        foreach (var cle in new[] { "UID", "PWD", "User ID", "Password", "UserID" })
            cs.Remove(cle);
        cs["UID"] = profil;
        cs["PWD"] = motDePasse;
        await using var essai = new OdbcConnection(cs.ConnectionString);
        await essai.OpenAsync(ct);
    }

    [GeneratedRegex("^[A-Z#$@][A-Z0-9_#$@]{0,9}$")]
    private static partial Regex NomObjetIbmi();
}
