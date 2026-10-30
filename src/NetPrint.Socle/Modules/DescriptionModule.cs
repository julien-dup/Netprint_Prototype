using System.Reflection;

namespace NetPrint.Socle.Modules;

// Chaque module se déclare avec une DescriptionModule (dans son AddModuleXxx) :
//   - NetPrint.Web s'en sert pour trouver les écrans du module (Assemblage) et construire le menu et l'accueil ;
//   - Ordre = ordre d'affichage (et ordre de réalisation du cadrage).
public sealed record DescriptionModule(
    string Code,
    string Nom,
    string Url,
    string Icone,
    int Ordre,
    Assembly Assemblage,
    string Description = "");
