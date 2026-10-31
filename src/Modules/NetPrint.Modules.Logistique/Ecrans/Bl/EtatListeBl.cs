using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Ecrans.Bl;

// État de la liste des BL, conservé pendant la session : en revenant d'un BL, on retrouve critères, liste, tri et sélection
public sealed class EtatListeBl
{
    public CriteresBl Criteres { get; set; } = new();
    public bool PlusDeCriteres { get; set; }
    public ResultatRecherche<ElementBl>? Resultat { get; set; }
    public string Tri { get; set; } = "";
    public bool TriDescendant { get; set; }
    public (int Bl, int Element)? Selection { get; set; }
}
