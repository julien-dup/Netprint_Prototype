using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Règles du pré-BL, sans base. État : EXP509.HENBL (blanc saisi, « E » pointage en cours, « O » transformé en BL).
// Transformation en BL (FEN_GestionPreBL, R25) : toutes les palettes pointées (statut 4) et aucune en picking ; en plus du
// WinDev, refusée pour un pré-BL déjà transformé, sans palette, avec une ligne d'article stocké sans palette ou un lot encore
// à produire (EXP540 transformait tout cela).
// =====================================================================
internal static class ReglesPreBl
{
    public static string Etat(string henbl) => henbl switch
    {
        "O" => EtatsPreBl.Livre,
        "E" => EtatsPreBl.EnPreparation,
        _ => EtatsPreBl.APreparer,
    };

    // Liste : toutes les palettes pointées, sans picking
    public static bool Pret(string etat, int palettes, int pointees, int picking) =>
        etat != EtatsPreBl.Livre && palettes > 0 && pointees == palettes && picking == 0;

    public static string RefusTransformation(FichePreBl f)
    {
        if (f.Etat == EtatsPreBl.Livre) return $"Le pré-BL {f.Numero} est déjà transformé en BL.";
        if (f.Lignes.Count == 0) return "Le pré-BL n'a aucune ligne.";
        if (f.Lignes.FirstOrDefault(l => l.Lots.Any(x => x.AProduire)) is { } aProduire)
            return $"Ligne {aProduire.Numero} ({aProduire.Article}) : un lot est encore à produire.";
        if (f.Lignes.FirstOrDefault(l => !l.Frais && !l.Lots.Any(x => x.PalettesSscc.Count > 0)) is { } sansPalette)
            return $"Ligne {sansPalette.Numero} ({sansPalette.Article}) : aucune palette.";
        var palettes = f.Lignes.SelectMany(l => l.Lots).SelectMany(l => l.PalettesSscc).ToList();
        var nonPointees = palettes.Count(p => !p.Pointee);
        if (nonPointees > 0) return $"{nonPointees} palette{(nonPointees > 1 ? "s ne sont pas pointées" : " n'est pas pointée")} en préparation.";
        var picking = palettes.Count(p => p.Picking);
        if (picking > 0) return $"{picking} palette{(picking > 1 ? "s restent" : " reste")} en picking.";
        return "";
    }
}

internal sealed class PreBlService(PreBlDb2 donnees, OprDb2 opr, SaisieBlDb2 bl, IUtilisateurCourant utilisateur) : IPreBl
{
    public async Task<DroitsOpr> DroitsAsync(CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return ReglesOpr.Droits(await opr.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
    }

    public async Task<ResultatRecherche<ElementPreBl>> RechercherAsync(CriteresPreBl criteres, CancellationToken ct = default)
    {
        var u = await AccesAsync(ct);
        return await donnees.RechercherAsync(u.Societe, criteres, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct);
    }

    public async Task<FichePreBl?> LireAsync(int numero, CancellationToken ct = default)
    {
        var u = await AccesAsync(ct);
        return await donnees.LireAsync(u.Societe, numero, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct);
    }

    // Contrôles de la fiche (palettes pointées…), date dans la période de stock ouverte (mois ouvert et suivant), pas
    // d'inventaire en cours ; puis transformation sous verrou, qui revérifie tout
    public async Task<TransformationPreBl> TransformerAsync(int numero, CancellationToken ct = default)
    {
        var u = await AccesAsync(ct, modifier: true);
        var fiche = await donnees.LireAsync(u.Societe, numero, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
                    ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        if (fiche.Refus != "") throw new RegleMetierException(fiche.Refus);
        var date = fiche.DateDepart ?? throw new RegleMetierException("Le pré-BL n'a pas de date de départ.");
        var parametres = await bl.ParametresAsync(u.Societe, ct);
        var erreurs = new List<string>();
        foreach (var typeStock in (await bl.TypesArticlesPreBlAsync(u.Societe, numero, ct))
                     .Select(t => parametres.TypeStockArticle(t.TypeArticle, t.TypeFacturation)).Where(t => t != "").Distinct())
        {
            var (periode, _) = await bl.TypeStockAsync(u.Societe, typeStock, ct);
            if (ReglesStock.HorsPeriode(date, periode) is { } debut)
                erreurs.Add($"La date de départ du {date:dd/MM/yyyy} est hors de la période de stock {typeStock} ({debut:MM/yyyy} et mois suivant).");
            if (await bl.InventaireEnCoursAsync(u.Societe, typeStock, date, ct)) erreurs.Add("Transformation impossible, inventaire en cours.");
        }
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs.Distinct()));
        var avertissements = await bl.TransformerPreBlAsync(u.Societe, u.Profil, numero, parametres, date, ct);
        return new TransformationPreBl(numero, avertissements);
    }

    private async Task<UtilisateurConnecte> AccesAsync(CancellationToken ct, bool modifier = false)
    {
        var u = await utilisateur.ExigerAsync();
        var droits = ReglesOpr.Droits(await opr.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
        if (!droits.Acces) throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des pré-BL (droits des OPR).");
        if (modifier && !droits.Modifier) throw new RegleMetierException("Vous ne pouvez pas transformer les pré-BL en BL (droit de modification des OPR).");
        return u;
    }
}
