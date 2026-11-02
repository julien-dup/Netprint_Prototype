using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Ordres de préparation (WinDev OPR) : société et profil de la session, droits PRT000P1 OPR100 contrôlés à chaque action
// (le WinDev ne faisait que griser des boutons), représentant limité imposé, transporteurs contrôlés.
// =====================================================================
internal sealed class OprService(OprDb2 donnees, IUtilisateurCourant utilisateur) : IOpr
{
    public async Task<DroitsOpr> DroitsAsync(CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return ReglesOpr.Droits(await donnees.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
    }

    public async Task<ResultatRecherche<ElementOpr>> RechercherAsync(CriteresOpr criteres, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(_ => true, "", ct);
        return await donnees.RechercherAsync(u.Societe, criteres, await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct);
    }

    public async Task<ResultatRecherche<LigneCandidateOpr>> CandidatesAsync(CriteresCandidatesOpr criteres, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(d => d.Creer, "Vous n'êtes pas autorisé à créer des OPR.", ct);
        var parametres = await donnees.ParametresAsync(u.Societe, ct);
        var representant = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        if (criteres.NumeroOpr <= 0)
        {
            if (criteres.Commande is not > 0 && criteres.Au is null)
                throw new RegleMetierException("Indiquez la date de fin, ou une commande.");
            if (criteres.Du is { } du && criteres.Au is { } au && du > au) throw new RegleMetierException("La date de début est postérieure à la date de fin.");
            return await donnees.CandidatesAsync(u.Societe, criteres, parametres, representant, null, null, null, ct);
        }
        // Ajout : lignes du client, de l'adresse et de la sous-traitance de l'OPR (et de sa commande en « 1 commande = 1 OPR ») ;
        // les critères de l'écran ne sont pas modifiés
        var reference = await ReferenceAsync(u, criteres.NumeroOpr, representant, ct);
        var ajout = new CriteresCandidatesOpr
        {
            Du = criteres.Du, Au = criteres.Au, Commande = criteres.Commande, Cle = criteres.Cle, Fabricants = criteres.Fabricants,
            Appel = criteres.Appel, Categorie = criteres.Categorie, Enseigne = criteres.Enseigne, NumeroOpr = criteres.NumeroOpr,
            CompteGeneral = reference.CompteGeneral, CompteClient = reference.CompteClient,
            NumeroAdresse = reference.NumeroAdresse > 0 ? reference.NumeroAdresse : null,
        };
        if (parametres.UneCommandeParOpr) (ajout.Commande, ajout.Cle) = (reference.Dossier, reference.Cle);
        var r = await donnees.CandidatesAsync(u.Societe, ajout, parametres, representant, null, null, null, ct);
        return r with
        {
            Lignes = [.. r.Lignes.Where(c => c.SousTraite == reference.SousTraite && ReglesOpr.MemeAdresse(c.Adresse, reference.Adresse))],
        };
    }

    public async Task<IReadOnlyList<int>> CreerAsync(IReadOnlyList<LigneSaisieOpr> lignes, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(d => d.Creer, "Vous n'êtes pas autorisé à créer des OPR.", ct);
        var parametres = await donnees.ParametresAsync(u.Societe, ct);
        var candidates = await CandidatesDeAsync(u, lignes, parametres, await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct);
        var erreurs = ReglesOpr.ControlerSaisie(lignes, candidates, null, parametres.UneCommandeParOpr);
        await ControlerTransporteursAsync(u, lignes.Select(l => l.Transporteur), erreurs, ct);
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs));
        return await donnees.CreerAsync(u.Societe, u.Profil, ReglesOpr.Regrouper(lignes, candidates, parametres.UneCommandeParOpr), parametres, ct);
    }

    // Ajout : la date prévue est celle de l'OPR (le WinDev la forçait aussi)
    public async Task AjouterAsync(int numeroOpr, IReadOnlyList<LigneSaisieOpr> lignes, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(d => d.Creer, "Vous n'êtes pas autorisé à créer des OPR.", ct);
        var parametres = await donnees.ParametresAsync(u.Societe, ct);
        var representant = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        var reference = await ReferenceAsync(u, numeroOpr, representant, ct);
        var date = reference.DatePrevue ?? throw new RegleMetierException($"L'OPR {numeroOpr} n'a pas de date prévue : modifiez-le d'abord.");
        lignes = [.. lignes.Select(l => l with { DatePrevue = date })];
        var candidates = await CandidatesDeAsync(u, lignes, parametres, representant, ct);
        var erreurs = ReglesOpr.ControlerSaisie(lignes, candidates, reference, parametres.UneCommandeParOpr);
        await ControlerTransporteursAsync(u, lignes.Select(l => l.Transporteur), erreurs, ct);
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs));
        await donnees.AjouterAsync(u.Societe, u.Profil, numeroOpr, lignes, parametres, ct);
    }

    public async Task<FicheOpr?> LireAsync(int numeroOpr, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(_ => true, "", ct);
        return await donnees.LireAsync(u.Societe, numeroOpr, await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct);
    }

    public async Task ModifierAsync(ModificationOpr modification, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(d => d.Modifier, "Vous ne pouvez pas modifier les OPR.", ct);
        var fiche = await FicheAsync(u, modification.Numero, ct);
        var erreurs = ReglesOpr.ControlerModification(fiche, modification);
        await ControlerTransporteursAsync(u, modification.Lignes.Where(l => !l.Supprimer).Select(l => l.Transporteur), erreurs, ct);
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs));
        await donnees.ModifierAsync(u.Societe, u.Profil, modification, ct);
    }

    public async Task SupprimerAsync(int numeroOpr, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(d => d.Supprimer, "Vous ne pouvez pas supprimer les OPR.", ct);
        var fiche = await FicheAsync(u, numeroOpr, ct);
        if (fiche.Etat == EtatsOpr.Livre) throw new RegleMetierException($"L'OPR {numeroOpr} est livré : il ne peut pas être supprimé.");
        await donnees.SupprimerAsync(u.Societe, u.Profil, numeroOpr, null, null, ct);
    }

    public async Task SupprimerElementAsync(int numeroOpr, int dossier, string cle, CancellationToken ct = default)
    {
        var (u, _) = await ContexteAsync(d => d.Supprimer, "Vous ne pouvez pas supprimer les OPR.", ct);
        var fiche = await FicheAsync(u, numeroOpr, ct);
        if (fiche.Etat == EtatsOpr.Livre) throw new RegleMetierException($"L'OPR {numeroOpr} est livré : il ne peut pas être modifié.");
        await donnees.SupprimerAsync(u.Societe, u.Profil, numeroOpr, dossier, cle, ct);
    }

    // Session, droits (accès à la gestion des OPR, puis droit de l'action)
    private async Task<(UtilisateurConnecte, DroitsOpr)> ContexteAsync(Func<DroitsOpr, bool> droit, string refus, CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var d = ReglesOpr.Droits(await donnees.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
        if (!d.Acces) throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des OPR.");
        if (!droit(d)) throw new RegleMetierException(refus);
        return (u, d);
    }

    private async Task<FicheOpr> FicheAsync(UtilisateurConnecte u, int numero, CancellationToken ct) =>
        await donnees.LireAsync(u.Societe, numero, await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
            ?? throw new RegleMetierException($"L'OPR {numero} n'existe pas ou ne vous est pas accessible.");

    // OPR de référence d'un ajout, accessible au profil (représentant limité : ses commandes)
    private async Task<ReferenceOpr> ReferenceAsync(UtilisateurConnecte u, int numero, int? representant, CancellationToken ct)
    {
        var reference = await donnees.ReferenceAsync(u.Societe, numero, ct);
        if (reference is null || representant is { } r && reference.Representant != r)
            throw new RegleMetierException($"L'OPR {numero} n'existe pas ou ne vous est pas accessible.");
        if (reference.Statut == "4") throw new RegleMetierException($"L'OPR {numero} est livré : on ne peut plus y ajouter de lignes.");
        return reference;
    }

    // Lignes saisies relues comme candidates (état actuel de la commande)
    private async Task<Dictionary<(int, string, int), LigneCandidateOpr>> CandidatesDeAsync(UtilisateurConnecte u, IReadOnlyList<LigneSaisieOpr> lignes,
        ParametresOpr parametres, int? representant, CancellationToken ct)
    {
        var cles = lignes.Select(l => (l.Dossier, l.Cle, l.LigneCommande)).Distinct().ToList();
        var lues = await donnees.CandidatesAsync(u.Societe, new CriteresCandidatesOpr(), parametres, representant, cles, null, null, ct);
        return lues.Lignes.ToDictionary(c => (c.Dossier, c.Cle, c.LigneCommande));
    }

    private async Task ControlerTransporteursAsync(UtilisateurConnecte u, IEnumerable<int> transporteurs, List<string> erreurs, CancellationToken ct)
    {
        foreach (var t in transporteurs.Where(t => t > 0).Distinct())
            if (!await donnees.TransporteurExisteAsync(u.Societe, t, ct)) erreurs.Add($"Le transporteur {t} n'existe pas.");
    }
}
