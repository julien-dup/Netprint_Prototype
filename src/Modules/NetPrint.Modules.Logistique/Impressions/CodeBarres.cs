using System.Text;
using ZXing;
using ZXing.OneD;

namespace NetPrint.Modules.Logistique.Impressions;

// Codes-barres des éditions, dessinés en SVG (vectoriel, net à l'impression). Code 39 pour les n° de lot du BL (David,
// 31/10/2026 ; le DDS d'EXP035P2 déclare du CODE128). Encodage par ZXing.Net. À déplacer dans le Socle quand une autre
// édition en aura besoin (étiquettes, palettes).
internal static class CodeBarres
{
    public static string Code39Svg(string valeur) =>
        Svg(new Code39Writer().encode(valeur.ToUpperInvariant(), BarcodeFormat.CODE_39, 0, 1));

    private static string Svg(ZXing.Common.BitMatrix matrice)
    {
        var largeur = matrice.Width;
        var svg = new StringBuilder($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {largeur} 40" preserveAspectRatio="none">""");
        for (var x = 0; x < largeur;)
        {
            if (!matrice[x, 0]) { x++; continue; }
            var debut = x;
            while (x < largeur && matrice[x, 0]) x++;
            svg.Append($"""<rect x="{debut}" y="0" width="{x - debut}" height="40" fill="#000"/>""");
        }
        return svg.Append("</svg>").ToString();
    }
}
