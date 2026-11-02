using NetPrint.Modules.Logistique.Contrats;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Génération des BL d'un OPR (FEN_CreationBL), sans base : situation de chaque ligne, découpage des palettes conditionnées
// (et des frais) en BL / éléments / lignes / lots selon le mode du client, conditionnement d'une ligne de BL.
// Corrigé par rapport au WinDev : client facturé et rupture sur la ligne de commande avant le lot (plus de lignes en double),
// n° d'élément repris à 1 à chaque BL, frais avec leur propre quantité, mode 3 (un BL par article) codé, état de livraison
// calculé sur la quantité livrée cumulée.
// =====================================================================

// Une palette conditionnée (statut 4) d'une ligne d'OPR, ou une ligne de frais (Frais : sans palette, quantité de l'OPR)
internal sealed record PaletteBlOpr(
    int LigneOpr, int Dossier, string Cle, int LigneCommande, string Article, string Indice, int Quantite, bool Frais, string Sscc, string Lot,
    string Emplacement, int Depot, int Colisage, decimal PoidsCarton, int MatiereCarton, int CompteGeneralFacture, int CompteClientFacture,
    string Enseigne);

internal sealed record LotBlOpr(string Lot, string Emplacement, int Depot, int Quantite, IReadOnlyList<string> Palettes);

internal sealed record LigneBlOpr(int LigneCommande, string Article, string Indice, int Quantite, bool Frais, IReadOnlyList<int> LignesOpr,
    IReadOnlyList<int> QuantitesPalettes, IReadOnlyList<LotBlOpr> Lots);

internal sealed record ElementBlOpr(int Dossier, string Cle, int CompteGeneralFacture, int CompteClientFacture, IReadOnlyList<LigneBlOpr> Lignes,
    IReadOnlyList<(int Colisage, decimal Poids, int Matiere)> Cartons);

internal sealed record BlPlanifieOpr(string Enseigne, IReadOnlyList<ElementBlOpr> Elements)
{
    public IEnumerable<(int Colisage, decimal Poids, int Matiere)> Cartons => Elements.SelectMany(e => e.Cartons).DistinctBy(c => c.Colisage);
    public int Colis => Cartons.Count(c => c.Colisage != 0);
    public int PoidsCartons => (int)Math.Ceiling(Cartons.Sum(c => c.Poids));
}

internal static class ReglesGenerationBlOpr
{
    public static string LibelleMode(string mode) => mode switch
    {
        "1" => "un BL par colis",
        "2" => "un BL par commande",
        "3" => "un BL par article",
        "4" => "un BL pour tout l'OPR",
        _ => "",
    };

    // État d'une ligne (FEN_BonLivraison), corrigé : chaque ligne jugée seule ; Bloquante = empêche la génération des BL
    public static (string Etat, bool Bloquante) Etat(bool frais, int quantite, int preparee, int conditionnee, bool cartonNonClos)
    {
        if (frais) return ("Frais", false);
        if (preparee == 0 && conditionnee == 0) return ("À sortir", true);
        if (conditionnee < preparee) return (preparee > quantite ? "En cours (qté supérieure)" : "En cours", true);
        if (cartonNonClos) return ("Conditionnement en cours", true);
        if (conditionnee > quantite) return ("OK qté supérieure", false);
        if (conditionnee < quantite) return ("OK incomplet", false);
        return ("OK", false);
    }

    // BL / éléments / lignes / lots. Clé du BL : 1 enseigne + colis, 2 enseigne + commande + client facturé, 3 enseigne + article,
    // 4 enseigne ; élément : commande + client facturé ; ligne : ligne de commande ; lot : lot + emplacement + dépôt
    public static List<BlPlanifieOpr> Planifier(IReadOnlyList<PaletteBlOpr> palettes, string mode)
    {
        // Un carton (colisage) partagé par plusieurs éléments ou BL n'est compté, rattaché et sorti du stock qu'une fois : il va
        // au premier élément où il apparaît
        var vus = new HashSet<int>();
        return [.. Decouper(palettes, mode).Select(bl => bl with
        {
            Elements = [.. bl.Elements.Select(e => e with { Cartons = [.. e.Cartons.Where(c => c.Colisage == 0 || vus.Add(c.Colisage))] })],
        })];
    }

    private static List<BlPlanifieOpr> Decouper(IReadOnlyList<PaletteBlOpr> palettes, string mode)
    {
        object CleBl(PaletteBlOpr p) => mode switch
        {
            "1" => (p.Enseigne, p.Colisage, 0, "", 0, 0),
            "2" => (p.Enseigne, 0, p.Dossier, p.Cle, p.CompteGeneralFacture, p.CompteClientFacture),
            "3" => (p.Enseigne, 0, 0, $"{p.Article}|{p.Indice}", 0, 0),
            _ => (p.Enseigne, 0, 0, "", 0, 0),
        };
        return [.. palettes
            .OrderBy(p => p.Enseigne, StringComparer.Ordinal).ThenBy(p => mode == "1" ? p.Colisage : 0)
            .ThenBy(p => mode == "3" ? p.Article : "", StringComparer.Ordinal).ThenBy(p => mode == "3" ? p.Indice : "", StringComparer.Ordinal)
            .ThenBy(p => p.Dossier).ThenBy(p => p.Cle, StringComparer.Ordinal).ThenBy(p => p.CompteGeneralFacture).ThenBy(p => p.CompteClientFacture)
            .ThenBy(p => p.LigneCommande).ThenBy(p => p.Lot, StringComparer.Ordinal).ThenBy(p => p.Emplacement, StringComparer.Ordinal)
            .GroupBy(CleBl)
            .Select(bl => new BlPlanifieOpr(bl.First().Enseigne, [.. bl
                .GroupBy(p => (p.Dossier, p.Cle, p.CompteGeneralFacture, p.CompteClientFacture))
                .Select(el => new ElementBlOpr(el.Key.Dossier, el.Key.Cle, el.Key.CompteGeneralFacture, el.Key.CompteClientFacture, [.. el
                    .GroupBy(p => p.LigneCommande)
                    .Select(li => new LigneBlOpr(li.Key, li.First().Article, li.First().Indice, li.Sum(p => p.Quantite), li.All(p => p.Frais),
                        [.. li.Select(p => p.LigneOpr).Distinct()], [.. li.Where(p => !p.Frais).Select(p => p.Quantite)],
                        [.. li.Where(p => !p.Frais).GroupBy(p => (p.Lot, p.Emplacement, p.Depot))
                            .Select(lo => new LotBlOpr(lo.Key.Lot, lo.Key.Emplacement, lo.Key.Depot, lo.Sum(p => p.Quantite), [.. lo.Select(p => p.Sscc)]))]))],
                    [.. el.Where(p => !p.Frais).Select(p => (p.Colisage, p.PoidsCarton, p.MatiereCarton)).DistinctBy(c => c.Colisage)]))]))];
    }

    // Conditionnement d'une ligne de BL (AjoutEXP015) : quantité par palette la plus fréquente = emballage, nombre de ces
    // palettes = colis, les autres palettes en mécompte (colis et exemplaires) ; rien sans emballage (« S ») ou pour un frais
    public static (int Colis, int EmballagePar, int ColisMecompte, int ExemplairesMecompte) Conditionnement(IReadOnlyList<int> quantitesPalettes,
        string typeEmballage)
    {
        if (typeEmballage == "S" || quantitesPalettes.Count == 0) return (0, 0, 0, 0);
        var plusFrequente = quantitesPalettes.GroupBy(q => q).OrderByDescending(g => g.Count()).ThenBy(g => quantitesPalettes.ToList().IndexOf(g.Key)).First();
        var autres = quantitesPalettes.Where(q => q != plusFrequente.Key).ToList();
        return (plusFrequente.Count(), plusFrequente.Key, autres.Count, autres.Sum());
    }

    // État de livraison de la ligne de BL : « T » si la ligne de commande est entièrement livrée avec ce BL, « A » pour un acompte
    public static string EtatLivraison(int dejaLivre, int livre, int commande, bool acompte) =>
        dejaLivre + livre >= commande ? "T" : acompte ? "A" : "";
}
