using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Changement de client, date ou enseigne d'une commande (F17 de DOS505), contrôles de DOS505 :
// client existant (CPT0001) et non clôturé (CLI0004) ; pas de BL (BL00022) ni de facture (CPT0019) pour le client actuel ;
// enseigne existante et du compte général du client. ÉCART : avertissement à accepter quand la commande utilise des n°
// d'adresse (ils désignent des adresses du client) ou un client facturé, qui restent ceux de l'ancien client ; DOS505
// changeait le client sans le signaler. Le changement est tracé dans l'audit des dossiers (EVE200 « MO »).
// =====================================================================
internal sealed class ChangementClientService(ChangementClientDb2 donnees, ISourceSaisieCommandes saisieDonnees, ISaisieCommandes saisie,
    ISourceCommandes commandes, IUtilisateurCourant utilisateur) : IChangementClientCommande
{
    public async Task<bool> ChangerAsync(CleCommande commande, ChangementClient changement, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var droits = ConsultationCommandesService.Droits(
            await commandes.OptionsProgrammeAsync(ConsultationCommandesService.ProgrammeDroits, u.Profil, ct));
        if (!droits.Modifier) throw new RegleMetierException("Vous n'êtes pas autorisé à modifier les commandes.");
        var e = await saisie.LireAsync(commande, ct) ?? throw new RegleMetierException("Cette commande n'existe pas ou ne vous est pas accessible.");
        if (e.Soldee) throw new RegleMetierException("Option impossible, dossier soldé.");
        if (e.Bloquee) throw new RegleMetierException("Commande sous-traitée. Modification impossible.");

        var erreurs = new List<ErreurSaisie>();
        void E(string zone, string message) => erreurs.Add(new(zone, message));
        var enseigne = (changement.Enseigne ?? "").Trim().ToUpperInvariant();
        if (changement.CompteGeneral is not > 0) E(nameof(changement.CompteGeneral), Messages.Obligatoire);
        if (changement.CompteClient is not > 0) E(nameof(changement.CompteClient), Messages.Obligatoire);
        if (changement.DateDossier is null) E(nameof(changement.DateDossier), Messages.Obligatoire);
        Lever(erreurs);
        var (gen, cli, date) = (changement.CompteGeneral!.Value, changement.CompteClient!.Value, changement.DateDossier!.Value);

        var client = await saisieDonnees.ClientAsync(u.Societe, gen, cli, ct);
        if (client is null) E(nameof(changement.CompteClient), Messages.ClientInexistant);
        else if (client.DateFermeture > 0) E(nameof(changement.CompteClient), Messages.ClientCloture);
        var p = await saisieDonnees.ParametresAsync(u.Societe, ct);
        if (p.Enseignes)
        {
            if (enseigne == "") E(nameof(changement.Enseigne), Messages.Obligatoire);
            else if (await saisieDonnees.CompteGeneralEnseigneAsync(u.Societe, enseigne, ct) is not { } genEnseigne)
                E(nameof(changement.Enseigne), Messages.EnseigneInexistante);
            else if (genEnseigne != 0 && genEnseigne != gen) E(nameof(changement.Enseigne), Messages.EnseigneCompteGeneral);
        }
        var clientChange = gen != e.CompteGeneral || cli != e.CompteClient;
        if (!clientChange)
        {
            Lever(erreurs);
            if (enseigne == e.Enseigne && date == e.DateDossier) return false;
        }
        else
        {
            var (bl, facture, numerosAdresse, clientsFactures) = await donnees.PiecesAsync(u.Societe, commande, e.CompteGeneral, e.CompteClient, ct);
            if (bl) E(nameof(changement.CompteClient), MessagesChangementClient.BlExistant);
            if (facture) E(nameof(changement.CompteClient), MessagesChangementClient.FactureExistante);
            Lever(erreurs);
            if ((numerosAdresse > 0 || clientsFactures > 0) && !changement.AvertissementAccepte)
                throw new SaisieInvalideException([new(nameof(changement.CompteClient),
                    MessagesChangementClient.ReferencesAncienClient(numerosAdresse, clientsFactures))]) { Forcable = true };
        }
        await donnees.ChangerAsync(u.Societe, u.Profil, commande, e, gen, cli, enseigne, date, ct);
        return true;
    }

    private static void Lever(List<ErreurSaisie> erreurs)
    {
        if (erreurs.Count > 0) throw new SaisieInvalideException([.. erreurs]);
    }
}

// Messages de PRTMSG (NP11P) repris tels quels, sans l'identifiant en tête
internal static class MessagesChangementClient
{
    public const string BlExistant = "Le client du dossier et celui du BL doivent être identiques.";      // BL00022
    public const string FactureExistante = "ALERTE Des factures ont été établies pour ce client.";      // CPT0019

    public static string ReferencesAncienClient(int numerosAdresse, int clientsFactures) =>
        "La commande garde des références de l'ancien client : " + string.Join(" et ", new[]
        {
            numerosAdresse > 0 ? $"{numerosAdresse} n° d'adresse (livraison des lignes, facture ou ARC)" : "",
            clientsFactures > 0 ? $"{clientsFactures} client facturé (en-tête ou lignes)" : "",
        }.Where(x => x != "")) + ". Vérifiez-les après le changement.";
}
