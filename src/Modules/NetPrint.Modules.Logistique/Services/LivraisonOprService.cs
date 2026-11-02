using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Livraison d'un OPR : situation (FEN_BonLivraison), génération des BL (FEN_CreationBL), finalisation (FEN_DetailBL).
// Contrôles de FEN_CreationBL : date dans la période de stock ouverte (mois ouvert et suivant), transporteur obligatoire et
// existant sauf enlèvement (port « E »), stock. Décisions du 31/10 : frais comptés comme livrés, palettes d'un autre dépôt
// transférées puis sorties du dépôt principal, cartons d'emballage du BL sortis s'il y en a assez (sinon avertissement),
// mode 3 (un BL par article) codé.
// =====================================================================
internal sealed class LivraisonOprService(OprDb2 donnees, SaisieBlDb2 bl, IUtilisateurCourant utilisateur) : ILivraisonOpr
{
    private static readonly string[] Ports = ["", "F", "D", "A", "E"];
    private static readonly string[] ModesLivraison = ["", "M", "E", "A", "I", "P"];

    public async Task<SituationOpr> SituationAsync(int numeroOpr, CancellationToken ct = default)
    {
        var u = await AccesAsync(numeroOpr, false, ct);
        var (mode, lignes) = await donnees.SituationAsync(u.Societe, numeroOpr, ct);
        var refus = Refus(mode, lignes);
        return new SituationOpr(numeroOpr, mode, ReglesGenerationBlOpr.LibelleMode(mode), refus == "", refus, lignes,
            await donnees.DefautsBlAsync(u.Societe, numeroOpr, ct));
    }

    // Pourquoi les BL ne peuvent pas être générés ("" s'ils le peuvent)
    internal static string Refus(string mode, IReadOnlyList<LigneSituationOpr> lignes)
    {
        if (ReglesGenerationBlOpr.LibelleMode(mode) == "") return "Ce client n'est pas paramétré pour générer les BL (mode de génération de la fiche client).";
        if (lignes.Count == 0) return "Toutes les lignes de l'OPR sont livrées.";
        if (lignes.FirstOrDefault(l => l.Bloquante) is { } l)
            return $"Ligne {l.NumeroLigne} ({l.Article}) : {l.Etat.ToLowerInvariant()}, la préparation n'est pas terminée.";
        if (mode == "1" && lignes.Any(x => x.Frais)) return "La génération d'un BL par colis ne permet pas d'inclure les frais.";
        if (!lignes.Any(x => x.Frais || x.Conditionnee > 0)) return "Aucune palette conditionnée à livrer.";
        return "";
    }

    public async Task<GenerationOprResultat> GenererBlAsync(GenerationBlOpr g, CancellationToken ct = default)
    {
        var u = await AccesAsync(g.Numero, true, ct);
        var (mode, lignes) = await donnees.SituationAsync(u.Societe, g.Numero, ct);
        if (Refus(mode, lignes) is { Length: > 0 } refus) throw new RegleMetierException(refus);
        var erreurs = Controler(g);
        if (g.TypePort != "E" && g.Transporteur > 0 && !await bl.TransporteurExisteAsync(u.Societe, g.Transporteur, ct))
            erreurs.Add($"Le transporteur {g.Transporteur} n'existe pas.");
        var parametres = await bl.ParametresAsync(u.Societe, ct);
        var source = await donnees.SourceBlAsync(u.Societe, g.Numero, mode, parametres, ct)
            ?? throw new RegleMetierException($"L'OPR {g.Numero} n'existe pas.");
        foreach (var typeStock in source.Lignes.Values.Where(l => l.TypeStock != "").Select(l => l.TypeStock).Distinct())
        {
            var (periode, _) = await bl.TypeStockAsync(u.Societe, typeStock, ct);
            if (ReglesStock.HorsPeriode(g.Date, periode) is { } debut)
                erreurs.Add($"La date est hors de la période de stock {typeStock} ({debut:MM/yyyy} et mois suivant).");
            if (await bl.InventaireEnCoursAsync(u.Societe, typeStock, g.Date, ct)) erreurs.Add("Création de BL impossible, inventaire en cours.");
        }
        if (erreurs.Count > 0) throw new RegleMetierException(string.Join("\n", erreurs.Distinct()));
        var plan = ReglesGenerationBlOpr.Planifier(source.Palettes, mode);
        if (plan.Count == 0) throw new RegleMetierException("Aucune palette conditionnée à livrer.");
        var (bls, avertissements) = await bl.EnregistrerBlOprAsync(u.Societe, u.Profil, source, plan, g, ct);
        return new GenerationOprResultat(bls, avertissements);
    }

    // Contrôles de la saisie, sans base
    internal static List<string> Controler(GenerationBlOpr g)
    {
        var erreurs = new List<string>();
        if (!Ports.Contains(g.TypePort)) erreurs.Add($"Le type de port « {g.TypePort} » n'existe pas.");
        if (!ModesLivraison.Contains(g.ModeLivraison)) erreurs.Add($"Le mode de livraison « {g.ModeLivraison} » n'existe pas.");
        if (g.TypePort != "E" && g.Transporteur <= 0) erreurs.Add("Le transporteur doit être renseigné.");
        if (g.Observations.Count > 3 || g.Observations.Any(o => (o ?? "").Length > 40)) erreurs.Add("Les observations tiennent sur 3 lignes de 40 caractères.");
        if ((g.Incoterm ?? "").Length > 40) erreurs.Add("L'observation incoterm fait 40 caractères au plus.");
        return erreurs;
    }

    public async Task FinaliserBlAsync(int numeroOpr, IReadOnlyList<FinalisationBlOpr> bls, CancellationToken ct = default)
    {
        var u = await AccesAsync(numeroOpr, true, ct);
        if (bls.Any(b => b.Palettes < 0 || b.Palettes > 99 || b.PoidsPalette < 0)) throw new RegleMetierException("Nombre de palettes (0 à 99) ou poids incorrect.");
        if (bls.Any(b => b.TextePied.Count > 999)) throw new RegleMetierException("Le texte de pied fait 999 lignes au plus.");
        await bl.FinaliserBlOprAsync(u.Societe, numeroOpr, bls, ct);
    }

    // Gestion des OPR permise (et modification pour livrer : l'OPR passe livré) ; OPR accessible au profil
    private async Task<UtilisateurConnecte> AccesAsync(int numeroOpr, bool livrer, CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var droits = ReglesOpr.Droits(await donnees.OptionsProgrammeAsync(ReglesOpr.Programme, u.Profil, ct));
        if (!droits.Acces) throw new RegleMetierException("Vous n'êtes pas autorisé à la gestion des OPR.");
        if (livrer && !droits.Modifier) throw new RegleMetierException("Vous ne pouvez pas livrer les OPR (droit de modification).");
        _ = await donnees.LireAsync(u.Societe, numeroOpr, await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct), ct)
            ?? throw new RegleMetierException($"L'OPR {numeroOpr} n'existe pas ou ne vous est pas accessible.");
        return u;
    }
}
