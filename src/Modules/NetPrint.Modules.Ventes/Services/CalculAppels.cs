namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Règles de calcul des appels de livraison, communes aux trois points d'entrée (NEG520 DE, navigateur, EDI).
// Le RPG en avait trois versions divergentes (NEG520 DE, SpOrigin, NEG300NAV) ; la règle retenue est celle de
// NEG300NAV, recalculée depuis la quantité du contrat et la somme des appels relue en base (jamais par différence).
// =====================================================================
internal static class CalculAppels
{
    // Ligne d'origine : quantité du contrat (DNQTEO), somme des quantités de ses appels (DNQTEA), livré sur la ligne
    // elle-même (DNQTEL, 0 en pratique) → reste à appeler (DNQTEC), reste à livrer (DNQTER), soldée (DNSOLD)
    internal static (int Reste, int ResteALivrer, bool Soldee) Origine(int quantiteContrat, int quantiteAppelee, int livree)
    {
        var reste = Math.Max(0, quantiteContrat - quantiteAppelee);
        var soldee = reste - livree <= 0;
        return (soldee ? 0 : reste, Math.Max(0, reste - livree), soldee);
    }

    // Origine soldée à la main (option SO) alors qu'il restait à appeler : le solde est conservé quand ses appels changent.
    // Un solde venu des appels (reste à appeler 0) est recalculé (NEG300NAV).
    internal static bool SoldeeParLUtilisateur(bool soldee, int resteEnregistre) => soldee && resteEnregistre > 0;

    // Quantité appelée au-delà du reste de l'origine (NEG0029) : à la création, la quantité ; en modification, l'écart
    internal static bool Depasse(int quantite, int ancienneQuantite, int resteOrigine) => quantite - ancienneQuantite > resteOrigine;

    // DOS009.PMCALC '2' : maxi de l'appel au prorata du maxi de l'origine (quantité × maxi / quantité du contrat)
    internal static int? MaxiProrata(int quantite, int maxiOrigine, int quantiteContrat) =>
        maxiOrigine > 0 && quantiteContrat > 0 && quantite > 0
            ? (int)Math.Round((decimal)quantite * maxiOrigine / quantiteContrat, MidpointRounding.AwayFromZero)
            : null;

    // DOS009.PMCALC '1' (F22) : ce qui reste du maxi de l'origine une fois déduit le livré de ses appels
    internal static int MaxiSolde(int maxiOrigine, int livreAppels) => Math.Max(0, maxiOrigine - livreAppels);
}
