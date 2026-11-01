using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Modules;

namespace NetPrint.Modules.Facturation;

// Branchement du module dans NetPrint.Web : builder.Services.AddModuleFacturation(); app.MapModuleFacturation();
public static class FacturationModule
{
    public static IServiceCollection AddModuleFacturation(this IServiceCollection services)
    {
        services.AddSingleton(new DescriptionModule(
            Code: "FACTURATION",
            Nom: "Factures",
            Url: "/facturation/factures",
            Icone: "🧾",
            Ordre: 8,
            Assemblage: typeof(FacturationModule).Assembly,
            Description: "Consultation des factures et avoirs : critères combinables, éléments et lignes. À venir : édition, saisie, facturation automatique, envoi."));

        services.AddScoped<ConsultationFacturesDb2>();
        services.AddScoped<IConsultationFactures, ConsultationFacturesService>();   // ConsultationFactures, FAC080S en lecture
        services.AddScoped<Ecrans.EtatListeFactures>();
        services.AddScoped<CalculFacturesDb2>();                                    // FAC199 / FAC201 (calcul)
        return services;
    }

    public static IEndpointRouteBuilder MapModuleFacturation(this IEndpointRouteBuilder app) => app;
}
