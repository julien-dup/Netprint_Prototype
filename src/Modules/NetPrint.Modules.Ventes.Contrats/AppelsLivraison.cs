namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Appels de livraison : lignes normales (statut 1) rattachées à une ligne « à la demande » (statut 3, le contrat),
// dont elles consomment la quantité. Un seul moteur pour la saisie de commande (option DE de NEG520), le navigateur
// logistique et le tableau de bord EDI (NEG300NAV), avec les mêmes contrôles et la même écriture que la saisie
// des lignes. Un appel se lit, se solde et se réactive par ISaisieLignesCommande ; EnregistrerAsync et SupprimerAsync
// de ISaisieLignesCommande passent par ce moteur quand la ligne est un appel (canal Saisie).
// Effets sur la ligne d'origine, dans la même transaction que l'appel :
//   quantité appelée = somme des quantités des appels (soldés compris) ; reste à appeler = quantité du contrat
//   − quantité appelée (0 au plus bas) ; ligne soldée quand il ne reste rien à appeler ni à livrer, remise en cours sinon.
// =====================================================================
public interface IAppelsLivraison
{
    // Nouvel appel sur une ligne « à la demande » : prix, unité, commission, adresse, commentaires, textes et n° d'appel
    // repris de la ligne d'origine ; quantité et date à saisir. N'écrit rien.
    Task<LigneSaisie> NouvelAsync(CleCommande commande, int ligneOrigine, CancellationToken ct = default);

    // Création (NumeroLigne = 0) ou modification d'un appel. Le prix, l'unité et l'article ne viennent jamais de l'appelant :
    // à la création ils sont ceux de l'origine, en modification ceux de l'appel (report de prix : ReporterPrixAsync).
    // SaisieInvalideException (Forcable = true) pour des avertissements non acceptés, dont le dépassement du reste à appeler.
    Task<int> EnregistrerAsync(CleCommande commande, LigneSaisie appel, CanalAppel canal, CancellationToken ct = default);

    Task SupprimerAsync(CleCommande commande, int numeroAppel, CanalAppel canal, CancellationToken ct = default);

    // NEG520B : prix de la ligne d'origine (prix, remise, unité, cession, commission, gratuit, prix à définir) reporté sur
    // ses appels ni soldés ni facturés. Renvoie les n° des appels modifiés.
    Task<IReadOnlyList<int>> ReporterPrixAsync(CleCommande commande, int ligneOrigine, CancellationToken ct = default);

    // « Solde du maxi » (F22 de NEG520, bouton du navigateur) : maxi livrable de l'origine − quantités livrées de ses appels
    Task<int> CalculerMaxiAsync(CleCommande commande, int ligneOrigine, CancellationToken ct = default);
}

// Origine de l'appel : un appel créé par l'EDI (EDI105) ne se modifie et ne se supprime que par l'EDI
public enum CanalAppel { Saisie, Navigateur, Edi, ImportExcel }
