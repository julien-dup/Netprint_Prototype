using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Paramètres de facturation automatique (F13 de DOS505, fenêtre DOS50505) : contrôles de DOS505, messages PRTMSG.
// ÉCART : le n° d'adresse est vérifié dans les adresses du client (A500F) ; DOS505 ne le contrôlait qu'à la recherche F4.
// =====================================================================
internal sealed class FacturationAutomatiqueService(FacturationAutomatiqueDb2 donnees, ISaisieCommandes saisie, ISourceCommandes commandes,
    IUtilisateurCourant utilisateur) : IFacturationAutomatique
{
    public async Task<FacturationAutomatique?> LireAsync(CleCommande commande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        // Lecture de l'en-tête : commande accessible au profil (représentant imposé)
        if (await saisie.LireAsync(commande, ct) is not { } e) return null;
        return await donnees.LireAsync(u.Societe, commande, e.CompteGeneral, e.CompteClient, ct);
    }

    public async Task EnregistrerAsync(CleCommande commande, FacturationAutomatique facturation, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        if (!droits.Modifier) throw new RegleMetierException("Vous n'êtes pas autorisé à modifier les commandes.");
        var e = await saisie.LireAsync(commande, ct) ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        if (e.Soldee) throw new RegleMetierException("Option impossible, dossier soldé.");
        if (e.Bloquee) throw new RegleMetierException("Commande sous-traitée. Modification impossible.");

        var f = Normaliser(facturation);
        var actuels = await donnees.LireAsync(u.Societe, commande, e.CompteGeneral, e.CompteClient, ct);
        f.AdresseSpecialeClient = actuels.AdresseSpecialeClient;
        var (adresse, origine) = await donnees.VerifierAsync(u.Societe, e.CompteGeneral, e.CompteClient, f, ct);
        var erreurs = Controler(f, adresse, origine, actuels);
        if (erreurs.Count > 0) throw new SaisieInvalideException(erreurs);
        await donnees.EnregistrerAsync(u.Societe, commande, f, ct);
    }

    // Contrôles de la fenêtre DOS50505, dans l'ordre du RPG, et tailles des zones AB001FA (FANAL 4 chiffres, FACPT 9,2).
    // Conditions de règlement et texte de pied : 1 ou 2, ou la valeur déjà enregistrée (posée par le 5250)
    internal static List<ErreurSaisie> Controler(FacturationAutomatique f, bool adresseExiste, bool origineExiste,
        FacturationAutomatique? enregistres = null)
    {
        var erreurs = new List<ErreurSaisie>();
        void E(bool condition, string zone, string message) { if (condition) erreurs.Add(new(zone, message)); }
        E(f.NumeroAdresse is > 0 && f.AdresseSaisie, nameof(f.NumeroAdresse), MessagesFacturation.AdresseOuNumero);
        E(!adresseExiste, nameof(f.NumeroAdresse), MessagesFacturation.AdresseInexistante);
        E(f.AdresseSpecialeClient && f.NumeroAdresse is not > 0 && !f.AdresseSaisie, nameof(f.NumeroAdresse), MessagesFacturation.AdresseSpeciale);
        E(!origineExiste, nameof(f.OrigineTechnique), MessagesFacturation.OrigineInexistante);
        E(f.NumeroAdresse is > 9999, nameof(f.NumeroAdresse), MessagesFacturation.ValeurIncorrecte);
        E(f.Acompte is < 0 or > 9_999_999.99m || f.Acompte is { } a && decimal.Round(a, 2) != a, nameof(f.Acompte), MessagesFacturation.ValeurIncorrecte);
        static bool Code(string v, string? enregistre) => v is ConditionsFacturation.DonneurOrdre or ConditionsFacturation.Facture || v == enregistre;
        E(!Code(f.ConditionsReglement, enregistres?.ConditionsReglement), nameof(f.ConditionsReglement), MessagesFacturation.ValeurIncorrecte);
        E(!Code(f.TextePied, enregistres?.TextePied), nameof(f.TextePied), MessagesFacturation.ValeurIncorrecte);
        return erreurs;
    }

    // Textes sans blancs autour, aux longueurs de AB001FA ; codes en majuscules
    internal static FacturationAutomatique Normaliser(FacturationAutomatique f)
    {
        static string T(string? v, int n) { var s = (v ?? "").Trim(); return s.Length > n ? s[..n] : s; }
        return new FacturationAutomatique
        {
            Automatique = f.Automatique, NumeroAdresse = f.NumeroAdresse is > 0 ? f.NumeroAdresse : null,
            FormeJuridique = T(f.FormeJuridique, 4), Nom = T(f.Nom, 34), Rue1 = T(f.Rue1, 38), Rue2 = T(f.Rue2, 38),
            CodePostal = T(f.CodePostal, 10).ToUpperInvariant(), Ville = T(f.Ville, 38), Attention = T(f.Attention, 39),
            EditionDetailBl = f.EditionDetailBl, OrigineTechnique = T(f.OrigineTechnique, 3).ToUpperInvariant(),
            Acompte = f.Acompte is 0 ? null : f.Acompte, ConditionsReglement = T(f.ConditionsReglement, 1), TextePied = T(f.TextePied, 1),
            Empreinte = f.Empreinte,
        };
    }
}

// Messages de PRTMSG (NP11P) repris tels quels, sans l'identifiant en tête
internal static class MessagesFacturation
{
    public const string AdresseOuNumero = "Vous devez saisir soit le numéro d'adresse soit l'adresse elle-même.";   // ADR0002
    public const string AdresseInexistante = "N° d'adresse inexistant.";                                            // ADR0003
    public const string AdresseSpeciale = "Attention, le client a une adresse spéciale de facturation.";             // FAC0068
    public const string OrigineInexistante = "Origine technique inexistante en table 'OR'.";                        // GEN0080
    public const string ValeurIncorrecte = "La valeur de cette zone est incorrecte.";                               // GEN0046
}
