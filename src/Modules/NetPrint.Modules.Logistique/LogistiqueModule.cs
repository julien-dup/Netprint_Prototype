using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Documents;
using NetPrint.Socle.Modules;

namespace NetPrint.Modules.Logistique;

// Branchement du module dans NetPrint.Web : builder.Services.AddModuleLogistique(); app.MapModuleLogistique();
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

        services.AddSingleton(new DescriptionModule(
            Code: "LOGISTIQUE-LIVRAISONS",
            Nom: "Livraisons à effectuer",
            Url: "/logistique/livraisons",
            Icone: "📦",
            Ordre: 5,
            Assemblage: typeof(LogistiqueModule).Assembly,
            Description: "Lignes de commande à livrer sur une période : stock, en-cours, OPR, pré-BL ; création du BL et fiche de préparation."));

        services.AddScoped<ConsultationBlDb2>();
        services.AddScoped<IConsultationBl, ConsultationBlService>();   // ConsultationBL / EXP010S
        services.AddScoped<Ecrans.Bl.EtatListeBl>();                    // critères et liste conservés pendant la session
        services.AddScoped<EditionBlDb2>();
        services.AddScoped<IEditionBl, EditionBlService>();             // EXP030 / EXP035P2
        services.AddScoped<IEnvoiBl, EnvoiBlService>();                 // EXP010S option EE (envoi par e-mail)
        services.AddScoped<SaisieBlDb2>();
        services.AddScoped<ISaisieBl, SaisieBlService>();               // EXP010, cahier des charges lot 1
        services.AddScoped<IBlDivers, BlDiversService>();               // BL divers (EXP010 état D)
        services.AddScoped<IRetourBl, RetourBlService>();               // retours de marchandises (EXP010S RM, ConsultationBL)
        services.AddScoped<PreparationDb2>();
        services.AddScoped<IPreparation, PreparationService>();           // EXP078
        services.AddScoped<LivraisonsDb2>();
        services.AddScoped<ILivraisons, LivraisonsService>();              // LivraisonsAEffectuer
        services.AddScoped<Ecrans.Livraisons.EtatLivraisons>();
        return services;
    }

    public static IEndpointRouteBuilder MapModuleLogistique(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/logistique").RequireAuthorization();

        // BL en PDF, sans écriture : GET /api/logistique/bl/165139/pdf?apercu=1 (aperçu : filigrane)
        api.MapGet("/bl/{numero:int}/pdf", async (int numero, int? apercu, HttpContext http, IEditionBl edition, CancellationToken ct) =>
        {
            try
            {
                return DocumentsPdf.Reponse(http, await edition.EditerAsync(numero, apercu == 1, ct));
            }
            catch (RegleMetierException ex)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            }
            catch (System.Data.Odbc.OdbcException ex)
            {
                return Results.Text($"Édition du BL impossible : {ex.Message}", "text/plain; charset=utf-8",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // PDF d'un BL tel qu'il a été envoyé (journal ENV100) : GET /api/logistique/bl/165139/envois/12/pdf
        api.MapGet("/bl/{numero:int}/envois/{id:long}/pdf", async (int numero, long id, HttpContext http, IEnvoiBl envoi, CancellationToken ct) =>
        {
            try
            {
                return await envoi.PdfEnvoyeAsync(numero, id, ct) is { } pdf ? DocumentsPdf.Reponse(http, pdf) : Results.NotFound();
            }
            catch (RegleMetierException ex)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            }
        });

        // Fiche de préparation (EXP078) : GET /api/logistique/preparation?du=2023-11-01&au=2023-11-30&gen=411100&cli=376
        api.MapGet("/preparation", async (HttpContext http, IPreparation preparation, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            int? Entier(string nom) => int.TryParse(q[nom], out var n) && n > 0 ? n : null;
            string? Texte(string nom) => string.IsNullOrWhiteSpace(q[nom]) ? null : q[nom].ToString();
            if (!DateOnly.TryParse(q["du"], out var du) || !DateOnly.TryParse(q["au"], out var au))
                return Results.Text("Indiquez la période (du, au).", "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                return DocumentsPdf.Reponse(http, await preparation.EditerAsync(new CriteresPreparation(du, au, Entier("gen"), Entier("cli"),
                    Entier("cde"), q["nonstockes"] == "1", Texte("fab"), Texte("ens"), Texte("art"), Entier("adr"), Texte("rfi"), Texte("appel"),
                    Entier("numopr"), Enum.TryParse<FiltreOpr>(q["opr"], out var opr) ? opr : FiltreOpr.Tous, Entier("rep")), ct));
            }
            catch (RegleMetierException ex)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            }
        });
        return app;
    }
}
