namespace NetPrint.Modules.Logistique.Services;

// Règles des livraisons à effectuer et de la fiche de préparation : conditionnement et montant restant à facturer
internal static class ReglesLivraisons
{
    // Conditionnement (WinDev) : UC = ⌈reste / emballage⌉ ; cartons = UC si l'emballage est le carton, sinon ⌈UC / UC par carton⌉
    // (au moins 1 dès qu'il y a des UC — le WinDev donnait 0 quand UC ≤ UC par carton) ; palettes = ⌈UC / UC par palette⌉, au moins 1 ;
    // poids = poids d'une UC × UC
    public static (int Uc, int Cartons, int Palettes, decimal Poids) Conditionnement(int reste, int emballage, int ucParCarton,
        int ucParPalette, decimal poidsUc, string typeEmballage)
    {
        if (reste <= 0 || emballage <= 0) return (0, 0, 0, 0);
        var uc = (reste + emballage - 1) / emballage;
        var cartons = typeEmballage == "C" ? uc : ucParCarton > 0 ? (uc + ucParCarton - 1) / ucParCarton : 0;
        var palettes = ucParPalette > 0 ? Math.Max(1, (uc + ucParPalette - 1) / ucParPalette) : 0;
        return (uc, cartons, palettes, poidsUc * uc);
    }

    // Montant restant à facturer : (commandé − facturé) × prix ÷ unité de vente ; « Le tout » (TNUM 999999999) : forfait au prorata
    public static decimal MontantAFacturer(int commande, int facture, decimal prix, decimal unite)
    {
        var reste = commande - facture;
        if (reste <= 0 || commande <= 0) return 0;
        return Math.Round(unite == 999_999_999m ? prix * reste / commande : prix * reste / (unite == 0 ? 1 : unite), 2, MidpointRounding.AwayFromZero);
    }
}
