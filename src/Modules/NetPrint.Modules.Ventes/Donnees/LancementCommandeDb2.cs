using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Données de l'écran de lancement (NEG515), en lecture : articles de la commande (NEG100), liens article ↔ OF (AB0020,
// toutes commandes), lots (AL00F, dépôts B100F non exclus du disponible), livraisons des lots sur le dépôt principal
// (A700F EX − RE), OF à ouvrir (AB001T), lots bloqués (CQU150), dernier cycle du BAG (AB009).
// =====================================================================
internal sealed partial class LancementCommandeDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    internal sealed record EnTete(string NomClient, bool Soldee, bool NonModifiable);

    public async Task<(EnTete EnTete, CalculLancement.Donnees Donnees)?> LireAsync(int societe, CleCommande cle, int? representant,
        CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);

        EnTete entete;
        await using (var rd = await Lire(cnx, $"""
            SELECT COALESCE(c.CNOM, ''), a.CCTAGS, a.CCOP11 FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ? AND a.CCTYPC IN ('3', '4'){(representant is null ? "" : " AND a.CCREP = ?")}
            """, ct, representant is null ? [societe, cle.Numero, cle.Cle] : [societe, cle.Numero, cle.Cle, representant]))
        {
            if (!await rd.ReadAsync(ct)) return null;
            entete = new EnTete(rd.Texte(0), rd.Texte(1) != "", rd.Texte(2) == "O");
        }

        // Paramètres de stock : types par nature d'article (GETTYPSTOC), dépôt principal
        var types = new Dictionary<string, string>();
        var depot = 0;
        await using (var rd = await Lire(cnx, $"SELECT PSTSNE, PSTSP1, PSTSP2, PSTSP3, PSDEPP FROM {b}.STO005 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct))
            {
                (types["NE"], types["P1"], types["P2"], types["P3"]) = (rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3));
                depot = rd.Entier(4);
            }

        // Articles de la commande : lignes regroupées par article et indice (soldées comprises ; NEG515 séparait aussi par
        // désignation, ce qui comptait deux fois les mêmes OF), désignation de la première ligne
        var articles = new List<CalculLancement.ArticleCommande>();
        var typeStock = new Dictionary<(string, string), string?>();
        await using (var rd = await Lire(cnx, $"""
            SELECT n.DNART, n.DNIND, MIN(n.DNDESA), SUM(n.DNQTEC), COALESCE(s.ASTK, 'N'), COALESCE(s.ARFIMP, ''), COALESCE(s.ATYPEA, ''),
                   COALESCE(s.ATYFAC, '')
            FROM {b}.NEG100 n
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNART <> ' '
            GROUP BY n.DNART, n.DNIND, s.ASTK, s.ARFIMP, s.ATYPEA, s.ATYFAC
            ORDER BY n.DNART, n.DNIND
            """, ct, societe, cle.Numero, cle.Cle))
            while (await rd.ReadAsync(ct))
            {
                var stocke = rd.Texte(4) == "O";
                articles.Add(new(rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(5), stocke, rd.Entier(3)));
                var nature = rd.Texte(6) switch { "1" => "NE", "2" => rd.Texte(7) switch { "2" => "P2", "3" => "P3", _ => "P1" }, _ => null };
                typeStock[(rd.Texte(0), rd.Texte(1))] = stocke && nature is not null && types.GetValueOrDefault(nature) is { Length: > 0 } t ? t : null;
            }

        // Une requête par table pour toute la commande (par paquets d'articles), réparties ensuite par article et indice
        var references = articles.Select(a => (a.Article, a.Indice)).Distinct().ToList();
        var refsArticles = references.Select(r => r.Article).Distinct().ToList();
        var liens = references.ToDictionary(r => r, _ => (IReadOnlyList<CalculLancement.Lien>)new List<CalculLancement.Lien>());
        var lots = references.ToDictionary(r => r, _ => (IReadOnlyList<CalculLancement.Lot>)new List<CalculLancement.Lot>());
        var livraisons = new Dictionary<(string, string), IReadOnlyList<CalculLancement.Livraison>>();
        var bags = new Dictionary<(NumeroOf, string), EtatBag>();
        var bloques = new HashSet<(NumeroOf, string)>();
        static string Marqueurs(int n) => string.Join(", ", Enumerable.Repeat("?", n));

        foreach (var paquet in refsArticles.Chunk(200))
        {
            object?[] p = [societe, .. paquet.Cast<object?>()];

            // Liens des articles, toutes commandes ; pour une affectation de stock soldée, la commande a-t-elle encore des
            // lignes de l'article à livrer (NEG515 spSelectAff)
            await using (var rd = await Lire(cnx, $"""
                SELECT l.LAART, l.LAIND, l.RDOSS, l.LADCLE, l.LADOSC, l.LACLDC, l.LAQTEC, l.LAQTEF, l.LAQTEA, l.LATYPE, l.LASOLD,
                       CASE WHEN l.LATYPE = 'S' AND l.LASOLD = 'O' THEN (SELECT COUNT(*) FROM {b}.NEG100 n WHERE n.RSOC = l.RSOC
                            AND n.RDOSS = l.LADOSC AND n.DNDCLE = l.LACLDC AND n.DNART = l.LAART AND n.DNIND = l.LAIND AND n.DNSOLD <> 'O')
                            ELSE 0 END
                FROM {b}.AB0020 l WHERE l.RSOC = ? AND l.LAART IN ({Marqueurs(paquet.Length)})
                ORDER BY l.RDOSS, l.LADCLE, l.LALIG
                """, ct, p))
                while (await rd.ReadAsync(ct))
                    if (liens.GetValueOrDefault((rd.Texte(0), rd.Texte(1))) is List<CalculLancement.Lien> l)
                        l.Add(new(new NumeroOf(rd.Entier(2), rd.Texte(3)), rd.Entier(4), rd.Texte(5), rd.Entier(6), rd.Nombre(7), rd.Entier(8),
                            rd.Texte(9), rd.Texte(10) == "O", rd.Entier(11) > 0));

            // Lots des articles gérés en stock, dépôts non exclus du disponible ; seul le type de stock de l'article compte
            await using (var rd = await Lire(cnx, $"""
                SELECT p.PLREF, p.PLIND, p.PLTSTO, p.PLLOT, COALESCE(d.DSTYPE, ''), SUM(p.PLQENT) FROM {b}.AL00F p
                LEFT OUTER JOIN {b}.B100F d ON d.RSOC = p.RSOC AND d.RDEP = p.PLDEP
                WHERE p.RSOC = ? AND p.PLREF IN ({Marqueurs(paquet.Length)}) AND p.PLQENT > 0 AND COALESCE(d.DEXDIS, 'N') <> 'O'
                GROUP BY p.PLREF, p.PLIND, p.PLTSTO, p.PLLOT, d.DSTYPE
                """, ct, p))
                while (await rd.ReadAsync(ct))
                    if (typeStock.GetValueOrDefault((rd.Texte(0), rd.Texte(1))) == rd.Texte(2)
                        && lots.GetValueOrDefault((rd.Texte(0), rd.Texte(1))) is List<CalculLancement.Lot> lo)
                        lo.Add(new(rd.Texte(3), rd.Texte(4), rd.Entier(5)));

            // Dernier cycle du BAG de chaque OF × article × indice
            await using (var rd = await Lire(cnx, $"""
                SELECT RDOSS, F9DCLE, F9ART, F9IND, F9LIG, F9DTRR, F9DTEP, F9DTER, F9DTR, F9ACT FROM {b}.AB009
                WHERE RSOC = ? AND F9ART IN ({Marqueurs(paquet.Length)}) ORDER BY RDOSS, F9DCLE, F9ART, F9IND, F9LIG
                """, ct, p))
                while (await rd.ReadAsync(ct))
                    bags[(new NumeroOf(rd.Entier(0), rd.Texte(1)), CalculLancement.Reference(rd.Texte(2), rd.Texte(3)))] = new EtatBag(
                        rd.Entier(4), BaseDb2.DateAAAAMMJJ(rd.Entier(5)), BaseDb2.DateAAAAMMJJ(rd.Entier(6)), BaseDb2.DateAAAAMMJJ(rd.Entier(7)),
                        BaseDb2.DateAAAAMMJJ(rd.Entier(8)), rd.Texte(9));

            // Lots bloqués (qualité) pour ces articles
            await using (var rd = await Lire(cnx, $"""
                SELECT LBDOSS, LBDCLE, LBART, LBIND FROM {b}.CQU150 WHERE RSOC = ? AND LBTOUS <> 'O' AND LBART IN ({Marqueurs(paquet.Length)})
                """, ct, p))
                while (await rd.ReadAsync(ct))
                    bloques.Add((new NumeroOf(rd.Entier(0), rd.Texte(1)), CalculLancement.Reference(rd.Texte(2), rd.Texte(3))));
        }

        // Dossiers bloqués pour toutes leurs références
        await using (var rd = await Lire(cnx, $"SELECT LBDOSS, LBDCLE FROM {b}.CQU150 WHERE RSOC = ? AND LBTOUS = 'O'", ct, societe))
            while (await rd.ReadAsync(ct)) bloques.Add((new NumeroOf(rd.Entier(0), rd.Texte(1)), CalculLancement.ToutesReferences));
        // Livraisons par lot sur le dépôt principal (toutes, et pour cette commande), en une requête par paquet de lots :
        // A700F n'est indexé que par lot (par article, une lecture coûte près d'une seconde)
        var numerosLots = liens.Values.SelectMany(l => l).Select(l => CalculLancement.NumeroLot(l.Of))
            .Concat(lots.Values.SelectMany(l => l).Select(l => l.Numero)).Distinct().ToList();
        var parArticle = articles.Select(a => (a.Article, a.Indice)).Distinct().ToDictionary(k => k, _ => new List<CalculLancement.Livraison>());
        foreach (var paquet in numerosLots.Chunk(500))
            await using (var rd = await Lire(cnx, $"""
                SELECT MVREF, MVIND, MVLOT, SUM(CASE WHEN MVCMVT = 'EX' THEN MVQTUS ELSE -MVQTUS END),
                       SUM(CASE WHEN MVDOS = ? AND MVDCLE = ? THEN CASE WHEN MVCMVT = 'EX' THEN MVQTUS ELSE -MVQTUS END ELSE 0 END)
                FROM {b}.A700F WHERE RSOC = ? AND MVDEP = ? AND MVCMVT IN ('EX', 'RE') AND MVLOT IN ({string.Join(", ", paquet.Select(_ => "?"))})
                GROUP BY MVREF, MVIND, MVLOT
                """, ct, [cle.Numero, cle.Cle, societe, depot, .. paquet.Cast<object?>()]))
                while (await rd.ReadAsync(ct))
                    if (parArticle.TryGetValue((rd.Texte(0), rd.Texte(1)), out var liste))
                        liste.Add(new(rd.Texte(2), rd.Entier(3), rd.Entier(4)));
        foreach (var (k, liste) in parArticle) livraisons[k] = liste;

        var aOuvrir = new HashSet<NumeroOf>();
        await using (var rd = await Lire(cnx, $"SELECT RDOSS, CCDCLE FROM {b}.AB001T WHERE RSOC = ?", ct, societe))
            while (await rd.ReadAsync(ct)) aOuvrir.Add(new NumeroOf(rd.Entier(0), rd.Texte(1)));

        return (entete, new CalculLancement.Donnees(articles, liens, lots, livraisons, aOuvrir, bloques, bags));
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return await cmd.ExecuteReaderAsync(ct);
    }
}
