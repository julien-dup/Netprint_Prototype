using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Ligne de la fiche de préparation (NEG100 + AB001 + SPF010 + adresse + transporteur)
internal sealed record LignePreparation(
    DateOnly? DateLivraison, int CompteGeneral, int CompteClient, string NomClient, bool ClientBloque, AdresseBl Adresse,
    IReadOnlyList<string> Commentaires, string Transporteur, int Dossier, string Cle, string Contact, string ReferenceCommande,
    DateOnly? DateCommande, bool Enlevement, int Ligne, string Article, string Indice, string ReferenceImprime, string Appel,
    string Designation, int Reste, int MaxiLivrable, int Emballage, string TypeEmballage, int UcParCarton, int UcParPalette, decimal PoidsUc,
    string Norme, IReadOnlyList<string> Textes);

// Lot en stock d'un article (dépôt principal, lots non bloqués)
internal sealed record LotPreparation(string Article, string Indice, string Lot, int Quantite, DateOnly? DateLimite);

// =====================================================================
// Données de la fiche de préparation (EXP078CLP / EXP078P) : lignes de commande normales (DNSTAT 1) non soldées de dossiers
// autres que « mise en stock », d'articles gérés en stock (sauf demande contraire), livrées dans la période ; triées par date,
// client, adresse, commande, article. Lots en stock du dépôt principal (AL00F, hors lots bloqués CQU150) et en-cours de
// fabrication (AB0020) des articles de la sélection, en une requête chacun.
// ÉCARTS : événements de préparation (EVE100 « PREP »), lots affectés aux commandes (AB0020L3) et détail par emplacement
// (AP00F) non repris ; lots du dépôt principal seulement.
// =====================================================================
internal sealed class PreparationDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Tronque : plus de lignes que le garde-fou de 10 000, la fiche le signale
    public async Task<(IReadOnlyList<LignePreparation> Lignes, IReadOnlyList<LotPreparation> Lots, IReadOnlyDictionary<(string, string), int> EnFabrication,
        bool Tronque)> LireAsync(int societe, string profil, CriteresPreparation c, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        var representant = await ConsultationBlDb2.RepresentantImposeAsync(cnx, Bib, societe, profil, ct) ?? c.Representant;

        var b = Bib;
        var (filtre, valeurs) = Filtre(c, representant);
        filtre = filtre.Replace("{b}", b);
        object?[] parametres = [societe, BaseDb2.AAAAMMJJ(c.Du), BaseDb2.AAAAMMJJ(c.Au), .. valeurs];
        var conditions = $"n.RSOC = ? AND n.DNSTAT = '1' AND a.CCTYPC <> '2' AND n.DNSOLD <> 'O' AND n.DNART <> ' ' AND n.DNDLIV BETWEEN ? AND ? {filtre}";
        var textes = string.Join(", ", Enumerable.Range(1, 19).Select(i => $"COALESCE(x.{(i < 10 ? $"DNTXT{i}" : $"DNTX{i}")}, '')"));

        var lignes = new List<LignePreparation>();
        await using (var rd = await Lecteur.ExecuterAsync(cnx, $"""
            SELECT n.DNDLIV, a.RGEN, a.RCLI, COALESCE(c.CNOM, ''),
                   CASE WHEN c.CQUACL IN ('4', '5') OR f.CQUACL IN ('4', '5') THEN 1 ELSE 0 END,
                   n.DNLIGA, COALESCE(g.ALFJ, n.DNFJ), COALESCE(g.ALRS, n.DNRS), COALESCE(g.ALRUE1, n.DNRUE1), COALESCE(g.ALRUE2, n.DNRUE2),
                   COALESCE(g.ALPOST, n.DNPOST), COALESCE(g.ALVILL, n.DNVILL), COALESCE(g.ALDEP, ''), COALESCE(g.ALPAY, n.DNPAY),
                   COALESCE(c.CFJ, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''), COALESCE(c.CVILLE, ''),
                   COALESCE(g.ALCOMM, c.CCOMTR, ''), COALESCE(g.ALCOM2, c.CCOMT2, ''), COALESCE(g.ALCOM3, c.CCOMT3, ''),
                   COALESCE(t.MNOM, ''), n.RDOSS, n.DNDCLE, a.CCNINT, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCECAL,
                   n.DNNLIG, n.DNART, n.DNIND, COALESCE(s.ARFIMP, ''), n.DNAPPE, n.DNDESA, n.DNQTEC - n.DNQTEL, n.DNQMAX,
                   COALESCE(s.AEMBP, 0), COALESCE(s.ATYEMB, ''), COALESCE(s.ANCAR, 0), COALESCE(s.ANPAL, 0), COALESCE(s.APDSU, 0), n.DNNORM,
                   {textes}
            FROM {b}.NEG100 n
            JOIN {b}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AA0003 f ON f.RSOC = a.RSOC AND f.RGEN = n.DNGENF AND f.RCLI = n.DNCLIF AND n.DNCLIF <> 0
            LEFT OUTER JOIN {b}.A500F g ON g.RSOC = n.RSOC AND g.RGEN = a.RGEN AND g.RCLI = a.RCLI AND g.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            LEFT OUTER JOIN {b}.B200F t ON t.RSOC = n.RSOC AND t.RTRANS = COALESCE(g.ALTRS, c.CTRANS)
            LEFT OUTER JOIN {b}.NEG100T x ON x.RSOC = n.RSOC AND x.RDOSS = n.RDOSS AND x.DNDCLE = n.DNDCLE AND x.DNNLIG = n.DNNLIG
            WHERE {conditions}
            ORDER BY n.DNDLIV, a.RGEN, a.RCLI, n.DNLIGA, n.RDOSS, n.DNDCLE, n.DNART, n.DNIND, n.DNNLIG
            FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY
            """, ct, parametres))
            while (await rd.ReadAsync(ct))
            {
                var adresse = new AdresseBl(rd.Entier(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Texte(12),
                    rd.Texte(13));
                if (adresse.RaisonSociale == "")
                    adresse = new AdresseBl(0, rd.Texte(14), rd.Texte(3), rd.Texte(15), rd.Texte(16), rd.Texte(17), rd.Texte(18), "", "");
                var lus = Enumerable.Range(45, 19).Select(rd.Texte).ToList();
                while (lus.Count > 0 && lus[^1] == "") lus.RemoveAt(lus.Count - 1);
                lignes.Add(new LignePreparation(BaseDb2.DateAAAAMMJJ(rd.Entier(0)), rd.Entier(1), rd.Entier(2), rd.Texte(3), rd.Entier(4) == 1, adresse,
                    [.. new[] { rd.Texte(19), rd.Texte(20), rd.Texte(21) }.Where(x => x != "")], rd.Texte(22), rd.Entier(23), rd.Texte(24),
                    rd.Texte(25), rd.Texte(26), BaseDb2.Date(rd.Entier(27), rd.Entier(28), rd.Entier(29)), rd.Texte(30) == "E", rd.Entier(31),
                    rd.Texte(32), rd.Texte(33), rd.Texte(34), rd.Texte(35), rd.Texte(36), Math.Max(0, rd.Entier(37)), rd.Entier(38), rd.Entier(39),
                    rd.Texte(40), rd.Entier(41), rd.Entier(42), rd.Nombre(43), rd.Texte(44), lus));
            }
        var tronque = lignes.Count > LimitesRecherche.Maximum;
        if (tronque) lignes.RemoveAt(lignes.Count - 1);
        if (lignes.Count == 0) return (lignes, [], new Dictionary<(string, string), int>(), false);

        // Articles de la sélection (déjà lus), par paquets : lots du dépôt principal (type de stock de l'article), hors lots
        // bloqués ; reste à fabriquer des OF non soldés
        var lots = new List<LotPreparation>();
        var fabrication = new Dictionary<(string, string), int>();
        foreach (var paquet in lignes.Select(l => (l.Article, l.Indice)).Distinct().Chunk(200))
        {
            var articles = $"VALUES {string.Join(", ", paquet.Select(_ => "(CAST(? AS CHAR(15)), CAST(? AS CHAR(2)))"))}";
            object?[] cles = [.. paquet.SelectMany(x => new object?[] { x.Article, x.Indice == "" ? " " : x.Indice })];
        await using (var rd = await Lecteur.ExecuterAsync(cnx, $"""
            SELECT l.PLREF, l.PLIND, l.PLLOT, l.PLQENT, l.PLDLU
            FROM {b}.AL00F l
            JOIN {b}.STO005 p ON p.RSOC = l.RSOC AND l.PLDEP = p.PSDEPP
            JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.PLREF AND s.AINDIC = l.PLIND
            WHERE l.RSOC = ? AND l.PLQENT > 0
              AND l.PLTSTO = CASE s.ATYPEA WHEN '1' THEN p.PSTSNE ELSE CASE s.ATYFAC WHEN '2' THEN p.PSTSP2 WHEN '3' THEN p.PSTSP3 ELSE p.PSTSP1 END END
              AND (l.PLREF, l.PLIND) IN ({articles})
              AND NOT EXISTS (SELECT 1 FROM {b}.CQU150 q WHERE q.RSOC = l.RSOC AND DIGITS(q.LBDOSS) = SUBSTR(l.PLLOT, 1, 5)
                              AND q.LBDCLE = SUBSTR(l.PLLOT, 6, 1) AND (q.LBTOUS = 'O' OR (q.LBART = l.PLREF AND q.LBIND = l.PLIND)))
            ORDER BY l.PLREF, l.PLIND, l.PLDTEN, l.PLLOT
            """, ct, [societe, .. cles]))
            while (await rd.ReadAsync(ct))
                lots.Add(new LotPreparation(rd.Texte(0), rd.Texte(1), rd.Texte(2), (int)rd.Nombre(3), BaseDb2.DateAAAAMMJJ(rd.Entier(4))));

        await using (var rd = await Lecteur.ExecuterAsync(cnx, $"""
            SELECT f.LAART, f.LAIND, SUM(f.LAQTEC - f.LAQTEF) FROM {b}.AB0020 f
            WHERE f.RSOC = ? AND f.LASOLD <> 'O' AND f.LAQTEC > f.LAQTEF AND (f.LAART, f.LAIND) IN ({articles})
            GROUP BY f.LAART, f.LAIND
            """, ct, [societe, .. cles]))
            while (await rd.ReadAsync(ct)) fabrication[(rd.Texte(0), rd.Texte(1))] = (int)rd.Nombre(2);
        }
        return (lignes, lots, fabrication, tronque);
    }

    internal static (string Sql, List<object?> Valeurs) Filtre(CriteresPreparation c, int? representant)
    {
        var sql = new StringBuilder();
        var valeurs = new List<object?>();
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); valeurs.AddRange(v); }
        if (!c.ArticlesNonStockes) Et("s.ASTK = 'O'");
        if (c.CompteGeneral is > 0) Et("a.RGEN = ?", c.CompteGeneral);
        if (c.CompteClient is > 0) Et("a.RCLI = ?", c.CompteClient);
        if (c.NumeroCommande is > 0) Et("n.RDOSS = ?", c.NumeroCommande);
        if (!string.IsNullOrWhiteSpace(c.Fabricant)) Et("a.CCINAT = ?", c.Fabricant.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(c.Enseigne)) Et("a.CCENSE = ?", c.Enseigne.Trim().ToUpperInvariant());
        if (representant is > 0) Et("a.CCREP = ?", representant);
        if (!string.IsNullOrWhiteSpace(c.Article)) Et("n.DNART = ?", c.Article.Trim().ToUpperInvariant());
        if (c.NumeroAdresse is > 0) Et("n.DNLIGA = ?", c.NumeroAdresse);
        if (!string.IsNullOrWhiteSpace(c.ReferenceImprime)) Et("(s.ARFIMP = ? OR a.CCRFIM = ?)", c.ReferenceImprime.Trim().ToUpperInvariant(), c.ReferenceImprime.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(c.Appel)) Et("n.DNAPPE = ?", c.Appel.Trim().ToUpperInvariant());
        var opr = "EXISTS (SELECT 1 FROM {b}.OPR015 o WHERE o.RSOC = n.RSOC AND o.ODNCDE = n.RDOSS AND o.ODNCLE = n.DNDCLE AND o.ODDLIG = n.DNNLIG AND o.ODLIVR <> 'O'";
        if (c.NumeroOpr is > 0) Et(opr + " AND o.ODNUM = ?)", c.NumeroOpr);
        if (c.Opr == FiltreOpr.AvecOpr) Et(opr + ")");
        if (c.Opr == FiltreOpr.SansOpr) Et("NOT " + opr + ")");
        return (sql.ToString(), valeurs);
    }
}
