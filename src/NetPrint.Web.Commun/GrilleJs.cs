using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace NetPrint.Web.Commun;

// Pont avec wwwroot/grille.js : largeur des colonnes réglée à la souris, position d'un élément à l'écran.
// Un écran en crée un (new GrilleJs(JS)), l'active sur sa table après le premier affichage et le libère à la fin.
public sealed class GrilleJs(IJSRuntime js) : IAsyncDisposable
{
    private const string Module = "./_content/NetPrint.Web.Commun/grille.js";

    private IJSObjectReference? module;
    private DotNetObjectReference<GrilleJs>? reference;

    // Appelé à la fin d'un redimensionnement : clé de la colonne, largeur en pixels (0 = largeur d'origine)
    public Func<string, int, Task>? LargeurChangee { get; set; }

    public sealed record Rectangle(double Gauche, double Haut, double Bas, double Droite, double LargeurFenetre);

    public async Task ActiverAsync(ElementReference table)
    {
        reference ??= DotNetObjectReference.Create(this);
        await (await ModuleAsync()).InvokeVoidAsync("activer", table, reference);
    }

    public async Task<Rectangle> RectangleAsync(ElementReference element) =>
        await (await ModuleAsync()).InvokeAsync<Rectangle>("rectangle", element);

    [JSInvokable]
    public Task FinRedimension(string cle, int largeur) => LargeurChangee?.Invoke(cle, largeur) ?? Task.CompletedTask;

    private async Task<IJSObjectReference> ModuleAsync() =>
        module ??= await js.InvokeAsync<IJSObjectReference>("import", Module);

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            try { await module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        reference?.Dispose();
    }
}
