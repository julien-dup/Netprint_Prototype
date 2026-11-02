using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Ecrans.PreBl;

// Critères, liste et sélection des pré-BL gardés pendant la session (retour de la fiche)
public sealed class EtatListePreBl
{
    public CriteresPreBl Criteres { get; set; } = new();
    public ResultatRecherche<ElementPreBl>? Resultat { get; set; }
    public int? Selection { get; set; }
}
