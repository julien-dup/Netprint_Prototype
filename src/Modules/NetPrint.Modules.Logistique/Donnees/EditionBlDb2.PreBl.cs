using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Données de l'édition d'un pré-BL (EXP535P2) dans le modèle de l'édition du BL : EXP535P2 est une copie d'EXP035P2 sur les
// tables du pré-BL. Le pré-BL est présenté comme le BL qu'il deviendra (transformation EXP540) : un élément par commande,
// une ligne par appel avec la désignation de la ligne de commande, ses textes (NEG100T), les observations de l'appel et les
// n° de palettes du récapitulatif (« Palette(s) … », sauf EXP005.FEOPT4 = O), puis les lots. Adresse : A500F du pré-BL ;
// revendeur : fiche client (AA0003.CREVEN). Totaux : ceux du pré-BL s'ils sont saisis, sinon colis des appels, poids
// UC × poids d'une UC plus palettes × poids d'une palette (EXP005.FPDSP), comme la transformation.
// =====================================================================
internal sealed partial class EditionBlDb2
{
    // null si le pré-BL n'existe pas ou si son client n'est pas du représentant imposé au profil
    public async Task<DonneesEditionBl?> LirePreBlAsync(int societe, string profil, int numero, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        var representant = await ConsultationBlDb2.RepresentantImposeAsync(cnx, Bib, societe, profil, ct);

        EnteteEditionBl entete;
        bool sansNumerosPalettes;
        int poidsPalette;
        await using (var rd = await Lire(cnx, """
            SELECT h.HNOBL, h.HDEXAA, h.HDEXMM, h.HDEXJJ, COALESCE(f.ALRS, c.CNOM, ''), COALESCE(f.ALRUE1, c.CRUE1, ''), COALESCE(f.ALRUE2, c.CRUE2, ''),
                   COALESCE(f.ALPOST, c.CPOSTA, ''), COALESCE(f.ALVILL, c.CVILLE, ''),
                   COALESCE((SELECT MIN(p.TLIB1) FROM {b}.AA00081 p WHERE p.RSOC = h.RSOC AND p.RTABLE = 'PY' AND p.RARG = COALESCE(f.ALPAY, c.CPAYS)), ''),
                   h.HOBS, h.HOBS2, h.HOBS3, h.HTYPP, h.HTYPL,
                   COALESCE(t.MNOM, ''), TRIM(COALESCE(t.MPOSTA, '')) || ' ' || TRIM(COALESCE(t.MVILLE, '')), COALESCE(t.MTELEP, ''),
                   h.HMENTI, h.HPDSTT, h.HNCOLT, h.HNPALT, h.HCLIE,
                   COALESCE(c.CNOM, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''), COALESCE(c.CVILLE, ''),
                   COALESCE(c.CREFFO, ''), COALESCE(c.CRFEXP, ''), COALESCE(c.CFREX, ''),
                   COALESCE((SELECT MIN(s.SRFEXP) FROM {b}.AA0007 s WHERE s.RSOC = h.RSOC), ''),
                   COALESCE(x.FEOPT1, ''), COALESCE(x.FEOPT2, ''), COALESCE(x.FEDART, ''),
                   COALESCE(c.CREVEN, ''), COALESCE(c.CENVBL, ''), COALESCE(c.CTELEP, ''), COALESCE(c.CTELEC, ''), COALESCE(c.CEMAIL, ''),
                   COALESCE(c.CDOP10, ''), COALESCE(c.CDCABB, ''), COALESCE(c.CDOP11, ''), COALESCE(c.CREPRE, 0),
                   COALESCE(x.FEOPT4, ''), COALESCE(x.FPDSP, 0),
                   COALESCE((SELECT MIN(a.CCENSE) FROM {b}.EXP510 l JOIN {b}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.HNODOS AND a.CCDCLE = l.HCLEF
                             WHERE l.RSOC = h.RSOC AND l.HNOBL = h.HNOBL), '')
            FROM {b}.EXP509 h
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            LEFT OUTER JOIN {b}.A500F f ON f.RSOC = h.RSOC AND f.RGEN = h.HGENE AND f.RCLI = h.HCLIE AND f.ALNLIG = h.HNADRE AND h.HNADRE <> 0
            LEFT OUTER JOIN {b}.B200F t ON t.RSOC = h.RSOC AND t.RTRANS = h.HTRAN
            LEFT OUTER JOIN {b}.EXP005 x ON x.RSOC = h.RSOC
            WHERE h.RSOC = ? AND h.HNOBL = ?
            FETCH FIRST 1 ROW ONLY
            """, ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) return null;
            if (representant is { } rep && rd.Entier(43) != rep) return null;
            entete = new EnteteEditionBl(rd.Entier(0), rd.Texte(46), BaseDb2.Date(rd.Entier(1), rd.Entier(2), rd.Entier(3)),
                rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9),
                [rd.Texte(10), rd.Texte(11), rd.Texte(12)], rd.Texte(13), "", rd.Texte(14),
                rd.Texte(15), rd.Texte(16), rd.Texte(17), rd.Texte(18), rd.Entier(19), rd.Entier(20), rd.Entier(21), rd.Entier(22),
                rd.Texte(23), rd.Texte(24), rd.Texte(25), rd.Texte(26), rd.Texte(27), rd.Texte(28), rd.Texte(29), rd.Texte(30),
                rd.Texte(31), rd.Texte(32) != "N", rd.Texte(33) == "O", rd.Texte(34) == "2",
                rd.Texte(35) == "O", rd.Texte(36), rd.Texte(37), rd.Texte(38), rd.Texte(39),
                rd.Texte(40), rd.Texte(41) == "O", rd.Texte(32) == "O", rd.Texte(42) == "O");
            (sansNumerosPalettes, poidsPalette) = (rd.Texte(44) == "O", rd.Entier(45));
        }

        // Commandes : un élément par commande (dossier et clé), dans l'ordre des commandes, comme la transformation
        var textesTechniques = new Dictionary<(int, string), List<string>>();
        await using (var rd = await Lire(cnx, """
            SELECT x.RDOSS, x.CCDCLE, x.CCTXT1, x.CCTXT2, x.CCTXT3, x.CCTXT4, x.CCTXT5, x.CCTXT6, x.CCTXT7, x.CCTXT8
            FROM {b}.AB0011 x
            WHERE x.RSOC = ? AND x.CCIBL = 'O'
              AND EXISTS (SELECT 1 FROM {b}.EXP510 l WHERE l.RSOC = x.RSOC AND l.HNOBL = ? AND l.HNODOS = x.RDOSS AND l.HCLEF = x.CCDCLE)
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
                textesTechniques[(rd.Entier(0), rd.Texte(1))] = Enumerable.Range(2, 8).Select(rd.Texte).Where(x => x != "").ToList();
        var elements = new List<ElementEditionBl>();
        await using (var rd = await Lire(cnx, """
            SELECT l.HNODOS, l.HCLEF, SUM(l.DQTE), COALESCE(MIN(a.CCREFC), ''), COALESCE(MIN(a.CCDENA), 0), COALESCE(MIN(a.CCDENM), 0),
                   COALESCE(MIN(a.CCDENJ), 0), COALESCE(MIN(a.CCREP), 0), COALESCE(MIN(a.CCOPT5), ''), COALESCE(MIN(a.CCEMBP), 0),
                   COALESCE(MIN(a.CCENSE), ''), COALESCE(MIN(a.CCNORM), '')
            FROM {b}.EXP510 l
            LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.HNODOS AND a.CCDCLE = l.HCLEF
            WHERE l.RSOC = ? AND l.HNOBL = ?
            GROUP BY l.HNODOS, l.HCLEF ORDER BY l.HNODOS, l.HCLEF
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
                elements.Add(new ElementEditionBl(elements.Count + 1, "C", 2, rd.Entier(0), rd.Texte(1), rd.Entier(2), "", 0, "", rd.Texte(3),
                    BaseDb2.Date(rd.Entier(4), rd.Entier(5), rd.Entier(6)), 0, 0, "", 0, 0, "", rd.Entier(7), "", rd.Texte(8) == "O",
                    rd.Entier(9), rd.Texte(10), rd.Texte(11), textesTechniques.GetValueOrDefault((rd.Entier(0), rd.Texte(1))) ?? [], "", null));
        var numeros = elements.ToDictionary(e => (e.Dossier, e.Cle), e => e.Numero);

        // Appels : une ligne article (désignation + 2 premiers textes) puis les textes suivants par 3, comme la transformation
        var palettesParLigne = new Dictionary<int, List<string>>();
        await using (var rd = await Lire(cnx, "SELECT DNUM, DLIBPAL FROM {b}.EXP516 WHERE RSOC = ? AND DNBL = ? AND DLIBPAL <> ' ' ORDER BY DNUM, DNLIG", ct,
                         societe, numero))
            while (await rd.ReadAsync(ct))
            {
                if (!palettesParLigne.TryGetValue(rd.Entier(0), out var l)) palettesParLigne[rd.Entier(0)] = l = [];
                l.Add($"Palette(s) {rd.Texte(1)}");
            }
        var colonnesTextes = string.Join(", ", Enumerable.Range(1, 19).Select(i => $"COALESCE(t.{(i < 10 ? $"DNTXT{i}" : $"DNTX{i}")}, '')"));
        var lignes = new List<LigneEditionBl>();
        var numerosLignes = new Dictionary<int, (int Element, int Ligne)>();
        decimal poidsLignes = 0;
        var colisLignes = 0;
        await using (var rd = await Lire(cnx, $"""
            SELECT l.HNUM, l.HNODOS, l.HCLEF, l.DART, l.DIND, l.DQTE, COALESCE(n.DNDESA, ''), l.DETALI,
                   COALESCE(NULLIF(ca.APDSU, 0), NULLIF(cc.APDSU, 0), s.APDSU, 0), l.DENUC, CASE WHEN l.DEEMBPE <> 0 THEN l.DEEMBPE ELSE l.DEMBP1 END,
                   l.DEEXMEC, l.DTYEMB, COALESCE(s.AOPT1, ''), COALESCE(s.AOPT3, ''), COALESCE(s.AEMBP, 0), COALESCE(s.ARFIMP, ''),
                   COALESCE((SELECT MIN(r.ARREF) FROM {"{b}"}.SPF022 r WHERE r.RSOC = l.RSOC AND r.AARTIC = l.DART AND r.AINDIC = l.DIND
                             AND r.ARGEN = h.HGENE AND r.ARCLI = h.HCLIE), ''),
                   COALESCE(s.ANOMARC, ''),
                   CASE WHEN (SELECT MIN(u.TNUM) FROM {"{b}"}.AA00083 u WHERE u.RSOC = l.RSOC AND u.RTABLE = 'UV' AND u.RARG = s.AUNITE
                              AND u.TTAG <> '*') = 1 THEN s.AUNITE ELSE '' END,
                   COALESCE(n.DNAPPE, ''), COALESCE(n.DNDTAC, 0), COALESCE(n.DNNORM, ''), COALESCE(s.ACABBL, ''),
                   l.DOBS1, l.DOBS2, l.DOBS3, {colonnesTextes}
            FROM {"{b}"}.EXP510 l
            JOIN {"{b}"}.EXP509 h ON h.RSOC = l.RSOC AND h.HNOBL = l.HNOBL
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
            LEFT OUTER JOIN {"{b}"}.SPF026 ca ON ca.RSOC = l.RSOC AND ca.AARTIC = l.DART AND ca.AINDIC = l.DIND AND ca.EAGEN = h.HGENE
                 AND ca.EACLI = h.HCLIE AND ca.EANLIG = h.HNADRE
            LEFT OUTER JOIN {"{b}"}.SPF026 cc ON cc.RSOC = l.RSOC AND cc.AARTIC = l.DART AND cc.AINDIC = l.DIND AND cc.EAGEN = h.HGENE
                 AND cc.EACLI = h.HCLIE AND cc.EANLIG = 0
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.HNODOS AND n.DNDCLE = l.HCLEF AND n.DNNLIG = l.HNUMLC
            LEFT OUTER JOIN {"{b}"}.NEG100T t ON t.RSOC = n.RSOC AND t.RDOSS = n.RDOSS AND t.DNDCLE = n.DNDCLE AND t.DNNLIG = n.DNNLIG
            WHERE l.RSOC = ? AND l.HNOBL = ? ORDER BY l.HNODOS, l.HCLEF, l.HNUMLC, l.HNUM
            """, ct, societe, numero))
        {
            var noParElement = new Dictionary<int, int>();
            while (await rd.ReadAsync(ct))
            {
                var element = numeros[(rd.Entier(1), rd.Texte(2))];
                var textes = Enumerable.Range(27, 19).Select(rd.Texte).Where(x => x != "")
                    .Concat(new[] { rd.Texte(24), rd.Texte(25), rd.Texte(26) }.Where(x => x != ""))
                    .Concat(sansNumerosPalettes ? [] : palettesParLigne.GetValueOrDefault(rd.Entier(0)) ?? []).ToList();
                var no = noParElement[element] = noParElement.GetValueOrDefault(element) + 1;
                numerosLignes[rd.Entier(0)] = (element, no);
                var (colis, exMecompte, poidsUc) = (rd.Entier(9), rd.Entier(11), rd.Nombre(8));
                colisLignes += colis + (exMecompte > 0 ? 1 : 0);
                poidsLignes += colis * poidsUc;
                lignes.Add(new LigneEditionBl(element, no, rd.Texte(3), rd.Texte(4), rd.Entier(5),
                    [.. new[] { rd.Texte(6) }.Concat(textes.Take(2)).Where(x => x != "")], rd.Texte(7), poidsUc, colis, rd.Entier(10),
                    exMecompte > 0 ? 1 : 0, exMecompte, rd.Texte(12), rd.Texte(13) != "N", rd.Texte(14) == "O", rd.Entier(15), rd.Texte(16),
                    rd.Texte(17), rd.Texte(18), rd.Texte(19), rd.Texte(20), BaseDb2.DateAAAAMMJJ(rd.Entier(21)), rd.Texte(22), rd.Texte(23) == "O"));
                foreach (var groupe in textes.Skip(2).Chunk(3))
                {
                    no = noParElement[element] = noParElement[element] + 1;
                    lignes.Add(new LigneEditionBl(element, no, "", "", 0, [.. groupe], "", 0, 0, 0, 0, 0, "", true, false, 0, "", "", "", "", "",
                        null, ""));
                }
            }
        }

        // Lots (EXP516) regroupés par lot ; quantité fabriquée et date de sortie de fabrication comme le BL
        var codesBarres = entete.CodesBarresSociete && (entete.CodeBarreClient || lignes.Any(l => l.CodeBarreArticle));
        var quantiteFab = codesBarres ? """
            (SELECT SUM(a.LAQTEC) FROM {b}.AB0020 a WHERE a.RSOC = d.RSOC AND a.LAART = l.DART AND a.LAIND = l.DIND
              AND DIGITS(a.RDOSS) = SUBSTR(d.DNLOT, 1, 5) AND a.LADCLE = SUBSTR(d.DNLOT, 6, 1)
              AND a.LADOSC = l.HNODOS AND a.LACLDC = l.HCLEF)
            """ : "CAST(NULL AS DECIMAL(11, 0))";
        var sortieFab = entete.DateFabSurLots ? """
            (SELECT f.SFDSAA * 10000 + f.SFDSMM * 100 + f.SFDSJJ FROM {b}.DOS010 f WHERE f.RSOC = d.RSOC
              AND DIGITS(f.SFNDOS) = SUBSTR(d.DNLOT, 1, 5) AND f.SFCLEF = SUBSTR(d.DNLOT, 6, 1) AND f.SFART = l.DART AND f.SFIND = l.DIND
             ORDER BY f.SFNFAB FETCH FIRST 1 ROW ONLY)
            """ : "CAST(NULL AS DECIMAL(8, 0))";
        var lots = new List<LotEditionBl>();
        await using (var rd = await Lire(cnx, $$"""
            SELECT d.DNUM, d.DNLOT, SUM(d.DNQTPL),
                   COALESCE((SELECT MIN(f.PLLOTI) FROM {b}.AL00F f WHERE f.RSOC = d.RSOC AND f.PLTSTO = ' ' AND f.PLREF = l.DART
                             AND f.PLIND = l.DIND AND f.PLLOT = d.DNLOT), ''),
                   {{quantiteFab}}, {{sortieFab}}
            FROM {b}.EXP516 d
            JOIN {b}.EXP510 l ON l.RSOC = d.RSOC AND l.HNOBL = d.DNBL AND l.HNUM = d.DNUM
            WHERE d.RSOC = ? AND d.DNBL = ? AND d.DNTYPL = '1'
            GROUP BY d.RSOC, d.DNUM, d.DNLOT, l.DART, l.DIND, l.HNODOS, l.HCLEF ORDER BY d.DNUM, d.DNLOT
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
                if (numerosLignes.TryGetValue(rd.Entier(0), out var cle))
                    lots.Add(new LotEditionBl(cle.Element, cle.Ligne, rd.Texte(1), rd.Entier(2), rd.Texte(3),
                        rd.Texte(4) == "" ? null : rd.Entier(4), BaseDb2.DateAAAAMMJJ(rd.Entier(5))));

        var pied = new List<string>();
        await using (var rd = await Lire(cnx, "SELECT HTTXT FROM {b}.EXP512 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numero))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);

        // Totaux du pré-BL s'ils sont saisis, sinon calculés
        entete = entete with
        {
            ColisTotal = entete.ColisTotal > 0 ? entete.ColisTotal : colisLignes,
            PoidsTotal = entete.PoidsTotal > 0 ? entete.PoidsTotal
                : (int)Math.Round(poidsLignes + entete.PalettesTotal * poidsPalette, MidpointRounding.AwayFromZero),
        };
        var demandes = elements.Where(e => e.NormeCommande != "").Select(e => (e.EnseigneCommande, e.NormeCommande))
            .Concat(lignes.Where(l => l.Norme != "").Select(l => (elements[l.Element - 1].EnseigneCommande, l.Norme)));
        return new DonneesEditionBl(false, entete, elements, lignes, lots, pied, await NormesAsync(cnx, societe, demandes, entete.DateExpedition, ct),
            await LibellesAsync(cnx, entete.Langue, ct));
    }
}
