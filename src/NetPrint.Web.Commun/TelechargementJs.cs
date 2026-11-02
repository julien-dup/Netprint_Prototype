using Microsoft.JSInterop;

namespace NetPrint.Web.Commun;

// Pont avec netprintTelecharger (wwwroot/saisie.js) : fait télécharger au navigateur un fichier produit par le serveur,
// quand l'écran veut d'abord traiter les erreurs (un simple lien vers l'API les montrerait comme un téléchargement raté)
public static class TelechargementJs
{
    public static async Task TelechargerAsync(this IJSRuntime js, string nom, string type, byte[] contenu)
    {
        using var flux = new DotNetStreamReference(new MemoryStream(contenu));
        await js.InvokeVoidAsync("netprintTelecharger", nom, type, flux);
    }
}
