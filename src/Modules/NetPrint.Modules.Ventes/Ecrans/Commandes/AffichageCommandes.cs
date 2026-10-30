using NetPrint.Modules.Ventes.Contrats;

namespace NetPrint.Modules.Ventes.Ecrans.Commandes;

// Mise en forme commune aux écrans des commandes (charte : 0 laissé vide, dates JJ/MM/AAAA)
internal static class AffichageCommandes
{
    public static string Libelle(EtatCommande e) => e switch
    {
        EtatCommande.EnCours => "En cours",
        EtatCommande.PartiellementLivree => "Partiellement livrée",
        EtatCommande.Livree => "Livrée",
        _ => "Soldée",
    };

    // Suffixe des classes CSS etat-… (commandes.css)
    public static string Classe(EtatCommande e) => e switch
    {
        EtatCommande.EnCours => "encours",
        EtatCommande.PartiellementLivree => "partielle",
        EtatCommande.Livree => "livree",
        _ => "soldee",
    };

    public static string Entier(int v) => v == 0 ? "" : v.ToString("N0");
    public static string Montant(decimal v) => v == 0 ? "" : v.ToString("N2");
    public static string Date(DateOnly? d) => d?.ToString("dd/MM/yyyy") ?? "";
    public static string ArticleIndice(LigneCommandeListe l) => l.Indice != "" ? $"{l.Article} {l.Indice}" : l.Article;
}
