using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Saisie d'un pré-BL : droits des OPR (création, modification, suppression), contrôles de la saisie (ReglesSaisiePreBl),
// transporteur existant ; l'écriture revérifie sous verrou lignes de commande et palettes.
// =====================================================================
internal sealed class SaisiePreBlService(PreBlDb2 donnees, OprDb2 opr, SaisieBlDb2 bl, IUtilisateurCourant utilisateur) : ISaisiePreBl
{
    public async Task<SaisiePreBl> NouveauAsync(int compteGeneral, int compteClient, int numeroAdresse, CancellationToken ct = default)
    {
        var u = await AccesAsync(d => d.Creer, "créer", ct);
        return await donnees.NouveauAsync(u.Societe, compteGeneral, compteClient, numeroAdresse, ct);
    }

    public async Task<SaisiePreBl> OuvrirAsync(int numero, CancellationToken ct = default)
    {
        var u = await AccesAsync(d => d.Modifier, "modifier", ct);
        var s = await donnees.OuvrirSaisieAsync(u.Societe, numero, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
                ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        if (s.Etat == EtatsPreBl.Livre) throw new RegleMetierException($"Le pré-BL {numero} est transformé en BL : il ne peut plus être modifié.");
        return s;
    }

    public async Task<IReadOnlyList<LigneCommandePreBl>> LignesCommandeAsync(SaisiePreBl saisie, int commande, string cle, CancellationToken ct = default)
    {
        var u = await AccesAsync(_ => true, "consulter", ct);
        if (commande <= 0) throw new RegleMetierException("Le n° de commande doit être renseigné.");
        return await donnees.LignesCommandeAsync(u.Societe, saisie, commande, (cle ?? "").Trim().ToUpperInvariant(), ct);
    }

    public async Task<IReadOnlyList<LotDisponiblePreBl>> LotsAsync(SaisiePreBl saisie, string article, string indice, CancellationToken ct = default)
    {
        var u = await AccesAsync(_ => true, "consulter", ct);
        return await donnees.LotsAsync(u.Societe, saisie.Numero, article, indice, saisie.StockPaye, ct);
    }

    public async Task<int> EnregistrerAsync(SaisiePreBl saisie, CancellationToken ct = default)
    {
        var u = saisie.Numero == 0 ? await AccesAsync(d => d.Creer, "créer", ct) : await AccesAsync(d => d.Modifier, "modifier", ct);
        if (saisie.Numero > 0)
            _ = await donnees.LireAsync(u.Societe, saisie.Numero, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
                ?? throw new RegleMetierException($"Le pré-BL {saisie.Numero} n'existe pas ou ne vous est pas accessible.");
        saisie.SansPalette = await donnees.SansPaletteAsync(u.Societe, ct);   // paramètre de la société, pas de l'écran
        var erreurs = ReglesSaisiePreBl.Controler(saisie);
        if (saisie.Transporteur > 0 && !await bl.TransporteurExisteAsync(u.Societe, saisie.Transporteur, ct)) erreurs.Add("Le transporteur n'existe pas.");
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs.Distinct()));
        var parametres = await bl.ParametresAsync(u.Societe, ct);
        return await bl.EnregistrerPreBlAsync(u.Societe, u.Profil, saisie, parametres.NumerotationParEnseigne, ct);
    }

    public async Task SupprimerAsync(int numero, CancellationToken ct = default)
    {
        var u = await AccesAsync(d => d.Supprimer, "supprimer", ct);
        _ = await donnees.LireAsync(u.Societe, numero, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
            ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        await bl.SupprimerPreBlAsync(u.Societe, u.Profil, numero, ct);
    }

    public async Task<RecapPalettesPreBl> RecapAsync(int numero, CancellationToken ct = default)
    {
        var u = await PreBlModifiableAsync(numero, ct);
        return ReglesRecapPalettes.Calculer(numero, await donnees.RecapAsync(u.Societe, numero, ct), []);
    }

    public async Task<RecapPalettesPreBl> CalculerRecapAsync(int numero, IReadOnlyList<GroupePaletteMixte> groupes, CancellationToken ct = default)
    {
        var u = await PreBlModifiableAsync(numero, ct);
        return ReglesRecapPalettes.Calculer(numero, await donnees.RecapAsync(u.Societe, numero, ct), groupes);
    }

    public async Task EnregistrerRecapAsync(int numero, IReadOnlyList<GroupePaletteMixte> groupes, CancellationToken ct = default)
    {
        var u = await PreBlModifiableAsync(numero, ct);
        var recap = ReglesRecapPalettes.Calculer(numero, await donnees.RecapAsync(u.Societe, numero, ct), groupes);
        if (recap.Erreur != "") throw new RegleMetierException(recap.Erreur);
        await bl.EnregistrerRecapPreBlAsync(u.Societe, u.Profil, recap, ct);
    }

    // Droit de modification, pré-BL accessible au profil et non transformé
    private async Task<UtilisateurConnecte> PreBlModifiableAsync(int numero, CancellationToken ct)
    {
        var u = await AccesAsync(d => d.Modifier, "modifier", ct);
        var fiche = await donnees.LireAsync(u.Societe, numero, await opr.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
                    ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas ou ne vous est pas accessible.");
        if (fiche.Etat == EtatsPreBl.Livre) throw new RegleMetierException($"Le pré-BL {numero} est transformé en BL.");
        return u;
    }

    private async Task<UtilisateurConnecte> AccesAsync(Func<DroitsOpr, bool> droit, string action, CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var droits = ReglesOpr.Droits(await opr.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
        if (!droits.Acces) throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des pré-BL (droits des OPR).");
        if (!droit(droits)) throw new RegleMetierException($"Vous ne pouvez pas {action} les pré-BL (droits des OPR).");
        return u;
    }
}
