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
    public CleDevis? Selection { get; set; }

    // Présentation du détail : « A » par atelier (défaut WinDev), « E » par élément
    public string VueDetail { get; set; } = "A";
}
