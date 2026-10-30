using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetPrint.Socle.Session;

namespace NetPrint.Web.Commun;

// Affichage d'une grille mémorisé par utilisateur et par société (repris de GestionOutils, preferences.ts) :
// colonnes masquées, largeurs réglées à la souris, tri.
// Charte : rien n'est stocké dans le navigateur. Le prototype range ces réglages côté serveur dans
// App_Data/preferences/<profil>-<société>.json ; une table PostgreSQL prendra le relais (seule
// l'implémentation de IPreferencesGrilles changera).
public sealed class PreferencesGrille
{
    public List<string> Masquees { get; set; } = [];
    public Dictionary<string, int> Largeurs { get; set; } = [];
    public string? TriCle { get; set; }
    public bool TriDescendant { get; set; }
}

public interface IPreferencesGrilles
{
    // Jamais d'exception : en cas de problème, l'affichage d'origine
    Task<PreferencesGrille> LireAsync(string grille);
    Task EcrireAsync(string grille, PreferencesGrille preferences);
}

internal sealed partial class PreferencesGrillesFichier(
    IUtilisateurCourant utilisateurCourant, IHostEnvironment environnement, ILogger<PreferencesGrillesFichier> journal)
    : IPreferencesGrilles
{
    private static readonly SemaphoreSlim verrou = new(1, 1);
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public async Task<PreferencesGrille> LireAsync(string grille)
    {
        try
        {
            var toutes = await LireFichierAsync(await CheminAsync());
            return toutes.TryGetValue(grille, out var p) ? p : new();
        }
        catch (Exception ex)
        {
            journal.LogWarning(ex, "Lecture des préférences de la grille {Grille} impossible", grille);
            return new();
        }
    }

    public async Task EcrireAsync(string grille, PreferencesGrille preferences)
    {
        try
        {
            var chemin = await CheminAsync();
            await verrou.WaitAsync();
            try
            {
                var toutes = await LireFichierAsync(chemin);
                toutes[grille] = preferences;
                Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
                await File.WriteAllTextAsync(chemin, JsonSerializer.Serialize(toutes, json));
            }
            finally
            {
                verrou.Release();
            }
        }
        catch (Exception ex)
        {
            journal.LogWarning(ex, "Enregistrement des préférences de la grille {Grille} impossible", grille);
        }
    }

    private async Task<string> CheminAsync()
    {
        var u = await utilisateurCourant.ExigerAsync();
        var nom = $"{Nettoyer(u.Profil)}-{Nettoyer(u.Societe.ToString())}.json";
        return Path.Combine(environnement.ContentRootPath, "App_Data", "preferences", nom);
    }

    private static async Task<Dictionary<string, PreferencesGrille>> LireFichierAsync(string chemin)
    {
        if (!File.Exists(chemin)) return [];
        await using var flux = File.OpenRead(chemin);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, PreferencesGrille>>(flux) ?? [];
    }

    // Le nom de fichier ne garde que lettres, chiffres, tiret et souligné
    private static string Nettoyer(string s) => CaracteresInterdits().Replace(s.Trim().ToUpperInvariant(), "_");

    [GeneratedRegex("[^A-Z0-9_-]")]
    private static partial Regex CaracteresInterdits();
}
