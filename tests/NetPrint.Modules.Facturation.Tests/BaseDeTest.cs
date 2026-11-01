using Microsoft.Extensions.Configuration;

namespace NetPrint.Modules.Facturation.Tests;

// Accès à la base DB2 de test (NP11F de l'AS400 de test) pour les tests d'intégration.
// Chaîne : user secrets de NetPrint.Web (ConnectionStrings:Db2) ou variable ConnectionStrings__Db2.
// Sans chaîne (vérification automatique, poste non configuré), les tests [FactDb2] sont ignorés.
// Données d'essai convenues : société 8, client 411100 376 (Clarins). Jamais d'écriture hors de ces données.
public static class BaseDeTest
{
    public const int Societe = 8;
    public const int CompteGeneral = 411100;
    public const int CompteClient = 376;

    public static IConfiguration Configuration { get; } = new ConfigurationBuilder()
        .AddUserSecrets("7f179701-3678-4b3f-b705-60054599b14e")   // UserSecretsId de NetPrint.Web
        .AddEnvironmentVariables()
        .Build();

    public static bool Disponible => !string.IsNullOrWhiteSpace(Configuration.GetConnectionString("Db2"));
}

public sealed class FactDb2Attribute : FactAttribute
{
    public FactDb2Attribute()
    {
        if (!BaseDeTest.Disponible) Skip = "Pas de connexion DB2 de test (ConnectionStrings:Db2) sur ce poste.";
    }
}

// Tests qui écrivent dans NP11F (données d'essai seulement) : lancés à la demande
public sealed class FactDb2EcritureAttribute : FactAttribute
{
    public FactDb2EcritureAttribute()
    {
        if (!BaseDeTest.Disponible) Skip = "Pas de connexion DB2 de test (ConnectionStrings:Db2) sur ce poste.";
        else if (Environment.GetEnvironmentVariable("NETPRINT_TESTS_ECRITURE") != "1")
            Skip = "Test d'écriture dans NP11F : lancer avec NETPRINT_TESTS_ECRITURE=1.";
    }
}

// Culture de l'application (Program.cs de NetPrint.Web), quel que soit le poste qui lance les tests
internal static class CultureDesTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialiser() =>
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture =
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CurrentUICulture =
                new System.Globalization.CultureInfo("fr-FR");
}
