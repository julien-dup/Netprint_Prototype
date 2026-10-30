using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Ecrans.Commandes;
using NetPrint.Modules.Ventes.Ecrans.Devis;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Modules;

namespace NetPrint.Modules.Ventes;

// Branchement du module dans NetPrint.Web (deux lignes dans Program.cs) :
//     builder.Services.AddModuleVentes();
//     app.MapModuleVentes();
public static class VentesModule
{
    public static IServiceCollection AddModuleVentes(this IServiceCollection services)
    {
        services.AddSingleton(new DescriptionModule(
            Code: "VENTES",
            Nom: "Consultation devis",
            Url: "/ventes/devis",
            Icone: "📄",
            Ordre: 2,
            Assemblage: typeof(VentesModule).Assembly,
            Description: "Consultation des devis. À venir : saisie, offres, commandes clients, ARC, proformas."));

        services.AddScoped<IConsultationDevis, ConsultationDevisService>();
        services.AddScoped<ConsultationDevisDb2>();
        services.AddScoped<IComplementsDevis, ComplementsDevisService>();   // texte libre, sous-totaux, coefficients…
        services.AddScoped<ComplementsDevisDb2>();
        services.AddScoped<IDecompositionDevis, DecompositionDevisService>();   // décomposition analytique, prix remis (lot E)
        services.AddScoped<DecompositionDevisDb2>();
        services.AddScoped<EtatConsultationDevis>();   // critères et liste conservés pendant la session (retour du détail)

        // Commandes clients (négoce et produit fini) : entrée de menu et tuile d'accueil propres
        services.AddSingleton(new DescriptionModule(
            Code: "VENTES-COMMANDES",
            Nom: "Commandes clients",
            Url: "/ventes/commandes",
            Icone: "🧾",
            Ordre: 2,
            Assemblage: typeof(VentesModule).Assembly,
            Description: "Commandes de négoce et de produit fini : recherche, lignes, livraisons. À venir : saisie, ARC."));
        services.AddScoped<IConsultationCommandes, ConsultationCommandesService>();
        services.AddScoped<ISourceCommandes, ConsultationCommandesDb2>();
        services.AddScoped<EtatListeCommandes>();
        services.AddScoped<ISaisieCommandes, SaisieCommandesService>();   // en-tête de commande (DOS505)
        services.AddScoped<ISourceSaisieCommandes, SaisieCommandesDb2>();
        services.AddScoped<SaisieLignesService>();   // lignes (NEG520 / CTL520 / MAJ520) et actions sur la commande (DOS500S)
        services.AddScoped<ISaisieLignesCommande>(s => s.GetRequiredService<SaisieLignesService>());
        services.AddScoped<IActionsCommande>(s => s.GetRequiredService<SaisieLignesService>());
        services.AddScoped<LignesCommandeDb2>();
        return services;
    }

    // Points d'API du module (assistants IA, EDI) : /api/ventes/...  Les écrans Blazor n'en ont pas besoin.
    public static IEndpointRouteBuilder MapModuleVentes(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/ventes").RequireAuthorization();

        // GET /api/ventes/devis/12647/0/2/C
        api.MapGet("/devis/{numero:int}/{version:int}/{hypothese:int}/{typeCout}",
            async (int numero, int version, int hypothese, string typeCout, IConsultationDevis devis, CancellationToken ct) =>
                await devis.LireDetailAsync(new CleDevis(numero, version, hypothese, typeCout.ToUpperInvariant()), ct)
                    is { } detail ? Results.Ok(detail) : Results.NotFound());

        // Récapitulatif PDF d'un devis, édité à la demande par l'IBM i (DEV100NAV) : GET /api/ventes/devis/12647/0/2/C/recapitulatif
        api.MapGet("/devis/{numero:int}/{version:int}/{hypothese:int}/{typeCout}/recapitulatif",
            async (int numero, int version, int hypothese, string typeCout, HttpContext http, IComplementsDevis complements,
                   CancellationToken ct) =>
            {
                try
                {
                    var cle = new CleDevis(numero, version, hypothese, typeCout.ToUpperInvariant());
                    if (await complements.EditerRecapitulatifAsync(cle, ct) is not { } pdf) return Results.NotFound();
                    http.Response.Headers.ContentDisposition =
                        new ContentDispositionHeaderValue("inline") { FileNameStar = pdf.Nom }.ToString();
                    return Results.File(pdf.Contenu, "application/pdf");
                }
                catch (RegleMetierException ex)
                {
                    return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status502BadGateway);
                }
                catch (System.Data.Odbc.OdbcException ex)
                {
                    return Results.Text($"Édition du récapitulatif impossible : {ex.Message}", "text/plain; charset=utf-8",
                        statusCode: StatusCodes.Status502BadGateway);
                }
            });

        // Ouverture d'un document d'un devis (fenêtre Documents) : GET /api/ventes/devis/documents/123
        // PDF, images et texte s'affichent dans le navigateur, les autres fichiers se téléchargent.
        api.MapGet("/devis/documents/{id:int}",
            async (int id, HttpContext http, IComplementsDevis complements, CancellationToken ct) =>
            {
                if (await complements.LireFichierAsync(id, ct) is not { } f) return Results.NotFound();
                if (!TypesFichiers.TryGetContentType(f.Nom, out var type)) type = "application/octet-stream";
                var enLigne = type == "application/pdf" || type.StartsWith("image/") || type.StartsWith("text/");
                http.Response.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue(enLigne ? "inline" : "attachment") { FileNameStar = f.Nom }.ToString();
                return Results.File(f.Contenu, type);
            });

        return app;
    }

    private static readonly FileExtensionContentTypeProvider TypesFichiers = new();
}
