namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Moteur de prix d'une ligne de commande : cascade SPRCPX / SPRCP1 de CTL520, unité de vente (SPUNIT),
// prix net, montant, commission (SPRCTC, AA000Z) et maxi livrable (DOS009 / PRT059).
// Fonctions pures : les données de tarif sont lues à part (Donnees/LignesCommandeDb2), le moteur les applique.
// Règles relues dans le source (rapport NEG520-lignes, §2) ; elles diffèrent de la fiche et du §4.4 des règles :
//   - la date de tarif est la date du jour (comme le RPG) ;
//   - AAPPTP est une zone de l'article : tarif modèle, puis prix de la fiche, puis tarif « A » ;
//   - le tarif spécial client (SPF011 code blanc) n'est pas lu pour une commande ;
//   - un tarif de catégorie ou une tranche trouvés arrêtent la cascade même à 0.
// =====================================================================
internal static class MoteurPrixCommande
{
    public const long TnumLeTout = 999999999;

    // ---------- Données lues ----------
    public sealed record Article(
        bool TarifModele,            // SPF010.AAPPTP = 'O'
        int Modele,                  // AMODEL
        decimal PrixVente,           // APVENT
        decimal PrixRevient,         // APRVT
        decimal PrixCession,         // APRCES
        string Unite,                // AUNITE
        int Emballage,               // AEMBP
        bool TarifParEmballage,      // AOPT2 = 'C' : tranches exprimées en nombre d'emballages
        decimal LargeurFini,         // ALRFF
        decimal HauteurFini);        // AHAFF

    // Ligne de SPF011 (deux prix datés)
    public sealed record TarifDate(string Type, int CompteGeneral, int CompteClient, string Tranche,
        decimal Prix1, DateOnly? Date1, decimal Cession1, decimal Prix2, DateOnly? Date2, decimal Cession2);

    // Ligne de tarif quantitatif (SPF020D ou MOD090D) : tranche de quantités
    public sealed record TarifQuantite(int Minimum, int Maximum, decimal Prix, decimal Cession, string Unite);

    public sealed record DonneesTarif(
        Article Article,
        string CodeTarifClient,                              // AA0003.CTARIF
        int CompteGeneral, int CompteClient,
        IReadOnlyList<TarifDate> Tarifs,                     // SPF011 de l'article (types CTARIF, Q, A)
        IReadOnlyDictionary<string, long> Tranches,          // AA00083 'TQ' : code tranche → quantité maxi
        IReadOnlyList<TarifQuantite> TarifQuantitatif,       // SPF020D du client, dernière date d'effet ≤ aujourd'hui
        IReadOnlyList<TarifQuantite> TarifModeleClient,      // MOD090D du client, dernière date ≤ aujourd'hui
        IReadOnlyList<TarifQuantite> TarifModeleGeneral,     // MOD090D général (0 / 0), si le client n'a pas de tarif modèle
        bool ProrataModele,                                  // MOD090E.CPRORAT = 'O'
        decimal LargeurModele, decimal HauteurModele,        // MOD010.MLRFF / MHAFF
        bool Mandataire);                                    // B000F.ITYPRP = 'M'

    public sealed record Resultat(decimal Prix, decimal Cession, string? Unite, string Source)
    {
        public static readonly Resultat Aucun = new(0, 0, null, "");
        public bool Trouve => Source != "";
    }

    // ---------- Cascade ----------
    public static Resultat Tarif(DonneesTarif d, int quantite, DateOnly aujourdhui, decimal tauxCommission)
    {
        var r = Cascade(d, quantite, aujourdhui);
        // Représentant mandataire : prix majoré de la commission, arrondi à l'unité (FRCPX)
        if (d.Mandataire && r.Prix != 0 && tauxCommission is > 0 and < 100)
            r = r with { Prix = Math.Round(r.Prix / (100 - tauxCommission) * 100, 0, MidpointRounding.AwayFromZero) };
        return r;
    }

    private static Resultat Cascade(DonneesTarif d, int quantite, DateOnly aujourdhui)
    {
        var a = d.Article;
        if (a.TarifModele) return TarifModele(d, quantite) ?? PrixFiche(a) ?? TarifA(d, aujourdhui);

        // Tarif de catégorie client : fin de la cascade même à 0
        if (d.CodeTarifClient != "")
        {
            var t = d.Tarifs.FirstOrDefault(x => x.Type == d.CodeTarifClient && x.CompteGeneral == 0 && x.CompteClient == 0 && x.Tranche == "");
            return t is null ? new Resultat(0, 0, null, "Catégorie (absente)") : Prix(t, aujourdhui, $"Tarif catégorie {d.CodeTarifClient}");
        }
        if (quantite == 0) return Resultat.Aucun;
        var recherche = a.TarifParEmballage && a.Emballage != 0 ? quantite / a.Emballage : quantite;

        // Tranches de quantité « Q » : celles du client, puis les générales ; fin même à 0
        if (Tranche(d, d.CompteGeneral, d.CompteClient, recherche, aujourdhui) is { } tc) return tc;
        if (Tranche(d, 0, 0, recherche, aujourdhui) is { } tg) return tg;

        // Tarif quantitatif daté du client (SPF020)
        var q = d.TarifQuantitatif.FirstOrDefault(x => x.Minimum <= recherche && recherche <= x.Maximum);
        if (q is not null) return new Resultat(q.Prix, q.Cession, q.Unite == "" ? null : q.Unite, "Tarif quantitatif client");

        return PrixFiche(a) ?? TarifA(d, aujourdhui);
    }

    private static Resultat? Tranche(DonneesTarif d, int gen, int cli, int quantite, DateOnly aujourdhui)
    {
        var candidates = d.Tarifs
            .Where(x => x.Type == "Q" && x.CompteGeneral == gen && x.CompteClient == cli && d.Tranches.ContainsKey(x.Tranche))
            .Select(x => (Tarif: x, Maxi: d.Tranches[x.Tranche]))
            .Where(x => x.Maxi >= quantite)
            .OrderBy(x => x.Maxi)
            .ToList();
        return candidates.Count == 0 ? null
            : Prix(candidates[0].Tarif, aujourdhui, gen == 0 ? "Tranche générale" : "Tranche client");
    }

    // Tarif modèle (MOD090) : client, sinon général ; prorata de surface éventuel. Pas de repli sur le général
    // si le client a un tarif modèle sans tranche convenable (comme le RPG).
    private static Resultat? TarifModele(DonneesTarif d, int quantite)
    {
        var lignes = d.TarifModeleClient.Count > 0 ? d.TarifModeleClient : d.TarifModeleGeneral;
        var t = lignes.FirstOrDefault(x => x.Minimum <= quantite && quantite <= x.Maximum);
        if (t is null) return null;
        var ratio = Ratio(d);
        return new Resultat(Math.Round(ratio * t.Prix, 2, MidpointRounding.AwayFromZero),
            Math.Round(ratio * t.Cession, 2, MidpointRounding.AwayFromZero), t.Unite == "" ? null : t.Unite, "Tarif modèle");
    }

    internal static decimal Ratio(DonneesTarif d)
    {
        if (!d.ProrataModele || d.LargeurModele <= 0 || d.HauteurModele <= 0) return 1;
        var ratio = d.Article.LargeurFini * d.Article.HauteurFini / (d.LargeurModele * d.HauteurModele);
        return Math.Truncate(ratio * 10_000_000m) / 10_000_000m;   // 7 décimales, tronqué
    }

    private static Resultat? PrixFiche(Article a) =>
        a.PrixVente > 0 ? new Resultat(a.PrixVente, 0, null, "Prix de la fiche article") : null;

    private static Resultat TarifA(DonneesTarif d, DateOnly aujourdhui)
    {
        var t = d.Tarifs.FirstOrDefault(x => x.Type == "A" && x.CompteGeneral == 0 && x.CompteClient == 0 && x.Tranche == "");
        return t is null ? Resultat.Aucun : Prix(t, aujourdhui, "Tarif A");
    }

    // SPRCP1 : des deux prix datés, celui dont la date est la plus récente sans dépasser aujourd'hui ;
    // aucune date valable ou deux dates égales : 0
    internal static Resultat Prix(TarifDate t, DateOnly aujourdhui, string source)
    {
        var d1 = t.Date1 is { } x1 && x1 <= aujourdhui ? x1 : (DateOnly?)null;
        var d2 = t.Date2 is { } x2 && x2 <= aujourdhui ? x2 : (DateOnly?)null;
        if (d1 is null && d2 is null || d1 == d2) return new Resultat(0, 0, null, source);
        return d2 is null || d1 > d2 ? new Resultat(t.Prix1, t.Cession1, null, source) : new Resultat(t.Prix2, t.Cession2, null, source);
    }

    // ---------- Calculs de la ligne ----------
    // Prix net = brut × (100 − remise) / 100, tronqué au centime (EVAL sans arrondi)
    public static decimal PrixNet(decimal brut, decimal remise) => Math.Truncate(brut * (100 - remise) / 100 * 100) / 100;

    // SPUNIT : nombre d'unités facturées ; « Le tout » ou unité sans diviseur : 1 (−1 si la quantité est négative)
    public static decimal Unites(int quantite, long tnum) =>
        tnum is TnumLeTout or <= 0 ? (quantite < 0 ? -1 : 1) : Math.Truncate((decimal)quantite / tnum * 100_000) / 100_000;

    public static decimal Montant(decimal prixNet, int quantite, long tnum) =>
        Math.Round(prixNet * Unites(quantite, tnum), 2, MidpointRounding.AwayFromZero);

    // SPRCTC : seuils PV / cession (T1) et taux (T) de AA000Z, si CFTYPE ∈ {B, E} ; sinon taux de l'en-tête
    public static decimal Commission(decimal prixNet, decimal cession, string typeCommission, IReadOnlyList<decimal> seuils,
        IReadOnlyList<decimal> taux, decimal tauxEntete)
    {
        if (typeCommission is not ("B" or "E")) return tauxEntete;
        if (cession <= 0 || seuils.Count < 10 || taux.Count < 10) return 0;
        var r = Math.Round(prixNet / cession, 2, MidpointRounding.AwayFromZero);
        if (r >= seuils[9]) return taux[9] * 100;
        for (var f = 0; f < 9; f++)
            if (r <= seuils[f]) return taux[f] * 100;
        return 0;
    }

    // Seuils et taux de AA000Z : TRP = 10 × 3 chiffres (centièmes), TCM = 10 × 4 chiffres (dix-millièmes)
    public static (List<decimal> Seuils, List<decimal> Taux) TableCommission(string trp, string tcm)
    {
        static decimal Lire(string s, int debut, int longueur, decimal diviseur) =>
            s.Length >= debut + longueur && decimal.TryParse(s.Substring(debut, longueur), out var v) ? v / diviseur : 0;
        var seuils = Enumerable.Range(0, 10).Select(k => Lire(trp.PadRight(30), 3 * k, 3, 100)).ToList();
        var taux = Enumerable.Range(0, 10).Select(k => Lire(tcm.PadRight(40), 4 * k, 4, 10_000)).ToList();
        return (seuils, taux);
    }

    // Maxi livrable : quantité × (100 + % de tolérance) / 100 ; % du client (PRT059) sinon de la société (DOS009),
    // première tranche dont le seuil couvre la quantité ; 0 % si aucune
    public static int MaxiLivrable(int quantite, IReadOnlyList<(int Seuil, decimal Pourcentage)> tranchesClient,
        IReadOnlyList<(int Seuil, decimal Pourcentage)> tranchesSociete)
    {
        var tranches = tranchesClient.Count > 0 && tranchesClient[0].Seuil > 0 ? tranchesClient : tranchesSociete;
        var pc = tranches.Where(t => t.Seuil > 0).FirstOrDefault(t => quantite <= t.Seuil).Pourcentage;
        return (int)Math.Round(quantite * (100 + pc) / 100, 0, MidpointRounding.AwayFromZero);
    }
}
