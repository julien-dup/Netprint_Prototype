using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Retour de marchandises : un seul chemin pour EXP010S (option RM) et ConsultationBL (retour des palettes SSCC).
// Éléments retournables : ceux que le profil voit (consultation des BL, représentant limité). Contrôles de la base : transporteur,
// client facturé, période de stock ouverte, inventaire en cours, emplacement de retour. Dépôt de retour : EXP005.FDEPRE (RPG),
// où vont aussi les palettes recréées, en stock (décision de David, 31/10).
// =====================================================================
internal sealed class RetourBlService(SaisieBlDb2 donnees, IConsultationBl consultation, IUtilisateurCourant utilisateur) : IRetourBl
{
    public async Task<PreparationRetour> PreparerAsync(int numeroBl, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (o, visibles) = await OrigineAsync(u, numeroBl, ct);
        if (o.Elements.Count == 0) throw new RegleMetierException("Ce BL n'a aucun élément.");
        if (o.Elements.All(e => e.Etat == "R")) throw new RegleMetierException("Ce BL est lui-même un retour de marchandises.");
        return ReglesRetourBl.Preparation(o, visibles);
    }

    public async Task<int> EnregistrerAsync(SaisieRetour saisie, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (o, visibles) = await OrigineAsync(u, saisie.NumeroBl, ct);
        var erreurs = ReglesRetourBl.Controler(o, saisie, visibles, DateOnly.FromDateTime(DateTime.Today));
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs));
        if (saisie.Transporteur > 0 && !await donnees.TransporteurExisteAsync(u.Societe, saisie.Transporteur, ct))
            throw new RegleMetierException($"Le transporteur {saisie.Transporteur} n'existe pas.");
        if (saisie.FacturationAutomatique && !await donnees.ClientExisteAsync(u.Societe, saisie.CompteGeneralFacture, saisie.CompteClientFacture, ct))
            throw new RegleMetierException($"Le client facturé {saisie.CompteGeneralFacture} {saisie.CompteClientFacture} n'existe pas.");

        var p = await donnees.ParametresAsync(u.Societe, ct);
        var (depotRetour, emplacementDepot, emplacementRe) = await donnees.ParametresRetourAsync(u.Societe, p.DepotPrincipal, ct);
        var emplacementRetour = depotRetour != 0 ? emplacementDepot : emplacementRe;
        // Types de stock touchés : période ouverte, pas d'inventaire en cours, emplacement de retour s'ils gèrent les emplacements
        var parEmplacement = new Dictionary<string, bool>();
        var elements = ReglesRetourBl.Repartir(o, saisie);
        foreach (var typeStock in elements.SelectMany(e => e.Lignes).SelectMany(l => l.Lots).Where(x => x.Origine.Stocke)
                     .Select(x => x.Origine.TypeStock).Distinct())
        {
            var (periode, gereEmplacement) = await donnees.TypeStockAsync(u.Societe, typeStock, ct);
            if (periode is { } debut && saisie.Date < debut)
                throw new RegleMetierException($"Stock {typeStock} : la période est close avant le {debut:MM/yyyy}, le retour doit être daté après.");
            if (await donnees.InventaireEnCoursAsync(u.Societe, typeStock, saisie.Date, ct))
                throw new RegleMetierException("Retour impossible, inventaire en cours.");
            if (gereEmplacement && emplacementRetour == "")
                throw new RegleMetierException(depotRetour != 0
                    ? $"Le dépôt de retour {depotRetour} n'a pas d'emplacement par défaut."
                    : "L'emplacement par défaut du retour de marchandises (mouvement RE) n'est pas renseigné.");
            parEmplacement[typeStock] = gereEmplacement;
        }
        elements = ReglesRetourBl.AvecEmplacement(elements, x => x.Stocke && parEmplacement.GetValueOrDefault(x.TypeStock) ? emplacementRetour : "");
        return await donnees.EnregistrerRetourAsync(u.Societe, u.Profil,
            new RetourAEcrire(o, saisie, p.DepotPrincipal, depotRetour, p.NumerotationParEnseigne, elements), ct);
    }

    // Suppression d'un retour (R34) : non facturé, palettes recréées toujours en stock, pas d'inventaire en cours
    public async Task SupprimerAsync(int numeroBlRetour, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        _ = await consultation.LireAsync(numeroBlRetour, ct)
            ?? throw new RegleMetierException($"Le BL {numeroBlRetour} n'existe pas ou ne vous est pas accessible.");
        var r = await donnees.RetourEnregistreAsync(u.Societe, numeroBlRetour, ct)
            ?? throw new RegleMetierException($"Le BL {numeroBlRetour} n'est pas un retour de marchandises.");
        if (r.Facture) throw new RegleMetierException("Ce retour est facturé : il ne peut pas être supprimé.");
        if (r.PalettesNonDisponibles.Count > 0)
            throw new RegleMetierException("Suppression impossible : certaines palettes du retour ne sont plus en stock (" +
                                           string.Join(", ", r.PalettesNonDisponibles.Take(5)) + (r.PalettesNonDisponibles.Count > 5 ? "…" : "") + ").");
        foreach (var typeStock in r.TypesStock)
            if (await donnees.InventaireEnCoursAsync(u.Societe, typeStock, DateOnly.FromDateTime(DateTime.Today), ct))
                throw new RegleMetierException("Suppression impossible, inventaire en cours.");
        await donnees.SupprimerRetourAsync(u.Societe, u.Profil, r, ct);
    }

    // BL d'origine et éléments que le profil voit (représentant limité : ses dossiers seulement)
    private async Task<(OrigineRetour Origine, IReadOnlySet<int> Visibles)> OrigineAsync(UtilisateurConnecte u, int numeroBl, CancellationToken ct)
    {
        var fiche = await consultation.LireAsync(numeroBl, ct)
            ?? throw new RegleMetierException($"Le BL {numeroBl} n'existe pas ou ne vous est pas accessible.");
        var o = await donnees.OrigineRetourAsync(u.Societe, numeroBl, ct)
            ?? throw new RegleMetierException($"Le BL {numeroBl} n'existe pas.");
        return (o, fiche.Elements.Select(e => e.Element).ToHashSet());
    }
}
