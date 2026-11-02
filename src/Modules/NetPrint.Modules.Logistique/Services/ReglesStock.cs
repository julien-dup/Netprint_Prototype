namespace NetPrint.Modules.Logistique.Services;

// Période de stock ouverte (AG00F) : le mois ouvert et le suivant. Date de mouvement hors période : rend le début de la période.
internal static class ReglesStock
{
    public static DateOnly? HorsPeriode(DateOnly date, DateOnly? debutPeriode) =>
        debutPeriode is { } debut && (date < debut || date > debut.AddMonths(2).AddDays(-1)) ? debut : null;
}
