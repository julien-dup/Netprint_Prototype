using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Lectures DB2 des compléments de la consultation (lot A).
// Requêtes reprises de FEN_VisuTexteLibre, FEN_RecapSousTotaux et FEN_CalculRecap, en SQL paramétré.
// Écarts volontaires avec le WinDev signalés « ÉCART ».
// =====================================================================
internal sealed partial class ComplementsDevisDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Bibliothèque des programmes IBM i (Db2:BibliothequePgm, NP11P par défaut ; ex-Parametre.Ini BibliothequePgm)
    private string BibPgm => BaseDb2.Bibliotheque(string.IsNullOrWhiteSpace(config["Db2:BibliothequePgm"]) ? "NP11P" : config["Db2:BibliothequePgm"]);

    // ---------- Bandeau commun (déclaration des fenêtres) ----------
    public async Task<EnteteComplementBrut?> LireEnteteAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"""
            SELECT a.RGEN, a.RCLI, a.RPROSP, b.CNOM, c.DRS, a.DEDES1, a.DEDES2, a.DEREPR
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA0003 b ON a.RSOC = b.RSOC AND a.RGEN = b.RGEN AND a.RCLI = b.RCLI AND b.CTAG <> '*'
            LEFT OUTER JOIN {b}.AX00F c ON a.RSOC = c.RSOC AND a.RPROSP = c.RPROSP AND c.DTAG <> '*'
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ?
            """, societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new EnteteComplementBrut(rd.Entier(0), rd.Entier(1), rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Texte(5),
                rd.Texte(6), rd.Entier(7))
            : null;
    }

    // ---------- FEN_VisuTexteLibre ----------
    // ÉCART : les lignes sont lues dans l'ordre de leur n° (F14LIG / F24LIG) ; le WinDev ne triait pas.
    public async Task<List<string>> LireTexteLibreAsync(int societe, CleDevis cle, int element, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = element == 0
            ? Commande(cnx, $"""
                SELECT F14TXT FROM {b}.D50004
                WHERE RSOC = ? AND RDEV = ? AND RVERS = ? AND RHYPO = ? AND RCOUT = ?
                ORDER BY F14LIG
                """, societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout)
            : Commande(cnx, $"""
                SELECT F24TXT FROM {b}.D60004
                WHERE RSOC = ? AND RDEV = ? AND RVERS = ? AND RHYPO = ? AND RCOUT = ? AND RELMT = ?
                ORDER BY F24LIG
                """, societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout, element);
        var lignes = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) lignes.Add(rd.Texte(0));
        return lignes;
    }

    // ---------- FEN_RecapSousTotaux ----------
    // Sous-totaux analytiques (code ST) : opérations par section (C800F.RSTOT), matières par matière (C000F.GTOT),
    // sous-traitance par lien fournisseur-opération (AZ00F.HSTOT). Fixes = lignes DDRSMP = 0, variables = DDRSMP > 0.
    public async Task<List<SousTotalBrut>> LireSousTotauxAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        const string Montants = """
            SUM(CASE WHEN a.DDRSMP = 0 THEN a.DDVALO ELSE 0 END),
            SUM(CASE WHEN a.DDRSMP > 0 THEN a.DDVALO ELSE 0 END), c.TLIB1,
            SUM(CASE WHEN a.DDRSMP > 0 THEN ((a.DDVALO / a.DDRSQT) * a.DDRSMP) / 100 ELSE 0 END)
            """;
        const string Devis = "a.RSOC = ? AND a.RCOUT = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.DDRSQT > 0";
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"""
            SELECT b.RSTOT, 'O', {Montants}
            FROM {b}.D60002 a
            LEFT OUTER JOIN {b}.C800F b ON a.RSOC = b.RSOC AND b.RSECT = SUBSTR(a.DDREF, 1, 3)
            LEFT OUTER JOIN {b}.AA00081 c ON b.RSOC = c.RSOC AND b.RSTOT = c.RARG AND c.RTABLE = 'ST' AND c.TTAG <> '*'
            WHERE {Devis} AND a.DDTYPL = 'O'
            GROUP BY b.RSTOT, c.TLIB1
            UNION ALL
            SELECT b.GTOT, 'M', {Montants}
            FROM {b}.D60002 a
            LEFT OUTER JOIN {b}.C000F b ON a.RSOC = b.RSOC AND a.DDREF = DIGITS(b.RMATIE)
            LEFT OUTER JOIN {b}.AA00081 c ON b.RSOC = c.RSOC AND b.GTOT = c.RARG AND c.RTABLE = 'ST' AND c.TTAG <> '*'
            WHERE {Devis} AND a.DDTYPL = 'M'
            GROUP BY b.GTOT, c.TLIB1
            UNION ALL
            SELECT b.HSTOT, 'S', {Montants}
            FROM {b}.D60002 a
            LEFT OUTER JOIN {b}.AZ00F b ON a.RSOC = b.RSOC AND DIGITS(b.RFOURN) = SUBSTR(a.DDREF, 1, 3)
                 AND b.ROPE = SUBSTR(a.DDREF, 4, 3) AND a.DDTYPL = 'S'
            LEFT OUTER JOIN {b}.AA00081 c ON b.RSOC = c.RSOC AND b.HSTOT = c.RARG AND c.RTABLE = 'ST' AND c.TTAG <> '*'
            WHERE {Devis} AND a.DDTYPL = 'S'
            GROUP BY b.HSTOT, c.TLIB1
            ORDER BY 1, 2
            """,
            societe, cle.TypeCout, cle.Numero, cle.Version, cle.Hypothese,
            societe, cle.TypeCout, cle.Numero, cle.Version, cle.Hypothese,
            societe, cle.TypeCout, cle.Numero, cle.Version, cle.Hypothese);
        var lignes = new List<SousTotalBrut>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lignes.Add(new SousTotalBrut(rd.Texte(0), rd.Texte(1), rd.Nombre(2), rd.Nombre(3), rd.Texte(4), rd.Nombre(5)));
        return lignes;
    }

    // Valeur du support (papier) de tous les éléments : poids × prix, converti en KG par CONVUP si besoin.
    // null si le devis n'a aucun élément.
    public async Task<decimal?> LireValeurSupportAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"""
            SELECT SUM(CASE WHEN a.DDUA = 'KG' THEN a.DDPDSP * a.DDPA
                            ELSE {b}.CONVUP(CHAR('KG'), CHAR(a.DDUA), CHAR(a.DDFOUB), a.DDLRFP, a.DDHAFP, a.DDGRAM, a.DDPDSP) * a.DDPA END)
            FROM {b}.D60001 a
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ?
            """, societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout);
        var valeur = await cmd.ExecuteScalarAsync(ct);
        return valeur is null or DBNull ? null : Convert.ToDecimal(valeur);
    }

    // ---------- FEN_CalculRecap ----------
    // Toutes les hypothèses de la version, avec les coefficients D50009 de la version.
    // ÉCART : jointure D50009 aussi sur le type de coût (clé de la table) ; tri par quantité (le WinDev ne triait pas).
    public async Task<List<CoefficientsBrut>> LireCoefficientsAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"""
            SELECT a.RHYPO, a.DEQTED, b.DPBASE, b.DPCOE1, b.DPCOE2, b.DPCOE3, b.DPCOE4, b.DPCOE5, b.DPCOE6, b.DPCOE7,
                   b.DPCOE8, b.DPCOE9, b.DPCO10, a.DEPRA, a.DEPR, c.TNUM, a.DEUNIT
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.D50009 b ON a.RSOC = b.RSOC AND a.RCOUT = b.RCOUT AND a.RDEV = b.RDEV AND a.RVERS = b.RVERS
            LEFT OUTER JOIN {b}.AA00083 c ON a.RSOC = c.RSOC AND a.DEUNIT = c.RARG AND c.RTABLE = 'UV' AND c.TTAG <> '*'
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RCOUT = ?
            ORDER BY a.DEQTED, a.RHYPO
            """, societe, cle.Numero, cle.Version, cle.TypeCout);
        var lignes = new List<CoefficientsBrut>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var coefs = new decimal[10];
            for (var i = 0; i < 10; i++) coefs[i] = rd.Nombre(3 + i);
            lignes.Add(new CoefficientsBrut(rd.Entier(0), rd.Entier(1), rd.Texte(2), coefs, rd.Nombre(13), rd.Nombre(14),
                rd.Entier(15), rd.Texte(16)));
        }
        return lignes;
    }

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, string sql, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v);
        return cmd;
    }
}

internal sealed record EnteteComplementBrut(
    int Rgen, int Rcli, int Rprosp, string Cnom, string Drs, string Dedes1, string Dedes2, int Representant);

// Code ST (vide si la section / matière / lien n'en a pas), type de ligne O / M / S
internal sealed record SousTotalBrut(string Code, string TypeLigne, decimal Fixes, decimal Variables, string Libelle, decimal MillePlus);

internal sealed record CoefficientsBrut(
    int Hypothese, int Quantite, string Base, decimal[] Coefficients, decimal Pra, decimal PrTotal, int Tnum, string Unite);
