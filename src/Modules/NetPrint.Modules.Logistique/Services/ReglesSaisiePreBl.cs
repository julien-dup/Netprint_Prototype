using NetPrint.Modules.Logistique.Contrats;

namespace NetPrint.Modules.Logistique.Services;

// Conditionnement d'un lot : palettes complètes, colis, exemplaires de l'UC incomplète, palette incomplète (R16)
internal sealed record ConditionnementLotPreBl(int Palettes, int Colis, int ExMecompte, bool PaletteIncomplete);

// =====================================================================
// Règles de la saisie d'un pré-BL, sans base (fiche docs/logistique/fiches/PreBL.md, R1 à R19). En plus du WinDev : quantité
// d'une palette bornée par sa quantité disponible (contrôlée sous verrou), une ligne de commande une seule fois, observations
// et mention de 40 caractères, quantité d'une ligne = somme de ses lots aussi pour la variante sans palette.
// =====================================================================
internal static class ReglesSaisiePreBl
{
    public static readonly string[] TypesLivraison = ["E", "M", "A", "I", "P"];
    public static readonly string[] TypesPort = ["A", "D", "E", "F"];
    public static readonly string[] EtatsLivraison = ["A", "T", "S"];

    public static int EmballagePar(LigneSaisiePreBl l) => l.EmballageParExceptionnel > 0 ? l.EmballageParExceptionnel : l.EmballagePar;
    public static int UcParPalette(LigneSaisiePreBl l) => l.UcParPaletteExceptionnel > 0 ? l.UcParPaletteExceptionnel : l.UcParPalette;

    // Colis = quantité ÷ emballage par ; reste = exemplaires de l'UC incomplète. Palette complète : quantité d'une palette =
    // UC par palette × emballage par ; sans palette choisie (variante), palettes complètes = colis ÷ UC par palette.
    public static ConditionnementLotPreBl Conditionnement(LigneSaisiePreBl l, LotSaisiePreBl lot, bool sansPalette)
    {
        var emballage = EmballagePar(l);
        var ucPalette = UcParPalette(l);
        var (colis, ex) = emballage > 0 ? (lot.Quantite / emballage, lot.Quantite % emballage) : (0, 0);
        var pleine = emballage * ucPalette;
        if (sansPalette || lot.Palettes.Count == 0)
        {
            var palettes = ucPalette > 0 ? colis / ucPalette : 0;
            return new ConditionnementLotPreBl(palettes, colis, ex, ex > 0 || (ucPalette > 0 && colis % ucPalette != 0));
        }
        var completes = pleine > 0 ? lot.Palettes.Count(p => p.Quantite == pleine) : 0;
        return new ConditionnementLotPreBl(completes, colis, ex, completes < lot.Palettes.Count);
    }

    public static List<string> Controler(SaisiePreBl s)
    {
        var e = new List<string>();
        if (s.CompteGeneral <= 0 || s.CompteClient <= 0) e.Add("Le client doit être renseigné.");
        if (s.Adresse.Numero <= 0) e.Add("Le n° d'adresse de livraison doit être renseigné.");
        if (s.Transporteur <= 0) e.Add("Le transporteur doit être renseigné.");
        if (!TypesLivraison.Contains(s.TypeLivraison)) e.Add("Le mode de transport doit être renseigné.");
        if (!TypesPort.Contains(s.TypePort)) e.Add("Le type de port doit être renseigné.");
        if (s.Observations.Length > 3 || s.Observations.Any(o => (o ?? "").Length > 40)) e.Add("Les observations tiennent sur 3 lignes de 40 caractères.");
        if ((s.Mention ?? "").Length > 40) e.Add("La mention BL / facture fait 40 caractères au plus.");
        if (s.Poids is < 0 or > 99999) e.Add("Le poids total est compris entre 0 et 99 999 kg.");
        var pied = (s.Pied ?? "").Replace("\r", "").Split('\n');
        if (pied.Length > 999) e.Add("Le texte de pied fait 999 lignes au plus.");
        if (pied.Any(x => x.TrimEnd().Length > 40)) e.Add("Le texte de pied : 40 caractères par ligne au plus.");
        if (s.Lignes.Count == 0) e.Add("Au moins un appel doit être saisi.");
        if (s.Lignes.GroupBy(l => (l.Dossier, l.Cle, l.LigneCommande)).FirstOrDefault(g => g.Count() > 1) is { } double_)
            e.Add($"La ligne {double_.Key.LigneCommande} de la commande {double_.Key.Dossier}/{double_.Key.Cle} est deux fois dans le pré-BL.");
        var factures = s.Lignes.Where(l => l.CompteGeneralFacture != 0 && l.CompteClientFacture != 0)
            .Select(l => (l.CompteGeneralFacture, l.CompteClientFacture)).Distinct().ToList();
        if (factures.Count > 1) e.Add("Toutes les lignes du pré-BL doivent avoir le même client facturé.");
        foreach (var l in s.Lignes)
        {
            var nom = $"{l.Article} (commande {l.Dossier}/{l.Cle} ligne {l.LigneCommande})";
            if (l.Quantite <= 0) e.Add($"{nom} : la quantité doit être renseignée.");
            if (l.Observations.Length > 3 || l.Observations.Any(o => (o ?? "").Length > 40)) e.Add($"{nom} : 3 observations de 40 caractères au plus.");
            if (!EtatsLivraison.Contains(l.EtatLivraison)) e.Add($"{nom} : état de livraison A, T ou S.");
            if (!l.Stocke)
            {
                if (l.Lots.Count > 0) e.Add($"{nom} : article non stocké, sans lot.");
                continue;
            }
            if ((l.EmballageParExceptionnel > 0) != (l.UcParPaletteExceptionnel > 0))
                e.Add($"{nom} : l'emballage par et le nombre d'UC par palette exceptionnels vont ensemble.");
            if (l.Lots.Count == 0) { e.Add($"{nom} : au moins un lot doit être choisi."); continue; }
            if (l.Lots.Sum(x => x.Quantite) != l.Quantite) e.Add($"{nom} : la somme des quantités des lots doit être égale à la quantité.");
            if (l.Lots.GroupBy(x => (x.Lot, x.Depot)).Any(g => g.Count() > 1)) e.Add($"{nom} : un même lot est pris deux fois.");
            foreach (var lot in l.Lots)
            {
                if (lot.Quantite <= 0) e.Add($"{nom}, lot {lot.Lot} : la quantité doit être renseignée.");
                if (s.SansPalette) continue;
                if (lot.Palettes.Count == 0) e.Add($"{nom}, lot {lot.Lot} : au moins une palette doit être sélectionnée.");
                else if (lot.Palettes.Sum(p => p.Quantite) != lot.Quantite) e.Add($"{nom}, lot {lot.Lot} : la somme des palettes doit être égale à la quantité.");
                if (lot.Palettes.Any(p => p.Quantite <= 0)) e.Add($"{nom}, lot {lot.Lot} : une palette sélectionnée n'a pas de quantité.");
            }
        }
        if (s.Lignes.SelectMany(l => l.Lots).SelectMany(x => x.Palettes).GroupBy(p => p.Sscc).FirstOrDefault(g => g.Count() > 1) is { } deuxFois)
            e.Add($"La palette {deuxFois.Key} est prise deux fois.");
        return e;
    }

    // Lignes de lot à garder telles quelles (récap palettes, clôture) : même lot, dépôt, quantité et palettes qu'enregistré
    public static bool LotInchange(LotSaisiePreBl avant, LotSaisiePreBl apres) =>
        avant.Lot == apres.Lot && avant.Depot == apres.Depot && avant.Quantite == apres.Quantite &&
        avant.Palettes.Select(p => (p.Sscc, p.Quantite)).OrderBy(x => x.Sscc, StringComparer.Ordinal)
            .SequenceEqual(apres.Palettes.Select(p => (p.Sscc, p.Quantite)).OrderBy(x => x.Sscc, StringComparer.Ordinal));
}
