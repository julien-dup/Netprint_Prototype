using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Règles des OPR (WinDev OPR), sans base : droits, contrôles de la saisie, regroupement des lignes en OPR.
// Écarts voulus avec le WinDev (docs/points-en-attente.md) : quantité positive, clé de commande et n° d'adresse vraiment
// contrôlés, sous-traitance dans le tri (pas d'OPR en trop), ajout limité à la commande de l'OPR en « 1 commande = 1 OPR ».
// =====================================================================
internal static class ReglesOpr
{
    public const string Programme = "OPR100";
    public const int LongueurObservation = 400;
    public const int LongueurTexte = 40;
    private const string Interdit = "**";

    // PRT000P1 OPR100 : AOPT01 total, 02 création, 03 modification, 04 suppression, 07 édition ; aucun paramétrage = tout permis
    public static DroitsOpr Droits(IReadOnlyList<string>? options)
    {
        if (options is null) return new DroitsOpr(true, true, true, true, true);
        if (options.ElementAtOrDefault(0) == Interdit) return new DroitsOpr(false, false, false, false, false);
        return new DroitsOpr(true, options.ElementAtOrDefault(1) != Interdit, options.ElementAtOrDefault(2) != Interdit,
            options.ElementAtOrDefault(3) != Interdit, options.ElementAtOrDefault(6) != Interdit);
    }

    // Contrôles d'une création ou d'un ajout ; reference : OPR auquel on ajoute (même client, adresse, sous-traitance ; même
    // commande en « 1 commande = 1 OPR »)
    public static List<string> ControlerSaisie(IReadOnlyList<LigneSaisieOpr> lignes, IReadOnlyDictionary<(int, string, int), LigneCandidateOpr> candidates,
        ReferenceOpr? reference, bool uneCommandeParOpr)
    {
        var erreurs = new List<string>();
        if (lignes.Count == 0)
        {
            erreurs.Add("Choisissez au moins une ligne de commande.");
            return erreurs;
        }
        foreach (var doublon in lignes.GroupBy(l => (l.Dossier, l.Cle, l.LigneCommande)).Where(g => g.Count() > 1))
            erreurs.Add($"La ligne {doublon.Key.LigneCommande} de la commande {doublon.Key.Dossier}/{doublon.Key.Cle} est saisie deux fois.");
        foreach (var l in lignes)
        {
            var nom = $"Commande {l.Dossier}/{l.Cle} ligne {l.LigneCommande}";
            if (!candidates.TryGetValue((l.Dossier, l.Cle, l.LigneCommande), out var c))
            {
                erreurs.Add($"{nom} : la ligne n'est plus à préparer (déjà dans un OPR, soldée ou modifiée).");
                continue;
            }
            if (l.Quantite <= 0) erreurs.Add($"{nom} : la quantité à préparer doit être positive.");
            else if (c.MaxiLivrable > 0 && l.Quantite > c.MaxiLivrable)
                erreurs.Add($"{nom} : la quantité à préparer ({l.Quantite:#,##0}) dépasse le maxi livrable de la ligne ({c.MaxiLivrable:#,##0}).");
            if ((l.Observation ?? "").Trim().Length > LongueurObservation) erreurs.Add($"{nom} : l'observation fait {LongueurObservation} caractères au plus.");
            if (l.Transporteur < 0) erreurs.Add($"{nom} : transporteur incorrect.");
            if (reference is null) continue;
            if (c.CompteGeneral != reference.CompteGeneral || c.CompteClient != reference.CompteClient)
                erreurs.Add($"{nom} : autre client que celui de l'OPR {reference.Numero}.");
            else if (!MemeAdresse(c.Adresse, reference.Adresse))
                erreurs.Add($"{nom} : autre adresse de livraison que celle de l'OPR {reference.Numero}.");
            if (c.SousTraite != reference.SousTraite)
                erreurs.Add($"{nom} : {(c.SousTraite ? "commande sous-traitée" : "commande non sous-traitée")}, contrairement à l'OPR {reference.Numero}.");
            if (uneCommandeParOpr && (c.Dossier, c.Cle) != (reference.Dossier, reference.Cle))
                erreurs.Add($"{nom} : un OPR ne porte que sur une commande ({reference.Dossier}/{reference.Cle}).");
        }
        return erreurs;
    }

    // Même adresse de livraison : n° d'adresse, raison sociale, rues, code postal et ville (comme le regroupement en OPR), sans
    // tenir compte des blancs ni de la casse
    public static bool MemeAdresse(AdresseBl a, AdresseBl b) =>
        a.Numero == b.Numero && Egal(a.RaisonSociale, b.RaisonSociale) && Egal(a.Rue1, b.Rue1) && Egal(a.Rue2, b.Rue2)
        && Egal(a.CodePostal, b.CodePostal) && Egal(a.Ville, b.Ville);

    private static bool Egal(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // Un OPR par client, adresse, date prévue, sous-traitance, transporteur (et commande si « 1 commande = 1 OPR »), dans cet
    // ordre ; lignes de chaque OPR par commande et n° de ligne (FEN_SaisieOPR.Validation)
    public static List<IReadOnlyList<LigneSaisieOpr>> Regrouper(IReadOnlyList<LigneSaisieOpr> lignes,
        IReadOnlyDictionary<(int, string, int), LigneCandidateOpr> candidates, bool uneCommandeParOpr) =>
        lignes.Select(l => (Ligne: l, Candidate: candidates[(l.Dossier, l.Cle, l.LigneCommande)]))
            .GroupBy(x => (x.Candidate.CompteGeneral, x.Candidate.CompteClient, x.Candidate.Adresse.Numero,
                RaisonSociale: x.Candidate.Adresse.RaisonSociale.Trim().ToUpperInvariant(), Rue1: x.Candidate.Adresse.Rue1.Trim().ToUpperInvariant(),
                Rue2: x.Candidate.Adresse.Rue2.Trim().ToUpperInvariant(), CodePostal: x.Candidate.Adresse.CodePostal.Trim(),
                Ville: x.Candidate.Adresse.Ville.Trim().ToUpperInvariant(), x.Ligne.DatePrevue,
                Dossier: uneCommandeParOpr ? x.Ligne.Dossier : 0, Cle: uneCommandeParOpr ? x.Ligne.Cle : "", x.Candidate.SousTraite, x.Ligne.Transporteur))
            .OrderBy(g => g.Key.CompteGeneral).ThenBy(g => g.Key.CompteClient).ThenBy(g => g.Key.Numero)
            .ThenBy(g => g.Key.RaisonSociale, StringComparer.Ordinal).ThenBy(g => g.Key.Ville, StringComparer.Ordinal).ThenBy(g => g.Key.DatePrevue)
            .ThenBy(g => g.Key.Dossier).ThenBy(g => g.Key.Cle, StringComparer.Ordinal).ThenBy(g => g.Key.SousTraite).ThenBy(g => g.Key.Transporteur)
            .Select(g => (IReadOnlyList<LigneSaisieOpr>)g.Select(x => x.Ligne).OrderBy(l => l.Dossier).ThenBy(l => l.Cle, StringComparer.Ordinal)
                .ThenBy(l => l.LigneCommande).ToList())
            .ToList();

    // Modification : seules les lignes à préparer changent ; l'OPR garde au moins une ligne
    public static List<string> ControlerModification(FicheOpr fiche, ModificationOpr m)
    {
        var erreurs = new List<string>();
        if (fiche.Etat == EtatsOpr.Livre) erreurs.Add($"L'OPR {fiche.Numero} est livré : il ne peut plus être modifié.");
        if (m.Textes.Count > 4 || m.Textes.Any(t => (t ?? "").Trim().Length > LongueurTexte))
            erreurs.Add($"Le commentaire de l'OPR tient sur 4 lignes de {LongueurTexte} caractères.");
        foreach (var l in m.Lignes)
        {
            var existante = fiche.Lignes.FirstOrDefault(x => x.NumeroLigne == l.NumeroLigne);
            if (existante is null)
            {
                erreurs.Add($"La ligne {l.NumeroLigne} n'existe pas dans l'OPR {fiche.Numero}.");
                continue;
            }
            if (!existante.Modifiable)
            {
                erreurs.Add($"Ligne {l.NumeroLigne} ({existante.Article}) : {existante.Etat.ToLowerInvariant()}, elle ne peut plus être modifiée.");
                continue;
            }
            if (l.Supprimer) continue;
            if (l.Quantite <= 0) erreurs.Add($"Ligne {l.NumeroLigne} ({existante.Article}) : la quantité à préparer doit être positive.");
            else if (existante.MaxiLivrable > 0 && l.Quantite > existante.MaxiLivrable)
                erreurs.Add($"Ligne {l.NumeroLigne} ({existante.Article}) : la quantité à préparer dépasse le maxi livrable ({existante.MaxiLivrable:#,##0}).");
            if ((l.Observation ?? "").Trim().Length > LongueurObservation)
                erreurs.Add($"Ligne {l.NumeroLigne} : l'observation fait {LongueurObservation} caractères au plus.");
            if (l.Transporteur < 0) erreurs.Add($"Ligne {l.NumeroLigne} : transporteur incorrect.");
        }
        var supprimees = m.Lignes.Where(l => l.Supprimer).Select(l => l.NumeroLigne).ToHashSet();
        if (fiche.Lignes.Count > 0 && fiche.Lignes.All(l => supprimees.Contains(l.NumeroLigne)))
            erreurs.Add("Vous ne pouvez pas retirer toutes les lignes de l'OPR : supprimez l'OPR.");
        return erreurs;
    }
}
