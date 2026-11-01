using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Données de l'édition d'une facture (FAC202P5) : FAC080 (en-tête, adresse figée), AA0003 (client facturé : adresse de
// secours, tiers, réf. fournisseur, banque ; donneur d'ordre : langue CDOP10), FAC090 / AB001 (éléments), FAC105 (textes
// d'élément), FAC100 / FAC020 (lignes), AA00081 (unités UV, modes de règlement RG, pays PY), FAC081 (textes de pied), FAC120
// (mentions ML et M2), PRT100F (banque du client, sinon par défaut), FAC010 (n° préfixé de l'année), AA0007 / PRT320F
// (réf. CEE), B000F (représentant), PRT008 (libellés traduits, programme « FACTURE »).
// =====================================================================
internal sealed class EditionFacturesDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task<DonneesEditionFacture?> LireAsync(int societe, int numero, DateOnly date, DateOnly echeance, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        Task<Lecteur> Lire(string sql, params object?[] v) => Lecteur.ExecuterAsync(cnx, sql.Replace("{b}", Bib), ct, v);
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };

        string langue;
        (string Type, string Edit, string Ens, string[] Adresse, string Att, string Ref, DateOnly? DateCde, string Plel, string Rep, string Rglt,
            string Releve, int Gen, int Cli, string Tiers, string RefFou, string Tva, int Banque, string Devise, int Fco, int Fca, string Cee,
            string Prefixe) e;
        await using (var rd = await Lire("""
            SELECT a.FDTYPE, a.FDEDIT, a.FDENS,
                   CASE WHEN a.FDNOM = ' ' THEN COALESCE(f.CNOM, '') ELSE TRIM(a.FDFJ || ' ' || a.FDNOM) END,
                   CASE WHEN a.FDNOM = ' ' THEN COALESCE(f.CRUE1, '') ELSE a.FDRUE1 END, CASE WHEN a.FDNOM = ' ' THEN COALESCE(f.CRUE2, '') ELSE a.FDRUE2 END,
                   CASE WHEN a.FDNOM = ' ' THEN TRIM(TRIM(COALESCE(f.CPOSTA, '')) || ' ' || TRIM(COALESCE(f.CVILLE, ''))) ELSE TRIM(TRIM(a.FDPOST) || ' ' || TRIM(a.FDVILL)) END,
                   COALESCE((SELECT MIN(p.TLIB1) FROM {b}.AA00081 p WHERE p.RSOC = a.RSOC AND p.RTABLE = 'PY'
                             AND p.RARG = CASE WHEN a.FDNOM = ' ' THEN f.CPAYS ELSE a.FDPAYS END), ''),
                   a.FDATT, a.FDREF, a.FDCNNE, a.FDCOIS, a.FDCOUR, a.FDPLEL,
                   COALESCE((SELECT MIN(r.INOMEX) FROM {b}.B000F r WHERE r.RSOC = a.RSOC AND r.RREPR = a.FDREP AND r.ITAG <> '*' AND a.FDREP <> 0), ''),
                   COALESCE((SELECT MIN(g.TLIB1) FROM {b}.AA00081 g WHERE g.RSOC = a.RSOC AND g.RTABLE = 'RG' AND g.RARG = a.FDRGLT), a.FDRGLT),
                   a.FDRELE, a.FDGENF, a.FDCLIF, COALESCE(f.CTIERS, ''), COALESCE(f.CREFFO, ''), COALESCE(f.CRFEXP, ''), COALESCE(f.CCBANQ, 0),
                   COALESCE(NULLIF(a.FDCDEV, ' '), 'EUR'), a.FDFACO, a.FDFACA,
                   COALESCE((SELECT MIN(n.ERFEXP) FROM {b}.PRT320F n WHERE n.RSOC = a.RSOC AND n.RENSEI = a.FDENS AND a.FDENS <> ' '),
                            (SELECT MIN(s.SRFEXP) FROM {b}.AA0007 s WHERE s.RSOC = a.RSOC), ''),
                   COALESCE((SELECT MIN(SUBSTR(x.PDESEC, 2, 1)) FROM {b}.FAC010 x WHERE x.RSOC = a.RSOC), ''),
                   COALESCE(d.CDOP10, '')
            FROM {b}.FAC080 a
            LEFT OUTER JOIN {b}.AA0003 f ON f.RSOC = a.RSOC AND f.RGEN = a.FDGENF AND f.RCLI = a.FDCLIF
            LEFT OUTER JOIN {b}.AA0003 d ON d.RSOC = a.RSOC AND d.RGEN = a.FDGEND AND d.RCLI = a.FDCLID
            WHERE a.RSOC = ? AND a.RNFAC = ? AND a.FDANNE = ? AND a.FDMOIS = ? AND a.FDJOUR = ?
            """, cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            e = (rd.Texte(0), rd.Texte(1), rd.Texte(2), [rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7)], rd.Texte(8), rd.Texte(9),
                BaseDb2.Date(rd.Entier(10), rd.Entier(11), rd.Entier(12)), rd.Texte(13), rd.Texte(14), rd.Texte(15), rd.Texte(16), rd.Entier(17),
                rd.Entier(18), rd.Texte(19), rd.Texte(20), rd.Texte(21), rd.Entier(22), rd.Texte(23), rd.Entier(24), rd.Entier(25), rd.Texte(26),
                rd.Texte(27));
            langue = rd.Texte(28);
        }

        var libelles = LibellesFacture.Francais;
        if (langue != "")
        {
            var colonnes = string.Join(", ", Enumerable.Range(1, 50).Select(i => $"TRADUC{i}"));
            await using var rd = await Lire($"SELECT {colonnes} FROM {{b}}.PRT008 WHERE MLANGUE = ? AND MPGM = 'FACTURE'", langue);
            if (await rd.ReadAsync(ct)) libelles = LibellesFacture.Pour([.. Enumerable.Range(0, 50).Select(rd.Texte)]);
        }

        // Éléments : titre (dossier, commande du client si plusieurs éléments, réf. imprimé), interlocuteur, textes FAC105
        var textes = new Dictionary<int, List<(string, bool)>>();
        await using (var rd = await Lire("""
            SELECT FTNEL, FTTXT, FTDB FROM {b}.FAC105 WHERE RSOC = ? AND RNFAC = ? AND FTANNE = ? AND FTMOIS = ? AND FTJOUR = ? ORDER BY FTNEL, FTNLG
            """, cle))
            while (await rd.ReadAsync(ct))
            {
                if (!textes.TryGetValue(rd.Entier(0), out var l)) textes[rd.Entier(0)] = l = [];
                l.Add((rd.Texte(1), rd.Texte(2) == "O"));
            }
        var elements = new List<ElementEditionFacture>();
        var interlocuteur = "";
        await using (var rd = await Lire("""
            SELECT x.FENUM, x.FEDOSS, x.FECLE, COALESCE(c.CCREFC, ''), COALESCE(c.CCDENA, 0), COALESCE(c.CCDENM, 0), COALESCE(c.CCDENJ, 0),
                   COALESCE(c.CCRFIM, ''), COALESCE(c.CCNINT, ''), x.FEDEV, x.FEMOD
            FROM {b}.FAC090 x
            LEFT OUTER JOIN {b}.AB001 c ON c.RSOC = x.RSOC AND c.RDOSS = x.FEDOSS AND c.CCDCLE = x.FECLE AND x.FEDOSS <> 0
            WHERE x.RSOC = ? AND x.RNFAC = ? AND x.FEANNE = ? AND x.FEMOIS = ? AND x.FEJOUR = ? ORDER BY x.FENUM
            """, cle))
        {
            var lus = new List<(int Num, int Dos, string Cle, string RefC, DateOnly? DateC, string RefIm, string Inter, int Dev, int Mod)>();
            while (await rd.ReadAsync(ct))
                lus.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), BaseDb2.Date(rd.Entier(4), rd.Entier(5), rd.Entier(6)), rd.Texte(7),
                    rd.Texte(8), rd.Entier(9), rd.Entier(10)));
            foreach (var x in lus)
            {
                var morceaux = new List<string>();
                if (x.Dos != 0) morceaux.Add($"{libelles.Dossier} {x.Dos:00000}/{x.Cle}");
                if (lus.Count > 1 && x.RefC != "") morceaux.Add($"{libelles.VotreCommande} {x.RefC}" + (x.DateC is { } dc ? $" {dc:dd/MM/yy}" : ""));
                if (x.Dev != 0) morceaux.Add($"Devis {x.Dev}");
                if (x.Mod != 0) morceaux.Add($"Modèle {x.Mod}");
                if (x.RefIm != "") morceaux.Add($"{libelles.VotreRef} {x.RefIm}");
                if (lus.Count > 1 && x.Inter != "") morceaux.Add(x.Inter);
                elements.Add(new ElementEditionFacture(x.Num, x.Dos, x.Cle, string.Join(" · ", morceaux), textes.GetValueOrDefault(x.Num) ?? []));
            }
            if (lus.Count == 1) interlocuteur = lus[0].Inter;
        }

        // Lignes : texte (code 9 ou type T), référence masquée pour les articles 999…, unité de vente libellée, remise en négatif
        var lignes = new List<LigneEditionFacture>();
        await using (var rd = await Lire("""
            SELECT l.FGNEL, l.FGART, l.FGIND, l.FGQTE, l.FGDES, COALESCE((SELECT MIN(u.TLIB1) FROM {b}.AA00083 u WHERE u.RSOC = l.RSOC
                   AND u.RTABLE = 'UV' AND u.RARG = l.FGUN), (SELECT MIN(u.TLIB1) FROM {b}.AA00081 u WHERE u.RSOC = l.RSOC
                   AND u.RTABLE = 'UV' AND u.RARG = l.FGUN), l.FGUN), l.FGPER, l.FGPU, l.FGMTN, l.FGCDL, COALESCE(t.LTYPE, '')
            FROM {b}.FAC100 l
            LEFT OUTER JOIN {b}.FAC020 t ON t.RSOC = l.RSOC AND t.RLIGNE = l.FGCDL
            WHERE l.RSOC = ? AND l.RNFAC = ? AND l.FGANNE = ? AND l.FGMOIS = ? AND l.FGJOUR = ? ORDER BY l.FGNEL, l.FGNLG
            """, cle))
            while (await rd.ReadAsync(ct))
            {
                var texte = rd.Texte(9) == "9" || rd.Texte(10) == "T";
                if (texte && rd.Texte(4) == "") continue;   // ligne de texte vide : pas d'espace inutile
                var article = rd.Texte(1);
                var reference = article == "" || article.All(ch => ch == '9') ? "" : $"{article} {rd.Texte(2)}".Trim();
                var signe = rd.Texte(10) == "R" ? -1 : 1;
                lignes.Add(new LigneEditionFacture(rd.Entier(0), texte ? "" : reference, rd.Nombre(3), rd.Texte(4), texte ? "" : rd.Texte(5), rd.Nombre(6),
                    signe * rd.Nombre(7), signe * rd.Nombre(8), texte));
            }

        var pied = new List<string>();
        await using (var rd = await Lire("SELECT FDTXT FROM {b}.FAC081 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ? ORDER BY FDTLIG", cle))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);

        var mentions = new Dictionary<string, List<string>>();
        await using (var rd = await Lire("SELECT TFCODE, TFLIB1, TFLIB2, TFLIB3, TFLIB4 FROM {b}.FAC120 WHERE RSOC = ? AND TFCODE IN ('ML', 'M2') AND TFTAG <> '*'",
                         societe))
            while (await rd.ReadAsync(ct)) mentions[rd.Texte(0)] = [.. Enumerable.Range(1, 4).Select(rd.Texte).Where(x => x != "")];

        BanqueFacture? banque = null;
        await using (var rd = await Lire("""
            SELECT JDES1, JIBAN, JBIC FROM {b}.PRT100F WHERE RSOC = ? AND (RBANQ = ? OR JDEFAUT = 'O') AND JIBAN <> ' '
            ORDER BY CASE WHEN RBANQ = ? THEN 0 ELSE 1 END FETCH FIRST 1 ROW ONLY
            """, societe, e.Banque, e.Banque))
            if (await rd.ReadAsync(ct) && rd.Texte(1) != "") banque = new BanqueFacture(rd.Texte(0), rd.Texte(1), rd.Texte(2));

        var avoir = e.Type == "A";
        // Référence de commande en en-tête seulement pour un seul élément (FDPLEL ≤ 1, comme FAC202P5) ; brouillon tant que la
        // facture n'est pas éditée en définitif (à éditer O ou à contrôler A)
        var unSeulElement = elements.Count <= 1;
        var entete = new EnTeteEditionFacture(numero, ReglesEditionFacture.NumeroImprime(numero, date, e.Prefixe == "O"), date, avoir,
            e.Edit is not ("E" or "S"), e.Edit == "S", e.Ens, [.. e.Adresse.Where(x => x != "")], e.Att, unSeulElement ? e.Ref : "",
            unSeulElement ? e.DateCde : null, interlocuteur, e.Cee, e.RefFou, e.Tva, e.Rep,
            avoir ? "" : e.Rglt, ReglesEditionFacture.Echeance(avoir, date, echeance, e.Releve is not ("N" or ""), libelles), e.Gen, e.Cli, e.Tiers,
            e.Devise, e.Fco != 0 ? (e.Fco, e.Fca) : null);
        return new DonneesEditionFacture(entete, elements, lignes, pied, mentions.GetValueOrDefault("ML") ?? [], mentions.GetValueOrDefault("M2") ?? [],
            banque, libelles);
    }
}
