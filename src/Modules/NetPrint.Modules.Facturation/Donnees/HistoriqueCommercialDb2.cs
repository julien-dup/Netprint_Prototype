using System.Data.Odbc;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Historique commercial (FAC070, FAC072) : lecture de ce qu'il faut pour le calculer (HistoriqueCommercial) et écriture, dans la
// transaction de l'édition définitive. En-tête FAC080, éléments FAC090 et mode de facturation de leur commande (AB001.CCFACT),
// lignes FAC100 avec leur type (FAC020), l'existence de l'article dans SPF010 et les prix de la ligne de commande (NEG100),
// paramètres AA000Z de la société et ristourne (AA0003.CRIST) du client facturé.
// =====================================================================
internal static class HistoriqueCommercialDb2
{
    public static async Task<(EnteteHistorique Entete, ParametresHistorique Parametres, List<ElementHistorique> Elements)?> LireAsync(
        OdbcConnection cnx, OdbcTransaction? tx, string bib, int societe, int numero, DateOnly date, CancellationToken ct)
    {
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
        Task<Lecteur> Lire(string sql, params object?[] v) => Lecteur.ExecuterAsync(cnx, tx, sql.Replace("{b}", bib), ct, v);

        EnteteHistorique f;
        await using (var rd = await Lire("""
            SELECT FDTYPE, FDENS, FDVEN, FDREP, FDESC, FDNECH, FDGEND, FDCLID, FDGENF, FDCLIF, FDCDEV
            FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
            """, cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            f = new EnteteHistorique(societe, numero, date, rd.Texte(0), rd.Texte(1).Trim(), rd.Texte(2).Trim(), rd.Entier(3), rd.Nombre(4), rd.Entier(5),
                rd.Entier(6), rd.Entier(7), rd.Entier(8), rd.Entier(9), rd.Texte(10).Trim());
        }

        decimal m3 = 0, m4 = 0, m5 = 0, stockage = 0, financiers = 0, generaux = 0, ristourne = 0;
        var typeCoef = "";
        await using (var rd = await Lire("SELECT CKMA3, CKMA4, CKMA5, CFRSTO, CFRFIN, CKFG1, CFTYPE FROM {b}.AA000Z WHERE RSOC = ?", societe))
            if (await rd.ReadAsync(ct))
                (m3, m4, m5, stockage, financiers, generaux, typeCoef) = (rd.Nombre(0), rd.Nombre(1), rd.Nombre(2), rd.Nombre(3), rd.Nombre(4),
                    rd.Nombre(5), rd.Texte(6).Trim());
        await using (var rd = await Lire("SELECT CRIST FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", societe, f.GeneralFacture, f.ClientFacture))
            if (await rd.ReadAsync(ct)) ristourne = rd.Nombre(0);

        var lignes = new Dictionary<int, List<LigneHistorique>>();
        await using (var rd = await Lire("""
            SELECT l.FGNEL, COALESCE(t.LTYPE, ''), l.FGQTE, l.FGDES, l.FGPER, l.FGART, l.FGIND, l.FGMTN, l.FGMTNA, l.FGTCOL, l.FGBL, l.FGBLEL,
                   l.FGBLLG,
                   (SELECT COUNT(*) FROM {b}.SPF010 a WHERE a.RSOC = l.RSOC AND a.AARTIC = l.FGART AND a.AINDIC = l.FGIND),
                   (SELECT MIN(n.DNPUS) FROM {b}.NEG100 n WHERE n.RSOC = l.RSOC AND n.RDOSS = e.FEDOSS AND n.DNDCLE = e.FECLE AND n.DNNLIG = l.FGLIGD
                        AND n.DNART = l.FGART AND n.DNIND = l.FGIND),
                   (SELECT MIN(n.DNPUT) FROM {b}.NEG100 n WHERE n.RSOC = l.RSOC AND n.RDOSS = e.FEDOSS AND n.DNDCLE = e.FECLE AND n.DNNLIG = l.FGLIGD
                        AND n.DNART = l.FGART AND n.DNIND = l.FGIND)
            FROM {b}.FAC100 l
            JOIN {b}.FAC090 e ON e.RSOC = l.RSOC AND e.RNFAC = l.RNFAC AND e.FEANNE = l.FGANNE AND e.FEMOIS = l.FGMOIS AND e.FEJOUR = l.FGJOUR
                 AND e.FENUM = l.FGNEL
            LEFT OUTER JOIN {b}.FAC020 t ON t.RSOC = l.RSOC AND t.RLIGNE = l.FGCDL
            WHERE l.RSOC = ? AND l.RNFAC = ? AND l.FGANNE = ? AND l.FGMOIS = ? AND l.FGJOUR = ?
            ORDER BY l.FGNEL, l.FGNLG   -- présentation 2 (FDPRES) : FAC199 lit FAC100L3 (ordre des BL, clé inconnue) ; ne joue qu'avec une remise %
            """, cle))
            while (await rd.ReadAsync(ct))
            {
                if (!lignes.TryGetValue(rd.Entier(0), out var liste)) lignes[rd.Entier(0)] = liste = [];
                liste.Add(new LigneHistorique(rd.Texte(1).Trim(), rd.Nombre(2), rd.Texte(3), rd.Entier(4), rd.Texte(5), rd.Texte(6), rd.Nombre(7),
                    rd.Nombre(8), rd.Nombre(9), rd.Entier(10), rd.Entier(11), rd.Entier(12), rd.Entier(13) > 0,
                    rd.Nul(14) ? null : (rd.Nombre(14), rd.Nombre(15))));
            }

        var elements = new List<ElementHistorique>();
        await using (var rd = await Lire("""
            SELECT e.FENUM, e.FEDOSS, e.FECLE, e.FECAT, e.FESCAT, e.FETYP, e.FEQTE, e.FETXC, e.FETXO, e.FENOM, e.FEMTO, e.FEFAA, e.FEORTE,
                   COALESCE(a.CCFACT, '')
            FROM {b}.FAC090 e
            LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = e.RSOC AND a.RDOSS = e.FEDOSS AND a.CCDCLE = e.FECLE
            WHERE e.RSOC = ? AND e.RNFAC = ? AND e.FEANNE = ? AND e.FEMOIS = ? AND e.FEJOUR = ?
            ORDER BY e.FENUM
            """, cle))
            while (await rd.ReadAsync(ct))
                elements.Add(new ElementHistorique(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Entier(5), rd.Nombre(6),
                    rd.Nombre(7), rd.Nombre(8), rd.Texte(9), rd.Nombre(10), rd.Texte(11), rd.Texte(12), rd.Texte(13).Trim(),
                    lignes.GetValueOrDefault(rd.Entier(0)) ?? []));
        return (f, new ParametresHistorique(m3, m4, m5, stockage, financiers, generaux, typeCoef, ristourne), elements);
    }

    // Écriture des lignes calculées (colonnes = clés du dictionnaire, toutes connues de HistoriqueCommercial : pas de saisie)
    public static async Task EcrireAsync(OdbcConnection cnx, OdbcTransaction tx, string bib, string table, IEnumerable<Dictionary<string, object>> lignes,
        CancellationToken ct)
    {
        foreach (var l in lignes)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {bib}.{table} ({string.Join(", ", l.Keys)}) VALUES ({string.Join(", ", l.Keys.Select(_ => "?"))})";
            foreach (var v in l.Values) cmd.Parameters.Valeur(v is string s && s == "" ? " " : v);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
