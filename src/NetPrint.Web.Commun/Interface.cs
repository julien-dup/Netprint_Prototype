using Microsoft.Extensions.DependencyInjection;

namespace NetPrint.Web.Commun;

// Charte : jamais de window.alert / window.confirm. Les écrans utilisent Dialogues (alerter / confirmer)
// et Notifications (message temporaire en bas à droite). Les zones d'affichage sont dans la mise en page.
public static class InterfaceNetPrint
{
    public static IServiceCollection AddInterfaceNetPrint(this IServiceCollection services)
    {
        services.AddScoped<Dialogues>();
        services.AddScoped<Notifications>();
        services.AddScoped<IPreferencesGrilles, PreferencesGrillesFichier>();
        return services;
    }
}

// Fenêtre modale « alerter » / « confirmer » (ex-Info / OuiNon du WinDev)
public sealed class Dialogues
{
    public sealed record Demande(string Titre, string Message, bool AvecAnnuler, string BoutonOk, TaskCompletionSource<bool> Reponse);

    public Demande? Courante { get; private set; }
    public event Action? Change;

    public Task AlerterAsync(string message, string titre = "Information") => Ouvrir(titre, message, false, "OK");

    public Task<bool> ConfirmerAsync(string message, string titre = "Confirmation", string boutonOk = "Confirmer") =>
        Ouvrir(titre, message, true, boutonOk);

    public void Repondre(bool ok)
    {
        var d = Courante;
        Courante = null;
        Change?.Invoke();
        d?.Reponse.TrySetResult(ok);
    }

    private Task<bool> Ouvrir(string titre, string message, bool avecAnnuler, string boutonOk)
    {
        Courante?.Reponse.TrySetResult(false);
        var d = new Demande(titre, message, avecAnnuler, boutonOk, new TaskCompletionSource<bool>());
        Courante = d;
        Change?.Invoke();
        return d.Reponse.Task;
    }
}

// Notifications temporaires (succès, information, erreur)
public sealed class Notifications
{
    public sealed record Notification(Guid Id, string Message, string Genre);

    private readonly List<Notification> liste = [];
    public IReadOnlyList<Notification> Liste => liste;
    public event Action? Change;

    public void Succes(string message) => _ = AjouterAsync(message, "succes");
    public void Info(string message) => _ = AjouterAsync(message, "info");
    public void Erreur(string message) => _ = AjouterAsync(message, "erreur");

    private async Task AjouterAsync(string message, string genre)
    {
        var n = new Notification(Guid.NewGuid(), message, genre);
        liste.Add(n);
        Change?.Invoke();
        await Task.Delay(genre == "erreur" ? 8000 : 4000);
        liste.Remove(n);
        Change?.Invoke();
    }
}
