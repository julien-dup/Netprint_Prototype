using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Facturation automatique (FAC400 → FAC401 → FAC402P, provisoire FAC412 / FAC412P) : critères contrôlés (FAC400), éléments de BL
// lus, factures regroupées par le moteur commun (GenerationFactures). Simulation sans écriture ; génération une facture après
// l'autre, chacune dans sa transaction (une facture refusée n'empêche pas les autres) ; édition définitive des factures sans
// anomalie si demandé. Droits : interdiction PRT000P1 sur FAC400 pour lancer, sur FAC200 pour éditer en définitif (FAC400 R2).
// Journal partiel et comptabilisation (FAC210CLP) : module comptable, plus tard (F7).
// =====================================================================
internal sealed class FacturationAutomatiqueService(FacturationAutoDb2 donnees, EditionDefinitiveDb2 definitive, DroitsFacturationDb2 droits,
    IUtilisateurCourant utilisateur) : IFacturationAutomatique
{
    public const string ProgrammeLancement = "FAC400";
    public const int Maxi = 10000;

    public async Task<ContexteFacturationAuto> PreparerAsync(CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var x = await donnees.ContexteAsync(u.Societe, "", ct);
        var edition = !EditionFacturesService.EditionInterdite(await droits.OptionsProgrammeAsync(EditionFacturesService.ProgrammeEdition, u.Profil, ct));
        return new ContexteFacturationAuto(x.GereEnseignes, edition);
    }

    public Task<ResultatFacturationAuto> SimulerAsync(CriteresFacturationAuto criteres, CancellationToken ct = default) =>
        TraiterAsync(criteres, generer: false, editerDefinitif: false, null, ct);

    public Task<ResultatFacturationAuto> GenererAsync(CriteresFacturationAuto criteres, bool editerDefinitif, string empreinte,
        CancellationToken ct = default) =>
        TraiterAsync(criteres, generer: true, editerDefinitif, empreinte, ct);

    private async Task<ResultatFacturationAuto> TraiterAsync(CriteresFacturationAuto c, bool generer, bool editerDefinitif, string? empreinte,
        CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        if (EditionFacturesService.EditionInterdite(await droits.OptionsProgrammeAsync(ProgrammeLancement, u.Profil, ct)))
            throw new RegleMetierException("Vous n'avez pas le droit de lancer la facturation automatique (FAC400).");
        if (generer && editerDefinitif
            && EditionFacturesService.EditionInterdite(await droits.OptionsProgrammeAsync(EditionFacturesService.ProgrammeEdition, u.Profil, ct)))
            throw new RegleMetierException("Vous n'avez pas le droit d'éditer les factures en définitif (FAC200).");

        var contexte = await donnees.ContexteAsync(u.Societe, c.Enseigne, ct);
        var erreurs = ReglesFacturationAuto.Controler(c, contexte, DateOnly.FromDateTime(DateTime.Today));
        if (erreurs.Count > 0) return new ResultatFacturationAuto(erreurs, null, [], [], false);
        var date = c.DateFacture!.Value;

        var (retenus, ecartes, tronque) = await donnees.LireAsync(u.Societe, c, Maxi, ct);
        var parametres = await donnees.ParametresAsync(u.Societe, ct);
        var noms = retenus.GroupBy(e => (e.CompteGeneral, e.CompteClient)).ToDictionary(g => g.Key, g => g.First().NomClient);
        var prevues = tronque ? [] : GenerationFactures.Generer(retenus, parametres);
        var numerotation = new NumerotationFactures(contexte.TypeNumerotation ?? 0, contexte.GereEnseignes, c.Enseigne);

        var factures = prevues.Select((f, i) => Vers(f, i + 1, noms.GetValueOrDefault((f.CompteGeneral, f.CompteClient)) ?? "")).ToList();
        if (generer)
        {
            if (tronque || ResultatFacturationAuto.Empreindre(factures) != empreinte)
                throw new RegleMetierException("Les factures à créer ne sont plus celles de la simulation (BL, prix ou clients modifiés entre-temps) : relancez la simulation.");
            // Compteur du mois (ou de l'année) absent : signalé avant de commencer, l'écran propose de le créer
            if (prevues.Count > 0 && !await donnees.CompteurExisteAsync(u.Societe, numerotation, date, ct)) throw numerotation.CompteurAbsent(date);
            // Une facture après l'autre : une erreur (règle ou base) n'arrête pas les suivantes
            for (var i = 0; i < prevues.Count; i++)
            {
                var f = prevues[i];
                try
                {
                    var numero = await donnees.EcrireAsync(u.Societe, u.Profil, numerotation, date, f, ct);
                    factures[i] = factures[i] with { Numero = numero };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    factures[i] = factures[i] with { Refus = ex is RegleMetierException or CompteurFacturesAbsentException ? ex.Message : $"Erreur : {ex.Message}" };
                    continue;
                }
                if (!editerDefinitif || f.AControler) continue;
                try
                {
                    await definitive.EditerAsync(u.Societe, factures[i].Numero!.Value, date, DateOnly.FromDateTime(DateTime.Today), ct);
                    factures[i] = factures[i] with { EditeeDefinitif = true };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    factures[i] = factures[i] with { Avertissement = $"Non éditée en définitif : {ex.Message}" };
                }
            }
        }
        return new ResultatFacturationAuto([], date, factures,
            [.. ecartes.Select(e => new BlEcarte(e.Bl, e.Element, e.Dossier, e.Cle, e.CompteGeneral, e.CompteClient, e.Nom, e.Raison))], tronque);
    }

    private static FactureAuto Vers(FacturePrevue f, int rang, string nom) =>
        new(rang, f.Avoir, f.CompteGeneral, f.CompteClient, nom, f.CompteGeneralFacture, f.CompteClientFacture, f.Mode, f.Ventes, f.Anomalies,
            [.. f.Elements.Select(e => new ElementFactureAuto(e.Numero, e.Dossier.Numero, e.Dossier.Cle, [.. e.Bls.Select(b => b.Bl).Distinct()],
                e.Ventes, [.. e.Lignes.Select(l => new LigneFactureAuto(l.CodeLigne, l.Designation, l.Quantite, l.Unite, l.Pu, l.Montant, l.Bl, l.Texte))]))]);
}
