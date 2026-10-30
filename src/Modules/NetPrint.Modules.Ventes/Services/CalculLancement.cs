using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Calculs de l'écran de lancement (NEG515 SPCHGT / SPTOT9, l. 1507-2017) : une ligne par OF de l'article (avec le stock
// de son lot), puis une ligne par lot sans lien, sinon une ligne vide. Fonction pure : les données viennent de
// LancementCommandeDb2. Règles reprises (rapport NEG515 § 2) et écarts :
//   - lien retenu : non soldé, ou affectation de stock (S) d'une commande qui a encore des lignes de l'article à livrer ;
//   - disponible = à fabriquer + stock + surplus − affecté (toutes commandes) + livré pour cette commande (borné à
//     l'affecté) ; un OF au disponible négatif sans rien d'affecté à cette commande est masqué, et son lot n'est pas
//     reproposé comme stock libre (arbitrage « discussion avec David », l. 1809) ;
//   - ÉCART : les lots des dépôts de type « A » comptent en surplus aussi pour les lots sans lien (NEG515 les oubliait) ;
//   - ÉCART : lignes dans l'ordre des OF puis des lots, l'affecté total de l'article sur l'article (pas sur la 1re ligne) ;
//   - ÉCART : un article est regroupé par article et indice (NEG515 séparait aussi par désignation et comptait alors deux
//     fois les mêmes OF) ;
//   - ÉCART : le livré de la commande réintégré au disponible est borné à l'affecté de la commande (NEG515 : à l'affecté de
//     toutes les commandes, ce qui pouvait montrer disponible un stock réservé à d'autres clients).
// =====================================================================
internal static class CalculLancement
{
    internal sealed record ArticleCommande(string Article, string Indice, string Designation, string ReferenceImprime, bool Stocke,
        int Commande);

    // Lien AB0020 de l'article (toutes commandes). EncoreALivrer : la commande du lien a encore des lignes de l'article
    // non soldées (sinon son affectation de stock ne compte plus, NEG515 spSelectAff)
    internal sealed record Lien(NumeroOf Of, int CommandeNumero, string CommandeCle, int Lance, decimal Fabrique, int Affecte, string Type,
        bool Solde, bool EncoreALivrer);

    // Lot de l'article : n° de lot = n° d'OF (5) + clé ; type du dépôt (M stock, S / A surplus)
    internal sealed record Lot(string Numero, string TypeDepot, int Quantite);

    internal sealed record Livraison(string Lot, int Total, int PourLaCommande);

    internal sealed record Donnees(
        IReadOnlyList<ArticleCommande> Articles, IReadOnlyDictionary<(string, string), IReadOnlyList<Lien>> Liens,
        IReadOnlyDictionary<(string, string), IReadOnlyList<Lot>> Lots, IReadOnlyDictionary<(string, string), IReadOnlyList<Livraison>> Livraisons,
        IReadOnlySet<NumeroOf> OfAOuvrir, IReadOnlySet<(NumeroOf, string)> Bloques, IReadOnlyDictionary<(NumeroOf, string), EtatBag> Bags);

    // Clé article + indice des blocages et des BAG (« * » : toutes les références du dossier)
    internal static string Reference(string article, string indice) => $"{article}|{indice}";
    internal const string ToutesReferences = "*";

    // N° de lot d'un produit fini : n° d'OF sur 5 + clé (sans blanc de fin si la clé est vide)
    internal static string NumeroLot(NumeroOf of) => $"{of.Numero:00000}{of.Cle}".TrimEnd();

    internal static List<ArticleALancer> Calculer(Donnees d, CleCommande commande)
    {
        var resultat = new List<ArticleALancer>();
        foreach (var a in d.Articles)
        {
            var cle = (a.Article, a.Indice);
            var liens = d.Liens.GetValueOrDefault(cle) ?? [];
            var lots = d.Lots.GetValueOrDefault(cle) ?? [];
            var livraisons = (d.Livraisons.GetValueOrDefault(cle) ?? []).ToDictionary(l => l.Lot);
            bool DeLaCommande(Lien l) => l.CommandeNumero == commande.Numero && l.CommandeCle == commande.Cle;
            var affecteArticle = liens.Where(DeLaCommande).Sum(l => l.Affecte);

            var sources = new List<SourceArticle>();
            var lotsVus = new HashSet<string>();
            foreach (var parOf in liens.GroupBy(l => l.Of).OrderBy(g => g.Key.Numero).ThenBy(g => g.Key.Cle))
            {
                var of = parOf.Key;
                var retenus = parOf.Where(l => !l.Solde || l.Type == "S" && l.EncoreALivrer).ToList();
                var aFabriquer = Math.Max(0, retenus.Sum(l => l.Lance - (int)l.Fabrique));
                var affecteTotal = retenus.Sum(l => l.Affecte);
                var affecteCommande = parOf.Where(DeLaCommande).Sum(l => l.Affecte);
                var numeroLot = NumeroLot(of);
                var (stock, surplus) = Stock(lots.Where(l => l.Numero == numeroLot));
                var livre = livraisons.GetValueOrDefault(numeroLot);
                var dispo = aFabriquer + stock + surplus - affecteTotal;
                if (livre is { PourLaCommande: > 0 }) dispo += Math.Min(livre.PourLaCommande, affecteCommande);
                lotsVus.Add(numeroLot);   // le lot de l'OF n'est jamais reproposé comme stock libre (arbitrage l. 1809)
                if (dispo < 0 && affecteCommande == 0) continue;
                if (dispo == 0 && affecteCommande == 0) continue;
                sources.Add(new SourceArticle(
                    d.OfAOuvrir.Contains(of) ? NatureSource.OfAOuvrir : NatureSource.OfOuvert, of, affecteCommande, aFabriquer,
                    retenus.Sum(l => l.Lance), (int)retenus.Sum(l => l.Fabrique), stock, surplus, affecteTotal, Math.Max(0, dispo),
                    // Nature de la ligne : le dernier lien de cette commande sur l'OF (sinon de l'OF)
                    livre?.Total ?? 0, livre?.PourLaCommande ?? 0, (parOf.LastOrDefault(DeLaCommande) ?? parOf.Last()).Type == "S",
                    Bloque(d, of, a), d.Bags.GetValueOrDefault((of, Reference(a.Article, a.Indice)))));
            }

            // Lots de l'article sans lien (stock libre)
            foreach (var parLot in lots.Where(l => l.Quantite > 0 && !lotsVus.Contains(l.Numero)).GroupBy(l => l.Numero).OrderBy(g => g.Key))
            {
                var (stock, surplus) = Stock(parLot);
                if (stock + surplus <= 0) continue;
                var of = OfDuLot(parLot.Key);
                var livre = livraisons.GetValueOrDefault(parLot.Key);
                sources.Add(new SourceArticle(NatureSource.Lot, of, 0, 0, 0, 0, stock, surplus, 0, stock + surplus,
                    livre?.Total ?? 0, livre?.PourLaCommande ?? 0, false, Bloque(d, of, a), null));
            }
            resultat.Add(new ArticleALancer(a.Article, a.Indice, a.Designation, a.ReferenceImprime, a.Stocke, a.Commande, affecteArticle, sources));
        }
        return resultat;
    }

    private static bool Bloque(Donnees d, NumeroOf of, ArticleCommande a) =>
        d.Bloques.Contains((of, Reference(a.Article, a.Indice))) || d.Bloques.Contains((of, ToutesReferences));

    private static (int Stock, int Surplus) Stock(IEnumerable<Lot> lots)
    {
        int stock = 0, surplus = 0;
        foreach (var l in lots.Where(l => l.Quantite > 0))
            if (l.TypeDepot == "M") stock += l.Quantite;
            else if (l.TypeDepot is "S" or "A") surplus += l.Quantite;
        return (stock, surplus);
    }

    // Lot « nnnnnc » → OF n° nnnnn clé c (lot d'un produit fini) ; un autre format donne le lot tel quel en clé
    internal static NumeroOf OfDuLot(string lot) =>
        lot.Length is 5 or 6 && int.TryParse(lot[..5], out var n) ? new NumeroOf(n, lot[5..]) : new NumeroOf(0, lot);
}
