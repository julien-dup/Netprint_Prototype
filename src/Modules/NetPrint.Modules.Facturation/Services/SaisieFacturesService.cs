using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Facturation.Services;

// =====================================================================
// Saisie d'une facture à partir de BL (FAC085 → FAC150) : clé contrôlée, BL facturables du donneur d'ordre (même lecture que la
// facturation automatique, sans l'exigence « à prendre en automatique »), facture préparée par le moteur commun (une commande =
// un élément), corrigée à l'écran, puis créée par la même écriture que la facturation automatique (une transaction, n° compris).
// Droits : interdiction PRT000P1 sur FAC150 pour saisir, sur FAC200 pour éditer en définitif.
// Pas encore repris : facture sans BL (client ou commande seuls), ajout d'élément, modification, copie, suppression.
// =====================================================================
internal sealed class SaisieFacturesService(FacturationAutoDb2 bls, SaisieFacturesDb2 donnees, EditionDefinitiveDb2 definitive,
    DroitsFacturationDb2 droits, IUtilisateurCourant utilisateur) : ISaisieFactures
{
    public const string ProgrammeSaisie = "FAC150";
    public const int Maxi = 10000;
    public const int BlsMaxi = 500;   // BL d'une même facture (paramètres de la relecture)

    private async Task<UtilisateurConnecte> ExigerDroitAsync(CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        // PRT000P1 : « ** » en option 1 (total) ou 2 (création) interdit la saisie
        if (await droits.OptionsProgrammeAsync(ProgrammeSaisie, u.Profil, ct) is [var total, var creation, ..] && (total == "**" || creation == "**"))
            throw new RegleMetierException("Vous n'avez pas le droit de créer des factures (FAC150).");
        return u;
    }

    public async Task<BlsAFacturer> ChercherBlsAsync(CleFacturation cle, CancellationToken ct = default)
    {
        var u = await ExigerDroitAsync(ct);
        var cles = new[] { cle.Bl is not null, cle.Commande is not null, cle.CompteGeneral is not null || cle.CompteClient is not null }.Count(x => x);
        if (cles != 1) return Refus(new ErreurLancement("", "Indiquez un BL, une commande ou un client, et un seul."));
        if (cle.CompteGeneral is null != cle.CompteClient is null)
            return Refus(new ErreurLancement(nameof(cle.CompteGeneral), "Indiquez le compte général et le compte client."));

        var r = await donnees.ResoudreAsync(u.Societe, cle, ct);
        if (r.Erreur is not null) return Refus(r.Erreur);
        var (retenus, ecartes, tronque) = await bls.LireManuelAsync(u.Societe, r.Commande is null ? (r.CompteGeneral, r.CompteClient) : null,
            r.Commande, null, Maxi, ct);
        var tout = r.Commande is not null;   // clé commande : tous ses BL cochés
        return new BlsAFacturer([], r.CompteGeneral, r.CompteClient, r.NomClient,
            [.. retenus.Select(e => new BlAFacturer(e.Bl, e.Element, e.Expedition, e.Dossier.Numero, e.Dossier.Cle, e.Dossier.Designation1,
                e.Dossier.Reference, e.QuantiteLivree, e.Retour, tout || r.Proposes.Contains((e.Bl, e.Element))))
                .OrderByDescending(b => b.Propose).ThenByDescending(b => b.Bl).ThenBy(b => b.Element)],   // cochés d'abord, puis les plus récents
            [.. ecartes.Select(e => new BlEcarte(e.Bl, e.Element, e.Dossier, e.Cle, e.CompteGeneral, e.CompteClient, e.Nom, e.Raison))], tronque);

        static BlsAFacturer Refus(ErreurLancement e) => new([e], 0, 0, "", [], [], false);
    }

    // Éléments de BL choisis relus : tous doivent être encore facturables, pas encore facturés, avec leur commande
    private async Task<(FacturePrevue Facture, ParametresGeneration Parametres, string Enseigne, string Nom)> RelireAsync(int societe,
        IReadOnlyList<ElementDeBl> choisis, CancellationToken ct)
    {
        if (choisis.Count == 0) throw new RegleMetierException("Cochez au moins un BL à facturer.");
        var numeros = choisis.Select(b => b.Bl).Distinct().ToList();
        if (numeros.Count > BlsMaxi) throw new RegleMetierException($"Une facture saisie porte au plus {BlsMaxi} BL.");
        var (retenus, ecartes, _) = await bls.LireManuelAsync(societe, null, null, numeros, Maxi, ct);
        var voulus = choisis.Select(b => (b.Bl, b.Element)).ToHashSet();
        foreach (var e in ecartes.Where(e => voulus.Contains((e.Bl, e.Element))))
            throw new RegleMetierException($"Le BL {e.Bl} (élément {e.Element}) ne peut pas être facturé : {e.Raison}.");
        var lus = retenus.Where(e => voulus.Contains((e.Bl, e.Element))).ToList();
        var absent = choisis.FirstOrDefault(b => !lus.Any(e => e.Bl == b.Bl && e.Element == b.Element));
        if (absent is not null)
            throw new RegleMetierException($"Le BL {absent.Bl} (élément {absent.Element}) n'est plus à facturer (facturé ou modifié entre-temps).");
        var enseignes = lus.Select(e => e.Enseigne).Distinct().ToList();
        if (enseignes.Count > 1) throw new RegleMetierException("Les BL choisis ne sont pas tous de la même enseigne.");
        var parametres = await bls.ParametresAsync(societe, ct);
        return (GenerationFactures.Regrouper(lus, parametres), parametres, enseignes[0], lus[0].NomClient);
    }

    public async Task<FactureSaisie> PreparerAsync(IReadOnlyList<ElementDeBl> choisis, CancellationToken ct = default)
    {
        var u = await ExigerDroitAsync(ct);
        var (f, p, _, nom) = await RelireAsync(u.Societe, choisis, ct);
        var (modes, representants) = await donnees.ListesAsync(u.Societe, ct);
        var peutEditer = !EditionFacturesService.EditionInterdite(
            await droits.OptionsProgrammeAsync(EditionFacturesService.ProgrammeEdition, u.Profil, ct));
        return SaisieFacture.VersSaisie(f, choisis, nom, p, DateOnly.FromDateTime(DateTime.Today), modes, representants, peutEditer);
    }

    public ControleFactureSaisie Recalculer(FactureSaisie facture) => SaisieFacture.Recalculer(facture);

    public async Task<FactureCreee> CreerAsync(FactureSaisie s, bool editerDefinitif, CancellationToken ct = default)
    {
        var u = await ExigerDroitAsync(ct);
        if (editerDefinitif && EditionFacturesService.EditionInterdite(
                await droits.OptionsProgrammeAsync(EditionFacturesService.ProgrammeEdition, u.Profil, ct)))
            throw new RegleMetierException("Vous n'avez pas le droit d'éditer les factures en définitif (FAC200).");
        var controle = SaisieFacture.Recalculer(s);
        if (controle.Erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", controle.Erreurs.Select(e => e.Message)));
        var date = s.Date!.Value;

        var (relue, p, enseigne, _) = await RelireAsync(u.Societe, s.Bls, ct);
        if (SaisieFacture.Empreinte(relue) != s.Empreinte)
            throw new RegleMetierException("Les BL ont changé depuis la préparation de la facture (lignes, prix ou commande) : recommencez.");
        var contexte = await bls.ContexteAsync(u.Societe, enseigne, ct);
        if (!contexte.Periodes.Any(x => date.Year * 100 + date.Month >= x.Debut && date.Year * 100 + date.Month <= x.Fin))
            throw new RegleMetierException("La date de la facture n'est pas dans une période comptable ouverte de la société.");
        if (contexte.TypeNumerotation is not { } type) throw new RegleMetierException("Les paramètres de facturation de la société (FAC010) n'existent pas.");
        if (contexte.GereEnseignes && type == 3 && enseigne.Trim() == "")
            throw new RegleMetierException("La numérotation est par enseigne et les BL n'ont pas d'enseigne.");
        if (s.CompteGeneralFacture is { } gf && s.CompteClientFacture is { } cf && !await donnees.ClientExisteAsync(u.Societe, gf, cf, ct))
            throw new RegleMetierException($"Le client facturé {gf} {cf} n'existe pas.");

        // Compteur absent : CompteurFacturesAbsentException levée à l'écriture (transaction annulée), l'écran propose de le créer
        var numerotation = new NumerotationFactures(type, contexte.GereEnseignes, enseigne);
        var f = SaisieFacture.Appliquer(relue, s, p, controle.Anomalies);
        var numero = await bls.EcrireAsync(u.Societe, u.Profil, numerotation, date, f, ct);
        if (!editerDefinitif || f.AControler) return new FactureCreee(numero, date, f.AControler, false, null);
        try
        {
            await definitive.EditerAsync(u.Societe, numero, date, DateOnly.FromDateTime(DateTime.Today), ct);
            return new FactureCreee(numero, date, false, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FactureCreee(numero, date, false, false, $"Facture créée, mais non éditée en définitif : {ex.Message}");
        }
    }
}
