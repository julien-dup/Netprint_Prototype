using System.Text;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Requête de la liste des commandes (types 3 et 4), construite à partir des critères.
// Reprise du bloc « Cdes PFS » de HistoriqueDossiers (BTN_Valider, InitWhere) et de DOS500S (SPSQLRQS).
// Séparée de la lecture pour être testée sans base : texte SQL + paramètres dans l'ordre des « ? ».
// Écarts volontaires (signalés « ÉCART ») :
//   - tout est paramétré (le WinDev et le RPG concaténaient les valeurs saisies) ;
//   - le filtre de statut des lignes est dans la jointure : une commande sans ligne reste visible ;
//   - BL, facture et documents en EXISTS / sous-requête : pas de ligne en double si plusieurs BL ou factures ;
//   - client facturé : jointure corrigée (le OR du WinDev n'était pas parenthésé).
// =====================================================================
internal static class RequeteListeCommandes
{
    public sealed record Requete(string Sql, IReadOnlyList<object?> Parametres);

    // Colonnes lues, dans l'ordre (voir ConsultationCommandesDb2.Lire)
    private const string Colonnes = """
        a.RDOSS, a.CCDCLE, a.CCTYPC, a.RGEN, a.RCLI, e.CNOM, COALESCE(f1.CNOM, f2.CNOM), a.CCREFC, a.CCDES1, a.CCDES2,
        a.CCDATA, a.CCDATM, a.CCDATJ, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCDTLA, a.CCDTLM, a.CCDTLJ,
        a.CCSLDA, a.CCSLDM, a.CCSLDJ, a.CCTAGS, a.CCREP, r.INOM, a.CCINAT, a.CCENSE,
        b.DNNLIG, b.DNART, b.DNIND, c.ARFIMP, b.DNDESA, b.DNSTAT, b.DNLORI, b.DNAPPE, b.DNDLIV,
        b.DNQTEC, b.DNQTEL, b.DNQTEF, b.DNQTER, b.DNPUS, b.DNUV, b.DNOPT2, b.DNOPT5, u.TNUM, b.DNNORM, c.AMODEL,
        c.AFAM, c.ASFAM, b.DNSOLD, b.DNCCOM,
        t.DNTXT1, t.DNTXT2, t.DNTXT3, t.DNTXT4, t.DNTXT5,
        (SELECT COUNT(*) FROM {b}.DOSDOC g WHERE g.RSOC = a.RSOC AND g.RDOSS = a.RDOSS AND g.CCDCLE = a.CCDCLE)
        """;

    public const int NbTextes = 5;
    public const int NbColonnes = 57;   // colonnes de liste ; la fiche ajoute ColonnesFiche à la suite

    // En-tête AB001 et lignes NEG100 avec leurs références. La liste ne garde que les lignes fermes et les lignes
    // « à la demande » mouvementées (comme HistoriqueDossiers) ; la fiche les montre toutes.
    private static string Source(string b, bool toutesLesLignes) => $"""
        FROM {b}.AB001 a
        LEFT OUTER JOIN {b}.NEG100 b ON b.RSOC = a.RSOC AND b.RDOSS = a.RDOSS AND b.DNDCLE = a.CCDCLE
        {(toutesLesLignes ? "" : "     AND (b.DNSTAT = '1' OR (b.DNSTAT = '3' AND (b.DNQTEC > 0 OR b.DNQTEL > 0)))")}
        LEFT OUTER JOIN {b}.SPF010 c ON c.RSOC = b.RSOC AND c.AARTIC = b.DNART AND c.AINDIC = b.DNIND
        LEFT OUTER JOIN {b}.AA0003 e ON e.RSOC = a.RSOC AND e.RGEN = a.RGEN AND e.RCLI = a.RCLI
        LEFT OUTER JOIN {b}.AA0003 f1 ON f1.RSOC = b.RSOC AND f1.RGEN = b.DNGENF AND f1.RCLI = b.DNCLIF AND b.DNCLIF <> 0
        LEFT OUTER JOIN {b}.AA0003 f2 ON f2.RSOC = a.RSOC AND f2.RGEN = a.CGENF AND f2.RCLI = a.CCLIF AND a.CCLIF <> 0
             AND (a.CGENF <> a.RGEN OR a.CCLIF <> a.RCLI)
        LEFT OUTER JOIN {b}.NEG100T t ON t.RSOC = b.RSOC AND t.RDOSS = b.RDOSS AND t.DNDCLE = b.DNDCLE AND t.DNNLIG = b.DNNLIG
        LEFT OUTER JOIN {b}.AA00083 u ON u.RSOC = b.RSOC AND u.RARG = b.DNUV AND u.RTABLE = 'UV' AND u.TTAG <> '*'
        LEFT OUTER JOIN {b}.B000F r ON r.RSOC = a.RSOC AND r.RREPR = a.CCREP AND r.ITAG <> '*'
        """;

    // Compléments de la fiche (en-tête puis ligne), lus après les colonnes de liste : voir ConsultationCommandesDb2.Fiche
    private const string ColonnesFiche = """
        a.CCFACT, a.CCNINT, a.CCDTSA, a.CCDTSS, a.CCTXCO, a.CCAT, a.CCSCAT, a.CCPROF, a.CCECAL, a.CCPORT,
        a.CCMOD, rg.TLIB1, a.CCECH, a.CCLE, a.CCART, a.CCCDEV, a.CCOP11, a.CCOP10, a.CGENF, a.CCLIF, f2.CNOM,
        b.DNRS, b.DNPOST, b.DNVILL, b.DNLIGA, b.DNDATB, b.DNCOMM, b.DNCOM2, b.DNCOM3, b.DNTXCO, b.DNTXRE,
        b.DNQTEA, b.DNQMAX, b.DNPXCE, b.DNDEV, b.DNVERS, b.DNHYPO, b.DNGENF, b.DNCLIF, f1.CNOM, b.DEMP,
        a.CCTYPV, a.CCNAEA, a.CTXRM, a.CCRCVD
        """;

    // Fiche : une commande de type 3 ou 4, toutes ses lignes, dans l'ordre des n° de ligne
    public static Requete ConstruireFiche(string bib, int societe, CleCommande cle)
    {
        var b = BaseDb2.Bibliotheque(bib);
        var sql = $"""
            SELECT {Colonnes.Replace("{b}", b)},
            {ColonnesFiche}
            {Source(b, toutesLesLignes: true)}
            LEFT OUTER JOIN {b}.AA00081 rg ON rg.RSOC = a.RSOC AND rg.RTABLE = 'RG' AND rg.RARG = a.CCMOD AND rg.TTAG <> '*'
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ? AND a.CCTYPC IN ('3', '4')
            ORDER BY b.DNNLIG
            """;
        return new Requete(sql, [societe, cle.Numero, cle.Cle]);
    }

    public static Requete Construire(string bib, int societe, CriteresCommandes c)
    {
        var b = BaseDb2.Bibliotheque(bib);
        var parametres = new List<object?>();
        var sql = new StringBuilder($"""
            SELECT {Colonnes.Replace("{b}", b)}
            {Source(b, toutesLesLignes: false)}
            WHERE a.RSOC = ?
            """);
        parametres.Add(societe);

        void Et(string condition, params object?[] valeurs)
        {
            sql.Append("\n  AND ").Append(condition);
            parametres.AddRange(valeurs);
        }

        Et(c.TypeCommande is TypesCommande.Negoce or TypesCommande.ProduitFini ? "a.CCTYPC = ?" : "a.CCTYPC IN ('3', '4')",
            c.TypeCommande is TypesCommande.Negoce or TypesCommande.ProduitFini ? [c.TypeCommande] : []);
        if (!c.AvecSoldees) Et("a.CCTAGS = ' '");

        if (c.Numero is > 0)
        {
            Et("a.RDOSS = ?", c.Numero);
            if (Rempli(c.Cle)) Et("a.CCDCLE = ?", Majuscules(c.Cle));
        }
        if (c.CompteClient is > 0) Et("a.RGEN = ? AND a.RCLI = ?", c.CompteGeneral ?? 0, c.CompteClient);
        if (c.Representant is > 0) Et("a.CCREP = ?", c.Representant);
        if (c.Devis is > 0) Et("(a.CCDEV = ? OR b.DNDEV = ?)", c.Devis, c.Devis);
        if (c.Produit is > 0) Et("c.AMODEL = ?", c.Produit);
        if (Rempli(c.Article))
        {
            var (condition, valeur) = Joker("b.DNART", Majuscules(c.Article));
            Et(condition, valeur);
        }
        if (Rempli(c.Indice)) Et("b.DNIND = ?", Majuscules(c.Indice));
        if (Rempli(c.ReferenceImprime)) Et($"UPPER(c.ARFIMP) LIKE ? {Echappement}", Commence(c.ReferenceImprime));
        if (Rempli(c.ReferenceCommandeClient)) Et($"UPPER(a.CCREFC) LIKE ? {Echappement}", Commence(c.ReferenceCommandeClient));
        if (Rempli(c.NumeroAppel)) Et("b.DNAPPE = ?", c.NumeroAppel!.Trim());
        if (Rempli(c.Fabricant)) Et("a.CCINAT = ?", Majuscules(c.Fabricant));
        if (c.BonLivraison is > 0)
            Et($"EXISTS (SELECT 1 FROM {b}.EXP010 x WHERE x.RSOC = a.RSOC AND x.HNODOS = a.RDOSS AND x.HCLEF = a.CCDCLE AND x.HNOBL = ?)",
                c.BonLivraison);
        if (c.Facture is > 0)
            Et($"EXISTS (SELECT 1 FROM {b}.FAC090 y WHERE y.RSOC = a.RSOC AND y.FEDOSS = a.RDOSS AND y.FECLE = a.CCDCLE AND y.RNFAC = ?)",
                c.Facture);
        if (Rempli(c.Categorie)) Et("c.AFAM = ?", Majuscules(c.Categorie));
        if (Rempli(c.SousCategorie)) Et("c.ASFAM = ?", Majuscules(c.SousCategorie));
        if (Rempli(c.Enseigne)) Et("a.CCENSE = ?", Majuscules(c.Enseigne));
        if (c.FiniLargeur is > 0) Et("c.ALRFF = ?", c.FiniLargeur);
        if (c.FiniHauteur is > 0) Et("c.AHAFF = ?", c.FiniHauteur);
        if (c.FiniTroisiemeCote is > 0) Et("c.A3COTE = ?", c.FiniTroisiemeCote);

        // Chaque mot dans la désignation de la ligne ou dans celle de la commande (sans tenir compte de la casse)
        foreach (var mot in Mots(c.Designation))
            Et($"(UPPER(b.DNDESA) LIKE ? {Echappement} OR UPPER(a.CCDES1) LIKE ? {Echappement} OR UPPER(a.CCDES2) LIKE ? {Echappement})",
                Contient(mot), Contient(mot), Contient(mot));

        const string dateOuverture = "(a.CCDATA * 10000 + a.CCDATM * 100 + a.CCDATJ)";
        if (c.DateDu is { } du) Et($"{dateOuverture} >= ?", BaseDb2.AAAAMMJJ(du));
        if (c.DateAu is { } au) Et($"{dateOuverture} <= ?", BaseDb2.AAAAMMJJ(au));
        if (c.LivraisonDu is { } ldu) Et("b.DNDLIV >= ?", BaseDb2.AAAAMMJJ(ldu));
        if (c.LivraisonAu is { } lau) Et("b.DNDLIV <= ?", BaseDb2.AAAAMMJJ(lau));

        // Plus récentes d'abord (DOS500S, vue par dossier), lignes dans l'ordre ; une de plus que le maximum pour savoir si on coupe
        sql.Append($"\nORDER BY a.RDOSS DESC, a.CCDCLE, b.DNNLIG\nFETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY");
        return new Requete(sql.ToString(), parametres);
    }

    // Jokers de HistoriqueDossiers : *x* contient, *x finit par, x* commence par, x égal
    internal static (string Condition, string Valeur) Joker(string colonne, string saisie)
    {
        var debut = saisie.StartsWith('*');
        var fin = saisie.EndsWith('*') && saisie.Length > 1;
        var texte = Echapper(saisie.Trim('*'));
        return (debut, fin) switch
        {
            (true, true) => ($"{colonne} LIKE ? {Echappement}", $"%{texte}%"),
            (true, false) => ($"TRIM({colonne}) LIKE ? {Echappement}", $"%{texte}"),
            (false, true) => ($"{colonne} LIKE ? {Echappement}", $"{texte}%"),
            _ => ($"{colonne} = ?", saisie),
        };
    }

    internal static IEnumerable<string> Mots(string? texte) =>
        (texte ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant()).Distinct();

    private static bool Rempli(string? s) => !string.IsNullOrWhiteSpace(s);
    private static string Majuscules(string? s) => (s ?? "").Trim().ToUpperInvariant();

    // Motifs LIKE : % et _ saisis sont cherchés tels quels (échappés par « \ »)
    private const string Echappement = "ESCAPE '\\'";
    private static string Commence(string? s) => Echapper(Majuscules(s)) + "%";
    private static string Contient(string mot) => "%" + Echapper(mot) + "%";
    internal static string Echapper(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
