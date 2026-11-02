namespace NetPrint.Modules.Facturation.Services;

// Ligne pour le calcul : type FAC020.LTYPE (V vente, R remise, P port taxable, E port exonéré, T texte), montant FAC100.FGMTN
// (positif, même pour une remise ou un avoir), taux de TVA du code TVA de son type de ligne (FAC025, taux daté)
internal sealed record LigneCalculFacture(string Type, decimal Montant, decimal TauxTva);

// Conditions de règlement de l'en-tête (FAC080) : taux d'escompte FDESC, acompte FDCPT, échéance (FDNLE « le », FDNECH nombre de
// jours, FDART arrêté au), échéance déjà fixée (FDECH*) ou non
internal sealed record ConditionsFacture(bool Avoir, DateOnly Date, decimal TauxEscompte, decimal Acompte, int Le, int NombreJours, int Arrete,
    DateOnly? Echeance);

// Ventilation de la TVA par taux (base = marchandises nettes d'escompte + port taxable de ce taux)
internal sealed record TvaParTaux(decimal Taux, decimal Base, decimal Montant);

// Totaux d'une facture, positifs (le signe d'un avoir est un affaire d'affichage, comme FAC080)
internal sealed record TotauxFacture(decimal Ht, decimal PortTaxable, decimal PortExonere, decimal Escompte, IReadOnlyList<TvaParTaux> Tva,
    decimal TotalTva, decimal Ttc, decimal Acompte, decimal NetAPayer, DateOnly Echeance);

// =====================================================================
// Calcul d'une facture (reprise de FAC201 / FAC199, docs : D:\docs\Netprint\retrodoc-saas\fiches\FAC201.md R3 à R9 et
// AXISRV.md GETECHEAN R18 à R22), sans base. Écart voulu : TVA ventilée par taux (le RPG prenait le taux de la dernière ligne
// de vente pour toute la facture). Pour une facture à un seul taux, résultat identique au RPG : TVA marchandises et TVA du port
// arrondies séparément, au centime le plus proche.
// =====================================================================
internal static class CalculFacture
{
    public static TotauxFacture Calculer(IReadOnlyList<LigneCalculFacture> lignes, ConditionsFacture c)
    {
        var ventes = lignes.Where(l => l.Type is "V" or "R").GroupBy(l => l.TauxTva)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Type == "R" ? -l.Montant : l.Montant));
        var ports = lignes.Where(l => l.Type == "P").GroupBy(l => l.TauxTva).ToDictionary(g => g.Key, g => g.Sum(l => l.Montant));
        var ht = ventes.Values.Sum();
        var portTaxable = ports.Values.Sum();
        var portExonere = lignes.Where(l => l.Type == "E").Sum(l => l.Montant);
        // Escompte (R8) : HT × taux, à 3 décimales puis arrondi au centime ; réparti sur les taux au prorata de leur HT
        var escompte = c.TauxEscompte == 0 ? 0m : Arrondi(Math.Round(ht * c.TauxEscompte / 100m, 3, MidpointRounding.AwayFromZero));
        var tva = new List<TvaParTaux>();
        foreach (var taux in ventes.Keys.Union(ports.Keys).Order())
        {
            var htTaux = ventes.GetValueOrDefault(taux);
            var escompteTaux = ht == 0 ? 0m : Arrondi(escompte * htTaux / ht);
            var baseMarchandises = htTaux - escompteTaux;
            var port = ports.GetValueOrDefault(taux);
            var montant = Arrondi(baseMarchandises * taux / 100m) + Arrondi(port * taux / 100m);
            tva.Add(new TvaParTaux(taux, baseMarchandises + port, montant));
        }
        var totalTva = tva.Sum(t => t.Montant);
        var ttc = ht - escompte + portTaxable + portExonere + totalTva;
        return new TotauxFacture(ht, portTaxable, portExonere, escompte, tva, totalTva, ttc, c.Acompte, ttc - c.Acompte, c.Echeance ?? Echeance(c));
    }

    private static decimal Arrondi(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    // Échéance (R9, GETECHEAN) : un avoir est échu à sa date ; délai usuel (30 à 180 jours = 1 à 6 mois, 45 = 1 mois et 15 jours,
    // 10 et 20 jours ; autre valeur = aucun délai), report d'un mois si la facture est postérieure au jour d'arrêté, puis jour fixe
    // « le » (1 à 30 : le mois suivant à ce jour ; 31 : fin de mois)
    public static DateOnly Echeance(ConditionsFacture c)
    {
        if (c.Avoir) return c.Date;
        var d = c.NombreJours switch
        {
            30 or 60 or 90 or 120 or 150 or 180 => c.Date.AddMonths(c.NombreJours / 30),
            45 => c.Date.AddMonths(1).AddDays(15),
            10 or 20 => c.Date.AddDays(c.NombreJours),
            _ => c.Date,
        };
        if (c.Arrete > 0 && c.Date.Day > c.Arrete) d = d.AddMonths(1);
        if (c.Le is >= 1 and <= 30)
        {
            d = d.AddMonths(1);
            d = new DateOnly(d.Year, d.Month, Math.Min(c.Le, DateTime.DaysInMonth(d.Year, d.Month)));
        }
        else if (c.Le == 31) d = new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
        return d;
    }
}
