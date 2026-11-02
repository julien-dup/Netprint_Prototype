using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Noyau.Evenements;

// =====================================================================
// Événements et commentaires d'un objet (EVE300 : commande client, lancement d'un article dans un OF…), tables EVE100
// (événements), EVE105 (profils prévenus) et DOS030 (types d'événements). Les événements automatiques (EVAUTO = 'O')
// sont créés par le batch EVE100 : on n'en modifie que le rappel et on ne les supprime pas.
// L'écran appelant a vérifié que l'utilisateur peut consulter l'objet ; la société vient de la session.
// =====================================================================
public interface IEvenements
{
    Task<ResultatRecherche<Evenement>> ListerAsync(ObjetEvenements objet, CancellationToken ct = default);

    // Types saisissables pour l'objet (DOS030 : « apparaît en … » = 'O', hors types automatiques 90xx)
    Task<IReadOnlyList<TypeEvenement>> TypesAsync(ObjetEvenements objet, CancellationToken ct = default);

    // Nouvel événement : type proposé s'il n'y en a qu'un, valeurs par défaut du type
    Task<SaisieEvenement> NouveauAsync(ObjetEvenements objet, CancellationToken ct = default);

    // Changement de type en création : rappel, agenda, navigateur et profils prévenus du type (SPEVT)
    Task<SaisieEvenement> AppliquerTypeAsync(ObjetEvenements objet, SaisieEvenement saisie, CancellationToken ct = default);

    Task<SaisieEvenement?> LireAsync(ObjetEvenements objet, long numero, CancellationToken ct = default);
    Task<long> CreerAsync(ObjetEvenements objet, SaisieEvenement saisie, CancellationToken ct = default);
    Task ModifierAsync(ObjetEvenements objet, long numero, SaisieEvenement saisie, CancellationToken ct = default);
    Task SupprimerAsync(ObjetEvenements objet, long numero, CancellationToken ct = default);
}

// Objet auquel les événements sont rattachés (paramètre PAR2 d'EVE300). Seuls les codes repris sont proposés.
public sealed record ObjetEvenements(string Code, int Dossier = 0, string Cle = "", string Article = "", string Indice = "",
    int Commande = 0, string CleCommande = "")
{
    public const string CodeCommande = "CDEC";
    public const string CodeLancement = "LAN";

    // Commande client (DOS500S / NEG510 option EC)
    public static ObjetEvenements DeCommande(int numero, string cle) => new(CodeCommande, Commande: numero, CleCommande: cle);

    // Article lancé dans un OF pour une commande (NEG515 option EC)
    public static ObjetEvenements DeLancement(int dossier, string cle, string article, string indice, int commande, string cleCommande) =>
        new(CodeLancement, dossier, cle, article, indice, commande, cleCommande);

    public string Libelle => Code switch
    {
        CodeCommande => $"Commande client {Commande:00000}/{CleCommande}",
        CodeLancement => $"Dossier {Dossier:00000}/{Cle} · article {Article}{(Indice != "" ? " " + Indice : "")}",
        _ => Code,
    };
}

public sealed record TypeEvenement(string Code, string Libelle, bool GestionRappel, string MomentRappel, TimeOnly? HeureParDefaut,
    bool Agenda, bool Navigateur, IReadOnlyList<string> Profils, bool PrevenirFabricant, bool PrevenirRepresentant);

public sealed record Evenement(long Numero, string Type, string LibelleType, DateOnly? Date, string Profil, DateOnly? DatePrevue,
    TimeOnly? HeurePrevue, DateOnly? DateRappel, TimeOnly? HeureRappel, IReadOnlyList<string> Textes, bool Automatique, bool Agenda,
    bool Navigateur);

// Fenêtre « Détail événement » (EVE30002)
public sealed class SaisieEvenement
{
    public long Numero { get; set; }            // 0 en création
    public string Type { get; set; } = "";
    public string LibelleType { get; set; } = "";
    public string Createur { get; set; } = "";
    public bool Automatique { get; set; }
    public DateOnly? DatePrevue { get; set; }
    public TimeOnly? HeurePrevue { get; set; }
    public string[] Textes { get; set; } = ["", "", "", ""];
    public bool Rappel { get; set; }
    public DateOnly? DateRappel { get; set; }
    public TimeOnly? HeureRappel { get; set; }
    public bool Agenda { get; set; }
    public bool Navigateur { get; set; }
    public List<string> Profils { get; set; } = [];
}

public sealed class EvenementInvalideException(IReadOnlyList<ErreurEvenement> erreurs)
    : Exception(string.Join(" ", erreurs.Select(e => e.Message)))
{
    public IReadOnlyList<ErreurEvenement> Erreurs { get; } = erreurs;
}

public sealed record ErreurEvenement(string Zone, string Message);

// Règles d'EVE300 (SPCTL, CalculRappel), sans accès aux données
public static class ReglesEvenements
{
    public const int TexteMaximum = 50;

    public static IReadOnlyList<ErreurEvenement> Controler(SaisieEvenement s, TypeEvenement? type, bool typePermis, Func<string, bool> profilExiste)
    {
        var erreurs = new List<ErreurEvenement>();
        void Erreur(string zone, string message) => erreurs.Add(new(zone, message));

        if (type is null) Erreur("type", s.Type.Trim() == "" ? MessagesEvenements.Obligatoire : MessagesEvenements.TypeInexistant);
        else if (!typePermis) Erreur("type", MessagesEvenements.TypeIncompatible);
        if ((s.DatePrevue is null) != (s.HeurePrevue is null)) Erreur("datePrevue", MessagesEvenements.DateHeureIncompatibles);
        if (s.Rappel && s.DatePrevue is null)
        {
            // Sans date prévue, le rappel ne se calcule pas : date et heure à saisir
            if (s.DateRappel is null) Erreur("dateRappel", MessagesEvenements.Obligatoire);
            if (s.HeureRappel is null) Erreur("heureRappel", MessagesEvenements.Obligatoire);
        }
        if (s.Agenda && s.DatePrevue is null && !erreurs.Any(e => e.Zone == "datePrevue")) Erreur("datePrevue", MessagesEvenements.Obligatoire);
        for (var i = 0; i < s.Textes.Length; i++)
            if ((s.Textes[i] ?? "").Length > TexteMaximum) Erreur($"texte{i + 1}", $"{TexteMaximum} caractères au plus.");
        var vus = new HashSet<string>();
        for (var i = 0; i < s.Profils.Count; i++)
        {
            var p = (s.Profils[i] ?? "").Trim().ToUpperInvariant();
            if (p == "") continue;
            if (!profilExiste(p)) Erreur($"profil{i}", MessagesEvenements.ProfilInexistant);
            else if (!vus.Add(p)) Erreur($"profil{i}", MessagesEvenements.ProfilEnDouble);
        }
        return erreurs;
    }

    // Rappel par défaut à partir de la date prévue (CalculRappel) : '1' la veille, '2' l'avant-veille, '5' le lendemain,
    // à l'heure par défaut du type ; autre moment : à la date prévue, sans heure
    public static (DateOnly Date, TimeOnly? Heure) RappelParDefaut(DateOnly datePrevue, TypeEvenement type) => type.MomentRappel switch
    {
        "1" => (datePrevue.AddDays(-1), type.HeureParDefaut),
        "2" => (datePrevue.AddDays(-2), type.HeureParDefaut),
        "5" => (datePrevue.AddDays(1), type.HeureParDefaut),
        _ => (datePrevue, null),
    };

    // Valeurs par défaut du type (SPEVT) : profils du type, du fabricant et du représentant ajoutés à ceux déjà saisis
    public static void AppliquerType(SaisieEvenement s, TypeEvenement type, IEnumerable<string> profilsSupplementaires)
    {
        s.Type = type.Code;
        s.LibelleType = type.Libelle;
        s.Rappel = type.GestionRappel;
        s.Agenda = type.Agenda;
        s.Navigateur = type.Navigateur;
        s.Profils = [.. s.Profils.Concat(type.Profils).Concat(profilsSupplementaires).Select(p => (p ?? "").Trim().ToUpperInvariant())
            .Where(p => p != "").Distinct()];
    }

    // Heures HH.MM (EVHREP, EVHRRA, TEHEUR) : 0 = pas d'heure
    public static TimeOnly? Heure(decimal valeur)
    {
        if (valeur <= 0) return null;
        var h = (int)valeur;
        var m = (int)Math.Round((valeur - h) * 100);
        return h is >= 0 and < 24 && m is >= 0 and < 60 ? new TimeOnly(h, m) : null;
    }

    public static decimal Heure(TimeOnly? heure) => heure is { } h ? h.Hour + h.Minute / 100m : 0m;
}

// Messages de PRTMSG (NP11P) repris tels quels, sans l'identifiant en tête
public static class MessagesEvenements
{
    public const string Obligatoire = "Cette zone doit être renseignée.";                         // GEN0001
    public const string TypeInexistant = "Ce type d'évènement n'existe pas.";                     // EVE0002
    public const string SuppressionAutomatique = "Suppression impossible, car généré par programme";   // EVE0003
    public const string TypeIncompatible = "Type d'évènement incompatible.";                      // EVE0004
    public const string DateHeureIncompatibles = "Incompatibilité entre date et heure prévues";  // EVE0005
    public const string ProfilInexistant = "Profil inexistant.";                                  // PRF0001
    public const string ProfilEnDouble = "Il ne peut y avoir 2 profils identiques.";              // PRF0005
    public const string EvenementInexistant = "Cet événement n'existe plus.";
}

internal sealed class EvenementsService(EvenementsDb2 donnees, IUtilisateurCourant utilisateur) : IEvenements
{
    private async Task<UtilisateurConnecte> UtilisateurAsync() => await utilisateur.ExigerAsync();

    public async Task<ResultatRecherche<Evenement>> ListerAsync(ObjetEvenements objet, CancellationToken ct = default) =>
        await donnees.ListerAsync((await UtilisateurAsync()).Societe, Verifier(objet), ct);

    public async Task<IReadOnlyList<TypeEvenement>> TypesAsync(ObjetEvenements objet, CancellationToken ct = default) =>
        (await donnees.TypesAsync((await UtilisateurAsync()).Societe, Verifier(objet).Code, ct)).Where(t => !t.Code.StartsWith("90")).ToList();

    public async Task<SaisieEvenement> NouveauAsync(ObjetEvenements objet, CancellationToken ct = default)
    {
        var u = await UtilisateurAsync();
        var s = new SaisieEvenement { Createur = u.Profil.ToUpperInvariant() };
        var types = await TypesAsync(objet, ct);
        if (types.Count == 1) await AppliquerAsync(u.Societe, objet, s, types[0], ct);
        return s;
    }

    public async Task<SaisieEvenement> AppliquerTypeAsync(ObjetEvenements objet, SaisieEvenement saisie, CancellationToken ct = default)
    {
        var u = await UtilisateurAsync();
        var type = (await TypesAsync(objet, ct)).FirstOrDefault(t => t.Code == saisie.Type.Trim().ToUpperInvariant());
        if (type is null) saisie.LibelleType = "";
        else await AppliquerAsync(u.Societe, objet, saisie, type, ct);
        return saisie;
    }

    private async Task AppliquerAsync(int societe, ObjetEvenements objet, SaisieEvenement s, TypeEvenement type, CancellationToken ct)
    {
        var (fabricant, representant) = await donnees.ProfilsDuDossierAsync(societe, objet, ct);
        ReglesEvenements.AppliquerType(s, type, [type.PrevenirFabricant ? fabricant : "", type.PrevenirRepresentant ? representant : ""]);
    }

    public async Task<SaisieEvenement?> LireAsync(ObjetEvenements objet, long numero, CancellationToken ct = default) =>
        await donnees.LireAsync((await UtilisateurAsync()).Societe, Verifier(objet), numero, ct);

    public async Task<long> CreerAsync(ObjetEvenements objet, SaisieEvenement saisie, CancellationToken ct = default)
    {
        var u = await UtilisateurAsync();
        Verifier(objet);
        saisie.Type = saisie.Type.Trim().ToUpperInvariant();
        // Type permis pour l'objet ; sinon, existe-t-il (EVE0004 / EVE0002) ?
        var type = (await TypesAsync(objet, ct)).FirstOrDefault(t => t.Code == saisie.Type);
        var permis = type is not null;
        if (type is null && saisie.Type != "") type = (await donnees.TypesAsync(u.Societe, null, ct)).FirstOrDefault(t => t.Code == saisie.Type);
        await ControlerAsync(saisie, type, permis, ct);
        Completer(saisie, type!);
        saisie.Createur = u.Profil.ToUpperInvariant();
        return await donnees.CreerAsync(u.Societe, objet, saisie, ct);
    }

    public async Task ModifierAsync(ObjetEvenements objet, long numero, SaisieEvenement saisie, CancellationToken ct = default)
    {
        var u = await UtilisateurAsync();
        var actuel = await donnees.LireAsync(u.Societe, Verifier(objet), numero, ct)
            ?? throw new RegleMetierException(MessagesEvenements.EvenementInexistant);
        var type = (await donnees.TypesAsync(u.Societe, null, ct)).FirstOrDefault(t => t.Code == actuel.Type);
        // Le type ne change pas ; un événement automatique ne se modifie que pour son rappel
        saisie.Type = actuel.Type;
        if (actuel.Automatique)
        {
            (saisie.DatePrevue, saisie.HeurePrevue, saisie.Textes, saisie.Agenda, saisie.Navigateur, saisie.Profils) =
                (actuel.DatePrevue, actuel.HeurePrevue, actuel.Textes, actuel.Agenda, actuel.Navigateur, actuel.Profils);
        }
        // Événement automatique : seules les zones du rappel sont contrôlées (les autres ne se saisissent pas)
        await ControlerAsync(saisie, type, true, ct, actuel.Automatique ? ["dateRappel", "heureRappel"] : null);
        if (type is not null) Completer(saisie, type);
        await donnees.ModifierAsync(u.Societe, objet, numero, saisie, ct);
    }

    public async Task SupprimerAsync(ObjetEvenements objet, long numero, CancellationToken ct = default)
    {
        var u = await UtilisateurAsync();
        var actuel = await donnees.LireAsync(u.Societe, Verifier(objet), numero, ct)
            ?? throw new RegleMetierException(MessagesEvenements.EvenementInexistant);
        if (actuel.Automatique) throw new RegleMetierException(MessagesEvenements.SuppressionAutomatique);
        await donnees.SupprimerAsync(u.Societe, objet, numero, ct);
    }

    private async Task ControlerAsync(SaisieEvenement s, TypeEvenement? type, bool permis, CancellationToken ct, string[]? zones = null)
    {
        var profils = s.Profils.Select(p => (p ?? "").Trim().ToUpperInvariant()).Where(p => p != "").Distinct().ToList();
        var existants = zones is null ? await donnees.ProfilsExistantsAsync(profils, ct) : [];
        var erreurs = ReglesEvenements.Controler(s, type, permis, existants.Contains)
            .Where(e => zones is null || zones.Contains(e.Zone)).ToList();
        if (erreurs.Count > 0) throw new EvenementInvalideException(erreurs);
    }

    // Rappel calculé s'il est demandé sans date (CalculRappel) ; sans rappel, ni date ni heure de rappel
    private static void Completer(SaisieEvenement s, TypeEvenement type)
    {
        if (!s.Rappel) (s.DateRappel, s.HeureRappel) = (null, null);
        else if (s.DateRappel is null && s.DatePrevue is { } prevue)
        {
            var (date, heure) = ReglesEvenements.RappelParDefaut(prevue, type);
            (s.DateRappel, s.HeureRappel) = (date, s.HeureRappel ?? heure);
        }
        s.Textes = [.. Enumerable.Range(0, 4).Select(i => i < s.Textes.Length ? (s.Textes[i] ?? "").TrimEnd() : "")];
        s.Profils = [.. s.Profils.Select(p => (p ?? "").Trim().ToUpperInvariant()).Where(p => p != "").Distinct()];
    }

    private static ObjetEvenements Verifier(ObjetEvenements o) => o.Code switch
    {
        ObjetEvenements.CodeCommande when o.Commande > 0 => o,
        ObjetEvenements.CodeLancement when o.Dossier > 0 && o.Article.Trim() != "" => o,
        _ => throw new ArgumentException($"Objet d'événements non pris en charge : {o.Code}", nameof(o)),
    };
}
