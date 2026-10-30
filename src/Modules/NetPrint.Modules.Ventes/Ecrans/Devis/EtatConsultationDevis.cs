using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Web.Commun;

namespace NetPrint.Modules.Ventes.Ecrans.Devis;

// État de l'écran de consultation, conservé pendant la session de l'utilisateur :
// en revenant du détail d'un devis, on retrouve ses critères, sa liste, son tri, ses filtres de colonnes et sa sélection.
public sealed class EtatConsultationDevis
{
    public CriteresDevis Criteres { get; set; } = new();
    public bool PlusDeCriteres { get; set; }
    public List<LigneListeDevis>? Lignes { get; set; }
    public bool Tronque { get; set; }
    public string? TriCle { get; set; }
    public bool TriDescendant { get; set; }

    // Filtres de colonnes, dans l'ordre où ils ont été posés (un par colonne au plus)
    public List<FiltreColonne> Filtres { get; set; } = [];

    // Affichage de la grille mémorisé pour l'utilisateur (colonnes masquées, largeurs, tri) : lu une fois par session
    public PreferencesGrille? Preferences { get; set; }
    public bool PreferencesListeAppliquees { get; set; }   // tri, colonnes et filtres par défaut appliqués à la liste
    public CleDevis? Selection { get; set; }

    // Chapitre de légende copié (« Copier » / « Coller » de la fenêtre Légende, lot E)
    public ChapitreLegende? ChapitreCopie { get; set; }

    // Présentation du détail : « A » par atelier (défaut WinDev), « E » par élément
    public string VueDetail { get; set; } = "A";
    public bool VueDetailLue { get; set; }

    // Préférences de l'écran (affichage de la grille + valeurs par défaut), même entrée pour la liste et le détail
    public const string NomGrille = "ventes.consultation-devis";

    // Valeurs par défaut (ex-ConsultationDevis.Ini, FEN_ValeursDefaut) : clés et valeurs possibles
    public const string DefautDevis = "devis";          // "" tous, "1" uniquement en cours, "2" uniquement en commande
    public const string DefautTypeDevis = "typeDevis";  // "" tous, "T" devis type, "C" devis client
    public const string DefautDetail = "detail";        // "A" par atelier, "E" par élément

    public static string Valeur(PreferencesGrille? p, string cle, string parDefaut = "") =>
        p is not null && p.Valeurs.TryGetValue(cle, out var v) ? v : parDefaut;

    // Filtres « Devis » et « Type devis » des critères (ouverture de l'écran, gomme, après « Valeurs par défaut »)
    public void AppliquerFiltresParDefaut()
    {
        Criteres.EtatDevis = Valeur(Preferences, DefautDevis);
        Criteres.TypeDevis = Valeur(Preferences, DefautTypeDevis);
    }

    public void AppliquerVueParDefaut()
    {
        VueDetail = Valeur(Preferences, DefautDetail, "A") == "E" ? "E" : "A";
        VueDetailLue = true;
    }
}
