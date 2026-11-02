namespace NetPrint.Modules.Logistique.Ecrans;

// Libellés des statuts de palette SSCC (PROD_ENTITE.STATUT), communs aux écrans OPR et pré-BL. 4 = conditionnée (pointée au
// terminal) ; 5 = expédiée (ou vidée vers la préparation, web services PHP) ; 6 = détruite, soldée ou regroupée.
internal static class LibellesPalette
{
    public static string Statut(string s) => s switch
    {
        "" => "Introuvable", "0" => "Déclarée", "1" => "Fabriquée", "2" => "En stock", "3" => "En prépa", "4" => "Conditionnée",
        "5" => "Expédiée", "6" => "Détruite / soldée", "7" => "Retournée", _ => s,
    };
}
