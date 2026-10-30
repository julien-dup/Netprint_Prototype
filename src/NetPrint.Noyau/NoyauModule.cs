using Microsoft.Extensions.DependencyInjection;
using NetPrint.Noyau.ListesDeChoix;
using NetPrint.Noyau.Messagerie;
using NetPrint.Noyau.Societes;
using NetPrint.Noyau.Tiers;

namespace NetPrint.Noyau;

public static class NoyauModule
{
    // Program.cs de NetPrint.Web : builder.Services.AddNoyau();
    public static IServiceCollection AddNoyau(this IServiceCollection services)
    {
        services.AddScoped<ISocietes, SocietesDb2>();
        services.AddScoped<IContactsTiers, ContactsTiersDb2>();   // destinataires des documents envoyés (PRT051F)
        services.AddScoped<IMessagerie, MessagerieSmtp>();        // envoi SMTP (FAC010), garde-fou Messagerie:Mode
        services.AddScoped<IListesDeChoix, ListesDeChoixDb2>();   // recherches F4 / bouton « … »
        return services;
    }
}
