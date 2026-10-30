using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace NetPrint.Socle.Donnees;

// Accès à la base PostgreSQL NetPrint (repris des essais, servira quand un module basculera sur PostgreSQL).
// Chaîne de connexion : ConnectionStrings:NetPrint (compte applicatif netprint_app)
//
// RÈGLE DE TRI (toutes les tables) : comme sur l'AS400, les lettres passent avant les chiffres.
// Tout ORDER BY sur une colonne texte s'écrit avec la fonction SQL NP11F.TRI_AS400 :
//     ORDER BY NP11F.TRI_AS400(a.DGOUT)          (et non ORDER BY a.DGOUT)
// Pas d'apostrophe dans les commentaires SQL des requêtes (voir Positionnel).
public static class BaseNetPrint
{
    public const string NomChaine = "NetPrint";

    public static NpgsqlConnection Connexion(IConfiguration config) =>
        new(config.GetConnectionString(NomChaine)
            ?? throw new InvalidOperationException(
                $"Chaîne de connexion « {NomChaine} » absente (user secrets ou variable ConnectionStrings__{NomChaine})."));

    // Commande PostgreSQL à partir d'une requête écrite avec des « ? » (même style que DB2 / ODBC)
    public static NpgsqlCommand Commande(string sql, NpgsqlConnection cnx, NpgsqlTransaction? tx = null) =>
        new(Positionnel(sql), cnx, tx);

    // PostgreSQL n'accepte pas « ? » : il attend $1, $2... dans l'ordre des paramètres.
    // Les « ? » situés entre apostrophes ('...') ne sont pas touchés.
    public static string Positionnel(string sql)
    {
        var sb = new StringBuilder(sql.Length + 16);
        var n = 0;
        var dansChaine = false;
        foreach (var c in sql)
        {
            if (c == '\'') dansChaine = !dansChaine;
            if (c == '?' && !dansChaine) sb.Append('$').Append(++n);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Paramètre positionnel ($1, $2... dans l'ordre d'ajout).
    // Les textes sont envoyés en CHAR (bpchar) : comme sur DB2, les blancs de fin ne comptent pas dans les comparaisons.
    public static NpgsqlParameter Valeur(this NpgsqlParameterCollection parametres, object? valeur)
    {
        var p = new NpgsqlParameter { Value = valeur ?? DBNull.Value };
        if (valeur is string) p.NpgsqlDbType = NpgsqlDbType.Char;
        return parametres.Add(p);
    }
}
