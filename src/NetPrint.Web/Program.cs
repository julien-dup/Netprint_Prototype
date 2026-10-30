using System.Globalization;
using NetPrint.Modules.Ventes;
using NetPrint.Noyau;
using NetPrint.Socle.Modules;
using NetPrint.Socle.Session;
using NetPrint.Web.Composants;
using NetPrint.Web.Commun;
using NetPrint.Web.Compte;

// Dates, nombres et montants au format français partout (écrans, PDF, API)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("fr-FR");

var builder = WebApplication.CreateBuilder(args);

// ---------- Interface : Blazor (Interactive Server), style de la charte NetPrint ----------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddInterfaceNetPrint();   // dialogues, notifications

// ---------- Socle (session, utilisateur courant) et Noyau ----------
builder.AjouterSocle();
builder.Services.AddNoyau();

// ---------- Modules : une ligne par module ----------
builder.Services.AddModuleVentes();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/erreur", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// Connexion / déconnexion (formulaires postés)
app.MapEndpointsCompte();

// API des modules : une ligne par module
app.MapModuleVentes();

// Écrans : ceux de NetPrint.Web + ceux de chaque module (trouvés via leur DescriptionModule)
var assemblagesModules = app.Services.GetServices<DescriptionModule>()
    .Select(m => m.Assemblage).Distinct().ToArray();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(assemblagesModules);

app.Run();
