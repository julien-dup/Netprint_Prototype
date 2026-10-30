namespace NetPrint.Noyau.Compteurs;

// N° de dossier attribué par la fonction IBM i GETDOSSIER : « nnnnn c » (n° sur 5, clé), « -1 » en cas d'échec.
// Utilisé par la création des commandes (Ventes) et des demandes d'OF (Production).
public static class NumerosDossier
{
    // (n°, clé) ; null si le retour n'est pas un n° valide (échec du compteur)
    public static (int Numero, string Cle)? Decouper(string? retour)
    {
        var r = (retour ?? "").Trim();
        if (r.Length < 5 || !int.TryParse(r[..5], out var numero) || numero <= 0) return null;
        return (numero, r.Length > 5 ? r[5..].Trim() : "");
    }

    // Clé calculée d'un dossier (GETCLE d'AXISRV, identique à PRTCLEF) : lettre A-Z, modulo 26 de client × 100 + (n° mod 26),
    // ramené sous 26 par divisions successives
    public static string CleCalculee(int numero, int client)
    {
        long modulo = (long)client * 100 + numero % 26;
        long reste = modulo % 26;
        while (true)
        {
            var resultat = modulo / 26;
            reste = modulo % 26;
            if (resultat < 26) break;
            modulo = resultat;
        }
        return ((char)('A' + reste)).ToString();
    }

    // Clés essayées pour un n° déjà utilisé : la clé préférée si elle est libre, sinon les chiffres 1 à 9 puis 0
    public const string ClesChiffres = "1234567890";

    public static string? PremiereCleLibre(IReadOnlySet<string> prises, string? preferee = null) =>
        preferee is { Length: 1 } p && !prises.Contains(p) ? p
            : ClesChiffres.Select(c => c.ToString()).FirstOrDefault(c => !prises.Contains(c));
}
