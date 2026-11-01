using NetPrint.Modules.Logistique.Contrats;

namespace NetPrint.Modules.Logistique.Services;

// Pré-BL relu sous verrou pour sa transformation en BL (EXP540) : en-tête, commandes, lignes, lots, palettes
internal sealed record PaletteSourcePreBl(string Sscc, int Quantite, bool Picking, string Statut, int Depot, string Emplacement, string Lot);

internal sealed record LotSourcePreBl(int Numero, bool AProduire, string LibellePalettes, IReadOnlyList<PaletteSourcePreBl> Palettes);

// UC : colis de la ligne (EXP510.DENUC) ; ExMecompte : exemplaires de l'UC incomplète ; Facture : client à facturer de la ligne
internal sealed record LigneSourcePreBl(
    int Numero, int Dossier, string Cle, int LigneCommande, string Article, string Indice, int Quantite, IReadOnlyList<string> Observations,
    bool AppelASolder, string EtatLivraison, int Uc, int ExMecompte, int EmballagePar, int EmballageParExceptionnel, string TypeEmballage,
    bool Stocke, string TypeStock, string Famille, string SousFamille, decimal PoidsUc, string Designation, string CodeLigne,
    IReadOnlyList<string> TextesCommande, int CompteGeneralFacture, int CompteClientFacture, IReadOnlyList<LotSourcePreBl> Lots);

internal sealed record CommandeSourcePreBl(
    int Dossier, string Cle, string Enseigne, int CompteGeneral, int CompteClient, string Reference, DateOnly? DateCommande, int Representant,
    string Revendeur, bool FacturationAutomatique, int CompteGeneralFacture, int CompteClientFacture);

internal sealed record SourcePreBl(
    int Numero, string Henbl, DateOnly Date, int CompteGeneral, int CompteClient, int NumeroAdresse, AdresseBl Adresse,
    IReadOnlyList<string> Observations, string Mention, int Palettes, int Poids, int Transporteur, string TypeLivraison, string TypePort,
    int EtiquettesParPalette, int Bareme, bool StockPaye, bool SansNumerosPalettes, int PoidsPalette, int DepotPrincipal,
    IReadOnlyList<LigneSourcePreBl> Lignes, IReadOnlyDictionary<(int, string), CommandeSourcePreBl> Commandes, IReadOnlyList<string> Pied);

// Un emplacement d'où sortent des palettes d'un lot (une ligne EXP016, des mouvements A700F)
internal sealed record SortiePreBl(int Depot, string Lot, string Emplacement, int Quantite, IReadOnlyList<string> Palettes);

// =====================================================================
// Règles de la transformation, sans base. Écarts voulus avec EXP540 (docs/logistique/fiches/EXP540.md, A1 à A15) :
// quantité livrée d'un article stocké = somme de ses palettes (EXP540 : quantité de la ligne pour la commande, quantité prévue du
// lot pour la sortie) ; un élément par commande (dossier ET clé) ; client facturé de chaque élément ; lignes de texte sans
// mécompte ; soldé gardé si la ligne l'était déjà.
// =====================================================================
internal static class ReglesTransformationPreBl
{
    // Contrôles sous verrou (en plus de ceux de la fiche : ReglesPreBl.RefusTransformation)
    public static string Refus(SourcePreBl s)
    {
        if (s.Henbl == "O") return $"Le pré-BL {s.Numero} est déjà transformé en BL.";
        if (s.Lignes.Count == 0) return "Le pré-BL n'a aucune ligne.";
        foreach (var l in s.Lignes)
        {
            if (l.Lots.Any(x => x.AProduire)) return $"Ligne {l.Numero} ({l.Article}) : un lot est encore à produire.";
            if (l.Stocke && !l.Lots.Any(x => x.Palettes.Count > 0)) return $"Ligne {l.Numero} ({l.Article}) : aucune palette.";
            if (!l.Stocke && l.Lots.Any(x => x.Palettes.Count > 0))
                return $"Ligne {l.Numero} ({l.Article}) : l'article n'est pas géré en stock mais des palettes lui sont rattachées.";
            var palettes = l.Lots.SelectMany(x => x.Palettes).ToList();
            if (palettes.FirstOrDefault(p => p.Statut != "4") is { } p)
                return $"Ligne {l.Numero} ({l.Article}) : la palette {p.Sscc} n'est pas pointée en préparation" +
                       (p.Statut == "" ? " (elle n'existe plus)." : $" (statut {p.Statut}).");
            if (palettes.FirstOrDefault(x => x.Picking) is { } k) return $"Ligne {l.Numero} ({l.Article}) : la palette {k.Sscc} est en picking.";
        }
        if (!s.Commandes.Any()) return "Les commandes du pré-BL n'existent plus.";
        if (s.Lignes.FirstOrDefault(l => !s.Commandes.ContainsKey((l.Dossier, l.Cle))) is { } sansCommande)
            return $"Ligne {sansCommande.Numero} : la commande {sansCommande.Dossier}/{sansCommande.Cle} n'existe plus.";
        return "";
    }

    // Quantité livrée : palettes d'un article stocké, quantité saisie sinon
    public static int Quantite(LigneSourcePreBl l) => l.Stocke ? l.Lots.SelectMany(x => x.Palettes).Sum(p => p.Quantite) : l.Quantite;

    // Sorties de stock d'une ligne : palettes regroupées par dépôt, lot et emplacement
    public static List<SortiePreBl> Sorties(LigneSourcePreBl l) =>
        [.. l.Lots.SelectMany(x => x.Palettes).GroupBy(p => (p.Depot, p.Lot, p.Emplacement))
            .OrderBy(g => g.Key.Depot).ThenBy(g => g.Key.Lot, StringComparer.Ordinal).ThenBy(g => g.Key.Emplacement, StringComparer.Ordinal)
            .Select(g => new SortiePreBl(g.Key.Depot, g.Key.Lot, g.Key.Emplacement, g.Sum(p => p.Quantite), [.. g.Select(p => p.Sscc)]))];

    // Textes de la ligne : textes de la ligne de commande, observations du pré-BL, n° de palettes (sauf EXP005.FEOPT4 = O)
    public static List<string> Textes(LigneSourcePreBl l, bool sansNumerosPalettes) =>
        [.. l.TextesCommande, .. l.Observations,
         .. sansNumerosPalettes ? [] : l.Lots.Where(x => x.LibellePalettes != "").Select(x => $"Palette(s) {x.LibellePalettes}")];

    // Colis (UC, plus une UC incomplète) et poids (UC × poids d'une UC) de la ligne, comme EXP540 avec un poids de palette
    public static (int Colis, decimal Poids) ColisPoids(LigneSourcePreBl l) =>
        (l.Uc + (l.ExMecompte > 0 ? 1 : 0), l.Uc * l.PoidsUc);

    // Poids du BL : celui du pré-BL s'il est saisi, sinon lignes + palettes × poids d'une palette
    public static int PoidsTotal(SourcePreBl s) =>
        s.Poids > 0 ? s.Poids : (int)Math.Round(s.Lignes.Sum(l => ColisPoids(l).Poids) + s.Palettes * s.PoidsPalette, MidpointRounding.AwayFromZero);

    // Client facturé d'un élément : celui de sa première ligne qui en a un, sinon celui de la commande, sinon le client livré
    public static (int Gen, int Cli) ClientFacture(IEnumerable<LigneSourcePreBl> lignes, CommandeSourcePreBl c)
    {
        if (lignes.FirstOrDefault(l => l.CompteGeneralFacture != 0 && l.CompteClientFacture != 0) is { } l)
            return (l.CompteGeneralFacture, l.CompteClientFacture);
        if (c.CompteGeneralFacture != 0 && c.CompteClientFacture != 0) return (c.CompteGeneralFacture, c.CompteClientFacture);
        return (c.CompteGeneral, c.CompteClient);
    }

    // Éléments du BL : une commande (dossier et clé) par élément, dans l'ordre des commandes
    public static List<IGrouping<(int Dossier, string Cle), LigneSourcePreBl>> Elements(SourcePreBl s) =>
        [.. s.Lignes.GroupBy(l => (l.Dossier, l.Cle)).OrderBy(g => g.Key.Dossier).ThenBy(g => g.Key.Cle, StringComparer.Ordinal)];

    // Lignes dont la quantité des palettes diffère de la quantité prévue (le BL livre les palettes)
    public static List<string> Avertissements(SourcePreBl s) =>
        [.. s.Lignes.Where(l => l.Stocke && Quantite(l) != l.Quantite)
            .Select(l => $"Ligne {l.Numero} ({l.Article}) : {Quantite(l):#,##0} livrés (palettes) pour {l.Quantite:#,##0} prévus.")];
}
