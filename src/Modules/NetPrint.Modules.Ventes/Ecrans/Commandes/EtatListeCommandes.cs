using NetPrint.Modules.Ventes.Contrats;

namespace NetPrint.Modules.Ventes.Ecrans.Commandes;

// État de la liste des commandes, conservé pendant la session : en revenant d'une commande,
// on retrouve ses critères, sa liste, sa vue, ses tris, filtres, commandes dépliées et sa sélection.
public sealed class EtatListeCommandes
{
    public CriteresCommandes Criteres { get; set; } = new();
    public bool PlusDeCriteres { get; set; }
    public ResultatRechercheCommandes? Resultat { get; set; }

    // « C » une ligne par commande (lignes dépliables), « L » une ligne par ligne de commande
    public string Vue { get; set; } = "C";

    // Filtre rapide sur l'état (pastilles au-dessus de la liste) ; null = tous
    public EtatCommande? FiltreEtat { get; set; }

    public HashSet<CleCommande> Depliees { get; } = [];
    public CleCommande? Selection { get; set; }

    internal EtatGrille GrilleCommandes { get; } = new("ventes.commandes");
    internal EtatGrille GrilleLignes { get; } = new("ventes.commandes-lignes");
    internal EtatGrille GrilleCourante => Vue == "L" ? GrilleLignes : GrilleCommandes;

    // Valeurs par défaut de l'utilisateur, rangées avec l'affichage de la grille des commandes
    public const string DefautVue = "vue";
    public const string DefautAvecSoldees = "avecSoldees";
    public bool ValeursDefautLues { get; set; }
}
