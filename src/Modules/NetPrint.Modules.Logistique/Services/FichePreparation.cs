using System.Globalization;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Fiche de préparation (EXP078P) : lignes lues → une fiche par jour de livraison × client × adresse (rupture N8), commandes
// (rupture N7) et lignes. Fonctions pures, testées sans base ni PDF.
// =====================================================================
internal sealed record FichePreparation(
    DateOnly? Date, string Client, bool ClientBloque, AdresseBl Adresse, IReadOnlyList<string> Commentaires, string Transporteur,
    IReadOnlyList<CommandePreparation> Commandes, decimal Poids, int Colis, int Palettes);

internal sealed record CommandePreparation(
    string Numero, string Contact, string ReferenceCommande, DateOnly? DateCommande, bool Enlevement, IReadOnlyList<LigneFichePreparation> Lignes);

internal sealed record LigneFichePreparation(
    string Article, string Designation, int Quantite, IReadOnlyList<string> Conditionnement, IReadOnlyList<string> Textes,
    IReadOnlyList<string> Lots, string EnFabrication, string CodeBarre);

internal static class Preparations
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static string N(int v) => v.ToString("#,##0", Fr);
    internal const int LotsMaximum = 10;

    public static IReadOnlyList<FichePreparation> Construire(IReadOnlyList<LignePreparation> lignes, IReadOnlyList<LotPreparation> lots,
        IReadOnlyDictionary<(string, string), int> enFabrication)
    {
        var lotsParArticle = lots.GroupBy(l => (l.Article, l.Indice)).ToDictionary(g => g.Key, g => g.ToList());
        var fiches = new List<FichePreparation>();
        foreach (var fiche in lignes.GroupBy(l => (l.DateLivraison, l.CompteGeneral, l.CompteClient, l.Adresse.Numero, l.Adresse.RaisonSociale,
                     l.Adresse.CodePostal, l.Adresse.Ville)))
        {
            var p = fiche.First();
            decimal poids = 0;
            int colis = 0, palettes = 0;
            var commandes = new List<CommandePreparation>();
            foreach (var cde in fiche.GroupBy(l => (l.Dossier, l.Cle)))
            {
                var c = cde.First();
                var lignesCde = new List<LigneFichePreparation>();
                foreach (var l in cde)
                {
                    var (uc, cartons, pal, pds) = ReglesLivraisons.Conditionnement(l.Reste, l.Emballage, l.UcParCarton, l.UcParPalette, l.PoidsUc, l.TypeEmballage);
                    poids += pds;
                    colis += l.UcParCarton > 0 && l.TypeEmballage != "C" ? cartons : uc;
                    palettes += pal;
                    var cond = new List<string>();
                    if (l.Emballage > 0) cond.Add($"{(l.TypeEmballage == "C" ? "Carton" : "Paquet")} de {N(l.Emballage)} ex. : {N(uc)} UC");
                    if (uc > 0 && l.UcParCarton > 0 && l.TypeEmballage != "C") cond.Add($"Carton de {N(l.UcParCarton)} UC : {N(cartons)} carton{(cartons > 1 ? "s" : "")}");
                    if (pal > 0) cond.Add($"Mise sur {pal} palette{(pal > 1 ? "s" : "")}");
                    var textes = new List<string>(l.Textes);
                    if (l.MaxiLivrable > 0) textes.Add($"Maxi livrable : {N(l.MaxiLivrable)}");
                    if (l.Norme != "") textes.Add($"Norme : {l.Norme}");
                    var article = l.Indice == "" ? l.Article : $"{l.Article}/{l.Indice}";
                    if (l.ReferenceImprime != "") article += $" · {l.ReferenceImprime}";
                    if (l.Appel != "") article += $" · appel {l.Appel}";
                    // 10 lots au plus (les plus anciens d'abord) : une ligne de la fiche ne se coupe pas d'une page à l'autre
                    var tousLots = lotsParArticle.GetValueOrDefault((l.Article, l.Indice)) ?? [];
                    var lotsArt = tousLots.Take(LotsMaximum)
                        .Select(x => $"Lot {x.Lot} : {N(x.Quantite)}" + (x.DateLimite is { } d ? $" (DLU {d:dd/MM/yyyy})" : "")).ToList();
                    if (tousLots.Count > LotsMaximum)
                        lotsArt.Add($"… et {tousLots.Count - LotsMaximum} autres lots ({N(tousLots.Skip(LotsMaximum).Sum(x => x.Quantite))})");
                    var fab = enFabrication.GetValueOrDefault((l.Article, l.Indice));
                    lignesCde.Add(new LigneFichePreparation(article, l.Designation, l.Reste, cond, textes,
                        lotsArt.Count == 0 ? ["Pas de stock au dépôt principal"] : lotsArt, fab > 0 ? $"En fabrication : {N(fab)}" : "",
                        CodeBarresPossible(l.Article) ? l.Article : ""));
                }
                commandes.Add(new CommandePreparation($"{c.Dossier:00000}/{c.Cle}", c.Contact, c.ReferenceCommande, c.DateCommande, c.Enlevement, lignesCde));
            }
            fiches.Add(new FichePreparation(p.DateLivraison, $"{p.NomClient} ({p.CompteGeneral} {p.CompteClient})", fiche.Any(l => l.ClientBloque),
                p.Adresse, p.Commentaires, p.Transporteur, commandes, poids, colis, palettes));
        }
        return fiches;
    }

    private static bool CodeBarresPossible(string article) => BonLivraison.Code39Possible(article);
}
