using NetPrint.Modules.Logistique.Contrats;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Règles de la saisie d'un BL (cahier des charges lot 1, § 4.3, 6 et 7) : fonctions pures, testées sans base.
// =====================================================================
// Tarif du port (B400F) : prix des barèmes 1 à 3, unité de prix K au kg, C aux 100 kg, T à la tonne, F forfait
internal sealed record TarifPort(decimal Bareme1, decimal Bareme2, decimal Bareme3, string Unite);

internal static class ReglesSaisieBl
{
    public const int QuantiteMaximum = 9_999_999;
    public static readonly string[] TypesLivraison = ["M", "E", "A", "I", "P"];
    public static readonly string[] TypesPort = ["F", "D", "A", "E"];
    public static readonly string[] TypesEmballage = ["B", "C", "F", "P", "Q", "R", "S", "T"];

    // Sélection des lignes de commande (§ 4.3) : au moins une, même destinataire, même client facturé
    public static string? ControlerSelection(IReadOnlyList<LigneProposee> choisies)
    {
        if (choisies.Count == 0) return "Choisissez au moins une ligne à livrer.";
        var premiere = choisies[0];
        if (choisies.Any(l => !l.Adresse.MemeDestinataire(premiere.Adresse)))
            return "Les lignes choisies doivent avoir la même adresse de livraison.";
        if (choisies.Any(l => (l.CompteGeneralFacture, l.CompteClientFacture) != (premiere.CompteGeneralFacture, premiere.CompteClientFacture)))
            return "Les lignes choisies doivent avoir le même client facturé.";
        return null;
    }

    // Ajout à un BL existant : la ligne va au destinataire (raison sociale, code postal, ville) et au client facturé du BL
    public static bool VaAuBl(LigneProposee l, AdresseBl adresse, int generalFacture, int clientFacture) =>
        l.Adresse.RaisonSociale == adresse.RaisonSociale && l.Adresse.CodePostal == adresse.CodePostal && l.Adresse.Ville == adresse.Ville
        && (l.CompteGeneralFacture, l.CompteClientFacture) == (generalFacture, clientFacture);

    // Lignes cochées à l'ouverture : les lignes normales (pas à la demande) si elles vont toutes au même destinataire et au même
    // client facturé (même règle que la sélection), sinon aucune
    public static IReadOnlyList<int> LignesCocheesParDefaut(IReadOnlyList<LigneProposee> lignes)
    {
        var normales = lignes.Where(l => !l.ADemande).ToList();
        return normales.Count > 0 && ControlerSelection(normales) is null ? [.. normales.Select(l => l.NumeroLigne)] : [];
    }

    // Colisage (§ 6.2) : colis = quantité ÷ emballage, mécompte = reste, colis incomplet = 1 s'il y a un reste ; « S » sans
    // conditionnement : tout à 0
    public static void Coliser(LigneSaisieBl l)
    {
        if (l.Texte) return;
        if (l.TypeEmballage == "S" || l.EmballagePar <= 0)
        {
            (l.Colis, l.Mecompte, l.ColisIncomplet) = (0, 0, 0);
            if (l.TypeEmballage == "S") l.EmballagePar = 0;
            return;
        }
        l.Colis = l.Quantite / l.EmballagePar;
        l.Mecompte = l.Quantite % l.EmballagePar;
        l.ColisIncomplet = l.Mecompte > 0 ? 1 : 0;
    }

    // Totaux de l'élément (§ 7) : poids des lignes (+ palettes × poids d'une palette si EXP005.FEOP05, recalcul complet), colis
    public static (int Poids, int Colis) Totaux(SaisieBl s, bool poidsPalettes, int poidsPalette)
    {
        var lignes = s.Lignes.Where(l => !l.Texte).ToList();
        var poids = lignes.Sum(l => l.Poids) + (poidsPalettes ? s.Palettes * poidsPalette : 0);
        return ((int)Math.Min(99_999, Math.Round(poids, MidpointRounding.AwayFromZero)), lignes.Sum(l => l.Colis + l.ColisIncomplet));
    }

    // Contrôles de saisie sans base (§ 6.1, 6.2, 6.3) : erreurs bloquantes et avertissements
    public static (List<string> Erreurs, List<string> Avertissements) Controler(SaisieBl s)
    {
        var e = new List<string>();
        var w = new List<string>();
        var a = s.Adresse;
        var enlevement = s.TypePort == "E";

        if (!TypesPort.Contains(s.TypePort)) e.Add("Type de port : F franco, D dû, A avancé ou E enlèvement.");
        if (enlevement)
        {
            if (a.RaisonSociale == "") e.Add("Enlèvement : la raison sociale (nom du client) est obligatoire.");
        }
        else
        {
            if (a.RaisonSociale == "" || a.Rue1 == "") e.Add("Le destinataire doit avoir une raison sociale et une rue.");
            if (!TypesLivraison.Contains(s.TypeLivraison))
                e.Add("Mode de livraison : M messagerie, E express, A affrètement, I international ou P multi-palettes.");
            if (s.Transporteur <= 0) e.Add("Le transporteur est obligatoire.");
        }
        if (s.Bareme is < 0 or > 3) e.Add("Barème transport : 1, 2 ou 3.");
        if (s.FacturationAutomatique && s.CompteClientFacture == 0) e.Add("Facturation automatique : le client facturé est obligatoire.");
        if (s.Palettes is < 0 or > 99) e.Add("Nombre de palettes : de 0 à 99.");

        var articles = s.Lignes.Where(l => !l.Texte).ToList();
        if (!articles.Any(l => l.Quantite > 0)) e.Add("Au moins une ligne doit avoir une quantité.");
        foreach (var l in articles)
        {
            var nom = $"Article {l.Article}";
            if (l.Quantite < 0 || l.Quantite > QuantiteMaximum) e.Add($"{nom} : quantité de 0 à {QuantiteMaximum:#,##0}.");
            if (l.Quantite == 0) continue;
            if (l.QuantiteEnUc && l.EmballagePar > 0 && l.Quantite % l.EmballagePar != 0)
                e.Add($"{nom} : la quantité doit être un multiple de l'emballage ({l.EmballagePar:#,##0}).");
            if (l.GereEnStock && l.TypeEmballage == "") e.Add($"{nom} : le type d'emballage est obligatoire.");
            if (l.TypeEmballage != "" && !TypesEmballage.Contains(l.TypeEmballage)) e.Add($"{nom} : type d'emballage inconnu.");
            if (l.QuantiteEnUc && l.TypeEmballage == "S") e.Add($"{nom} : article exprimé en UC, un conditionnement est obligatoire.");
            if (!l.Fractionnable && l.ColisIncomplet > 0) e.Add($"{nom} : article non fractionnable, pas de colis incomplet.");
            if (l.GereEnStock && l.Reparti != l.Quantite)
                e.Add($"{nom} : {l.Reparti:#,##0} réparti(s) sur {l.Quantite:#,##0} — répartissez toute la quantité par lot.");
            if (l.Prises.Any(p => p.Quantite < 0)) e.Add($"{nom} : quantité négative sur un lot.");
            if (l.Quantite > l.Reste) w.Add($"{nom} : vous livrez {l.Quantite:#,##0}, le reste à livrer est de {l.Reste:#,##0}.");
        }
        return (e, w);
    }

    // Quantités prises sur chaque dépôt / lot / emplacement par tout le BL (les lignes du même article se cumulent, § 6.3)
    public static Dictionary<(string Article, string Indice, int Depot, string Lot, string Emplacement), int> Prises(SaisieBl s, LigneSaisieBl? sauf = null) =>
        s.Lignes.Where(l => l != sauf && l.GereEnStock)
            .SelectMany(l => l.Prises.Where(p => p.Quantite != 0).Select(p => (l.Article, l.Indice, p.Depot, p.Lot, p.Emplacement, p.Quantite)))
            .GroupBy(x => (x.Article, x.Indice, x.Depot, x.Lot, x.Emplacement))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantite));

    // Disponible affiché (§ 6.3) : stock − quantités prises sur la même position par les autres lignes du BL + (modification)
    // quantités enregistrées de l'élément, rendues au stock par la validation ; positions enregistrées sans stock restant ajoutées
    public static IReadOnlyList<PositionStock> Disponibles(SaisieBl s, LigneSaisieBl ligne, IReadOnlyList<PositionStock> positions)
    {
        var prises = Prises(s, ligne);
        var enregistrees = s.Lignes.Where(l => l.GereEnStock && l.Article == ligne.Article && l.Indice == ligne.Indice)
            .SelectMany(l => l.PrisesEnregistrees)
            .GroupBy(p => (p.Depot, p.Lot, p.Emplacement)).ToDictionary(g => g.Key, g => g.Sum(p => p.Quantite));
        var toutes = positions.ToList();
        foreach (var ((depot, lot, emplacement), _) in enregistrees)
            if (!toutes.Any(p => (p.Depot, p.Lot, p.Emplacement) == (depot, lot, emplacement)))
                toutes.Add(new PositionStock(depot, lot, emplacement, null, null, 0));
        return toutes.Select(p => p with
        {
            Stock = p.Stock - prises.GetValueOrDefault((ligne.Article, ligne.Indice, p.Depot, p.Lot, p.Emplacement))
                    + enregistrees.GetValueOrDefault((p.Depot, p.Lot, p.Emplacement)),
        }).ToList();
    }

    // Variation de stock de tout le BL par position (§ 6.3) : quantité enregistrée (modification) − quantité saisie
    public static Dictionary<(string TypeStock, string Article, string Indice, int Depot, string Lot, string Emplacement), int> Variations(SaisieBl s) =>
        s.Lignes.Where(l => l.GereEnStock)
            .SelectMany(l => l.Prises.Where(p => l.Quantite > 0 && p.Quantite > 0).Select(p => (l, p, Signe: -1))
                .Concat(l.PrisesEnregistrees.Select(p => (l, p, Signe: 1))))
            .GroupBy(x => (x.l.TypeStock, x.l.Article, x.l.Indice, x.p.Depot, x.p.Lot, x.p.Emplacement))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Signe * x.p.Quantite));

    // Un seul lot disponible : la quantité y est prise d'office (dans la limite du disponible)
    public static void PrendreSiUnSeulLot(LigneSaisieBl l, IReadOnlyList<PositionStock> disponibles)
    {
        if (!l.GereEnStock || l.Prises.Count > 0 || disponibles.Count != 1 || l.Quantite <= 0) return;
        var p = disponibles[0];
        if (p.Stock <= 0) return;
        l.Prises.Add(new PriseLot { Depot = p.Depot, Lot = p.Lot, Emplacement = p.Emplacement, Quantite = Math.Min(l.Quantite, p.Stock) });
    }

    // ---------- Port (§ 7, SPCPOR d'EXP010) ----------

    // Tranche de poids : n° (1 à 20) de la première tranche B300F ≥ poids (plafonné à 99 999), 0 si aucune
    public static int Tranche(int poids, IReadOnlyList<int> tranches)
    {
        var p = Math.Min(poids, 99_999);
        for (var i = 0; i < tranches.Count; i++)
            if (tranches[i] >= p && tranches[i] > 0) return i + 1;
        return 0;
    }

    // Port = poids ÷ unité de prix (arrondi à 6 décimales) × prix du barème (arrondi au centime) + frais de timbre ; prix nul :
    // pas de port. Barème autre que 1, 2, 3 : barème 1 (comme le RPG ; le cahier des charges disait « port 0 »).
    public static decimal Port(int poids, TarifPort tarif, int bareme, decimal timbre)
    {
        var prix = bareme switch { 2 => tarif.Bareme2, 3 => tarif.Bareme3, _ => tarif.Bareme1 };
        if (prix == 0) return 0;
        decimal unite = tarif.Unite switch { "C" => 100, "T" => 1000, "F" => poids, _ => 1 };
        if (unite == 0) unite = 1;
        var quantite = Math.Round(poids / unite, 6, MidpointRounding.AwayFromZero);
        return Math.Round(quantite * prix, 2, MidpointRounding.AwayFromZero) + timbre;
    }

    // Répartition d'un coût entre les éléments au poids (au centime, le reste sur le dernier) ; poids nuls : parts égales
    public static IReadOnlyList<decimal> RepartirAuPoids(decimal total, IReadOnlyList<int> poids)
    {
        if (poids.Count == 0) return [];
        var somme = poids.Sum();
        var parts = poids.Select(p => Math.Round(somme == 0 ? total / poids.Count : total * p / somme, 2, MidpointRounding.AwayFromZero)).ToList();
        parts[^1] += total - parts.Sum();
        return parts;
    }

    // Textes d'une ligne de commande (NEG100T) en lignes de texte du BL, par trois (DDES, DDES2, DDES3)
    public static IEnumerable<LigneSaisieBl> LignesDeTexte(IReadOnlyList<string> textes)
    {
        for (var i = 0; i < textes.Count; i += 3)
            yield return new LigneSaisieBl
            {
                Texte = true,
                Designation = textes[i],
                Designation2 = i + 1 < textes.Count ? textes[i + 1] : "",
                Designation3 = i + 2 < textes.Count ? textes[i + 2] : "",
                Etat = "",
            };
    }
}
