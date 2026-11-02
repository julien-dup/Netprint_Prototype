using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Données de l'édition d'un OPR (OPR150) : options société (DOS005 : poids, textes de commande, page par commande), en-tête
// (initiales du créateur, 4 lignes de commentaire) et lignes imprimables (articles gérés en stock, ligne de commande non soldée,
// article « éditable au BL »), avec le stock, les lots et la fabrication de chaque article.
internal sealed record DonneesEditionOpr(
    int Numero, string Initiales, IReadOnlyList<string> Textes, bool ImprimerPoids, bool TextesCommande, bool PageParCommande,
    IReadOnlyList<LigneEditionOpr> Lignes);

// Adresse : null pour un OPR manuel (commande 99999) sans adresse
internal sealed record LigneEditionOpr(
    int NumeroLigne, int Dossier, string Cle, DateOnly? DateDepart, int CompteGeneral, int CompteClient, string NomClient, string Contact,
    string ReferenceCommande, DateOnly? DateCommande, AdresseBl? Adresse, string LibellePays, IReadOnlyList<string> ObservationsLivraison,
    string Article, string Indice, string Designation, int Quantite, string Unite, string Norme, string TypeEmballage, int EmballagePar,
    int UcParCarton, int ColisParPalette, decimal PoidsUc, string RefImprime, string Appel, IReadOnlyList<string> TextesCommande, int MaxiLivrable,
    int Bl, IReadOnlyList<string> Observations, int Transporteur, string NomTransporteur, int Groupage, decimal LargeurFormat, decimal HauteurFormat,
    int MatiereUc, bool ASolder, bool Mecompte, bool Enlevement, bool Imperatif, bool Acompte, string SousTraitance,
    IReadOnlyList<string> LotsBloques, IReadOnlyList<string> LotsPrelevement, IReadOnlyList<string> LotsAffectes, IReadOnlyList<StockDepotOpr> Stock,
    int CommandeFournisseur, IReadOnlyList<(string Lot, int Quantite)> EnFabrication);

// Stock d'un dépôt : total (AK00F) et positions par lot / emplacement (AP00F) ou par lot (AL00F)
internal sealed record StockDepotOpr(int Depot, bool Principal, int Quantite, IReadOnlyList<(string Lot, string Emplacement, int Quantite)> Positions);

internal sealed partial class OprDb2
{
    public const int CommandeManuelle = 99999;

    public async Task<DonneesEditionOpr?> EditionAsync(int societe, int numero, bool ajoutsSeulement, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        string profil;
        var textes = new List<string>();
        await using (var rd = await Lire(cnx, null, "SELECT OEPROF, OETXT1, OETXT2, OETXT3, OETXT4 FROM {b}.OPR010 WHERE RSOC = ? AND OENUM = ?", ct,
                         societe, numero))
        {
            if (!await rd.ReadAsync(ct)) return null;
            profil = rd.Texte(0);
            for (var i = 1; i < 5; i++) textes.Add(rd.Texte(i));
        }
        var initiales = "";
        await using (var rd = await Lire(cnx, null, "SELECT ACINI FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil))
            if (await rd.ReadAsync(ct)) initiales = rd.Texte(0);
        (bool Poids, bool Textes, bool Page, int Depot, int E1, int E2, int E3) p = (false, false, false, 1, 0, 0, 0);
        await using (var rd = await Lire(cnx, null, """
            SELECT COALESCE(d.PDPOPR, ''), COALESCE(d.POPREDC, ''), COALESCE(d.POPRSDP, ''), COALESCE(s.PSDEPP, 1), COALESCE(s.PSEMP1, 0),
                   COALESCE(s.PSEMP2, 0), COALESCE(s.PSEMP3, 0)
            FROM {b}.AA0007 c LEFT OUTER JOIN {b}.DOS005 d ON d.RSOC = c.RSOC LEFT OUTER JOIN {b}.STO005 s ON s.RSOC = c.RSOC
            WHERE c.RSOC = ? FETCH FIRST 1 ROW ONLY
            """, ct, societe))
            if (await rd.ReadAsync(ct))
                p = (rd.Texte(0) == "O", rd.Texte(1) == "O", rd.Texte(2) == "O", rd.Entier(3) == 0 ? 1 : rd.Entier(3), rd.Entier(4), rd.Entier(5), rd.Entier(6));

        // Lignes (curseur VUE d'OPR150, triées aussi par n° de ligne) ; l'adresse suit la même règle que la liste des OPR
        var brutes = new List<(int Ligne, int Dossier, string Cle, int LigneCde, DateOnly? Date, int Gen, int Cli, string Nom, string Contact, string Ref,
            DateOnly? DateCde, AdresseBl? Adresse, string Pays, List<string> Obs, string Article, string Indice, string Designation, int Quantite,
            string UniteArticle, string Norme, string TypeEmb, int EmbPar, int Ncar, int Npal, decimal Pdsu, string RefImp, string Appel, int Max,
            int Bl, int Transp, string NomTransp, int Groupage, decimal Larg, decimal Haut, int Matiere, bool Solder, bool Meco, bool Enle, bool Imp,
            bool Acompte, string SousTraitance, bool Prelevement, string TypeStock)>();
        await using (var rd = await Lire(cnx, null, $"""
            SELECT l.ODNLIG, l.ODNCDE, l.ODNCLE, l.ODDLIG, l.ODDTLV, COALESCE(a.RGEN, 0), COALESCE(a.RCLI, 0), TRIM(COALESCE(c.CFJ, '') || ' ' || COALESCE(c.CNOM, '')),
                   COALESCE(a.CCNINT, ''), COALESCE(a.CCREFC, ''), COALESCE(a.CCDENA, 0), COALESCE(a.CCDENM, 0), COALESCE(a.CCDENJ, 0),
                   COALESCE(n.DNDESA, ''), l.ODDESI, l.ODQTEA, COALESCE(s.AUNITE, ''), COALESCE(n.DNNORM, ''), COALESCE(s.ATYEMB, ''), COALESCE(s.AEMBP, 0),
                   COALESCE(s.ANCAR, 0), COALESCE(s.ANPAL, 0), COALESCE(s.APDSU, 0), COALESCE(s.ARFIMP, ''), COALESCE(n.DNAPPE, ''), COALESCE(n.DNQMAX, 0),
                   l.ODBL, l.ODTRS, COALESCE(t.MNOM, ''), l.ODNGRP, COALESCE(s.ALRFF, 0), COALESCE(s.AHAFF, 0), COALESCE(s.AMATUC, 0),
                   l.ODSLDA, l.ODMECO, l.ODENLE, l.ODIMPE, l.ODACOMP, COALESCE(ns.TLIB1, ''),
                   CASE WHEN (c.CDOPT9 = 'O' OR s.AOPT5 = 'O') AND l.ODRETU = 'O' THEN 1 ELSE 0 END,
                   CASE WHEN s.ATYPEA = '1' THEN st.PSTSNE WHEN s.ATYPEA = '2' AND s.ATYFAC = '1' THEN st.PSTSP1
                        WHEN s.ATYPEA = '2' AND s.ATYFAC = '2' THEN st.PSTSP2 WHEN s.ATYPEA = '2' AND s.ATYFAC = '3' THEN st.PSTSP3
                        WHEN s.ATYPEA = '3' THEN st.PSTSSF ELSE '' END,
                   COALESCE(uv.TNUM, 0), COALESCE(n.DNCOMM, ''), COALESCE(n.DNCOM2, ''), COALESCE(n.DNCOM3, ''),
                   COALESCE(f.ALCOMM, ''), COALESCE(f.ALCOM2, ''), COALESCE(f.ALCOM3, ''), COALESCE(c.CCOMTR, ''), COALESCE(c.CCOMT2, ''), COALESCE(c.CCOMT3, ''),
                   CASE WHEN f.ALRS IS NOT NULL AND f.ALRS <> ' ' THEN COALESCE(pa.TLIB1, '') WHEN n.DNRS <> ' ' THEN COALESCE(pn.TLIB1, '')
                        ELSE COALESCE(pc.TLIB1, '') END,
                   l.ODART, l.ODIND,
                   {ColonnesAdresse}
            FROM {"{b}"}.OPR015 l
            JOIN {"{b}"}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.ODART AND s.AINDIC = l.ODIND
            LEFT OUTER JOIN {"{b}"}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.ODNCDE AND a.CCDCLE = l.ODNCLE
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.ODNCDE AND n.DNDCLE = l.ODNCLE AND n.DNNLIG = l.ODDLIG
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.A500F f ON f.RSOC = a.RSOC AND f.RGEN = a.RGEN AND f.RCLI = a.RCLI AND f.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            LEFT OUTER JOIN {"{b}"}.B200F t ON t.RSOC = l.RSOC AND t.RTRANS = l.ODTRS AND t.MTAG <> '*'
            LEFT OUTER JOIN {"{b}"}.STO005 st ON st.RSOC = l.RSOC
            LEFT OUTER JOIN {"{b}"}.AA00083 uv ON uv.RSOC = l.RSOC AND uv.RTABLE = 'UV' AND uv.RARG = s.AUNITE
            LEFT OUTER JOIN {"{b}"}.AA00081 ns ON ns.RSOC = l.RSOC AND ns.RTABLE = 'NS' AND ns.RARG = s.ANATST AND s.ANATST <> ' '
            LEFT OUTER JOIN {"{b}"}.AA00081 pa ON pa.RSOC = l.RSOC AND pa.RTABLE = 'PY' AND pa.RARG = f.ALPAY
            LEFT OUTER JOIN {"{b}"}.AA00081 pn ON pn.RSOC = l.RSOC AND pn.RTABLE = 'PY' AND pn.RARG = n.DNPAY
            LEFT OUTER JOIN {"{b}"}.AA00081 pc ON pc.RSOC = l.RSOC AND pc.RTABLE = 'PY' AND pc.RARG = c.CPAYS
            WHERE l.RSOC = ? AND l.ODNUM = ? AND s.ASTK = 'O' AND s.AOPT1 <> 'N'
              AND (n.DNART <> ' ' OR l.ODNCDE = ?) AND (n.DNSOLD IS NULL OR n.DNSOLD <> 'O')
              AND (CAST(? AS CHAR(1)) = 'N' OR l.ODEDIT <> 'O')
            ORDER BY l.ODDTLV, a.RGEN, a.RCLI, n.DNLIGA, n.DNRS, l.ODNCDE, l.ODNCLE, l.ODNLIG
            """, ct, societe, numero, CommandeManuelle, ajoutsSeulement ? "O" : "N"))
            while (await rd.ReadAsync(ct))
            {
                var manuel = rd.Entier(1) == CommandeManuelle;
                var adresse = Adresse(rd, 54);
                var sansAdresse = manuel && adresse.RaisonSociale == "";
                // Observations de livraison : adresse A500F (remplacées par celles de la ligne si elle en a), ligne, sinon client
                var obsLigne = new[] { rd.Texte(42), rd.Texte(43), rd.Texte(44) };
                var obs = adresse.Numero != 0 ? (obsLigne[0] != "" ? obsLigne : [rd.Texte(45), rd.Texte(46), rd.Texte(47)])
                    : rd.Texte(64) != "" ? obsLigne : [rd.Texte(48), rd.Texte(49), rd.Texte(50)];
                var designation = rd.Texte(13) != "" ? rd.Texte(13) : rd.Texte(14);
                brutes.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), BaseDb2.DateAAAAMMJJ(rd.Entier(4)), rd.Entier(5), rd.Entier(6),
                    rd.Texte(7), rd.Texte(8), rd.Texte(9), BaseDb2.Date(rd.Entier(10), rd.Entier(11), rd.Entier(12)), sansAdresse ? null : adresse,
                    rd.Texte(51), [.. obs.Where(o => o != "")], rd.Texte(52), rd.Texte(53), designation, rd.Entier(15),
                    Unite(rd.Entier(41), rd.Texte(16)), rd.Texte(17), rd.Texte(18), rd.Entier(19), rd.Entier(20), rd.Entier(21),
                    rd.Nombre(22), rd.Texte(23), rd.Texte(24), rd.Entier(25), rd.Entier(26), rd.Entier(27), rd.Texte(28), rd.Entier(29), rd.Nombre(30),
                    rd.Nombre(31), rd.Entier(32), rd.Texte(33) == "O", rd.Texte(34) == "O", rd.Texte(35) == "O", rd.Texte(36) == "O", rd.Texte(37) == "O",
                    rd.Texte(38), rd.Entier(39) == 1, rd.Texte(40)));
            }

        // Par article : stock, lots bloqués, fabrication (lus une fois par article) ; gestion par emplacement lue une fois par type
        var parArticle = new Dictionary<(string, string, string), (List<StockDepotOpr> Stock, int Cde, List<string> Bloques, List<(string, int)> Fab)>();
        var parEmplacement = new Dictionary<string, bool>();
        foreach (var (article, indice, typeStock) in brutes.Select(b => (b.Article, b.Indice, b.TypeStock)).Distinct())
        {
            if (typeStock != "" && !parEmplacement.ContainsKey(typeStock))
                await using (var rd = await Lire(cnx, null, "SELECT TSEMP FROM {b}.AG00F WHERE RSOC = ? AND TSTSTO = ?", ct, societe, typeStock))
                    parEmplacement[typeStock] = await rd.ReadAsync(ct) && rd.Texte(0) == "O";
            var (stock, commande) = await StockAsync(cnx, societe, typeStock, parEmplacement.GetValueOrDefault(typeStock), article, indice, p.Depot,
                (p.E1, p.E2, p.E3), ct);
            parArticle[(article, indice, typeStock)] = (stock, commande, await LotsBloquesAsync(cnx, societe, article, indice, ct),
                await EnFabricationAsync(cnx, societe, article, indice, ct));
        }
        var observationsOpr = await ObservationsOprAsync(cnx, societe, numero, ct);

        var lignes = new List<LigneEditionOpr>();
        foreach (var b in brutes)
        {
            var a = parArticle[(b.Article, b.Indice, b.TypeStock)];
            lignes.Add(new LigneEditionOpr(b.Ligne, b.Dossier, b.Cle, b.Date, b.Gen, b.Cli, b.Nom, b.Contact, b.Ref, b.DateCde, b.Adresse, b.Pays, b.Obs,
                b.Article, b.Indice, b.Designation, b.Quantite, b.UniteArticle, b.Norme, b.TypeEmb, b.EmbPar, b.Ncar, b.Npal, b.Pdsu, b.RefImp, b.Appel,
                p.Textes ? await TextesCommandeAsync(cnx, societe, b.Dossier, b.Cle, b.LigneCde, ct) : [], b.Max, b.Bl,
                observationsOpr.GetValueOrDefault(b.Ligne) ?? [], b.Transp, b.NomTransp, b.Groupage, b.Larg, b.Haut, b.Matiere, b.Solder,
                b.Meco, b.Enle, b.Imp, b.Acompte, b.SousTraitance, a.Bloques,
                b.Prelevement ? await LotsPrelevementAsync(cnx, societe, b.Article, b.Indice, b.Dossier, b.Cle, p.Depot, ct) : [],
                await LotsAffectesAsync(cnx, societe, b.TypeStock, b.Article, b.Indice, b.Dossier, b.Cle, ct), a.Stock, a.Cde, a.Fab));
        }
        return new DonneesEditionOpr(numero, initiales, textes, p.Poids, p.Textes, p.Page, lignes);
    }

    // Unité imprimée (OPR150) : celle de l'article si son unité de vente vaut 1 exemplaire (table UV) ou n'y est pas, sinon « ex »
    internal static string Unite(int nombreParUnite, string uniteArticle) =>
        nombreParUnite is 0 or 1 && uniteArticle != "" ? uniteArticle : "ex";

    // Lignes d'OPR imprimées marquées éditées (« uniquement les ajouts » de la prochaine édition) : toutes, comme OPR150, ou les
    // lignes données (celles du PDF des ajouts)
    public async Task MarquerEditeAsync(int societe, int numero, IReadOnlyList<int>? lignes, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        if (lignes is null)
        {
            await Executer(cnx, null, "UPDATE {b}.OPR015 SET ODEDIT = 'O' WHERE RSOC = ? AND ODNUM = ? AND ODEDIT <> 'O'", ct, societe, numero);
            return;
        }
        foreach (var lot in lignes.Distinct().Chunk(200))
            await Executer(cnx, null, $"UPDATE {{b}}.OPR015 SET ODEDIT = 'O' WHERE RSOC = ? AND ODNUM = ? AND ODNLIG IN ({string.Join(", ", lot.Select(_ => "?"))})",
                ct, [societe, numero, .. lot.Cast<object?>()]);
    }

    private async Task<List<string>> TextesCommandeAsync(OdbcConnection cnx, int societe, int dossier, string cle, int ligne, CancellationToken ct)
    {
        var colonnes = string.Join(", ", Enumerable.Range(1, 19).Select(i => i < 10 ? $"DNTXT{i}" : $"DNTX{i}"));
        await using var rd = await Lire(cnx, null, $"SELECT {colonnes} FROM {{b}}.NEG100T WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct,
            societe, dossier, cle, ligne);
        return await rd.ReadAsync(ct) ? [.. Enumerable.Range(0, 19).Select(rd.Texte).Where(t => t != "")] : [];
    }

    // Observations OPR016 de toutes les lignes de l'OPR, lues en une fois
    private async Task<Dictionary<int, List<string>>> ObservationsOprAsync(OdbcConnection cnx, int societe, int numero, CancellationToken ct)
    {
        var r = new Dictionary<int, List<string>>();
        await using var rd = await Lire(cnx, null, """
            SELECT ODNLIG, OTTXT1, OTTXT2, OTTXT3, OTTXT4, OTTXT5, OTTXT6, OTTXT7, OTTXT8, OTTXT9, OTTX10 FROM {b}.OPR016 WHERE RSOC = ? AND ODNUM = ?
            """, ct, societe, numero);
        while (await rd.ReadAsync(ct)) r[rd.Entier(0)] = [.. Enumerable.Range(1, 10).Select(rd.Texte).Where(t => t != "")];
        return r;
    }

    // Stock de chaque dépôt (B100F actifs), dépôt principal d'abord : total AK00F, positions AP00F (gestion par emplacement,
    // AG00F.TSEMP) ou AL00F (lots, négatifs compris comme OPR150)
    // Rend aussi la quantité commandée au fournisseur (AK00F.PRQCDE) du dépôt principal
    private async Task<(List<StockDepotOpr> Stock, int CommandeFournisseur)> StockAsync(OdbcConnection cnx, int societe, string typeStock,
        bool parEmplacement, string article, string indice, int depotPrincipal, (int A, int R, int E) decoupe, CancellationToken ct)
    {
        if (typeStock == "") return ([], 0);
        var totaux = new Dictionary<int, int>();
        var commande = 0;
        await using (var rd = await Lire(cnx, null, """
            SELECT k.PRDEP, k.PRQENT, k.PRQCDE FROM {b}.AK00F k JOIN {b}.B100F d ON d.RSOC = k.RSOC AND d.RDEP = k.PRDEP AND d.DTAG = ' '
            WHERE k.RSOC = ? AND k.PRTSTO = ? AND k.PRREF = ? AND k.PRIND = ?
            """, ct, societe, typeStock, article, indice))
            while (await rd.ReadAsync(ct))
            {
                totaux[rd.Entier(0)] = (int)rd.Nombre(1);
                if (rd.Entier(0) == depotPrincipal) commande = (int)rd.Nombre(2);
            }
        var positions = new List<(int Depot, string Lot, string Emplacement, int Quantite)>();
        var sql = parEmplacement
            ? "SELECT PEDEP, PELOT, PEEMP, PEQTE FROM {b}.AP00F WHERE RSOC = ? AND PETSTO = ? AND PEREF = ? AND PEIND = ? AND PEQTE > 0 ORDER BY PEDEP, PELOT, PEEMP"
            : "SELECT PLDEP, PLLOT, ' ', PLQENT FROM {b}.AL00F WHERE RSOC = ? AND PLTSTO = ? AND PLREF = ? AND PLIND = ? AND PLQENT <> 0 ORDER BY PLDEP, PLLOT";
        await using (var rd = await Lire(cnx, null, sql, ct, societe, typeStock, article, indice))
            while (await rd.ReadAsync(ct)) positions.Add((rd.Entier(0), rd.Texte(1), Emplacement(rd.Texte(2), decoupe), (int)rd.Nombre(3)));
        return ([.. totaux.Keys.Union(positions.Select(x => x.Depot)).Distinct()
            .Where(d => totaux.GetValueOrDefault(d) != 0 || positions.Any(x => x.Depot == d))
            .OrderBy(d => d == depotPrincipal ? 0 : 1).ThenBy(d => d)
            .Select(d => new StockDepotOpr(d, d == depotPrincipal, totaux.GetValueOrDefault(d),
                [.. positions.Where(x => x.Depot == d).Select(x => (x.Lot, x.Emplacement, x.Quantite))]))], commande);
    }

    // Emplacement découpé en allée.rack.emplacement selon STO005 (PSEMP1 / 2 / 3), comme SPSTR d'OPR150
    internal static string Emplacement(string brut, (int A, int R, int E) d)
    {
        brut = brut.Trim();
        if (brut == "" || (d.R == 0 && d.E == 0) || d.A <= 0) return brut;
        var morceaux = new List<string>();
        var i = 0;
        foreach (var n in new[] { d.A, d.R, d.E }.Where(n => n > 0))
        {
            if (i >= brut.Length) break;
            morceaux.Add(brut.Substring(i, Math.Min(n, brut.Length - i)));
            i += n;
        }
        if (i < brut.Length) morceaux.Add(brut[i..]);
        return string.Join(".", morceaux);
    }

    // Dossiers de fabrication de l'article bloqués au contrôle qualité (CQU150, pour l'article ou pour tous), une fois chacun
    private async Task<List<string>> LotsBloquesAsync(OdbcConnection cnx, int societe, string article, string indice, CancellationToken ct)
    {
        var r = new List<string>();
        await using var rd = await Lire(cnx, null, """
            SELECT DISTINCT z.RDOSS, z.LADCLE FROM {b}.AB0020 z
            WHERE z.RSOC = ? AND z.LAART = ? AND z.LAIND = ?
              AND EXISTS (SELECT 1 FROM {b}.CQU150 q WHERE q.RSOC = z.RSOC AND q.LBDOSS = z.RDOSS AND q.LBDCLE = z.LADCLE
                          AND ((q.LBART = z.LAART AND q.LBIND = z.LAIND) OR q.LBART = ' '))
            ORDER BY z.RDOSS, z.LADCLE
            """, ct, societe, article, indice);
        while (await rd.ReadAsync(ct)) r.Add($"{rd.Entier(0):00000}/{rd.Texte(1)}");
        return r;
    }

    // Dossiers de fabrication de la commande dont le lot n'est pas encore sorti du dépôt principal (prélèvement en retour usine)
    private async Task<List<string>> LotsPrelevementAsync(OdbcConnection cnx, int societe, string article, string indice, int dossier, string cle, int depot,
        CancellationToken ct)
    {
        var r = new List<string>();
        await using var rd = await Lire(cnx, null, """
            SELECT DISTINCT z.RDOSS, z.LADCLE FROM {b}.AB0020 z
            WHERE z.RSOC = ? AND z.LAART = ? AND z.LAIND = ? AND z.LADOSC = ? AND z.LACLDC = ? AND z.LAQTEC > 0
              AND DIGITS(z.RDOSS) CONCAT z.LADCLE NOT IN (SELECT m.MVLOT FROM {b}.A700F m WHERE m.RSOC = z.RSOC AND m.MVDEP = ?
                  AND m.MVREF = z.LAART AND m.MVIND = z.LAIND AND m.MVCMVT = 'EX')
            ORDER BY z.RDOSS, z.LADCLE
            """, ct, societe, article, indice, dossier, cle, depot);
        while (await rd.ReadAsync(ct)) r.Add($"{rd.Entier(0):00000}/{rd.Texte(1)}");
        return r;
    }

    // Dossiers de fabrication affectés à la commande, encore en stock ou à fabriquer (5 au plus, comme OPR150)
    private async Task<List<string>> LotsAffectesAsync(OdbcConnection cnx, int societe, string typeStock, string article, string indice, int dossier, string cle,
        CancellationToken ct)
    {
        var r = new List<string>();
        await using var rd = await Lire(cnx, null, """
            SELECT z.RDOSS, z.LADCLE FROM {b}.AB0020 z
            WHERE z.RSOC = ? AND z.LAART = ? AND z.LAIND = ? AND z.LADOSC = ? AND z.LACLDC = ?
              AND (z.LAQTEC - z.LAQTEF > 0 OR (SELECT COALESCE(SUM(l.PLQENT), 0) FROM {b}.AL00F l WHERE l.RSOC = z.RSOC AND l.PLTSTO = ?
                       AND l.PLREF = z.LAART AND l.PLIND = z.LAIND AND l.PLLOT = DIGITS(z.RDOSS) CONCAT z.LADCLE) > 0)
            ORDER BY z.RDOSS, z.LADCLE FETCH FIRST 5 ROWS ONLY
            """, ct, societe, article, indice, dossier, cle, typeStock);
        while (await rd.ReadAsync(ct)) r.Add($"{rd.Entier(0)}/{rd.Texte(1)}");
        return r;
    }

    // En fabrication : dossiers non soldés de l'article (toutes commandes), reste à fabriquer cumulé par dossier (6 au plus)
    private async Task<List<(string, int)>> EnFabricationAsync(OdbcConnection cnx, int societe, string article, string indice, CancellationToken ct)
    {
        var r = new List<(string, int)>();
        await using var rd = await Lire(cnx, null, """
            SELECT RDOSS, LADCLE, SUM(LAQTEC - LAQTEF) FROM {b}.AB0020
            WHERE RSOC = ? AND LAART = ? AND LAIND = ? AND LASOLD <> 'O' AND LAQTEC <> 0 AND LAQTEC > LAQTEF
            GROUP BY RDOSS, LADCLE ORDER BY RDOSS, LADCLE FETCH FIRST 6 ROWS ONLY
            """, ct, societe, article, indice);
        while (await rd.ReadAsync(ct)) r.Add(($"{rd.Entier(0):00000}{rd.Texte(1)}", (int)rd.Nombre(2)));
        return r;
    }
}
