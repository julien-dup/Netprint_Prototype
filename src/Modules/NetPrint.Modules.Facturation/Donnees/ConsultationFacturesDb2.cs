using System.Data.Odbc;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Lecture des factures (ConsultationFactures) : FAC080 (en-tête, adresse figée, totaux posés à l'édition), FAC090 (éléments),
// FAC100 (lignes), AA0003 (clients ; adresse de la fiche si la facture n'en a pas figé), AB001 (commandes), EXP009 (date de livraison du BL), NEG100 (appel), FAC020 (type de
// ligne). Factures supprimées (FDEDIT = 'S') exclues. Représentant limité (B000F.ILIMIT) : ses factures seulement.
// =====================================================================
internal sealed class ConsultationFacturesDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private Task<Lecteur> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs) =>
        Lecteur.ExecuterAsync(cnx, sql.Replace("{b}", Bib), ct, valeurs);

    // Représentant imposé au profil (B000F.ILIMIT = O) : il ne voit que ses factures (FDREP) ; null sinon
    public async Task<int?> RepresentantImposeAsync(int societe, string profil, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await RepresentantImposeAsync(cnx, societe, profil, ct);
    }

    private async Task<int?> RepresentantImposeAsync(OdbcConnection cnx, int societe, string profil, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, "SELECT RREPR, ILIMIT FROM {b}.B000F WHERE RSOC = ? AND IPROF = ? AND ITAG <> '*' FETCH FIRST 1 ROW ONLY", ct,
            societe, profil.ToUpperInvariant());
        if (!await rd.ReadAsync(ct)) return null;
        return rd.Texte(1) == "O" ? rd.Entier(0) : null;
    }

    // Représentant limité (B000F.ILIMIT) : seules ses factures (FDREP) lui sont accessibles, aussi pour les modifier (FAC080S *IN07) ;
    // facture introuvable : vrai, l'appelant la dit inexistante
    public async Task<bool> AccessibleAsync(int societe, string profil, int numero, DateOnly date, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        if (await RepresentantImposeAsync(cnx, societe, profil, ct) is not { } r) return true;
        await using var rd = await Lire(cnx, "SELECT FDREP FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
            ct, societe, numero, date.Year, date.Month, date.Day);
        return !await rd.ReadAsync(ct) || rd.Entier(0) == r;
    }

    // En-tête : colonnes 0 à 33
    private const string ColonnesEnTete = """
        a.RNFAC, a.FDANNE, a.FDMOIS, a.FDJOUR, a.FDTYPE, a.FDGENF, a.FDCLIF, CASE WHEN a.FDNOM = ' ' THEN COALESCE(b.CNOM, '') ELSE a.FDNOM END,
        CASE WHEN a.FDNOM = ' ' THEN COALESCE(b.CVILLE, '') ELSE a.FDVILL END, a.FDGEND, a.FDCLID,
        COALESCE(h.CNOM, ''), a.FDECHA, a.FDECHM, a.FDECHJ, a.FDMTN, a.FDPOT, a.FDPOE, a.FDMES, a.FDTVA, a.FDCPT, a.FDEDIT, a.FDCOMP,
        a.FDREP, a.FDTAGE, a.FDNONE, a.FDDATEE, a.FDMANAU, a.FDMETHE, a.FDTYPEE, a.FDENS,
        CASE WHEN a.FDNOM = ' ' THEN COALESCE(b.CFJ, '') ELSE a.FDFJ END, CASE WHEN a.FDNOM = ' ' THEN COALESCE(b.CRUE1, '') ELSE a.FDRUE1 END,
        CASE WHEN a.FDNOM = ' ' THEN COALESCE(b.CRUE2, '') ELSE a.FDRUE2 END
        """;

    private static FactureListe Facture(Lecteur rd)
    {
        var date = BaseDb2.Date(rd.Entier(1), rd.Entier(2), rd.Entier(3)) ?? DateOnly.MinValue;
        var avoir = rd.Texte(4) == "A";
        var signe = avoir ? -1 : 1;
        var (ht, pot, poe, esc, tva, cpt) = (rd.Nombre(15), rd.Nombre(16), rd.Nombre(17), rd.Nombre(18), rd.Nombre(19), rd.Nombre(20));
        return new FactureListe(rd.Entier(0), date, avoir, rd.Entier(5), rd.Entier(6), rd.Texte(7), rd.Texte(8), rd.Entier(9), rd.Entier(10),
            rd.Texte(11), BaseDb2.Date(rd.Entier(12), rd.Entier(13), rd.Entier(14)), signe * ht, signe * (pot + poe), signe * esc, signe * tva,
            signe * ReglesFactures.Ttc(ht, pot, poe, esc, tva), signe * cpt, ReglesFactures.Statut(rd.Texte(21), rd.Texte(22)), rd.Entier(23),
            ReglesFactures.Envoi(rd.Texte(24), rd.Texte(25), rd.Entier(26), rd.Texte(27), rd.Texte(28), rd.Texte(29)), rd.Texte(30));
    }

    // ---------- Liste : les plus récentes d'abord, garde-fou de 10 000 lignes ----------
    public async Task<ResultatRecherche<FactureListe>> RechercherAsync(int societe, string profil, CriteresFactures c, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var representant = await RepresentantImposeAsync(cnx, societe, profil, ct);
        var sql = new StringBuilder($"""
            SELECT {ColonnesEnTete}
            FROM {"{b}"}.FAC080 a
            LEFT OUTER JOIN {"{b}"}.AA0003 h ON h.RSOC = a.RSOC AND h.RGEN = a.FDGEND AND h.RCLI = a.FDCLID
            LEFT OUTER JOIN {"{b}"}.AA0003 b ON b.RSOC = a.RSOC AND b.RGEN = a.FDGENF AND b.RCLI = a.FDCLIF
            WHERE a.RSOC = ? AND a.FDEDIT <> 'S'
            """);
        var p = new List<object?> { societe };
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); p.AddRange(v); }
        const string Element = "x.RSOC = a.RSOC AND x.RNFAC = a.RNFAC AND x.FEANNE = a.FDANNE AND x.FEMOIS = a.FDMOIS AND x.FEJOUR = a.FDJOUR";
        const string Ligne = "y.RSOC = a.RSOC AND y.RNFAC = a.RNFAC AND y.FGANNE = a.FDANNE AND y.FGMOIS = a.FDMOIS AND y.FGJOUR = a.FDJOUR";
        if (c.Numero is > 0) Et("a.RNFAC = ?", c.Numero);
        if (c.CompteGeneralFacture is > 0) Et("a.FDGENF = ?", c.CompteGeneralFacture);
        if (c.CompteClientFacture is > 0) Et("a.FDCLIF = ?", c.CompteClientFacture);
        if (c.CompteGeneral is > 0) Et("a.FDGEND = ?", c.CompteGeneral);
        if (c.CompteClient is > 0) Et("a.FDCLID = ?", c.CompteClient);
        if (c.Commande is > 0)
        {
            if (!string.IsNullOrWhiteSpace(c.Cle))
                Et($"EXISTS (SELECT 1 FROM {{b}}.FAC090 x WHERE {Element} AND x.FEDOSS = ? AND x.FECLE = ?)", c.Commande, c.Cle.Trim().ToUpperInvariant());
            else Et($"EXISTS (SELECT 1 FROM {{b}}.FAC090 x WHERE {Element} AND x.FEDOSS = ?)", c.Commande);
        }
        if (!string.IsNullOrWhiteSpace(c.ReferenceCommande))
            Et($"""
                (a.FDREF = ? OR EXISTS (SELECT 1 FROM {"{b}"}.FAC090 x JOIN {"{b}"}.AB001 z ON z.RSOC = x.RSOC AND z.RDOSS = x.FEDOSS AND z.CCDCLE = x.FECLE
                                        WHERE {Element} AND z.CCREFC = ?))
                """, c.ReferenceCommande.Trim(), c.ReferenceCommande.Trim());
        if (c.Bl is > 0) Et($"EXISTS (SELECT 1 FROM {{b}}.FAC100 y WHERE {Ligne} AND y.FGBL = ?)", c.Bl);
        if (!string.IsNullOrWhiteSpace(c.Article))
        {
            if (!string.IsNullOrWhiteSpace(c.Indice))
                Et($"EXISTS (SELECT 1 FROM {{b}}.FAC100 y WHERE {Ligne} AND y.FGART = ? AND y.FGIND = ?)", c.Article.Trim().ToUpperInvariant(),
                    c.Indice.Trim().ToUpperInvariant());
            else Et($"EXISTS (SELECT 1 FROM {{b}}.FAC100 y WHERE {Ligne} AND y.FGART = ?)", c.Article.Trim().ToUpperInvariant());
        }
        if (representant is { } r) Et("a.FDREP = ?", r);
        else if (c.Representant is > 0) Et("a.FDREP = ?", c.Representant);
        if (c.Du is { } du) Et("a.FDANNE * 10000 + a.FDMOIS * 100 + a.FDJOUR >= ?", BaseDb2.AAAAMMJJ(du));
        if (c.Au is { } au) Et("a.FDANNE * 10000 + a.FDMOIS * 100 + a.FDJOUR <= ?", BaseDb2.AAAAMMJJ(au));
        if (c.EcheanceDu is { } ed) Et("a.FDECHA * 10000 + a.FDECHM * 100 + a.FDECHJ >= ?", BaseDb2.AAAAMMJJ(ed));
        if (c.EcheanceAu is { } ea) Et("a.FDECHA * 10000 + a.FDECHM * 100 + a.FDECHJ <= ?", BaseDb2.AAAAMMJJ(ea));
        // Montant HT signé (un avoir de 100 vaut -100), contrairement au WinDev qui ignorait le signe
        if (c.MontantHtDe is { } de) Et("CASE WHEN a.FDTYPE = 'A' THEN -a.FDMTN ELSE a.FDMTN END >= ?", de);
        if (c.MontantHtA is { } aa) Et("CASE WHEN a.FDTYPE = 'A' THEN -a.FDMTN ELSE a.FDMTN END <= ?", aa);
        if (c.AvoirsSeulement) sql.Append(" AND a.FDTYPE = 'A'");
        sql.Append($" ORDER BY a.FDANNE DESC, a.FDMOIS DESC, a.FDJOUR DESC, a.RNFAC DESC FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY");

        await using var rd = await Lire(cnx, sql.ToString(), ct, [.. p]);
        var lignes = new List<FactureListe>();
        while (await rd.ReadAsync(ct)) lignes.Add(Facture(rd));
        var tronque = lignes.Count > LimitesRecherche.Maximum;
        return new ResultatRecherche<FactureListe>(tronque ? lignes[..LimitesRecherche.Maximum] : lignes, tronque);
    }

    // ---------- Fiche : en-tête, éléments, lignes (textes rattachés à la ligne qui les précède) ----------
    public async Task<FicheFacture?> LireAsync(int societe, string profil, int numero, DateOnly date, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var representant = await RepresentantImposeAsync(cnx, societe, profil, ct);
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
        FactureListe facture;
        (string Fj, string Rue1, string Rue2, string Cp, string Reglement, string Attention, string Ref, string Tva) adresse;
        await using (var rd = await Lire(cnx, $"""
            SELECT {ColonnesEnTete}, CASE WHEN a.FDNOM = ' ' THEN COALESCE(b.CPOSTA, '') ELSE a.FDPOST END, a.FDRGLT, a.FDATT, a.FDREF,
                   COALESCE(b.CRFEXP, '')
            FROM {"{b}"}.FAC080 a
            LEFT OUTER JOIN {"{b}"}.AA0003 h ON h.RSOC = a.RSOC AND h.RGEN = a.FDGEND AND h.RCLI = a.FDCLID
            LEFT OUTER JOIN {"{b}"}.AA0003 b ON b.RSOC = a.RSOC AND b.RGEN = a.FDGENF AND b.RCLI = a.FDCLIF
            WHERE a.RSOC = ? AND a.RNFAC = ? AND a.FDANNE = ? AND a.FDMOIS = ? AND a.FDJOUR = ? AND a.FDEDIT <> 'S'
            """, ct, cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            facture = Facture(rd);
            if (representant is { } r && facture.Representant != r) return null;
            adresse = (rd.Texte(31), rd.Texte(32), rd.Texte(33), rd.Texte(34), rd.Texte(35), rd.Texte(36), rd.Texte(37), rd.Texte(38));
        }
        var elements = new List<ElementFacture>();
        await using (var rd = await Lire(cnx, """
            SELECT e.FENUM, e.FEDOSS, e.FECLE, COALESCE(d.CCREFC, ''), COALESCE(d.CCDATA, 0), COALESCE(d.CCDATM, 0), COALESCE(d.CCDATJ, 0),
                   TRIM(e.FECAT) || CASE WHEN e.FESCAT <> ' ' THEN '/' || TRIM(e.FESCAT) ELSE '' END, COALESCE(d.CCRFIM, ''), COALESCE(d.CCREP, 0)
            FROM {b}.FAC090 e
            LEFT OUTER JOIN {b}.AB001 d ON d.RSOC = e.RSOC AND d.RDOSS = e.FEDOSS AND d.CCDCLE = e.FECLE AND e.FEDOSS <> 0
            WHERE e.RSOC = ? AND e.RNFAC = ? AND e.FEANNE = ? AND e.FEMOIS = ? AND e.FEJOUR = ?
            ORDER BY e.FENUM
            """, ct, cle))
            while (await rd.ReadAsync(ct))
                elements.Add(new ElementFacture(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), BaseDb2.Date(rd.Entier(4), rd.Entier(5), rd.Entier(6)),
                    rd.Texte(7), rd.Texte(8), rd.Entier(9)));

        var brutes = new List<(int Element, int Numero, string Code, string Type, string Article, string Indice, decimal Qte, string Des, decimal Pu,
            string Unite, decimal Montant, int Bl, DateOnly? Livraison, string Appel, string RefImprime)>();
        await using (var rd = await Lire(cnx, """
            SELECT l.FGNEL, l.FGNLG, l.FGCDL, COALESCE(t.LTYPE, ''), l.FGART, l.FGIND, l.FGQTE, l.FGDES, l.FGPU, l.FGUN, l.FGMTN, l.FGBL,
                   COALESCE(x.HDEXAA, 0), COALESCE(x.HDEXMM, 0), COALESCE(x.HDEXJJ, 0), COALESCE(n.DNAPPE, ''),
                   CASE WHEN d.CCTYPC = '1' THEN COALESCE(d.CCRFIM, '') ELSE COALESCE(s.ARFIMP, '') END
            FROM {b}.FAC100 l
            LEFT OUTER JOIN {b}.FAC090 e ON e.RSOC = l.RSOC AND e.RNFAC = l.RNFAC AND e.FEANNE = l.FGANNE AND e.FEMOIS = l.FGMOIS AND e.FEJOUR = l.FGJOUR
                 AND e.FENUM = l.FGNEL
            LEFT OUTER JOIN {b}.AB001 d ON d.RSOC = e.RSOC AND d.RDOSS = e.FEDOSS AND d.CCDCLE = e.FECLE AND e.FEDOSS <> 0
            LEFT OUTER JOIN {b}.NEG100 n ON n.RSOC = e.RSOC AND n.RDOSS = e.FEDOSS AND n.DNDCLE = e.FECLE AND n.DNNLIG = l.FGLIGD AND l.FGLIGD <> 0
            LEFT OUTER JOIN {b}.EXP009 x ON x.RSOC = l.RSOC AND x.HNOBL = l.FGBL AND l.FGBL <> 0
            LEFT OUTER JOIN {b}.FAC020 t ON t.RSOC = l.RSOC AND t.RLIGNE = l.FGCDL
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.FGART AND s.AINDIC = l.FGIND AND l.FGART <> ' '
            WHERE l.RSOC = ? AND l.RNFAC = ? AND l.FGANNE = ? AND l.FGMOIS = ? AND l.FGJOUR = ?
            ORDER BY l.FGNEL, l.FGNLG
            """, ct, cle))
            while (await rd.ReadAsync(ct))
                brutes.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Nombre(6), rd.Texte(7), rd.Nombre(8),
                    rd.Texte(9), rd.Nombre(10), rd.Entier(11), BaseDb2.Date(rd.Entier(12), rd.Entier(13), rd.Entier(14)), rd.Texte(15), rd.Texte(16)));

        var lignes = ReglesFactures.Lignes(facture.Avoir, brutes.Select(b => new LigneFacture(b.Element, b.Numero, b.Code, b.Type, b.Article, b.Indice,
            b.Qte, b.Des, [], b.Pu, b.Unite, b.Montant, b.Bl, b.Livraison, b.Appel, b.RefImprime)).ToList());
        return new FicheFacture(facture, adresse.Fj, adresse.Rue1, adresse.Rue2, adresse.Cp, adresse.Reglement, adresse.Attention, adresse.Ref,
            adresse.Tva, elements, lignes);
    }
}
