using NetPrint.Socle.Documents;
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
        // Lignes (NEG520 / CTL520 / MAJ520), actions sur la commande (DOS500S), appels de livraison (option DE, NEG300NAV)
        services.AddScoped<SaisieLignesService>();
        services.AddScoped<ISaisieLignesCommande>(s => s.GetRequiredService<SaisieLignesService>());
        services.AddScoped<IActionsCommande>(s => s.GetRequiredService<SaisieLignesService>());
        services.AddScoped<IAppelsLivraison>(s => s.GetRequiredService<SaisieLignesService>());
        services.AddScoped<ArcCommandeDb2>();
        services.AddScoped<IEditionArc, EditionArcService>();   // ARC des commandes (ARC105P), PDF produit par l'application
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

        // ARC d'une commande en PDF : GET /api/ventes/commandes/24/1/arc?soldees=O&apercu=1
        // soldees : O / N (absent = paramètre société) ; apercu = 1 : filigrane, sans le droit « Édition »
        api.MapGet("/commandes/{numero:int}/{cle}/arc",
            async (int numero, string cle, string? soldees, int? apercu, HttpContext http, IEditionArc arc, CancellationToken ct) =>
            {
                try
                {
                    var doc = await arc.EditerAsync(new CleCommande(numero, cle.Trim().ToUpperInvariant()),
                        soldees switch { "O" or "o" => true, "N" or "n" => false, _ => null }, apercu == 1, ct);
                    return DocumentsPdf.Reponse(http, doc);
                }
                catch (RegleMetierException ex)
                {
                    // Commande inexistante ou hors du périmètre du représentant : 404 ; droit « Édition » manquant : 403
                    var statut = ex.Message.Contains("autorisé") ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound;
                    return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: statut);
                }
                catch (System.Data.Odbc.OdbcException ex)
                {
                    return Results.Text($"Édition de l'ARC impossible : {ex.Message}", "text/plain; charset=utf-8",
                        statusCode: StatusCodes.Status502BadGateway);
                }
            });

        // Brouillon de message Outlook (.eml) avec les ARC en pièces jointes, sans serveur SMTP :
        // GET /api/ventes/commandes/arc/message?c=24-1&c=25-1&a=client@exemple.fr&cc=autre@exemple.fr&soldees=O
        api.MapGet("/commandes/arc/message",
            async (string[]? c, string[]? a, string[]? cc, string? soldees, HttpContext http, IEditionArc arc, CancellationToken ct) =>
            {
                var cles = new List<CleCommande>();
                foreach (var v in c ?? [])
                    if (v.Split('-') is [var n, var k] && int.TryParse(n, out var numero)) cles.Add(new CleCommande(numero, k.Trim().ToUpperInvariant()));
                    else return Results.BadRequest($"Commande invalide : {v}");
                try
                {
                    var eml = await arc.PreparerMessageAsync(cles, a ?? [], cc ?? [],
                        soldees switch { "O" or "o" => true, "N" or "n" => false, _ => null }, ct);
                    http.Response.Headers.ContentDisposition =
                        new ContentDispositionHeaderValue("attachment") { FileNameStar = eml.Nom }.ToString();
                    return Results.File(eml.Contenu, MessageBrouillon.TypeEml);
                }
                catch (RegleMetierException ex)
                {
                    return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
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
