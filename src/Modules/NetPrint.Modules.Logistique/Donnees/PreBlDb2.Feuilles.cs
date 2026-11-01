using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Données des feuilles du pré-BL : feuille de préparation (ETAT_FeuilleKimmel) et feuille camion (ETAT_FeuilleCamion).
// =====================================================================
internal sealed partial class PreBlDb2
{
    // En-tête d'une feuille ; null si le pré-BL n'existe pas ou n'est pas du représentant imposé
    private async Task<EnTeteFeuillePreBl?> EnTeteFeuilleAsync(OdbcConnection cnx, int societe, int numero, int? representant, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, $"""
            SELECT {ColonnesEnTete}, h.HPDSTT
            FROM {"{b}"}.EXP509 h
            {JointuresEnTete}
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numero);
        if (!await rd.ReadAsync(ct)) return null;
        if (representant is { } r && rd.Entier(30) != r) return null;
        return new EnTeteFeuillePreBl(numero, BaseDb2.Date(rd.Entier(5), rd.Entier(6), rd.Entier(7)),
            rd.Entier(8) > 0 ? $"{rd.Entier(8)} {rd.Texte(9)}".Trim() : "", Adresse(rd), rd.Entier(2), rd.Entier(3), rd.Texte(4), rd.Entier(34),
            rd.Texte(12) == "O");
    }

    // ---------- Feuille de préparation : palettes choisies, par dépôt, emplacement, SSCC ----------
    public async Task<(EnTeteFeuillePreBl EnTete, List<PalettePreparation> Palettes)?> FeuillePreparationAsync(int societe, int numero, int? depot,
        int? representant, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var entete = await EnTeteFeuilleAsync(cnx, societe, numero, representant, ct);
        if (entete is null) return null;
        var (allee, rack, place) = (0, 0, 0);
        await using (var rd = await Lire(cnx, "SELECT PSEMP1, PSEMP2, PSEMP3 FROM {b}.STO005 WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", ct, societe))
            if (await rd.ReadAsync(ct)) (allee, rack, place) = (rd.Entier(0), rd.Entier(1), rd.Entier(2));
        var palettes = new List<PalettePreparation>();
        await using (var rd = await Lire(cnx, """
            SELECT d.DNDEP, COALESCE(j.DLIB, ''), p.DPSSCC, l.DART, l.DIND, COALESCE(s.ARFIMP, ''), COALESCE(s.ADES, ''), COALESCE(e.EMPLACEMENT, ''),
                   COALESCE(e.QUANTITE_EX, 0) - COALESCE((SELECT SUM(x.DPQTEP) FROM {b}.EXP517 x JOIN {b}.EXP509 y ON y.RSOC = x.RSOC AND y.HNOBL = x.DNBL
                       WHERE x.RSOC = p.RSOC AND x.DPSSCC = p.DPSSCC AND x.DNBL <> p.DNBL AND y.HENBL <> 'O'), 0),
                   COALESCE(e.PALETTE_GROUP, ''), CASE WHEN p.DPPICK = 'O' THEN p.DPQTEP ELSE 0 END, COALESCE(c.CMODIR, ''), p.DPOBS
            FROM {b}.EXP517 p
            JOIN {b}.EXP516 d ON d.RSOC = p.RSOC AND d.DNBL = p.DNBL AND d.DNUM = p.DNUM AND d.DNLIG = p.DNLIG
            JOIN {b}.EXP510 l ON l.RSOC = d.RSOC AND l.HNOBL = d.DNBL AND l.HNUM = d.DNUM
            JOIN {b}.EXP509 h ON h.RSOC = l.RSOC AND h.HNOBL = l.HNOBL
            LEFT OUTER JOIN {b}.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            LEFT OUTER JOIN {b}.B100F j ON j.RSOC = d.RSOC AND j.RDEP = d.DNDEP AND j.DTAG <> '*'
            WHERE p.RSOC = ? AND p.DNBL = ? AND (CAST(? AS INTEGER) = 0 OR d.DNDEP = ?)
            ORDER BY d.DNDEP, e.EMPLACEMENT, p.DPSSCC
            """, ct, societe, numero, depot ?? 0, depot ?? 0))
            while (await rd.ReadAsync(ct))
                palettes.Add(new PalettePreparation(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6),
                    ReglesFeuillesPreBl.Emplacement(rd.Texte(7), allee, rack, place), (int)rd.Nombre(8), rd.Texte(9), rd.Entier(10), rd.Texte(11),
                    rd.Texte(12)));
        return (entete, palettes);
    }

    // ---------- Feuille camion : lots des articles stockés et frais (AOPT7 = O) des pré-BL ----------
    public async Task<(List<EnTeteFeuillePreBl> EnTetes, List<LotCamion> Lots, int PoidsPalette)> FeuilleCamionAsync(int societe,
        IReadOnlyList<int> numeros, int? representant, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var entetes = new List<EnTeteFeuillePreBl>();
        foreach (var n in numeros.Distinct().Order())
            if (await EnTeteFeuilleAsync(cnx, societe, n, representant, ct) is { } e) entetes.Add(e);
        var poidsPalette = 0;
        await using (var rd = await Lire(cnx, "SELECT FPDSP FROM {b}.EXP005 WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", ct, societe))
            if (await rd.ReadAsync(ct)) poidsPalette = rd.Entier(0);
        var lots = new List<LotCamion>();
        foreach (var e in entetes)
        {
            await using var rd = await Lire(cnx, """
                SELECT l.DART, l.DIND, COALESCE(s.ARFIMP, ''), COALESCE(s.ADES, n.DNDESA, ''), COALESCE(d.DNDEP, 0), COALESCE(j.DLIN, j.DLIB, ''),
                       COALESCE(d.DLIBPAL, ''), COALESCE(d.DNPALM, 0),
                       CASE WHEN COALESCE(s.ASTK, '') = 'O'
                            THEN COALESCE((SELECT SUM(p.DPQTEP) FROM {b}.EXP517 p WHERE p.RSOC = d.RSOC AND p.DNBL = d.DNBL AND p.DNUM = d.DNUM
                                           AND p.DNLIG = d.DNLIG), d.DNQTPL)
                            ELSE l.DQTE END,
                       COALESCE(d.DNPAL, 0),
                       CASE WHEN l.DEEMBPE <> 0 THEN l.DEEMBPE ELSE COALESCE(NULLIF(ca.AEMBP, 0), NULLIF(cc.AEMBP, 0), s.AEMBP, 0) END,
                       CASE WHEN l.DENPALE <> 0 THEN l.DENPALE ELSE COALESCE(NULLIF(ca.ANPAL, 0), NULLIF(cc.ANPAL, 0), s.ANPAL, 0) END,
                       COALESCE((SELECT MIN(m.GLIBAB) FROM {b}.C000F m WHERE m.RSOC = l.RSOC AND m.GTAG <> '*'
                                 AND m.RMATIE = COALESCE(NULLIF(ca.AMATUC, 0), NULLIF(cc.AMATUC, 0), s.AMATUC)), ''),
                       COALESCE(NULLIF(ca.APDSU, 0), NULLIF(cc.APDSU, 0), s.APDSU, 0), l.HNODOS, l.HCLEF, COALESCE(a.CCREFC, ''),
                       COALESCE(s.ASTK, ''), COALESCE(n.DNPUS, 0), COALESCE(u.TNUM, 0)
                FROM {b}.EXP510 l
                JOIN {b}.EXP509 h ON h.RSOC = l.RSOC AND h.HNOBL = l.HNOBL
                LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
                LEFT OUTER JOIN {b}.EXP516 d ON d.RSOC = l.RSOC AND d.DNBL = l.HNOBL AND d.DNUM = l.HNUM AND COALESCE(s.ASTK, '') = 'O'
                LEFT OUTER JOIN {b}.SPF026 ca ON ca.RSOC = l.RSOC AND ca.AARTIC = l.DART AND ca.AINDIC = l.DIND AND ca.EAGEN = h.HGENE
                     AND ca.EACLI = h.HCLIE AND ca.EANLIG = h.HNADRE
                LEFT OUTER JOIN {b}.SPF026 cc ON cc.RSOC = l.RSOC AND cc.AARTIC = l.DART AND cc.AINDIC = l.DIND AND cc.EAGEN = h.HGENE
                     AND cc.EACLI = h.HCLIE AND cc.EANLIG = 0
                LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.HNODOS AND a.CCDCLE = l.HCLEF
                LEFT OUTER JOIN {b}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.HNODOS AND n.DNDCLE = l.HCLEF AND n.DNNLIG = l.HNUMLC
                LEFT OUTER JOIN {b}.AA00083 u ON u.RSOC = n.RSOC AND u.RTABLE = 'UV' AND u.RARG = n.DNUV AND u.TTAG <> '*'
                LEFT OUTER JOIN {b}.B100F j ON j.RSOC = d.RSOC AND j.RDEP = d.DNDEP AND j.DTAG <> '*'
                WHERE l.RSOC = ? AND l.HNOBL = ? AND (COALESCE(s.ASTK, '') = 'O' OR COALESCE(s.AOPT7, '') = 'O')
                ORDER BY l.HNODOS, l.HCLEF, l.HNUMLC, l.DART, l.DIND, d.DNDEP, d.DNLIG
                """, ct, societe, e.Numero);
            while (await rd.ReadAsync(ct))
            {
                var frais = rd.Texte(17) != "O";
                var (quantite, prix, unite) = (rd.Entier(8), rd.Nombre(18), rd.Nombre(19));
                var montant = !frais ? 0m : unite is 0 or 999999999 ? prix : Math.Round(prix * quantite / unite, 2, MidpointRounding.AwayFromZero);
                lots.Add(new LotCamion(e.Numero, rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Entier(4), rd.Texte(5), e.StockPaye,
                    rd.Texte(6), rd.Entier(7), quantite, rd.Entier(9), rd.Entier(10), rd.Entier(11), rd.Texte(12), rd.Nombre(13), rd.Entier(14),
                    rd.Texte(15), rd.Texte(16), frais, montant, frais ? prix : 0));
            }
        }
        return (entetes, lots, poidsPalette);
    }
}
