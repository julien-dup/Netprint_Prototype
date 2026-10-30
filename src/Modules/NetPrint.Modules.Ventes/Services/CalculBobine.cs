namespace NetPrint.Modules.Ventes.Services;

// R14 : papier en bobine — coefficient de prix et nombre de bobines (détail du devis et décomposition analytique).
// Longueur utile d'une bobine = conditionnement − perte de fin de bobine (AA0002.TPPERB) ; tours = tours + gâche fixe.
internal static class CalculBobine
{
    public static (decimal Coefficient, int NbBobines) Coefficient(
        string feuilleBobine, decimal conditionnement, decimal perteBobine, decimal hauteurTirage, decimal tours)
    {
        if (feuilleBobine != "B" || conditionnement <= perteBobine || hauteurTirage <= 0) return (1m, 0);
        var toursParBobine = Math.Floor((conditionnement - perteBobine) / (hauteurTirage / 1000m));
        if (toursParBobine <= 0 || tours <= 0) return (1m, 0);
        var nbBobines = tours / toursParBobine;
        var reste = tours % toursParBobine;
        var longueur = tours * hauteurTirage / 1000m;
        var coef = (Math.Truncate(nbBobines) * conditionnement + reste * hauteurTirage / 1000m) / longueur;
        return (coef, (int)Math.Ceiling(nbBobines));
    }
}
