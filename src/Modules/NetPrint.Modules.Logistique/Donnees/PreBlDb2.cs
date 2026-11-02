using System.Data.Odbc;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Pré-BL : EXP509 (en-tête), EXP510 (lignes de commande), EXP516 (lots), EXP517 (palettes SSCC), EXP512 (pied) ;
// statut des palettes dans PROD_ENTITE (pointage des terminaux) ; variante sans palette : EXP005.FEPREB.
// Adresse : A500F (EXP509.HNADRE), sinon fiche client.
// =====================================================================
internal sealed partial class PreBlDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private Task<Lecteur> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs) =>
        Lecteur.ExecuterAsync(cnx, null, sql.Replace("{b}", Bib), ct, valeurs);

    // En-tête : client, adresse (A500F sinon client), transporteur ; colonnes 0 à 33
    private const string ColonnesEnTete = """
        h.HNOBL, h.HENBL, h.HGENE, h.HCLIE, COALESCE(c.CNOM, ''), h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HTRAN, COALESCE(t.MNOM, ''),
        h.HPROF, h.HDCREA, h.HSTOCKP, h.HNADRE,
        COALESCE(f.ALFJ, ''), COALESCE(f.ALRS, ''), COALESCE(f.ALRUE1, ''), COALESCE(f.ALRUE2, ''), COALESCE(f.ALPOST, ''),
        COALESCE(f.ALVILL, ''), COALESCE(f.ALDEP, ''), COALESCE(f.ALPAY, ''),
        COALESCE(c.CFJ, ''), COALESCE(c.CNOM, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''),
        COALESCE(c.CVILLE, ''), COALESCE(c.CDEPT, ''), COALESCE(c.CPAYS, ''), COALESCE(c.CREPRE, 0),
        h.HTYPL, h.HTYPP, h.HMENTI
        """;

    private const string JointuresEnTete = """
        LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
        LEFT OUTER JOIN {b}.A500F f ON f.RSOC = h.RSOC AND f.RGEN = h.HGENE AND f.RCLI = h.HCLIE AND f.ALNLIG = h.HNADRE AND h.HNADRE <> 0
        LEFT OUTER JOIN {b}.B200F t ON t.RSOC = h.RSOC AND t.RTRANS = h.HTRAN AND t.MTAG <> '*'
        """;

    // Palettes rattachées à un lot d'une ligne du pré-BL (comme la fiche, qui ignore les palettes orphelines)
    private const string PalettesDesLots = """
        {b}.EXP517 p
        JOIN {b}.EXP516 d ON d.RSOC = p.RSOC AND d.DNBL = p.DNBL AND d.DNUM = p.DNUM AND d.DNLIG = p.DNLIG
        JOIN {b}.EXP510 l ON l.RSOC = d.RSOC AND l.HNOBL = d.DNBL AND l.HNUM = d.DNUM
        LEFT OUTER JOIN {b}.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC
        WHERE p.RSOC = h.RSOC AND p.DNBL = h.HNOBL
        """;

    private static AdresseBl Adresse(Lecteur rd)
    {
        if (rd.Texte(15) != "")
            return new AdresseBl(rd.Entier(13), rd.Texte(14), rd.Texte(15), rd.Texte(16), rd.Texte(17), rd.Texte(18), rd.Texte(19), rd.Texte(20), rd.Texte(21));
        return new AdresseBl(0, rd.Texte(22), rd.Texte(23), rd.Texte(24), rd.Texte(25), rd.Texte(26), rd.Texte(27), rd.Texte(28), rd.Texte(29));
    }

    // ---------- Liste (FEN_GestionPreBL), les plus récents d'abord ----------
    public async Task<ResultatRecherche<ElementPreBl>> RechercherAsync(int societe, CriteresPreBl c, int? representant, CancellationToken ct)
    {
        var sql = new StringBuilder($"""
            SELECT {ColonnesEnTete},
                   (SELECT COUNT(*) FROM {"{b}"}.EXP510 l WHERE l.RSOC = h.RSOC AND l.HNOBL = h.HNOBL),
                   (SELECT COUNT(*) FROM {PalettesDesLots}),
                   (SELECT COUNT(*) FROM {PalettesDesLots} AND e.STATUT = '4'),
                   (SELECT COUNT(*) FROM {PalettesDesLots} AND p.DPPICK = 'O')
            FROM {"{b}"}.EXP509 h
            {JointuresEnTete}
            WHERE h.RSOC = ?
            """);
        var p = new List<object?> { societe };
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); p.AddRange(v); }
        if (c.Numero is > 0) Et("h.HNOBL = ?", c.Numero);
        if (c.CompteGeneral is > 0) Et("h.HGENE = ?", c.CompteGeneral);
        if (c.CompteClient is > 0) Et("h.HCLIE = ?", c.CompteClient);
        if (c.Commande is > 0 || !string.IsNullOrWhiteSpace(c.Cle))
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM {b}.EXP510 x WHERE x.RSOC = h.RSOC AND x.HNOBL = h.HNOBL");
            if (c.Commande is > 0) { sql.Append(" AND x.HNODOS = ?"); p.Add(c.Commande); }
            if (!string.IsNullOrWhiteSpace(c.Cle)) { sql.Append(" AND x.HCLEF = ?"); p.Add(c.Cle.Trim().ToUpperInvariant()); }
            sql.Append(')');
        }
        if (c.DateDepart is { } d) Et("h.HDEXAA = ? AND h.HDEXMM = ? AND h.HDEXJJ = ?", d.Year, d.Month, d.Day);
        if (!c.AvecLivres) sql.Append(" AND h.HENBL <> 'O'");
        if (representant is { } r) Et("c.CREPRE = ?", r);
        sql.Append($" ORDER BY h.HNOBL DESC FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY");

        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, sql.ToString(), ct, [.. p]);
        var lignes = new List<ElementPreBl>();
        while (await rd.ReadAsync(ct))
            lignes.Add(new ElementPreBl(rd.Entier(0), ReglesPreBl.Etat(rd.Texte(1)), rd.Entier(2), rd.Entier(3), rd.Texte(4),
                BaseDb2.Date(rd.Entier(5), rd.Entier(6), rd.Entier(7)), Adresse(rd), rd.Entier(8), rd.Texte(9), rd.Texte(10),
                BaseDb2.DateAAAAMMJJ(rd.Entier(11)), rd.Texte(12) == "O", rd.Entier(34), rd.Entier(35), rd.Entier(36), rd.Entier(37),
                ReglesPreBl.Pret(ReglesPreBl.Etat(rd.Texte(1)), rd.Entier(35), rd.Entier(36), rd.Entier(37))));
        var tronque = lignes.Count > LimitesRecherche.Maximum;
        return new ResultatRecherche<ElementPreBl>(tronque ? lignes[..LimitesRecherche.Maximum] : lignes, tronque);
    }

    // ---------- Fiche : en-tête, lignes, lots, palettes et leur statut, pied ----------
    public async Task<FichePreBl?> LireAsync(int societe, int numero, int? representant, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        FichePreBl fiche;
        await using (var rd = await Lire(cnx, $"""
            SELECT {ColonnesEnTete}, h.HOBS, h.HOBS2, h.HOBS3, h.HNCOLT, h.HNPALT, h.HPDSTT,
                   COALESCE((SELECT MAX(x.FEPREB) FROM {"{b}"}.EXP005 x WHERE x.RSOC = h.RSOC), '')
            FROM {"{b}"}.EXP509 h
            {JointuresEnTete}
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) return null;
            if (representant is { } r && rd.Entier(30) != r) return null;
            fiche = new FichePreBl(numero, ReglesPreBl.Etat(rd.Texte(1)), rd.Texte(40) == "O", rd.Entier(2), rd.Entier(3), rd.Texte(4), Adresse(rd),
                BaseDb2.Date(rd.Entier(5), rd.Entier(6), rd.Entier(7)), rd.Entier(8), rd.Texte(9), rd.Texte(31), rd.Texte(32),
                [.. new[] { rd.Texte(34), rd.Texte(35), rd.Texte(36) }.Where(o => o != "")], rd.Texte(33), rd.Entier(37), rd.Entier(38),
                rd.Entier(39), rd.Texte(12) == "O", rd.Texte(10), BaseDb2.DateAAAAMMJJ(rd.Entier(11)), [], [], "");
        }

        // Palettes par lot
        var palettes = new Dictionary<(int, int), List<PalettePreBl>>();
        await using (var rd = await Lire(cnx, """
            SELECT p.DNUM, p.DNLIG, p.DPSSCC, p.DPQTEP, p.DPPICK, COALESCE(e.STATUT, ''), p.DPOBS
            FROM {b}.EXP517 p
            LEFT OUTER JOIN {b}.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC
            WHERE p.RSOC = ? AND p.DNBL = ? ORDER BY p.DNUM, p.DNLIG, p.DPSSCC
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var cle = (rd.Entier(0), rd.Entier(1));
                if (!palettes.TryGetValue(cle, out var l)) palettes[cle] = l = [];
                l.Add(new PalettePreBl(rd.Texte(2), rd.Entier(3), rd.Texte(4) == "O", rd.Texte(5), rd.Texte(6)));
            }

        // Lots par ligne
        var lots = new Dictionary<int, List<LotPreBl>>();
        await using (var rd = await Lire(cnx, """
            SELECT DNUM, DNLIG, DNTYPL, DNLOT, DNDEP, DNQTPL, DNPAL, DNPALM, DNUC, DLIBPAL, DCLOTU, DDOBS1, DDOBS2
            FROM {b}.EXP516 WHERE RSOC = ? AND DNBL = ? ORDER BY DNUM, DNLIG
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                if (!lots.TryGetValue(rd.Entier(0), out var l)) lots[rd.Entier(0)] = l = [];
                l.Add(new LotPreBl(rd.Entier(1), rd.Texte(2) == "2", rd.Texte(3), rd.Entier(4), rd.Entier(5), rd.Entier(6), rd.Entier(7), rd.Entier(8),
                    rd.Texte(9), rd.Texte(10) == "O", [.. new[] { rd.Texte(11), rd.Texte(12) }.Where(o => o != "")],
                    palettes.GetValueOrDefault((rd.Entier(0), rd.Entier(1))) ?? []));
            }

        var lignes = new List<LignePreBl>();
        await using (var rd = await Lire(cnx, """
            SELECT l.HNUM, l.HNODOS, l.HCLEF, l.HNUMLC, COALESCE(a.CCREFC, ''), COALESCE(n.DNAPPE, ''), l.DART, l.DIND, COALESCE(s.ARFIMP, ''),
                   l.DQTE, l.DOBS1, l.DOBS2, l.DOBS3, l.DAPPSOLD, l.DETALI, l.DENPAL, l.DENUC, l.DEEXMEC, COALESCE(s.ASTK, '')
            FROM {b}.EXP510 l
            LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.HNODOS AND a.CCDCLE = l.HCLEF
            LEFT OUTER JOIN {b}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.HNODOS AND n.DNDCLE = l.HCLEF AND n.DNNLIG = l.HNUMLC
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
            WHERE l.RSOC = ? AND l.HNOBL = ? ORDER BY l.HNUM
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
                lignes.Add(new LignePreBl(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7),
                    rd.Texte(8), rd.Entier(9), [.. new[] { rd.Texte(10), rd.Texte(11), rd.Texte(12) }.Where(o => o != "")], rd.Texte(13) == "O",
                    rd.Texte(14), rd.Entier(15), rd.Entier(16), rd.Entier(17), rd.Texte(18) != "O", lots.GetValueOrDefault(rd.Entier(0)) ?? []));

        var pied = new List<string>();
        await using (var rd = await Lire(cnx, "SELECT HTTXT FROM {b}.EXP512 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numero))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);

        fiche = fiche with { Lignes = lignes, Pied = pied };
        return fiche with { Refus = ReglesPreBl.RefusTransformation(fiche) };
    }
}
