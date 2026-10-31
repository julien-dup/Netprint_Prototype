using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Ecrans.Opr;

// Critères, liste et sélection des OPR gardés pendant la session (retour de la fiche ou de la création)
public sealed class EtatListeOpr
{
    public CriteresOpr Criteres { get; set; } = new();
    public ResultatRecherche<ElementOpr>? Resultat { get; set; }
    public (int Opr, int Dossier, string Cle)? Selection { get; set; }

    // Critères de la dernière création (lignes de commande à préparer)
    public CriteresCandidatesOpr CriteresCreation { get; set; } = new() { Au = DateOnly.FromDateTime(DateTime.Today).AddDays(7) };
}
