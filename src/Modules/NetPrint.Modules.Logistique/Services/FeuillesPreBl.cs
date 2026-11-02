using NetPrint.Modules.Logistique.Contrats;

namespace NetPrint.Modules.Logistique.Services;

// En-tête commun aux feuilles d'un pré-BL : départ, transporteur, adresse, client ; poids saisi (EXP509.HPDSTT)
internal sealed record EnTeteFeuillePreBl(int Numero, DateOnly? Depart, string Transporteur, AdresseBl Adresse, int CompteGeneral, int CompteClient,
    string NomClient, int PoidsSaisi, bool StockPaye);

// Feuille de préparation (ETAT_FeuilleKimmel) : une ligne par palette choisie ; Reste : quantité de la palette moins ce que
// les autres pré-BL non livrés en prennent ; Groupe : palette groupée (PROD_ENTITE.PALETTE_GROUP) ; Picking : quantité prise
// si la palette est entamée (0 sinon)
internal sealed record PalettePreparation(int Depot, string NomDepot, string Sscc, string Article, string Indice, string RefImprime, string Designation,
    string Emplacement, int Reste, string Groupe, int Picking, string Client, string Observation);

internal sealed record FeuillePreparation(string Societe, EnTeteFeuillePreBl EnTete, IReadOnlyList<PalettePreparation> Palettes, bool CodesBarres);

// Feuille camion : une ligne de lot (article stocké) ou une ligne de frais (article non stocké, SPF010.AOPT7 = O)
internal sealed record LotCamion(int PreBl, string Article, string Indice, string RefImprime, string Designation, int Depot, string NomDepot,
    bool StockPaye, string LibellePalettes, int PaletteMixte, int Quantite, int Palettes, int EmballagePar, int UcParPalette, string TypeColis,
    decimal PoidsUc, int Dossier, string Cle, string RefCommande, bool Frais, decimal Montant, decimal PrixUnitaire);

internal sealed record LigneCamion(int PreBl, string Article, string RefImprime, string Designation, string Depot, bool StockPaye,
    string NumerosPalettes, int Quantite, int Palettes, int Poids, int ExParPalette, int ColisParPalette, int ExParColis, string TypeColis,
    string Commande, string RefCommande, decimal Montant, decimal PrixUnitaire);

internal sealed record TotalCamion(int PreBl, int Palettes, int Poids, int PoidsSaisi, decimal Montant);

internal sealed record FeuilleCamion(string Societe, EnTeteFeuillePreBl EnTete, IReadOnlyList<LigneCamion> Lignes, IReadOnlyList<TotalCamion> Totaux,
    IReadOnlyList<(string Depot, int Palettes)> PalettesParDepot, int CumulPalettes, int CumulPoids, bool Valorisee);

// =====================================================================
// Règles des feuilles du pré-BL, sans base (docs/logistique/fiches/Editions-PreBL.md § 2 et 3). Écarts voulus avec le WinDev :
// un seul calcul de poids (colis × poids d'une UC + palettes × poids d'une palette, comme l'édition et la transformation) ;
// lignes de frais avec leur quantité (le WinDev imprimait 0) ; frais valorisés seulement si demandé (le total s'imprimait
// toujours) ; palettes de tous les dépôts détaillées (le WinDev s'arrêtait à 4).
// =====================================================================
internal static class ReglesFeuillesPreBl
{
    // Palettes d'un dépôt : une par SSCC, une seule par palette groupée
    public static int Palettes(IEnumerable<PalettePreparation> palettes) =>
        palettes.Select(p => p.Groupe != "" ? $"G{p.Groupe}" : $"S{p.Sscc}").Distinct().Count();

    // Emplacement découpé en allée, rack, emplacement (STO005.PSEMP1/2/3)
    public static string Emplacement(string emplacement, int allee, int rack, int place)
    {
        if (allee <= 0 || emplacement.Length <= allee) return emplacement;
        var morceaux = new List<string>();
        var reste = emplacement;
        foreach (var n in new[] { allee, rack, place })
        {
            if (reste == "") break;
            var t = n <= 0 || n > reste.Length ? reste.Length : n;
            morceaux.Add(reste[..t]);
            reste = reste[t..];
        }
        if (reste != "") morceaux[^1] += reste;
        return string.Join(".", morceaux.Where(m => m.Trim() != "").Select(m => m.Trim()));
    }

    // Lignes de la feuille camion : un lot avec une palette mixte donne une ligne pour ses palettes complètes (n° avant « et »)
    // puis une pour la palette mixte (quantité incomplète, 1 palette, poids de la palette compté une fois par dépôt et n°)
    // poidsSaisis : un pré-BL par clé (ceux sans ligne ont aussi leur total)
    public static (List<LigneCamion> Lignes, List<TotalCamion> Totaux, List<(string, int)> ParDepot) Lignes(
        IReadOnlyList<LotCamion> lots, IReadOnlyDictionary<int, int> poidsSaisis, int poidsPalette)
    {
        var lignes = new List<LigneCamion>();
        var mixtesVues = new HashSet<(int, int, int)>();
        var parDepot = new Dictionary<string, HashSet<string>>();
        var totaux = poidsSaisis.Keys.ToDictionary(n => n, _ => (Palettes: 0, Poids: 0m, Montant: 0m));
        void Ajouter(LotCamion l, string numeros, int quantite, int palettes, bool palettePesee, string clePalette)
        {
            var colis = l.EmballagePar > 0 ? (quantite + l.EmballagePar - 1) / l.EmballagePar : 0;
            var poids = l.Frais ? 0m : colis * l.PoidsUc + (palettePesee ? palettes * poidsPalette : 0);
            lignes.Add(new LigneCamion(l.PreBl, $"{l.Article} {l.Indice}".Trim(), l.RefImprime, l.Designation, l.NomDepot, l.StockPaye, numeros,
                quantite, palettes, (int)Math.Round(poids, MidpointRounding.AwayFromZero), l.EmballagePar * l.UcParPalette, l.UcParPalette,
                l.EmballagePar, l.TypeColis, $"{l.Dossier:00000}/{l.Cle}", l.RefCommande, l.Montant, l.PrixUnitaire));
            var t = totaux.GetValueOrDefault(l.PreBl);
            totaux[l.PreBl] = (t.Palettes + (palettePesee ? palettes : 0), t.Poids + poids, t.Montant + l.Montant);
            if (!l.Frais && palettes > 0)
            {
                if (!parDepot.TryGetValue(l.NomDepot, out var p)) parDepot[l.NomDepot] = p = [];
                for (var i = 0; i < palettes; i++) p.Add(clePalette != "" ? clePalette : $"{l.PreBl}-{lignes.Count}-{i}");
            }
        }
        foreach (var l in lots)
        {
            if (l.Frais || l.PaletteMixte == 0)
            {
                Ajouter(l, l.LibellePalettes, l.Quantite, l.Frais ? 0 : l.Palettes, true, "");
                continue;
            }
            // Sans conditionnement (exemplaires par palette inconnus), pas de partage possible : une ligne pour tout le lot
            var parPalette = l.EmballagePar * l.UcParPalette;
            var incomplets = parPalette > 0 ? Math.Clamp(l.Quantite - parPalette * l.Palettes, 0, l.Quantite) : l.Quantite;
            var et = l.LibellePalettes.IndexOf(" et ", StringComparison.OrdinalIgnoreCase);
            var completes = l.Palettes > 0 && parPalette > 0;
            if (completes) Ajouter(l, et > 0 ? l.LibellePalettes[..et] : l.LibellePalettes, l.Quantite - incomplets, l.Palettes, true, "");
            var premiere = mixtesVues.Add((l.PreBl, l.Depot, l.PaletteMixte));
            Ajouter(l, $"N° {l.PaletteMixte}", incomplets, 1, premiere, $"{l.PreBl}-{l.Depot}-M{l.PaletteMixte}");
        }
        var listeTotaux = totaux.OrderBy(t => t.Key).Select(t => new TotalCamion(t.Key, t.Value.Palettes,
            (int)Math.Round(t.Value.Poids, MidpointRounding.AwayFromZero), poidsSaisis.GetValueOrDefault(t.Key), t.Value.Montant)).ToList();
        return (lignes, listeTotaux, [.. parDepot.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => (d.Key, d.Value.Count))]);
    }

    // Pré-BL d'une même feuille camion : même client, date de départ, transporteur et adresse (R28)
    public static string RefusCamion(IReadOnlyList<EnTeteFeuillePreBl> entetes)
    {
        if (entetes.Count == 0) return "Choisissez au moins un pré-BL.";
        var p = entetes[0];
        return entetes.Any(e => (e.CompteGeneral, e.CompteClient, e.Depart, e.Transporteur, e.Adresse.Numero) !=
                                (p.CompteGeneral, p.CompteClient, p.Depart, p.Transporteur, p.Adresse.Numero))
            ? "Les pré-BL d'une feuille camion doivent avoir le même client, la même date de départ, le même transporteur et la même adresse."
            : "";
    }
}
