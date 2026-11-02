using System.Data.Odbc;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Livraisons à effectuer : lignes de commande (NEG100 + AB001) non soldées, livrables (pas à la demande), de dossiers de
// négoce ou de produit fini, livrées dans la période (DNDLIV). En une requête (le WinDev relisait SPF026, EVE100 et AB0020
// ligne par ligne) :
//  - stock : AK00F de l'article dans son type de stock (STO005), dépôts disponibles et non « facturés » (B100F) ;
//  - en fabrication : quantités restant à fabriquer des OF non soldés de l'article (AB0020) ;
//  - OPR non livré (OPR015), pré-BL (VWEXP510), adresse (A500F, sinon ligne, sinon client).
// ÉCARTS : dossiers de fabrication (EXP020) et recherche par n° d'OF non repris (BL de fabrication hors lot 1) ; conditionnement
// spécifique client (SPF026) non repris.
// =====================================================================
internal sealed class LivraisonsDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task<ResultatRecherche<LigneALivrer>> RechercherAsync(int societe, string profil, CriteresLivraisons c, DateOnly aujourdhui,
        CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        // Représentant limité : imposé
        var representant = await ConsultationBlDb2.RepresentantImposeAsync(cnx, Bib, societe, profil, ct) ?? c.Representant;

        var (du, au) = c.Bornes(aujourdhui);
        var (filtre, valeurs) = Filtre(c, representant);
        filtre = filtre.Replace("{b}", Bib);
        var maximum = LimitesRecherche.Maximum;
        var b = Bib;
        await using var lecture = await Lecteur.ExecuterAsync(cnx, $"""
            SELECT n.RDOSS, n.DNDCLE, n.DNNLIG, a.CCREFC, a.RGEN, a.RCLI, COALESCE(c.CNOM, ''),
                   CASE WHEN c.CQUACL IN ('4', '5') OR fc.CQUACL IN ('4', '5') THEN '4' ELSE '' END,
                   n.DNART, n.DNIND, COALESCE(s.ARFIMP, ''), n.DNDESA, n.DNDLIV, n.DNDATB, n.DNAPPE, n.DNQTEC, n.DNQTEL, n.DNQTEF, n.DNSOLD,
                   n.DNPUS, COALESCE(u.TNUM, 1), n.DNQMAX, n.DNLIGA,
                   COALESCE(g.ALFJ, n.DNFJ), COALESCE(g.ALRS, n.DNRS), COALESCE(g.ALRUE1, n.DNRUE1), COALESCE(g.ALRUE2, n.DNRUE2),
                   COALESCE(g.ALPOST, n.DNPOST), COALESCE(g.ALVILL, n.DNVILL), COALESCE(g.ALDEP, ''), COALESCE(g.ALPAY, n.DNPAY),
                   COALESCE(c.CFJ, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''), COALESCE(c.CVILLE, ''),
                   COALESCE(c.CDEPT, ''), COALESCE(c.CPAYS, ''),
                   (SELECT MIN(o.ODNUM) FROM {b}.OPR015 o WHERE o.RSOC = n.RSOC AND o.ODNCDE = n.RDOSS AND o.ODNCLE = n.DNDCLE AND o.ODDLIG = n.DNNLIG
                     AND o.ODLIVR <> 'O'),
                   (SELECT MIN(p.HNOBL) FROM {b}.VWEXP510 p WHERE p.RSOC = n.RSOC AND p.HNODOS = n.RDOSS AND p.HCLEF = n.DNDCLE AND p.HNUMLC = n.DNNLIG
                     AND p.HENBL <> 'O'),
                   COALESCE((SELECT SUM(k.PRQENT) FROM {b}.AK00F k
                             WHERE k.RSOC = n.RSOC AND k.PRREF = n.DNART AND k.PRIND = n.DNIND
                               AND k.PRTSTO = CASE s.ATYPEA WHEN '1' THEN t.PSTSNE ELSE CASE s.ATYFAC WHEN '2' THEN t.PSTSP2 WHEN '3' THEN t.PSTSP3
                                                                                    ELSE t.PSTSP1 END END
                               AND k.PRDEP IN (SELECT d.RDEP FROM {b}.B100F d WHERE d.RSOC = n.RSOC AND d.DTAG <> '*' AND d.DEXDIS <> 'O'
                                                 AND d.DFACTUR <> 'O')), 0),
                   COALESCE((SELECT SUM(f.LAQTEC - f.LAQTEF) FROM {b}.AB0020 f WHERE f.RSOC = n.RSOC AND f.LAART = n.DNART AND f.LAIND = n.DNIND
                             AND f.LASOLD <> 'O' AND f.LAQTEC > f.LAQTEF), 0),
                   COALESCE(s.AEMBP, 0), COALESCE(s.ANCAR, 0), COALESCE(s.ANPAL, 0), COALESCE(s.APDSU, 0), COALESCE(s.ATYEMB, ''),
                   a.CCINAT, a.CCREP
            FROM {b}.NEG100 n
            JOIN {b}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AA0003 fc ON fc.RSOC = a.RSOC AND fc.RGEN = n.DNGENF AND fc.RCLI = n.DNCLIF AND n.DNCLIF <> 0
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            LEFT OUTER JOIN {b}.STO005 t ON t.RSOC = n.RSOC AND t.PSDEPP = (SELECT MIN(t2.PSDEPP) FROM {b}.STO005 t2 WHERE t2.RSOC = n.RSOC)
            LEFT OUTER JOIN {b}.AA00083 u ON u.RSOC = n.RSOC AND u.RTABLE = 'UV' AND u.RARG = n.DNUV
            LEFT OUTER JOIN {b}.A500F g ON g.RSOC = n.RSOC AND g.RGEN = a.RGEN AND g.RCLI = a.RCLI AND g.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            WHERE n.RSOC = ? AND n.DNSTAT <> '3' AND a.CCTYPC > '2' AND n.DNART <> ' ' AND n.DNDLIV BETWEEN ? AND ? {filtre}
            ORDER BY n.DNDLIV, a.RGEN, a.RCLI, n.DNLIGA, n.RDOSS, n.DNDCLE, n.DNNLIG
            FETCH FIRST {maximum + 1} ROWS ONLY
            """, ct, [societe, BaseDb2.AAAAMMJJ(du), BaseDb2.AAAAMMJJ(au), .. valeurs]);
        var lignes = new List<LigneALivrer>();
        while (await lecture.ReadAsync(ct))
        {
            var r = lecture;
            var (commande, livre, facture) = (r.Entier(15), r.Entier(16), r.Entier(17));
            var soldee = r.Texte(18) == "O";
            var reste = soldee ? 0 : Math.Max(0, commande - livre);
            var adresse = new AdresseBl(r.Entier(22), r.Texte(23), r.Texte(24), r.Texte(25), r.Texte(26), r.Texte(27), r.Texte(28), r.Texte(29),
                r.Texte(30));
            if (adresse.RaisonSociale == "")
                adresse = new AdresseBl(0, r.Texte(31), r.Texte(6), r.Texte(32), r.Texte(33), r.Texte(34), r.Texte(35), r.Texte(36), r.Texte(37));
            var (uc, cartons, palettes, poids) = ReglesLivraisons.Conditionnement(reste, r.Entier(42), r.Entier(43), r.Entier(44), r.Nombre(45), r.Texte(46));
            lignes.Add(new LigneALivrer(r.Entier(0), r.Texte(1), r.Entier(2), r.Texte(3), r.Entier(4), r.Entier(5), r.Texte(6), r.Texte(7) is "4" or "5",
                r.Texte(8), r.Texte(9), r.Texte(10), r.Texte(11), BaseDb2.DateAAAAMMJJ(r.Entier(12)), BaseDb2.DateAAAAMMJJ(r.Entier(13)),
                r.Texte(14), commande, livre, reste, soldee, adresse, r.Nul(38) ? null : r.Entier(38), r.Nul(39) ? null : r.Entier(39),
                (int)r.Nombre(40), (int)r.Nombre(41), uc, cartons, palettes, poids,
                ReglesLivraisons.MontantAFacturer(commande, facture, r.Nombre(19), r.Nombre(20)), r.Entier(21), r.Texte(47), r.Entier(48)));
        }
        return new ResultatRecherche<LigneALivrer>(lignes.Take(maximum).ToList(), lignes.Count > maximum);
    }

    internal static (string Sql, List<object?> Valeurs) Filtre(CriteresLivraisons c, int? representant)
    {
        var sql = new StringBuilder();
        var valeurs = new List<object?>();
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); valeurs.AddRange(v); }

        if (!c.LignesSoldees) Et("n.DNSOLD <> 'O'");
        if (!c.ArticlesNonStockes) Et("s.ASTK = 'O'");
        if (c.CompteClient is > 0) Et("a.RCLI = ?", c.CompteClient);
        if (c.CompteGeneral is > 0) Et("a.RGEN = ?", c.CompteGeneral);
        if (c.NumeroAdresse is > 0) Et("n.DNLIGA = ?", c.NumeroAdresse);
        if (c.NumeroCommande is > 0) Et("n.RDOSS = ?", c.NumeroCommande);
        if (Texte(c.Article) is { } art) Et("n.DNART = ?", art);
        if (Texte(c.ReferenceImprime) is { } rfi) Et("(s.ARFIMP = ? OR a.CCRFIM = ?)", rfi, rfi);
        if (Texte(c.Appel) is { } appel) Et("n.DNAPPE = ?", appel);
        if (Texte(c.Fabricant) is { } fab) Et("a.CCINAT = ?", fab);
        if (representant is > 0) Et("a.CCREP = ?", representant);
        if (Texte(c.Enseigne) is { } ens) Et("a.CCENSE = ?", ens);
        var opr = "EXISTS (SELECT 1 FROM {b}.OPR015 o WHERE o.RSOC = n.RSOC AND o.ODNCDE = n.RDOSS AND o.ODNCLE = n.DNDCLE AND o.ODDLIG = n.DNNLIG AND o.ODLIVR <> 'O'";
        if (c.NumeroOpr is > 0) Et(opr + " AND o.ODNUM = ?)", c.NumeroOpr);
        if (c.Opr == FiltreOpr.AvecOpr) Et(opr + ")");
        if (c.Opr == FiltreOpr.SansOpr) Et("NOT " + opr + ")");
        return (sql.ToString(), valeurs);
    }

    private static string? Texte(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
}
