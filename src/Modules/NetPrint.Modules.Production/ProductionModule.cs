using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Production.Donnees;
using NetPrint.Modules.Production.Services;

namespace NetPrint.Modules.Production;

// Branchement du module dans NetPrint.Web : builder.Services.AddModuleProduction();
// Pas encore d'écran (donc pas de DescriptionModule ni d'entrée de menu) : seulement l'initialisation de la production
// demandée par la vente (IInitialisationProduction).
public static class ProductionModule
{
    public static IServiceCollection AddModuleProduction(this IServiceCollection services)
    {
        services.AddScoped<DemandesOfDb2>();
        services.AddScoped<ReferencesProductionDb2>();
        services.AddScoped<IInitialisationProduction, InitialisationProductionService>();   // demandes d'OF (NEG515, NEG510 F10)
        return services;
    }
}
