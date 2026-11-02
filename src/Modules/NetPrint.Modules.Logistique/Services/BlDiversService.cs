using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// BL divers (EXP010 état « D », cahier des charges lot 1 § 11) : un et un seul tiers (client, dossier ou fournisseur),
// destinataire, transport, quantité, colis, poids, palettes, désignations libres ; ni stock, ni commande, ni dossier.
// Suppression : comme les autres BL (ISaisieBl.SupprimerAsync).
// ÉCART : adresses de livraison des fournisseurs non reprises (fiche B500F seule).
// =====================================================================
internal sealed class BlDiversService(SaisieBlDb2 donnees, IUtilisateurCourant utilisateur) : IBlDivers
{
    public const int DesignationsMaximum = 30;   // 30 lignes de désignation, comme l'écran EXP010C5

    public async Task<SaisieBlDivers> NouveauAsync(TiersBlDivers tiers, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var t = await TiersAsync(u.Societe, tiers, ct, creation: true);
        var p = await donnees.ParametresAsync(u.Societe, ct);
        var enseignes = await donnees.EnseignesAsync(u.Societe, ct);
        return new SaisieBlDivers
        {
            Tiers = tiers, NomTiers = t.Nom, Adresses = t.Adresses, Adresse = t.Adresses[0], Enseignes = enseignes,
            Enseigne = t.Enseigne != "" ? t.Enseigne : enseignes.FirstOrDefault() ?? "",
            DateExpedition = DateOnly.FromDateTime(DateTime.Today), TypePort = "F", TypeLivraison = p.TypeLivraisonDefaut,
            Transporteur = t.Transporteur, Bareme = t.Bareme,
        };
    }

    // creation : client de qualité 4 ou 5 refusé, fournisseur supprimé refusé ; un BL existant reste modifiable
    private async Task<TiersDivers> TiersAsync(int societe, TiersBlDivers tiers, CancellationToken ct, bool creation)
    {
        if (tiers.Renseignes != 1) throw new RegleMetierException("Indiquez un et un seul destinataire : client, dossier ou fournisseur.");
        var t = await donnees.TiersDiversAsync(societe, tiers, ct, actifSeulement: creation)
            ?? throw new RegleMetierException(tiers.Fournisseur > 0 ? "Ce fournisseur n'existe pas." : tiers.Dossier > 0 ? "Ce dossier n'existe pas." : "Ce client n'existe pas.");
        if (creation && t.QualiteClient is "4" or "5") throw new RegleMetierException("Impossible de créer un BL pour un client de qualité 4 ou 5.");
        if (creation && t.Adresses.Count == 0) throw new RegleMetierException("Ce destinataire n'a pas d'adresse.");
        return t;
    }

    public async Task<SaisieBlDivers> OuvrirAsync(int numeroBl, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var s = await donnees.DiversAsync(u.Societe, numeroBl, ct) ?? throw new RegleMetierException("BL inexistant.");
        var t = await TiersAsync(u.Societe, s.Tiers, ct, creation: false);
        // Adresse du BL proposée en tête si elle n'est plus dans les adresses du tiers (adresse supprimée, saisie libre)
        var adresses = t.Adresses.Any(a => a == s.Adresse) ? t.Adresses : [s.Adresse, .. t.Adresses];
        (s.NomTiers, s.Adresses, s.Enseignes) = (t.Nom, adresses, [s.Enseigne]);
        return s;
    }

    public async Task<IReadOnlyList<string>> ControlerAsync(SaisieBlDivers saisie, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var erreurs = Controler(saisie);
        if (saisie.TypePort != "E" && saisie.Transporteur > 0 && !await donnees.TransporteurExisteAsync(u.Societe, saisie.Transporteur, ct))
            erreurs.Add($"Le transporteur {saisie.Transporteur} n'existe pas.");
        if (saisie.NumeroBl == 0 && saisie.Enseigne != "" && !(await donnees.EnseignesAsync(u.Societe, ct)).Contains(saisie.Enseigne))
            erreurs.Add($"L'enseigne {saisie.Enseigne} n'existe pas.");
        return erreurs;
    }

    // Contrôles sans base : destinataire et transport comme un BL de produit fini, quantités positives, 30 désignations de
    // 40 caractères au plus (jamais coupées sans prévenir)
    internal static List<string> Controler(SaisieBlDivers s)
    {
        var e = new List<string>();
        ReglesSaisieBl.ControlerExpedition(e, s.Adresse, s.TypePort, s.TypeLivraison, s.Transporteur, s.Bareme, s.Palettes);
        if (s.Quantite is < 0 or > ReglesSaisieBl.QuantiteMaximum) e.Add("Quantité : de 0 à 9 999 999.");
        if (s.Colis is < 0 or > 99_999) e.Add("Nombre de colis : de 0 à 99 999.");
        if (s.Poids is < 0 or > 99_999) e.Add("Poids : de 0 à 99 999 kg.");
        var designations = s.Designations.Select(x => x.Trim()).ToList();
        while (designations.Count > 0 && designations[^1] == "") designations.RemoveAt(designations.Count - 1);
        if (designations.Count > DesignationsMaximum) e.Add($"{DesignationsMaximum} lignes de désignation au plus.");
        foreach (var (texte, i) in designations.Select((x, i) => (x, i)).Where(x => x.x.Length > 40))
            e.Add($"Désignation ligne {i + 1} : 40 caractères au plus ({texte.Length} saisis).");
        if (s.TextePied.Any(x => x.Trim().Length > 40)) e.Add("Texte de pied : 40 caractères au plus par ligne.");
        if (designations.Count == 0 && s.Quantite == 0 && s.Colis == 0 && s.Poids == 0 && s.Palettes == 0)
            e.Add("Indiquez ce qui est livré : désignation, quantité, colis, poids ou palettes.");
        return e;
    }

    public async Task<int> ValiderAsync(SaisieBlDivers saisie, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var erreurs = await ControlerAsync(saisie, ct);
        if (erreurs.Count > 0) throw new RegleMetierException(erreurs[0]);
        var t = await TiersAsync(u.Societe, saisie.Tiers, ct, creation: saisie.NumeroBl == 0);
        return await donnees.EnregistrerDiversAsync(u.Societe, u.Profil, saisie, t, await donnees.ParametresAsync(u.Societe, ct), ct);
    }
}
