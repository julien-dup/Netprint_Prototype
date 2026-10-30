using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Modules.Ventes.Ecrans.Devis;
using NetPrint.Modules.Ventes.Services;
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
        services.AddScoped<EtatConsultationDevis>();   // critères et liste conservés pendant la session (retour du détail)
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

        return app;
    }
}
