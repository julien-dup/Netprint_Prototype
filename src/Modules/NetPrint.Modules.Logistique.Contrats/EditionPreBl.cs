using NetPrint.Socle.Documents;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Éditions du pré-BL (WinDev PreBL : « Imprimer » → EXP530NAV → EXP535P2) en PDF natif, sans rien écrire.
// =====================================================================
public interface IEditionPreBl
{
    // Le pré-BL, présenté comme le BL qu'il deviendra (même mise en page que le BL)
    Task<DocumentGenere> EditerAsync(int numero, bool apercu, CancellationToken ct = default);

    // Feuille de préparation (ETAT_FeuilleKimmel) : palettes à prendre par dépôt (depot : un seul dépôt, sinon tous) ;
    // codesBarres : code-barres de chaque palette (réglage de poste ImprimerCodeBarreUiteFeuillePrepa du WinDev)
    Task<DocumentGenere> FeuillePreparationAsync(int numero, int? depot, bool codesBarres, CancellationToken ct = default);

    // Feuille camion (ETAT_FeuilleCamion) de pré-BL d'un même client, départ, transporteur et adresse ; valorisee : montants des
    // frais (réglage de poste FeuilleCamionValorisee du WinDev)
    Task<DocumentGenere> FeuilleCamionAsync(IReadOnlyList<int> numeros, bool valorisee, CancellationToken ct = default);
}
