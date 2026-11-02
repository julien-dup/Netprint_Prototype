using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
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

        services.AddSingleton(new DescriptionModule(
            Code: "FACTURATION-AUTO",
            Nom: "Facturation automatique",
            Url: "/facturation/automatique",
            Icone: "⚙",
            Ordre: 9,
            Assemblage: typeof(FacturationModule).Assembly,
            Description: "Factures et avoirs des BL facturables selon le mode de facturation des clients (FAC400) : simulation, puis génération."));

        services.AddScoped<FacturationAutoDb2>();
        services.AddScoped<ICompteursFactures, CompteursFacturesService>();       // FAC040 (compteur manquant, créé sur place)
        services.AddScoped<SaisieFacturesDb2>();
        services.AddScoped<ISaisieFactures, SaisieFacturesService>();             // FAC085 → FAC150 (factures de BL choisis)
        services.AddScoped<IFacturationAutomatique, FacturationAutomatiqueService>();   // FAC400 → FAC402P, provisoire FAC412
        services.AddScoped<ConsultationFacturesDb2>();
        services.AddScoped<IConsultationFactures, ConsultationFacturesService>();   // ConsultationFactures, FAC080S en lecture
        services.AddScoped<Ecrans.EtatListeFactures>();
        services.AddScoped<CalculFacturesDb2>();                                    // FAC199 / FAC201 (calcul)
        services.AddScoped<EditionFacturesDb2>();
        services.AddScoped<EditionDefinitiveDb2>();
        services.AddScoped<DroitsFacturationDb2>();                                 // PRT000P1
        services.AddScoped<IEditionFactures, EditionFacturesService>();             // FAC202P5 (PDF)
        services.AddScoped<EnvoiFacturesDb2>();
        services.AddScoped<IEnvoiFactures, EnvoiFacturesService>();                 // EnvoideFactures (mail, PDF)
        return services;
    }

    public static IEndpointRouteBuilder MapModuleFacturation(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/facturation").RequireAuthorization();

        // Facture en PDF, sans écriture : GET /api/facturation/factures/50001/20260501/pdf
        api.MapGet("/factures/{numero:int}/{jour}/pdf", async (int numero, string jour, HttpContext http, IEditionFactures edition, CancellationToken ct) =>
        {
            if (!DateOnly.TryParseExact(jour, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return Results.Text("Date de facture invalide.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                return DocumentsPdf.Reponse(http, await edition.EditerAsync(numero, date, ct));
            }
            catch (RegleMetierException ex)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            }
            catch (System.Data.Odbc.OdbcException ex)
            {
                return Results.Text($"Édition de la facture impossible : {ex.Message}", "text/plain; charset=utf-8", statusCode: StatusCodes.Status502BadGateway);
            }
        });
        return app;
    }
}
