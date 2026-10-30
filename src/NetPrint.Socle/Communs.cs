namespace NetPrint.Socle;

// Résultat d'une recherche : pas de pagination (charte d'interface), mais un garde-fou côté serveur.
public sealed record ResultatRecherche<T>(IReadOnlyList<T> Lignes, bool Tronque);

public static class LimitesRecherche
{
    // Au-delà, la liste est coupée et l'écran demande d'affiner les critères
    public const int Maximum = 10_000;
}

// Règle métier non respectée : le message est affiché tel quel à l'utilisateur
public sealed class RegleMetierException(string message) : Exception(message);
