using NetPrint.Modules.Ventes.Contrats;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Reprise d'un devis dans une nouvelle ligne de commande (NEG520 F10, routine SPDEV) : règles sans accès aux données.
// La ligne est « à la demande » (statut 3), au prix remis du devis ; un devis « le tout » (unité de diviseur 999999999)
// devient un prix au mille.
// =====================================================================
internal static class RepriseDevis
{
    public const string UniteLeMille = "LM";

    // D5000F : désignation, référence imprimée, unité, quantité devisée, prix remis (DEPVR), prix de revient (DEPR) ;
    // diviseurs (AA00083 'UV') de l'unité du devis et du mille, option société SOP18 (article = référence imprimée)
    public sealed record Devis(ReferenceDevis Reference, string Designation, string ReferenceImprime, string Unite, int Quantite,
        decimal PrixRemis, decimal PrixRevient, long? TnumUnite, long? TnumLeMille, bool ArticleDuDevis);

    public static bool LeTout(Devis d) => d.TnumUnite == MoteurPrixCommande.TnumLeTout;

    // Unité et prix unitaire de la ligne : un prix « le tout » est ramené au mille (DEPVR × 1000 / quantité)
    public static (string Unite, decimal Prix) PrixDeVente(Devis d) =>
        LeTout(d)
            ? (UniteLeMille, d.Quantite != 0 ? Tronquer(d.PrixRemis * 1000 / d.Quantite, 2) : 0m)
            : (d.Unite, d.PrixRemis);

    // Diviseur de l'unité de la ligne
    public static long? TnumLigne(Devis d) => LeTout(d) ? d.TnumLeMille : d.TnumUnite;

    // Prix de cession : prix de revient unitaire × diviseur de l'unité de la ligne ; unité inconnue : 0
    public static decimal PrixCession(Devis d) =>
        d.Quantite == 0 || TnumLigne(d) is not { } tnum ? 0m : Tronquer(Tronquer(d.PrixRevient / d.Quantite, 5) * tnum, 2);

    // Valeurs du devis sur la ligne (déjà pourvue des valeurs par défaut d'une nouvelle ligne) : désignation, unité, prix,
    // statut « à la demande », quantité et adresse. Adresses du devis : aucune → quantité devisée, adresse par défaut
    // conservée ; une (ou celle choisie) → sa quantité, et le n° d'adresse du client, le siège (adresse vide) ou
    // l'adresse saisie dans le devis avec ses commentaires.
    public static void Appliquer(LigneSaisie l, Devis d, AdresseDevis? adresse)
    {
        if (d.ArticleDuDevis && d.ReferenceImprime != "") (l.Article, l.Indice) = (d.ReferenceImprime, "");
        l.Designation = d.Designation;
        (l.Unite, var prix) = PrixDeVente(d);
        l.PrixBrut = prix;
        l.PrixSaisi = true;                       // le tarif ne remplace pas le prix du devis
        var cession = PrixCession(d);
        l.PrixCession = cession == 0 ? null : cession;
        l.Statut = StatutsLigne.ALaDemande;
        l.Devis = d.Reference;
        if (adresse is null)
        {
            l.Quantite = d.Quantite;
            return;
        }
        // ÉCART : NEG520 laissait la quantité à 0 quand l'adresse choisie parmi plusieurs était un n° d'adresse du client
        l.Quantite = adresse.Quantite;
        (l.AdresseRaisonSociale, l.AdresseRue1, l.AdresseRue2, l.AdresseCodePostal, l.AdresseVille, l.AdressePays) = ("", "", "", "", "", "");
        if (adresse.Priorite != 0)
        {
            (l.NumeroAdresse, l.AdresseSiege) = (adresse.Priorite, false);
            return;
        }
        l.NumeroAdresse = null;
        l.AdresseSiege = adresse.Vide;
        if (adresse.Vide) return;
        (l.AdresseRaisonSociale, l.AdresseRue1, l.AdresseRue2, l.AdresseCodePostal, l.AdresseVille) =
            (adresse.RaisonSociale, adresse.Rue1, adresse.Rue2, adresse.CodePostal, adresse.Ville);
        (l.Commentaire1, l.Commentaire2, l.Commentaire3) = (adresse.Commentaire1, adresse.Commentaire2, adresse.Commentaire3);
    }

    private static decimal Tronquer(decimal v, int decimales)
    {
        var f = (decimal)Math.Pow(10, decimales);
        return Math.Truncate(v * f) / f;
    }
}
