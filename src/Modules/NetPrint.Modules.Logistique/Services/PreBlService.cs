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
        if (palettes.Count == 0) return "Aucune palette n'est rattachée au pré-BL.";
        var nonPointees = palettes.Count(p => !p.Pointee);
        if (nonPointees > 0) return $"{nonPointees} palette{(nonPointees > 1 ? "s ne sont pas pointées" : " n'est pas pointée")} en préparation.";
        var picking = palettes.Count(p => p.Picking);
        if (picking > 0) return $"{picking} palette{(picking > 1 ? "s restent" : " reste")} en picking.";
        return "";
    }
}

internal sealed class PreBlService(PreBlDb2 donnees, OprDb2 opr, IUtilisateurCourant utilisateur) : IPreBl
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

    private async Task<UtilisateurConnecte> AccesAsync(CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        if (!ReglesOpr.Droits(await opr.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct)).Acces)
            throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des pré-BL (droits des OPR).");
        return u;
    }
}
