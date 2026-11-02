using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Ecrans.Livraisons;

// État de l'écran des livraisons à effectuer, conservé pendant la session : critères, liste et lignes cochées
public sealed class EtatLivraisons
{
    public CriteresLivraisons Criteres { get; set; } = new();
    public bool PlusDeCriteres { get; set; }
    public ResultatRecherche<LigneALivrer>? Resultat { get; set; }
    public HashSet<(int Dossier, string Cle, int Ligne)> Cochees { get; } = [];
}
