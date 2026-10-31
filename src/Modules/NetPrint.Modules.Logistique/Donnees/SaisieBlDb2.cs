using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Saisie des BL (cahier des charges lot 1) : lectures. Écritures dans SaisieBlDb2.Ecritures.cs.
// Le BL met à jour les tables de la commande (NEG100, AB001, DOS040, OPR015) dans sa propre transaction : ce sont les
// mêmes tables que le RPG EXP010 et l'application OPR (cartographie : EXP010, EXP540, OPR).
// =====================================================================

// Paramètres société : stock (STO005), expédition (EXP005), numérotation (AA0007)
internal sealed record ParametresBl(
    int DepotPrincipal, string StockNegoce, string StockP1, string StockP2, string StockP3, string StockSemiFini,
    string TypeLivraisonDefaut, string EditionBl, string EditionEtiquettes, string EditionPalettes, bool PoidsPalettes, int PoidsPalette,
    string EmplacementExpedition, bool NumerotationParEnseigne, bool CalculPort = false)
{
    // Type de stock d'un article (SPF010.ATYPEA / ATYFAC)
    public string TypeStockArticle(string typeArticle, string typeFacturation) => typeArticle switch
    {
        "1" => StockNegoce,
        "2" => typeFacturation switch { "2" => StockP2, "3" => StockP3, _ => StockP1 },
        "3" => StockSemiFini,
        _ => "",
    };

    // Type de stock d'un dossier (contrôle d'inventaire C5) : négoce, ou produit fini selon AB001.CCFACT
    public string TypeStockDossier(string typeDossier, string facturation) =>
        typeDossier == "3" ? StockNegoce : facturation switch { "2" => StockP2, "3" => StockP3, _ => StockP1 };
}

// Dossier (AB001, AB001FA) et son client (AA0003)
internal sealed record DossierBl(
    int Dossier, string Cle, string Type, string Facturation, string Enseigne, string Designation, int CompteGeneral, int CompteClient,
    string NomClient, string QualiteClient, bool SousTraite, bool SousTraitanceSynchronisee, string Revendeur, string Port,
    int Representant, string ReferenceCommande, DateOnly? DateCommande, int Devis, int Hypothese, int Modele, string RefImprime,
    string FacturationAuto, string OptionFacturationClient, int CompteGeneralFactureClient, int CompteClientFactureClient,
    AdresseBl AdresseClient, int TransporteurClient, int BaremeClient);

// Article d'une ligne (SPF010)
internal sealed record ArticleBl(
    bool GereEnStock, string TypeArticle, string TypeFacturation, int EmballagePar, decimal PoidsUc, string TypeEmballage,
    bool Fractionnable, bool QuantiteEnUc, string Famille, string SousFamille);

internal sealed partial class SaisieBlDb2(IConfiguration config)
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

    private Task<Lecteur> Lire(OdbcConnection cnx, OdbcTransaction tx, string sql, CancellationToken ct, params object?[] valeurs) =>
        Lecteur.ExecuterAsync(cnx, tx, sql.Replace("{b}", Bib), ct, valeurs);

    public async Task<ParametresBl> ParametresAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, """
            SELECT COALESCE(s.PSDEPP, 1), COALESCE(s.PSTSNE, ''), COALESCE(s.PSTSP1, ''), COALESCE(s.PSTSP2, ''), COALESCE(s.PSTSP3, ''),
                   COALESCE(s.PSTSSF, ''), COALESCE(x.FETYPL, ''), COALESCE(x.FEBLED, ''), COALESCE(x.FEECED, ''), COALESCE(x.FEEPED, ''),
                   COALESCE(x.FEOP05, ''), COALESCE(x.FPDSP, 0), COALESCE(x.FEEMPE, ''), c.SOP03, c.SOP17, c.SOP09
            FROM {b}.AA0007 c
            LEFT OUTER JOIN {b}.STO005 s ON s.RSOC = c.RSOC
            LEFT OUTER JOIN {b}.EXP005 x ON x.RSOC = c.RSOC
            WHERE c.RSOC = ? FETCH FIRST 1 ROW ONLY
            """, ct, societe);
        if (!await rd.ReadAsync(ct)) throw new InvalidOperationException($"Société {societe} absente d'AA0007.");
        return new ParametresBl(rd.Entier(0) == 0 ? 1 : rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5),
            rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10) == "O", rd.Entier(11), rd.Texte(12),
            rd.Texte(13) == "O" && rd.Texte(14) == "O", rd.Texte(15) == "2");
    }

    public async Task<DossierBl?> DossierAsync(int societe, int dossier, string cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, """
            SELECT a.RDOSS, a.CCDCLE, a.CCTYPC, a.CCFACT, a.CCENSE, a.CCDES1, a.RGEN, a.RCLI, COALESCE(c.CNOM, ''), COALESCE(c.CQUACL, ''),
                   a.CCOP10, a.CCOP11, a.CCRCVD, a.CCPORT, a.CCREP, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCDEV, a.CCHYP, a.CCMODL,
                   a.CCRFIM, COALESCE(f.FAAUTO, ''), COALESCE(c.CDOPT2, ''), a.CGENF, a.CCLIF,
                   COALESCE(c.CFJ, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''), COALESCE(c.CVILLE, ''),
                   COALESCE(c.CDEPT, ''), COALESCE(c.CPAYS, ''), COALESCE(c.CTRANS, 0), COALESCE(c.CODBAR, 0)
            FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AB001FA f ON f.RSOC = a.RSOC AND f.RDOSS = a.RDOSS AND f.CCDCLE = a.CCDCLE
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ?
            """, ct, societe, dossier, cle);
        if (!await rd.ReadAsync(ct)) return null;
        return new DossierBl(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Entier(6), rd.Entier(7),
            rd.Texte(8), rd.Texte(9), rd.Texte(10) == "O", rd.Texte(11) == "O", rd.Texte(12), rd.Texte(13), rd.Entier(14), rd.Texte(15),
            BaseDb2.Date(rd.Entier(16), rd.Entier(17), rd.Entier(18)), rd.Entier(19), rd.Entier(20), rd.Entier(21), rd.Texte(22),
            rd.Texte(23), rd.Texte(24), rd.Entier(25), rd.Entier(26),
            new AdresseBl(0, rd.Texte(27), rd.Texte(8), rd.Texte(28), rd.Texte(29), rd.Texte(30), rd.Texte(31), rd.Texte(32), rd.Texte(33)),
            rd.Entier(34), rd.Entier(35));
    }

    // Profil qui tient le verrou applicatif du dossier (PRTLCK 'DOSSIER'), sinon null
    public async Task<string?> VerrouDossierAsync(int societe, int dossier, string cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT PLPROF FROM {b}.PRTLCK WHERE PLFIC = 'DOSSIER' AND PLCLE = ? FETCH FIRST 1 ROW ONLY",
            ct, CleVerrouDossier(societe, dossier, cle));
        return await rd.ReadAsync(ct) ? rd.Texte(0) : null;
    }

    internal static string CleVerrouDossier(int societe, int dossier, string cle) => $"{societe:0000}{dossier:00000}{cle}";

    // Inventaire en cours sur le type de stock à une date ≥ date du jour (STOINE)
    public async Task<bool> InventaireEnCoursAsync(int societe, string typeStock, DateOnly jour, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT 1 FROM {b}.STOINE WHERE RSOC = ? AND SITSTO = ? AND SIDATE >= ? FETCH FIRST 1 ROW ONLY",
            ct, societe, typeStock, BaseDb2.AAAAMMJJ(jour));
        return await rd.ReadAsync(ct);
    }

    // Période de stock ouverte (AG00F.TSAA / TSMM) et gestion par emplacement (TSEMP) d'un type de stock
    public async Task<(DateOnly? Periode, bool ParEmplacement)> TypeStockAsync(int societe, string typeStock, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT TSAA, TSMM, TSEMP FROM {b}.AG00F WHERE RSOC = ? AND TSTSTO = ?", ct, societe, typeStock);
        if (!await rd.ReadAsync(ct)) return (null, false);
        return (BaseDb2.Date(rd.Entier(0), rd.Entier(1), 1), rd.Texte(2) == "O");
    }

    // Lignes de commande non soldées du dossier, avec adresse (A500F n° DNLIGA, sinon adresse de la ligne, sinon client),
    // client facturé (ligne, sinon dossier AB001.CGENF/CCLIF, sinon client du dossier) et ordre de préparation traité non livré
    public async Task<IReadOnlyList<LigneProposee>> LignesAsync(int societe, DossierBl d, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, """
            SELECT n.DNNLIG, n.DNART, n.DNIND, n.DNDESA, n.DNQTEC, n.DNQTEL, n.DNSTAT, n.DNLIGA,
                   COALESCE(a.ALFJ, n.DNFJ), COALESCE(a.ALRS, n.DNRS), COALESCE(a.ALRUE1, n.DNRUE1), COALESCE(a.ALRUE2, n.DNRUE2),
                   COALESCE(a.ALPOST, n.DNPOST), COALESCE(a.ALVILL, n.DNVILL), COALESCE(a.ALDEP, ''), COALESCE(a.ALPAY, n.DNPAY),
                   n.DNGENF, n.DNCLIF,
                   (SELECT MIN(o.ODNUM) FROM {b}.OPR015 o WHERE o.RSOC = n.RSOC AND o.ODNCDE = n.RDOSS AND o.ODNCLE = n.DNDCLE
                     AND o.ODDLIG = n.DNNLIG AND o.ODSTAT = '3' AND o.ODLIVR <> 'O'),
                   n.DNDLIV
            FROM {b}.NEG100 n
            LEFT OUTER JOIN {b}.A500F a ON a.RSOC = n.RSOC AND a.RGEN = ? AND a.RCLI = ? AND a.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNSOLD <> 'O' AND n.DNART <> ' '
            ORDER BY n.DNNLIG
            """, ct, d.CompteGeneral, d.CompteClient, societe, d.Dossier, d.Cle);
        var lignes = new List<LigneProposee>();
        while (await rd.ReadAsync(ct))
        {
            var adresse = new AdresseBl(rd.Entier(7), rd.Texte(8), rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Texte(12), rd.Texte(13),
                rd.Texte(14), rd.Texte(15));
            if (adresse.RaisonSociale == "") adresse = d.AdresseClient;
            var (gen, cli) = rd.Entier(17) != 0 ? (rd.Entier(16), rd.Entier(17))
                : d.CompteClientFactureClient != 0 ? (d.CompteGeneralFactureClient, d.CompteClientFactureClient)
                : (d.CompteGeneral, d.CompteClient);
            lignes.Add(new LigneProposee(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Entier(4), rd.Entier(5),
                Math.Max(0, rd.Entier(4) - rd.Entier(5)), rd.Texte(6) == "3", adresse, gen, cli, rd.Nul(18) ? null : rd.Entier(18),
                BaseDb2.DateAAAAMMJJ(rd.Entier(19))));
        }
        return lignes;
    }

    // Lignes de commande choisies : code ligne, article (SPF010) et textes de la ligne (NEG100T)
    public async Task<IReadOnlyList<(int Ligne, string CodeLigne, ArticleBl? Article, IReadOnlyList<string> Textes)>> DetailLignesAsync(
        int societe, int dossier, string cle, IReadOnlyList<int> lignes, CancellationToken ct)
    {
        if (lignes.Count == 0) return [];
        await using var cnx = await OuvrirAsync(ct);
        var textes = Enumerable.Range(1, 19).Select(i => i < 10 ? $"t.DNTXT{i}" : $"t.DNTX{i}");
        await using var rd = await Lire(cnx, $"""
            SELECT n.DNNLIG, n.DNCLIG, s.AARTIC, COALESCE(s.ASTK, ''), COALESCE(s.ATYPEA, ''), COALESCE(s.ATYFAC, ''), COALESCE(s.AEMBP, 0),
                   COALESCE(s.APDSU, 0), COALESCE(s.ATYEMB, ''), COALESCE(s.AFRACL, ''), COALESCE(s.AOPT3, ''), COALESCE(s.AFAM, ''),
                   COALESCE(s.ASFAM, ''), {string.Join(", ", textes.Select(x => $"COALESCE({x}, '')"))}
            FROM {"{b}"}.NEG100 n
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            LEFT OUTER JOIN {"{b}"}.NEG100T t ON t.RSOC = n.RSOC AND t.RDOSS = n.RDOSS AND t.DNDCLE = n.DNDCLE AND t.DNNLIG = n.DNNLIG
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNNLIG IN ({string.Join(", ", lignes.Select(_ => "?"))})
            ORDER BY n.DNNLIG
            """, ct, [societe, dossier, cle, .. lignes.Cast<object?>()]);
        var r = new List<(int, string, ArticleBl?, IReadOnlyList<string>)>();
        while (await rd.ReadAsync(ct))
        {
            if (r.Count > 0 && r[^1].Item1 == rd.Entier(0)) continue;   // une seule lecture des textes par ligne
            var article = rd.Texte(2) == "" ? null : new ArticleBl(rd.Texte(3) == "O", rd.Texte(4), rd.Texte(5), rd.Entier(6), rd.Nombre(7),
                rd.Texte(8), rd.Texte(9) != "N", rd.Texte(10) == "O", rd.Texte(11), rd.Texte(12));
            var lus = Enumerable.Range(13, 19).Select(rd.Texte).ToList();
            while (lus.Count > 0 && lus[^1] == "") lus.RemoveAt(lus.Count - 1);
            r.Add((rd.Entier(0), rd.Texte(1), article, lus));
        }
        return r;
    }

    // Positions de stock d'un article dans le dépôt principal : par emplacement (AP00F) si le type de stock l'est, sinon par lot
    // (AL00F) ; lots bloqués (CQU150) exclus ; emplacement d'expédition seul si EXP005.FEEMPE est renseigné
    public async Task<IReadOnlyList<PositionStock>> PositionsAsync(int societe, string typeStock, bool parEmplacement, int depot,
        string article, string indice, string emplacementExpedition, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var bloque = """
            NOT EXISTS (SELECT 1 FROM {b}.CQU150 q WHERE q.RSOC = l.RSOC AND DIGITS(q.LBDOSS) = SUBSTR(l.PLLOT, 1, 5)
                        AND q.LBDCLE = SUBSTR(l.PLLOT, 6, 1) AND (q.LBTOUS = 'O' OR (q.LBART = l.PLREF AND q.LBIND = l.PLIND)))
            """;
        var sql = parEmplacement
            ? $"""
              SELECT e.PEDEP, e.PELOT, e.PEEMP, l.PLDLU, l.PLDTEN, e.PEQTE
              FROM {"{b}"}.AP00F e
              JOIN {"{b}"}.AL00F l ON l.RSOC = e.RSOC AND l.PLTSTO = e.PETSTO AND l.PLDEP = e.PEDEP AND l.PLREF = e.PEREF
                   AND l.PLIND = e.PEIND AND l.PLLOT = e.PELOT
              WHERE e.RSOC = ? AND e.PETSTO = ? AND e.PEDEP = ? AND e.PEREF = ? AND e.PEIND = ? AND e.PEQTE > 0 AND {bloque}
                    {(emplacementExpedition == "" ? "" : "AND e.PEEMP = ?")}
              ORDER BY l.PLDTEN, e.PELOT, e.PEEMP
              """
            : $"""
              SELECT l.PLDEP, l.PLLOT, '', l.PLDLU, l.PLDTEN, l.PLQENT
              FROM {"{b}"}.AL00F l
              WHERE l.RSOC = ? AND l.PLTSTO = ? AND l.PLDEP = ? AND l.PLREF = ? AND l.PLIND = ? AND l.PLQENT > 0 AND {bloque}
              ORDER BY l.PLDTEN, l.PLLOT
              """;
        object?[] valeurs = parEmplacement && emplacementExpedition != ""
            ? [societe, typeStock, depot, article, indice, emplacementExpedition]
            : [societe, typeStock, depot, article, indice];
        await using var rd = await Lire(cnx, sql, ct, valeurs);
        var r = new List<PositionStock>();
        while (await rd.ReadAsync(ct))
            r.Add(new PositionStock(rd.Entier(0), rd.Texte(1), rd.Texte(2), BaseDb2.DateAAAAMMJJ(rd.Entier(3)),
                BaseDb2.DateAAAAMMJJ(rd.Entier(4)), (int)rd.Nombre(5)));
        return r;
    }

    // Stock de l'article bloqué (CQU150) dans le dépôt : pour dire explicitement pourquoi rien n'est proposé
    public async Task<int> StockBloqueAsync(int societe, string typeStock, int depot, string article, string indice, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, """
            SELECT COALESCE(SUM(l.PLQENT), 0) FROM {b}.AL00F l
            WHERE l.RSOC = ? AND l.PLTSTO = ? AND l.PLDEP = ? AND l.PLREF = ? AND l.PLIND = ? AND l.PLQENT > 0
              AND EXISTS (SELECT 1 FROM {b}.CQU150 q WHERE q.RSOC = l.RSOC AND DIGITS(q.LBDOSS) = SUBSTR(l.PLLOT, 1, 5)
                          AND q.LBDCLE = SUBSTR(l.PLLOT, 6, 1) AND (q.LBTOUS = 'O' OR (q.LBART = l.PLREF AND q.LBIND = l.PLIND)))
            """, ct, societe, typeStock, depot, article, indice);
        return await rd.ReadAsync(ct) ? (int)rd.Nombre(0) : 0;
    }

    // Transporteur (B200F), adresse de livraison (A500F) : existence
    public async Task<bool> TransporteurExisteAsync(int societe, int transporteur, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT 1 FROM {b}.B200F WHERE RSOC = ? AND RTRANS = ?", ct, societe, transporteur);
        return await rd.ReadAsync(ct);
    }

    // Qualité du client facturé (AA0003.CQUACL) ; null si le client n'existe pas
    public async Task<string?> QualiteClientAsync(int societe, int general, int client, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT CQUACL FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", ct, societe, general, client);
        return await rd.ReadAsync(ct) ? rd.Texte(0) : null;
    }

    // Contrôle de stock (CTRPOSSTK / CTRPOSEMP) : 0 = possible, sinon date AAAAMMJJ où le stock deviendrait négatif, -1 = erreur
    public async Task<int> ControlerStockAsync(int societe, string typeStock, bool parEmplacement, int depot, string article, string indice,
        string lot, string emplacement, DateOnly date, decimal variation, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await ControlerStockAsync(cnx, null, societe, typeStock, parEmplacement, depot, article, indice, lot, emplacement, date, variation, ct);
    }

    private async Task<int> ControlerStockAsync(OdbcConnection cnx, OdbcTransaction? tx, int societe, string typeStock, bool parEmplacement,
        int depot, string article, string indice, string lot, string emplacement, DateOnly date, decimal variation, CancellationToken ct)
    {
        const string Debut = """
            CAST(? AS NUMERIC(4, 0)), CAST(? AS CHAR(2)), CAST(? AS NUMERIC(3, 0)), CAST(? AS CHAR(15)), CAST(? AS CHAR(2)),
            CAST(? AS CHAR(6)), CAST(? AS NUMERIC(8, 0))
            """;
        var sql = parEmplacement
            ? $"SELECT {{b}}.CTRPOSEMP({Debut}, CAST(? AS CHAR(8)), CAST(? AS NUMERIC(11, 3))) FROM SYSIBM.SYSDUMMY1"
            : $"SELECT {{b}}.CTRPOSSTK({Debut}, CAST(? AS NUMERIC(11, 3))) FROM SYSIBM.SYSDUMMY1";
        object?[] valeurs = parEmplacement
            ? [societe, typeStock, depot, article, indice, lot, BaseDb2.AAAAMMJJ(date), emplacement, variation]
            : [societe, typeStock, depot, article, indice, lot, BaseDb2.AAAAMMJJ(date), variation];
        await using var rd = await Lecteur.ExecuterAsync(cnx, tx, sql.Replace("{b}", Bib), ct, valeurs);
        return await rd.ReadAsync(ct) ? rd.Entier(0) : -1;
    }
}
