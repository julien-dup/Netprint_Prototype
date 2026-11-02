using System.Data.Odbc;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Paramètres société des OPR : 1 commande = 1 OPR (DOS005.POPT08), articles non stockés proposés (PARTFR), dépôt principal
internal sealed record ParametresOpr(bool UneCommandeParOpr, bool ArticlesNonStockes, int DepotPrincipal);

// En-tête d'un OPR pour l'ajout : client, adresse, sous-traitance, commande et date de sa première ligne
internal sealed record ReferenceOpr(int Numero, string Statut, int CompteGeneral, int CompteClient, int NumeroAdresse, AdresseBl Adresse,
    bool SousTraite, int Dossier, string Cle, DateOnly? DatePrevue, int Representant = 0);

// =====================================================================
// OPR010 (en-tête), OPR015 (lignes), OPR016 (texte de ligne), compteur AA00082 « NQ », verrou PRTLCK « OPR ».
// Lectures : liste par élément (OPR + commande), lignes de commande candidates, fiche. Écritures en une transaction.
// =====================================================================
internal sealed partial class OprDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private Task<Lecteur> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs) =>
        Lecteur.ExecuterAsync(cnx, tx, sql.Replace("{b}", Bib), ct, valeurs);

    private async Task<int> Executer(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql.Replace("{b}", Bib);
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ParametresOpr> ParametresAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, """
            SELECT COALESCE(d.POPT08, ''), COALESCE(d.PARTFR, ''), COALESCE(s.PSDEPP, 1)
            FROM {b}.AA0007 c
            LEFT OUTER JOIN {b}.DOS005 d ON d.RSOC = c.RSOC
            LEFT OUTER JOIN {b}.STO005 s ON s.RSOC = c.RSOC
            WHERE c.RSOC = ? FETCH FIRST 1 ROW ONLY
            """, ct, societe);
        if (!await rd.ReadAsync(ct)) return new ParametresOpr(false, false, 1);
        return new ParametresOpr(rd.Texte(0) == "O", rd.Texte(1) == "O", rd.Entier(2) == 0 ? 1 : rd.Entier(2));
    }

    // Les 8 options PRT000P1 d'un programme pour le profil (le profil prime sur son groupe) ; null = aucun paramétrage.
    // Même lecture que le module Ventes : à regrouper dans le Noyau (docs/points-en-attente.md).
    public async Task<IReadOnlyList<string>?> OptionsProgrammeAsync(string programme, string profil, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        const string options = "AOPT01, AOPT02, AOPT03, AOPT04, AOPT05, AOPT06, AOPT07, AOPT08";
        await using var cnx = await OuvrirAsync(ct);
        await using (var rd = await Lire(cnx, null, $"SELECT {options} FROM {{b}}.PRT000P1 WHERE APGM = ? AND AUSR = ? FETCH FIRST 1 ROW ONLY", ct,
                         programme, profil))
            if (await rd.ReadAsync(ct)) return Options(rd);
        var groupe = "";
        await using (var rd = await Lire(cnx, null, "SELECT AGROUP FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil))
            if (await rd.ReadAsync(ct)) groupe = rd.Texte(0);
        if (groupe == "") return null;
        await using (var rd = await Lire(cnx, null, $"SELECT {options} FROM {{b}}.PRT000P1 WHERE APGM = ? AND AGROUP = ? FETCH FIRST 1 ROW ONLY", ct,
                         programme, groupe))
            if (await rd.ReadAsync(ct)) return Options(rd);
        return null;

        static List<string> Options(Lecteur rd) => Enumerable.Range(0, 8).Select(rd.Texte).ToList();
    }

    public Task<int?> RepresentantImposeAsync(int societe, string profil, CancellationToken ct) =>
        UtiliserConnexionAsync(cnx => ConsultationBlDb2.RepresentantImposeAsync(cnx, Bib, societe, profil, ct), ct);

    private async Task<T> UtiliserConnexionAsync<T>(Func<OdbcConnection, Task<T>> action, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await action(cnx);
    }

    // ---------- Adresse effective (FEN_GestionOPR, FEN_SelectionLignesCde) : A500F (n° d'adresse de la ligne), sinon adresse
    // saisie sur la ligne de commande (NEG100.DNRS…), sinon fiche client. Colonnes attendues à partir de debut :
    // DNLIGA, ALFJ, ALRS, ALRUE1, ALRUE2, ALPOST, ALVILL, ALDEP, ALPAY, DNFJ, DNRS, DNRUE1, DNRUE2, DNPOST, DNVILL, DNPAY,
    // CFJ, CNOM, CRUE1, CRUE2, CPOSTA, CVILLE, CDEPT, CPAYS (24 colonnes) ----------
    private const string ColonnesAdresse = """
        COALESCE(n.DNLIGA, 0), COALESCE(f.ALFJ, ''), COALESCE(f.ALRS, ''), COALESCE(f.ALRUE1, ''), COALESCE(f.ALRUE2, ''),
        COALESCE(f.ALPOST, ''), COALESCE(f.ALVILL, ''), COALESCE(f.ALDEP, ''), COALESCE(f.ALPAY, ''),
        COALESCE(n.DNFJ, ''), COALESCE(n.DNRS, ''), COALESCE(n.DNRUE1, ''), COALESCE(n.DNRUE2, ''), COALESCE(n.DNPOST, ''),
        COALESCE(n.DNVILL, ''), COALESCE(n.DNPAY, ''),
        COALESCE(c.CFJ, ''), COALESCE(c.CNOM, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''),
        COALESCE(c.CVILLE, ''), COALESCE(c.CDEPT, ''), COALESCE(c.CPAYS, '')
        """;

    private static AdresseBl Adresse(Lecteur rd, int i)
    {
        var numero = rd.Entier(i);
        if (numero != 0 && rd.Texte(i + 2) != "")
            return new AdresseBl(numero, rd.Texte(i + 1), rd.Texte(i + 2), rd.Texte(i + 3), rd.Texte(i + 4), rd.Texte(i + 5), rd.Texte(i + 6),
                rd.Texte(i + 7), rd.Texte(i + 8));
        if (rd.Texte(i + 10) != "")
        {
            var cp = rd.Texte(i + 13);
            return new AdresseBl(0, rd.Texte(i + 9), rd.Texte(i + 10), rd.Texte(i + 11), rd.Texte(i + 12), cp, rd.Texte(i + 14),
                cp.Length >= 2 ? cp[..2] : cp, rd.Texte(i + 15));
        }
        return new AdresseBl(0, rd.Texte(i + 16), rd.Texte(i + 17), rd.Texte(i + 18), rd.Texte(i + 19), rd.Texte(i + 20), rd.Texte(i + 21),
            rd.Texte(i + 22), rd.Texte(i + 23));
    }

    // ---------- Liste des OPR : un élément par OPR et commande, valeurs de sa première ligne ----------
    public async Task<ResultatRecherche<ElementOpr>> RechercherAsync(int societe, CriteresOpr c, int? representant, CancellationToken ct)
    {
        var sql = new StringBuilder($"""
            WITH e AS (SELECT RSOC, ODNUM, ODNCDE, ODNCLE, MIN(ODNLIG) AS PREMIERE, COUNT(*) AS NB FROM {"{b}"}.OPR015 WHERE RSOC = ?
                       GROUP BY RSOC, ODNUM, ODNCDE, ODNCLE)
            SELECT o.OENUM, e.ODNCDE, e.ODNCLE, o.OESTAT, o.OEPROF, o.OEDATE, e.NB, l.ODDTLV, l.ODTRS, COALESCE(t.MNOM, ''),
                   COALESCE(a.RGEN, 0), COALESCE(a.RCLI, 0), COALESCE(a.CCREFC, ''), COALESCE(c.CQUACL, ''), COALESCE(a.CCOP11, ''),
                   (SELECT COUNT(*) FROM {"{b}"}.OPR015 x WHERE x.RSOC = o.RSOC AND x.ODNUM = o.OENUM AND x.ODQPREP > 0)
                   + (SELECT COUNT(*) FROM {"{b}"}.PROD_ENTITE p WHERE p.SOCIETE = o.RSOC AND p.NUMERO_OPR = o.OENUM),
                   {ColonnesAdresse}
            FROM {"{b}"}.OPR010 o
            JOIN e ON e.RSOC = o.RSOC AND e.ODNUM = o.OENUM
            JOIN {"{b}"}.OPR015 l ON l.RSOC = e.RSOC AND l.ODNUM = e.ODNUM AND l.ODNLIG = e.PREMIERE
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.ODNCDE AND n.DNDCLE = l.ODNCLE AND n.DNNLIG = l.ODDLIG
            LEFT OUTER JOIN {"{b}"}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.ODNCDE AND a.CCDCLE = l.ODNCLE
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.A500F f ON f.RSOC = a.RSOC AND f.RGEN = a.RGEN AND f.RCLI = a.RCLI AND f.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            LEFT OUTER JOIN {"{b}"}.B200F t ON t.RSOC = l.RSOC AND t.RTRANS = l.ODTRS AND t.MTAG <> '*'
            WHERE o.RSOC = ?
            """);
        var p = new List<object?> { societe, societe };
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); p.AddRange(v); }
        if (c.CompteGeneral is > 0) Et("a.RGEN = ?", c.CompteGeneral);
        if (c.CompteClient is > 0) Et("a.RCLI = ?", c.CompteClient);
        if (c.Commande is > 0) Et("e.ODNCDE = ?", c.Commande);
        if (!string.IsNullOrWhiteSpace(c.Cle)) Et("e.ODNCLE = ?", c.Cle.Trim().ToUpperInvariant());
        if (c.DatePrevue is { } d)
            Et("EXISTS (SELECT 1 FROM {b}.OPR015 x WHERE x.RSOC = o.RSOC AND x.ODNUM = o.OENUM AND x.ODDTLV = ?)", BaseDb2.AAAAMMJJ(d));
        if (c.NumeroOpr is > 0) Et("o.OENUM = ?", c.NumeroOpr);
        if (!c.AvecLivres) sql.Append(" AND o.OESTAT <> '4'");
        if (representant is { } r) Et("a.CCREP = ?", r);
        sql.Append($" ORDER BY o.OENUM DESC, e.ODNCDE DESC, e.ODNCLE FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY");

        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, sql.ToString(), ct, [.. p]);
        var lignes = new List<ElementOpr>();
        while (await rd.ReadAsync(ct))
        {
            var etat = rd.Texte(3) == "4" ? EtatsOpr.Livre : rd.Entier(15) > 0 ? EtatsOpr.EnPreparation : EtatsOpr.APreparer;
            var adresse = Adresse(rd, 16);
            lignes.Add(new ElementOpr(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(10), rd.Entier(11), rd.Texte(33), rd.Texte(12),
                BaseDb2.DateAAAAMMJJ(rd.Entier(7)), adresse, rd.Entier(8), rd.Texte(9), etat, rd.Texte(13) == "5", rd.Texte(4),
                BaseDb2.DateAAAAMMJJ(rd.Entier(5)), rd.Entier(6), rd.Texte(14) == "O"));
        }
        var tronque = lignes.Count > LimitesRecherche.Maximum;
        return new ResultatRecherche<ElementOpr>(tronque ? lignes[..LimitesRecherche.Maximum] : lignes, tronque);
    }

    // ---------- Lignes de commande à mettre en préparation (FEN_SelectionLignesCde) ----------
    // Commandes 3 / 4 non taguées, hors option 10, lignes normales non soldées avec un reste, absentes de tout OPR non livré ;
    // articles stockés seulement sauf PARTFR. Cles : lignes précises (relecture avant écriture).
    public async Task<ResultatRecherche<LigneCandidateOpr>> CandidatesAsync(int societe, CriteresCandidatesOpr c, ParametresOpr parametres,
        int? representant, IReadOnlyList<(int Dossier, string Cle, int Ligne)>? cles, OdbcConnection? connexion, OdbcTransaction? tx,
        CancellationToken ct)
    {
        var sql = new StringBuilder($"""
            SELECT n.RDOSS, n.DNDCLE, n.DNNLIG, a.RGEN, a.RCLI, COALESCE(a.CCREFC, ''), n.DNAPPE, n.DNART, n.DNIND, COALESCE(s.ARFIMP, ''),
                   COALESCE(s.ADES, n.DNDESA), n.DNDLIV, n.DNDATB, n.DNQTEC - n.DNQTEL, n.DNQMAX, COALESCE(c.CTRANS, 0), COALESCE(t.MNOM, ''),
                   COALESCE(a.CCOP11, ''), COALESCE(s.ASTK, ''), n.DNOPT5, COALESCE(c.CQUACL, ''), COALESCE(s.ANOMARC, ''),
                   COALESCE((SELECT MAX(TRIM(v.EVTXT1) || ' ' || TRIM(v.EVTXT2) || ' ' || TRIM(v.EVTXT3) || ' ' || TRIM(v.EVTXT4)) FROM {"{b}"}.EVE100 v
                             WHERE v.RSOC = n.RSOC AND v.EVCDC = n.RDOSS AND v.EVCDCC = n.DNDCLE AND v.EVCDCL = n.DNNLIG AND v.EVTYPE = 'PREP'), ''),
                   CASE WHEN (c.CDOPT9 = 'O' OR s.AOPT5 = 'O') AND (SELECT COUNT(*) FROM {"{b}"}.AB0020 z WHERE z.RSOC = n.RSOC AND z.LAART = n.DNART
                             AND z.LAIND = n.DNIND AND z.LADOSC = n.RDOSS AND z.LACLDC = n.DNDCLE AND z.LAQTEC > 0
                             AND DIGITS(z.RDOSS) CONCAT z.LADCLE NOT IN (SELECT m.MVLOT FROM {"{b}"}.A700F m WHERE m.RSOC = n.RSOC AND m.MVDEP = ?
                                 AND m.MVREF = n.DNART AND m.MVIND = n.DNIND AND m.MVCMVT = 'EX')) > 0 THEN 1 ELSE 0 END,
                   {ColonnesAdresse}
            FROM {"{b}"}.NEG100 n
            JOIN {"{b}"}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.A500F f ON f.RSOC = a.RSOC AND f.RGEN = a.RGEN AND f.RCLI = a.RCLI AND f.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            LEFT OUTER JOIN {"{b}"}.B200F t ON t.RSOC = c.RSOC AND t.RTRANS = c.CTRANS AND t.MTAG <> '*'
            WHERE n.RSOC = ? AND a.CCTYPC IN ('3', '4') AND a.CCOP10 <> 'O' AND a.CCTAGS = ' ' AND n.DNSTAT = '1' AND n.DNSOLD <> 'O'
              AND n.DNQTEC > n.DNQTEL
              AND NOT EXISTS (SELECT 1 FROM {"{b}"}.OPR015 x WHERE x.RSOC = n.RSOC AND x.ODNCDE = n.RDOSS AND x.ODNCLE = n.DNDCLE
                              AND x.ODDLIG = n.DNNLIG AND x.ODLIVR <> 'O')
            """);
        var p = new List<object?> { parametres.DepotPrincipal, societe };
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); p.AddRange(v); }
        if (!parametres.ArticlesNonStockes) sql.Append(" AND s.ASTK = 'O'");
        if (cles is not null)
        {
            if (cles.Count == 0) return new ResultatRecherche<LigneCandidateOpr>([], false);
            sql.Append(" AND (").Append(string.Join(" OR ", cles.Select(_ => "(n.RDOSS = ? AND n.DNDCLE = ? AND n.DNNLIG = ?)"))).Append(')');
            foreach (var k in cles) p.AddRange([k.Dossier, k.Cle, k.Ligne]);
        }
        else
        {
            if (c.Du is { } du) Et("n.DNDLIV >= ?", BaseDb2.AAAAMMJJ(du));
            if (c.Au is { } au) Et("n.DNDLIV <= ?", BaseDb2.AAAAMMJJ(au));
            if (c.CompteGeneral is > 0) Et("a.RGEN = ?", c.CompteGeneral);
            if (c.CompteClient is > 0) Et("a.RCLI = ?", c.CompteClient);
            if (c.Commande is > 0) Et("n.RDOSS = ?", c.Commande);
            if (!string.IsNullOrWhiteSpace(c.Cle)) Et("n.DNDCLE = ?", c.Cle.Trim().ToUpperInvariant());
            if (c.NumeroAdresse is > 0) Et("n.DNLIGA = ?", c.NumeroAdresse);
            var fabricants = c.Fabricants.Select(x => x.Trim().ToUpperInvariant()).Where(x => x != "").Distinct().ToList();
            if (fabricants.Count > 0) Et($"a.CCINAT IN ({string.Join(", ", fabricants.Select(_ => "?"))})", [.. fabricants]);
            if (!string.IsNullOrWhiteSpace(c.Appel)) Et("n.DNAPPE = ?", c.Appel.Trim());
            if (!string.IsNullOrWhiteSpace(c.Categorie)) Et("s.AFAM = ?", c.Categorie.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(c.Enseigne)) Et("a.CCENSE = ?", c.Enseigne.Trim().ToUpperInvariant());
        }
        if (representant is { } r) Et("a.CCREP = ?", r);
        sql.Append($" ORDER BY a.RGEN, a.RCLI, n.DNDLIV, n.RDOSS, n.DNDCLE, n.DNNLIG FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY");
        if (tx is not null) sql.Append(" WITH RS USE AND KEEP EXCLUSIVE LOCKS");

        var cnx = connexion ?? await OuvrirAsync(ct);
        try
        {
            await using var rd = await Lire(cnx, tx, sql.ToString(), ct, [.. p]);
            var lignes = new List<LigneCandidateOpr>();
            while (await rd.ReadAsync(ct))
                lignes.Add(new LigneCandidateOpr(rd.Entier(0), rd.Texte(1), rd.Entier(2), rd.Entier(3), rd.Entier(4), rd.Texte(41), rd.Texte(5),
                    rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10), BaseDb2.DateAAAAMMJJ(rd.Entier(11)),
                    BaseDb2.DateAAAAMMJJ(rd.Entier(12)), rd.Entier(13), rd.Entier(14), Adresse(rd, 24), rd.Entier(15), rd.Texte(16),
                    rd.Texte(17) == "O", rd.Texte(18) == "O", rd.Texte(19) == "O", rd.Texte(20) == "5", rd.Entier(23) == 1, rd.Texte(22).Trim(),
                    rd.Texte(21)));
            var tronque = lignes.Count > LimitesRecherche.Maximum;
            return new ResultatRecherche<LigneCandidateOpr>(tronque ? lignes[..LimitesRecherche.Maximum] : lignes, tronque);
        }
        finally
        {
            if (connexion is null) await cnx.DisposeAsync();
        }
    }

    // ---------- Fiche d'un OPR (FEN_ModificationOPR) ----------
    public async Task<FicheOpr?> LireAsync(int societe, int numero, int? representant, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        string statut, profil;
        DateOnly? creation;
        var textes = new List<string>();
        await using (var rd = await Lire(cnx, null, "SELECT OESTAT, OEPROF, OEDATE, OETXT1, OETXT2, OETXT3, OETXT4 FROM {b}.OPR010 WHERE RSOC = ? AND OENUM = ?",
                         ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) return null;
            (statut, profil, creation) = (rd.Texte(0), rd.Texte(1), BaseDb2.DateAAAAMMJJ(rd.Entier(2)));
            for (var i = 3; i < 7; i++) textes.Add(rd.Texte(i));
        }
        var lignes = new List<LigneOpr>();
        (int Gen, int Cli, string Nom, AdresseBl? Adresse, bool SousTraite, int Rep) client = (0, 0, "", null, false, 0);
        await using (var rd = await Lire(cnx, null, $"""
            SELECT l.ODNLIG, l.ODNCDE, l.ODNCLE, l.ODDLIG, COALESCE(a.CCREFC, ''), COALESCE(n.DNAPPE, ''), l.ODART, l.ODIND,
                   COALESCE(s.ARFIMP, ''), COALESCE(s.ADES, n.DNDESA, ''), l.ODDTLV, l.ODQTEA, COALESCE(n.DNQTEC - n.DNQTEL, 0),
                   COALESCE(n.DNQMAX, 0), l.ODTRS, COALESCE(t.MNOM, ''), l.ODIMPE, l.ODMECO, l.ODSLDA, l.ODRETU, l.ODACOMP, l.ODLIVR,
                   l.ODQPREP, (SELECT COUNT(*) FROM {"{b}"}.PROD_ENTITE p WHERE p.SOCIETE = l.RSOC AND p.NUMERO_OPR = l.ODNUM
                               AND p.NUMERO_LIGNE_OPR = l.ODNLIG),
                   COALESCE(s.ASTK, ''), l.ODBL,
                   COALESCE((SELECT o.OTTXT1 || o.OTTXT2 || o.OTTXT3 || o.OTTXT4 || o.OTTXT5 || o.OTTXT6 || o.OTTXT7 || o.OTTXT8 || o.OTTXT9 || o.OTTX10
                             FROM {"{b}"}.OPR016 o WHERE o.RSOC = l.RSOC AND o.ODNUM = l.ODNUM AND o.ODNLIG = l.ODNLIG), ''),
                   COALESCE(a.RGEN, 0), COALESCE(a.RCLI, 0), COALESCE(a.CCOP11, ''), COALESCE(a.CCREP, 0),
                   {ColonnesAdresse}
            FROM {"{b}"}.OPR015 l
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.ODNCDE AND n.DNDCLE = l.ODNCLE AND n.DNNLIG = l.ODDLIG
            LEFT OUTER JOIN {"{b}"}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.ODNCDE AND a.CCDCLE = l.ODNCLE
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.ODART AND s.AINDIC = l.ODIND
            LEFT OUTER JOIN {"{b}"}.A500F f ON f.RSOC = a.RSOC AND f.RGEN = a.RGEN AND f.RCLI = a.RCLI AND f.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            LEFT OUTER JOIN {"{b}"}.B200F t ON t.RSOC = l.RSOC AND t.RTRANS = l.ODTRS AND t.MTAG <> '*'
            WHERE l.RSOC = ? AND l.ODNUM = ? ORDER BY l.ODNCDE, l.ODNCLE, l.ODNLIG
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var etat = rd.Texte(21) == "O" ? EtatsOpr.Livre : rd.Entier(22) > 0 || rd.Entier(23) > 0 ? EtatsOpr.EnPreparation : EtatsOpr.APreparer;
                lignes.Add(new LigneOpr(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7),
                    rd.Texte(8), rd.Texte(9), BaseDb2.DateAAAAMMJJ(rd.Entier(10)), rd.Entier(11), rd.Entier(12), rd.Entier(13), rd.Entier(14),
                    rd.Texte(15), rd.Texte(16) == "O", rd.Texte(17) == "O", rd.Texte(18) == "O", rd.Texte(19) == "O", rd.Texte(20) == "O",
                    rd.Texte(26).TrimEnd(), etat, rd.Texte(24) == "O", rd.Entier(22), rd.Entier(25)));
                if (client.Adresse is null)
                    client = (rd.Entier(27), rd.Entier(28), rd.Texte(48), Adresse(rd, 31), rd.Texte(29) == "O", rd.Entier(30));
            }
        if (lignes.Count == 0 && client.Adresse is null) client.Adresse = new AdresseBl(0, "", "", "", "", "", "", "", "");
        if (representant is { } r && client.Rep != r) return null;
        var etatOpr = statut == "4" ? EtatsOpr.Livre : lignes.Any(l => l.Etat != EtatsOpr.APreparer) ? EtatsOpr.EnPreparation : EtatsOpr.APreparer;
        return new FicheOpr(numero, etatOpr, profil, creation, client.Gen, client.Cli, client.Nom, client.Adresse!, client.SousTraite, textes, lignes);
    }

    // Client, adresse, sous-traitance, commande et date de la première ligne d'un OPR (ajout de lignes)
    public async Task<ReferenceOpr?> ReferenceAsync(int societe, int numero, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, $"""
            SELECT o.OESTAT, COALESCE(a.RGEN, 0), COALESCE(a.RCLI, 0), COALESCE(a.CCOP11, ''), l.ODNCDE, l.ODNCLE, l.ODDTLV, COALESCE(a.CCREP, 0),
                   {ColonnesAdresse}
            FROM {"{b}"}.OPR010 o
            JOIN {"{b}"}.OPR015 l ON l.RSOC = o.RSOC AND l.ODNUM = o.OENUM
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.ODNCDE AND n.DNDCLE = l.ODNCLE AND n.DNNLIG = l.ODDLIG
            LEFT OUTER JOIN {"{b}"}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.ODNCDE AND a.CCDCLE = l.ODNCLE
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.A500F f ON f.RSOC = a.RSOC AND f.RGEN = a.RGEN AND f.RCLI = a.RCLI AND f.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            WHERE o.RSOC = ? AND o.OENUM = ? ORDER BY l.ODNLIG FETCH FIRST 1 ROW ONLY
            """, ct, societe, numero);
        if (!await rd.ReadAsync(ct)) return null;
        var adresse = Adresse(rd, 8);
        return new ReferenceOpr(numero, rd.Texte(0), rd.Entier(1), rd.Entier(2), rd.Entier(8), adresse, rd.Texte(3) == "O", rd.Entier(4), rd.Texte(5),
            BaseDb2.DateAAAAMMJJ(rd.Entier(6)), rd.Entier(7));
    }

    public async Task<bool> TransporteurExisteAsync(int societe, int transporteur, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, "SELECT 1 FROM {b}.B200F WHERE RSOC = ? AND RTRANS = ? AND MTAG <> '*'", ct, societe, transporteur);
        return await rd.ReadAsync(ct);
    }
}
