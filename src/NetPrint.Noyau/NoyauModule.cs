using Microsoft.Extensions.DependencyInjection;
using NetPrint.Noyau.Societes;

namespace NetPrint.Noyau;

public static class NoyauModule
{
    // Program.cs de NetPrint.Web : builder.Services.AddNoyau();
    public static IServiceCollection AddNoyau(this IServiceCollection services)
    {
        services.AddScoped<ISocietes, SocietesDb2>();
        return services;
    }
}
