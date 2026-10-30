using System.Globalization;

namespace NetPrint.Web.Commun;

// Tri et filtres par colonne des grilles (comme les tables WinDev), repris de GestionOutils (filtresColonnes.ts).
// Le filtrage se fait en mémoire, sur les lignes déjà chargées par la recherche.

public enum TypeColonne { Texte, Nombre, Date, OuiNon }

// Valeur et Valeur2 sont saisies dans la bulle ; les dates sont au format AAAA-MM-JJ (champ date HTML)
public sealed record FiltreColonne(string Colonne, string Operateur, string? Valeur = null, string? Valeur2 = null);

// Événements d'un titre de colonne : nouveau filtre (null = filtre retiré) ;
// nouveau tri (Descendant null = retour à l'ordre par défaut de la grille)
public sealed record ChangementFiltre(string Colonne, FiltreColonne? Filtre);
public sealed record ChangementTri(string Colonne, bool? Descendant);

public sealed record OperateurFiltre(string Code, string Libelle, int NbValeurs);

public static class FiltresColonnes
{
    private static readonly Dictionary<TypeColonne, OperateurFiltre[]> operateurs = new()
    {
        [TypeColonne.Texte] =
        [
            new("contient", "contient", 1),
            new("commence", "commence par", 1),
            new("egal", "est égal à", 1),
            new("different", "est différent de", 1),
            new("necontientpas", "ne contient pas", 1),
            new("vide", "est vide", 0),
            new("nonvide", "n'est pas vide", 0),
        ],
        [TypeColonne.Nombre] =
        [
            new("eq", "=", 1),
            new("ne", "≠", 1),
            new("gt", ">", 1),
            new("ge", "≥", 1),
            new("lt", "<", 1),
            new("le", "≤", 1),
        ],
        [TypeColonne.Date] =
        [
            new("le", "le", 1),
            new("avant", "avant le", 1),
            new("apres", "après le", 1),
            new("entre", "entre", 2),
            new("vide", "sans date", 0),
            new("nonvide", "avec une date", 0),
        ],
        [TypeColonne.OuiNon] =
        [
            new("avec", "avec", 0),
            new("sans", "sans", 0),
        ],
    };

    private static readonly CultureInfo fr = CultureInfo.GetCultureInfo("fr-FR");
    private const CompareOptions SansCasseNiAccents = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static IReadOnlyList<OperateurFiltre> Operateurs(TypeColonne type) => operateurs[type];

    public static int NbValeurs(TypeColonne type, string operateur) =>
        operateurs[type].FirstOrDefault(o => o.Code == operateur)?.NbValeurs ?? 1;

    // Message d'erreur affiché dans la bulle, ou null si la saisie est correcte
    public static string? Controler(TypeColonne type, string operateur, string? valeur, string? valeur2)
    {
        var nb = NbValeurs(type, operateur);
        if (nb >= 1 && string.IsNullOrWhiteSpace(valeur)) return type == TypeColonne.Date ? "Saisissez une date." : "Saisissez une valeur.";
        if (nb == 2 && string.IsNullOrWhiteSpace(valeur2)) return "Saisissez les deux dates.";
        if (nb >= 1 && type == TypeColonne.Nombre && LireNombre(valeur) is null) return "Saisissez un nombre.";
        if (nb >= 1 && type == TypeColonne.Date && LireDate(valeur) is null) return "Saisissez une date.";
        if (nb == 2 && type == TypeColonne.Date && LireDate(valeur2) is null) return "Saisissez une date.";
        return null;
    }

    // Texte de la pastille : « Nom commence par « DUP » », « Date entre 01/01/2026 et 31/03/2026 »
    public static string Decrire(FiltreColonne f, string libelle, TypeColonne type)
    {
        var op = operateurs[type].FirstOrDefault(o => o.Code == f.Operateur);
        if (op is null) return libelle;
        if (type == TypeColonne.OuiNon) return $"{libelle} : {op.Libelle}";
        if (op.NbValeurs == 0) return $"{libelle} {op.Libelle}";
        var v = type == TypeColonne.Date ? DateFr(f.Valeur) : f.Valeur;
        if (op.NbValeurs == 2) return $"{libelle} {op.Libelle} {v} et {DateFr(f.Valeur2)}";
        return type == TypeColonne.Texte ? $"{libelle} {op.Libelle} « {v} »" : $"{libelle} {op.Libelle} {v}";
    }

    // La valeur de la ligne : string (texte), nombre (int, decimal…, null = 0), DateOnly? (date),
    // bool ou nombre (oui/non : « avec » = vrai ou différent de 0)
    public static bool Accepte(FiltreColonne f, TypeColonne type, object? valeur) => type switch
    {
        TypeColonne.Texte => AccepteTexte(f, valeur as string ?? valeur?.ToString() ?? ""),
        TypeColonne.Nombre => AccepteNombre(f, valeur is null ? 0m : Convert.ToDecimal(valeur, CultureInfo.InvariantCulture)),
        TypeColonne.Date => AccepteDate(f, valeur as DateOnly?),
        TypeColonne.OuiNon => (f.Operateur == "avec") == EstVrai(valeur),
        _ => true,
    };

    private static bool AccepteTexte(FiltreColonne f, string texte)
    {
        texte = texte.Trim();
        var v = (f.Valeur ?? "").Trim();
        var ci = fr.CompareInfo;
        return f.Operateur switch
        {
            "contient" => ci.IndexOf(texte, v, SansCasseNiAccents) >= 0,
            "commence" => ci.IsPrefix(texte, v, SansCasseNiAccents),
            "egal" => ci.Compare(texte, v, SansCasseNiAccents) == 0,
            "different" => ci.Compare(texte, v, SansCasseNiAccents) != 0,
            "necontientpas" => ci.IndexOf(texte, v, SansCasseNiAccents) < 0,
            "vide" => texte == "",
            "nonvide" => texte != "",
            _ => true,
        };
    }

    private static bool AccepteNombre(FiltreColonne f, decimal n)
    {
        if (LireNombre(f.Valeur) is not { } v) return true;
        return f.Operateur switch
        {
            "eq" => n == v,
            "ne" => n != v,
            "gt" => n > v,
            "ge" => n >= v,
            "lt" => n < v,
            "le" => n <= v,
            _ => true,
        };
    }

    private static bool AccepteDate(FiltreColonne f, DateOnly? d)
    {
        if (f.Operateur == "vide") return d is null;
        if (f.Operateur == "nonvide") return d is not null;
        if (d is null || LireDate(f.Valeur) is not { } v) return false;
        switch (f.Operateur)
        {
            case "le": return d == v;
            case "avant": return d < v;
            case "apres": return d > v;
            case "entre":
                if (LireDate(f.Valeur2) is not { } v2) return d >= v;
                var (debut, fin) = v <= v2 ? (v, v2) : (v2, v);
                return d >= debut && d <= fin;
            default: return true;
        }
    }

    private static bool EstVrai(object? valeur) => valeur switch
    {
        null => false,
        bool b => b,
        string s => s.Trim() != "",
        _ => Convert.ToDecimal(valeur, CultureInfo.InvariantCulture) != 0,
    };

    // Saisie française acceptée : « 1 234,5 » ou « 1234.5 »
    private static decimal? LireNombre(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var propre = s.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(propre, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static DateOnly? LireDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string DateFr(string? iso) => LireDate(iso)?.ToString("dd/MM/yyyy") ?? "";
}
