namespace NetPrint.Modules.Ventes.Ecrans.Devis;

// Mise en forme des nombres et des dates dans les grilles (charte : 0 laissé vide, dates JJ/MM/AAAA)
internal static class Affichage
{
    public static string Entier(int v) => v == 0 ? "" : v.ToString("N0");
    public static string Entier(decimal v) => v == 0 ? "" : v.ToString("N0");
    public static string Montant(decimal v) => v == 0 ? "" : v.ToString("N2");
    public static string Montant(decimal? v) => v is null or 0 ? "" : v.Value.ToString("N2");
    public static string Nombre(decimal v) => v == 0 ? "" : v.ToString("#,##0.###");
    public static string Date(DateOnly? d) => d?.ToString("dd/MM/yyyy") ?? "";

    public static string ClasseNature(Contrats.NatureLigne n) => n switch
    {
        Contrats.NatureLigne.Titre => "ligne-titre",
        Contrats.NatureLigne.SousTotal => "ligne-sous-total",
        _ => "",
    };
}
