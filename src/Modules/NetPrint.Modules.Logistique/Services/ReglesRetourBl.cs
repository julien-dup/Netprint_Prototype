using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Règles du retour de marchandises (EXP010S R28-R30, ConsultationBL FEN_RetourBLSelection / FEN_RetourBLSaisie), sans base
// =====================================================================
internal static class ReglesRetourBl
{
    public const int LongueurMotif = 40;

    // Pourquoi un élément ne peut pas être retourné ("" s'il le peut) : seul un élément complet (pas un retour, un divers, une
    // sous-traitance ou un BL vierge), d'un dossier non sous-traité, avec des quantités livrées
    public static string Refus(ElementOrigine e) => e.Etat switch
    {
        "R" => "C'est déjà un retour.",
        "D" => "BL divers : pas de retour de marchandises.",
        "S" => "BL de sous-traitance : pas de retour de marchandises.",
        "V" => "BL vierge : pas de retour de marchandises.",
        not "C" => "Élément non retournable.",
        _ when e.SousTraite && e.TypeDossier is "3" or "4" => "Dossier sous-traité : opération impossible.",
        _ when e.Fabrication && e.QuantiteLivree <= 0 => "Aucune quantité livrée.",
        _ when !e.Fabrication && e.Lignes.Count == 0 => "Aucune ligne d'article.",
        _ => "",
    };

    // Valeurs proposées (R28) : client facturé du BL d'origine (premier élément qui en a un), sinon le client du BL
    public static PreparationRetour Preparation(OrigineRetour o, IReadOnlySet<int> visibles)
    {
        var facture = o.Elements.FirstOrDefault(e => e.CompteGeneralFacture != 0);
        var elements = o.Elements.Where(e => visibles.Contains(e.Element)).Select(e => new ElementRetour(e.Element, e.Dossier, e.Cle, e.Designation,
            e.Fabrication, e.QuantiteLivree, e.DejaRetourne,
            [.. e.Lignes.SelectMany(l => l.Lots.Select(x => new LotRetour(l.LigneBl, l.LigneCommande, l.Article, l.Indice, l.Designation, x.Lot,
                x.Emplacement, x.Quantite, x.DejaRetourne, [.. x.Palettes.Select(p => new PaletteRetour(p.Sscc, p.Quantite, p.Retournee))])))],
            Refus(e))).ToList();
        return new PreparationRetour(o.NumeroBl, o.Date, o.CompteGeneral, o.CompteClient, o.NomClient,
            facture?.CompteGeneralFacture ?? o.CompteGeneral, facture?.CompteClientFacture ?? o.CompteClient, o.Transporteur, elements);
    }

    // Contrôles de la saisie (R29 et ConsultationBL), sans la base ; visibles : éléments vus par le profil
    public static List<string> Controler(OrigineRetour o, SaisieRetour s, IReadOnlySet<int> visibles, DateOnly aujourdhui)
    {
        var erreurs = new List<string>();
        if (s.Date > aujourdhui) erreurs.Add("La date du retour ne peut pas être postérieure à aujourd'hui.");
        if (o.Date is { } d && s.Date < d) erreurs.Add($"La date du retour est antérieure à la date du BL ({d:dd/MM/yyyy}).");
        if (s.Motif.Count > 3) erreurs.Add("Le motif tient sur 3 lignes.");
        if (s.Motif.Any(m => (m ?? "").Trim().Length > LongueurMotif)) erreurs.Add($"Chaque ligne du motif fait {LongueurMotif} caractères au plus.");
        if (s.Colis < 0 || s.Poids < 0 || s.Transporteur < 0) erreurs.Add("Colis, poids et transporteur ne peuvent pas être négatifs.");
        if (s.FacturationAutomatique && (s.CompteGeneralFacture <= 0 || s.CompteClientFacture <= 0))
            erreurs.Add("Facturation automatique : indiquez le client facturé.");
        if (s.Quantites.Any(q => q.Quantite < 0)) erreurs.Add("Les quantités retournées sont positives.");

        var quantites = s.Quantites.Where(q => q.Quantite > 0).ToList();
        if (quantites.Count == 0)
        {
            erreurs.Add("Indiquez au moins une quantité retournée.");
            return erreurs;
        }
        foreach (var parElement in quantites.GroupBy(q => q.Element))
        {
            var e = o.Elements.FirstOrDefault(x => x.Element == parElement.Key);
            if (e is null || !visibles.Contains(e.Element))
            {
                erreurs.Add($"L'élément {parElement.Key} n'existe pas sur ce BL.");
                continue;
            }
            if (Refus(e) is { Length: > 0 } refus)
            {
                erreurs.Add($"Élément {e.Element} : {refus}");
                continue;
            }
            if (e.Fabrication)
            {
                var total = parElement.Sum(q => q.Quantite);
                if (parElement.Any(q => q.LigneBl != 0 || q.Sscc != ""))
                    erreurs.Add($"Élément {e.Element} (fabrication) : le retour se fait sur la quantité de l'élément.");
                else if (total > e.QuantiteLivree - e.DejaRetourne)
                    erreurs.Add($"Élément {e.Element} : la quantité retournée ({total:#,##0}) dépasse la quantité livrée moins les retours déjà faits " +
                                $"({Math.Max(0, e.QuantiteLivree - e.DejaRetourne):#,##0}).");
                continue;
            }
            foreach (var parLot in parElement.GroupBy(q => (q.LigneBl, q.Lot)))
            {
                var ligne = e.Lignes.FirstOrDefault(l => l.LigneBl == parLot.Key.LigneBl);
                var lot = ligne?.Lots.FirstOrDefault(x => x.Lot == parLot.Key.Lot);
                if (ligne is null || lot is null)
                {
                    erreurs.Add($"Élément {e.Element}, ligne {parLot.Key.LigneBl} : lot {parLot.Key.Lot} non livré par ce BL.");
                    continue;
                }
                if (lot.Palettes.Count > 0)
                {
                    foreach (var q in parLot)
                    {
                        var palette = lot.Palettes.FirstOrDefault(p => p.Sscc == q.Sscc);
                        if (palette is null) erreurs.Add($"Article {ligne.Article}, lot {lot.Lot} : choisissez les palettes retournées.");
                        else if (palette.Retournee) erreurs.Add($"La palette {palette.Sscc} a déjà été retournée.");
                        else if (q.Quantite > palette.Quantite)
                            erreurs.Add($"Palette {palette.Sscc} : la quantité retournée ne doit pas être supérieure à la quantité livrée ({palette.Quantite:#,##0}).");
                    }
                    foreach (var doublon in parLot.GroupBy(q => q.Sscc).Where(g => g.Count() > 1))
                        erreurs.Add($"La palette {doublon.Key} est saisie deux fois.");
                }
                else
                {
                    var total = parLot.Sum(q => q.Quantite);
                    if (parLot.Any(q => q.Sscc != "")) erreurs.Add($"Article {ligne.Article}, lot {lot.Lot} : ce lot n'a pas été livré en palettes.");
                    else if (total > lot.Quantite - lot.DejaRetourne)
                        erreurs.Add($"Article {ligne.Article}{(lot.Lot == "" ? "" : $", lot {lot.Lot}")} : la quantité retournée ({total:#,##0}) dépasse " +
                                    $"la quantité livrée moins les retours déjà faits ({Math.Max(0, lot.Quantite - lot.DejaRetourne):#,##0}).");
                }
            }
        }
        return erreurs;
    }

    // Quantités saisies réparties par élément / ligne / lot / palette (saisie contrôlée), sans emplacement (voir AvecEmplacement)
    public static List<ElementAEcrire> Repartir(OrigineRetour o, SaisieRetour s)
    {
        var quantites = s.Quantites.Where(q => q.Quantite > 0).ToList();
        var resultat = new List<ElementAEcrire>();
        foreach (var e in o.Elements)
        {
            var duSaisi = quantites.Where(q => q.Element == e.Element).ToList();
            if (duSaisi.Count == 0) continue;
            if (e.Fabrication)
            {
                resultat.Add(new ElementAEcrire(e, duSaisi.Sum(q => q.Quantite), []));
                continue;
            }
            var lignes = new List<LigneAEcrire>();
            foreach (var l in e.Lignes)
            {
                var lots = new List<LotAEcrire>();
                foreach (var x in l.Lots)
                {
                    var saisis = duSaisi.Where(q => q.LigneBl == l.LigneBl && q.Lot == x.Lot).ToList();
                    if (saisis.Count == 0) continue;
                    var palettes = x.Palettes.Count == 0 ? []
                        : saisis.Select(q => (Palette: x.Palettes.First(p => p.Sscc == q.Sscc), q.Quantite)).ToList();
                    lots.Add(new LotAEcrire(x, saisis.Sum(q => q.Quantite), "", palettes));
                }
                if (lots.Count > 0) lignes.Add(new LigneAEcrire(l, lots));
            }
            if (lignes.Count > 0) resultat.Add(new ElementAEcrire(e, 0, lignes));
        }
        return resultat;
    }

    // Emplacement où entre chaque lot retourné (emplacement de retour si son type de stock gère les emplacements)
    public static List<ElementAEcrire> AvecEmplacement(IEnumerable<ElementAEcrire> elements, Func<LotOrigine, string> emplacement) =>
        [.. elements.Select(e => e with { Lignes = [.. e.Lignes.Select(l => l with { Lots = [.. l.Lots.Select(x => x with { Emplacement = emplacement(x.Origine) })] })] })];
}
