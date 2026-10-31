using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Modules;

namespace NetPrint.Modules.Logistique;

// Branchement du module dans NetPrint.Web : builder.Services.AddModuleLogistique();
public static class LogistiqueModule
{
    public static IServiceCollection AddModuleLogistique(this IServiceCollection services)
    {
        services.AddSingleton(new DescriptionModule(
            Code: "LOGISTIQUE-BL",
            Nom: "Bons de livraison",
            Url: "/logistique/bl",
            Icone: "🚚",
            Ordre: 4,
            Assemblage: typeof(LogistiqueModule).Assembly,
            Description: "Consultation des bons de livraison. À venir : saisie des BL, livraisons à effectuer, OPR, pré-BL."));

        services.AddScoped<ConsultationBlDb2>();
        services.AddScoped<IConsultationBl, ConsultationBlService>();   // ConsultationBL / EXP010S
        services.AddScoped<Ecrans.Bl.EtatListeBl>();                    // critères et liste conservés pendant la session
        return services;
    }
}
