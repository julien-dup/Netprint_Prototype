using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Facturation.Ecrans;

// Critères, liste et sélection des factures gardés pendant la session (retour de la fiche)
public sealed class EtatListeFactures
{
    public CriteresFactures Criteres { get; set; } = new();
    public ResultatRecherche<FactureListe>? Resultat { get; set; }
    public string? Selection { get; set; }
}
