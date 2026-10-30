using System.Data.Common;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// Fiche lue telle quelle : en-tête et lignes (zones de liste + compléments), BL, factures, journal, historique
internal sealed record FicheBrute(
    ComplementEnteteBrut Entete,
    IReadOnlyList<(LigneCommandeBrute Ligne, ComplementLigneBrut Complement)> Lignes,
    IReadOnlyList<LivraisonCommande> Livraisons,
    IReadOnlyList<FactureCommande> Factures,
    IReadOnlyList<EvenementLigne> Journal,
    IReadOnlyList<HistoriqueDossier> Historique,
    bool JournalTronque = false,
    bool HistoriqueTronque = false);

internal sealed record ComplementEnteteBrut(
    LigneCommandeBrute Commune, string Ccfact, string Ccnint, int Ccdtsa, int Ccdtss, decimal Cctxco, string Ccat,
    string Ccscat, string Ccprof, string Ccecal, string Ccport, string Ccmod, string LibReglement, int Ccech, int Ccle,
    int Ccart, string Cccdev, string Ccop11, string Ccop10, int Cgenf, int Cclif, string NomFactureEntete,
    string Cctypv, int Ccnaea, int Ctxrm, string Ccrcvd);

internal sealed record ComplementLigneBrut(
    string Dnrs, string Dnpost, string Dnvill, int Dnliga, int Dndatb, string Dncomm, string Dncom2, string Dncom3,
    decimal Dntxco, int Dntxre, int Dnqtea, int Dnqmax, decimal Dnpxce, int Dndev, int Dnvers, int Dnhypo,
    int Dngenf, int Dnclif, string NomFactureLigne, string Demp);

internal sealed partial class ConsultationCommandesDb2
{
    // Limites d'affichage du journal des lignes et de l'historique (les plus récents d'abord)
    public const int MaxJournal = 1000;
    public const int MaxHistorique = 500;

    public async Task<FicheBrute?> LireFicheAsync(int societe, CleCommande cle, int? representant, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);

        // En-tête et lignes
        var requete = RequeteListeCommandes.ConstruireFiche(b, societe, cle, representant);
        ComplementEnteteBrut? entete = null;
        var lignes = new List<(LigneCommandeBrute, ComplementLigneBrut)>();
        await using (var cmd = Commande(cnx, requete.Sql, [.. requete.Parametres]))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var commune = Lire(rd);
                entete ??= LireEntete(rd, commune);
                if (!rd.IsDBNull(27)) lignes.Add((commune, LireComplementLigne(rd)));
            }
        }
        if (entete is null) return null;

        var journal = await JournalAsync(cnx, b, societe, cle, ct);
        var historique = await HistoriqueAsync(cnx, b, societe, cle, ct);
        return new FicheBrute(entete, lignes,
            await LivraisonsAsync(cnx, b, societe, cle, ct),
            await FacturesAsync(cnx, b, societe, cle, ct),
            journal.Take(MaxJournal).ToList(),
            historique.Take(MaxHistorique).ToList(),
            JournalTronque: journal.Count > MaxJournal,
            HistoriqueTronque: historique.Count > MaxHistorique);
    }

    private const int F = RequeteListeCommandes.NbColonnes;   // première colonne des compléments de la fiche

    private static ComplementEnteteBrut LireEntete(DbDataReader rd, LigneCommandeBrute commune) => new(
        commune, Ccfact: rd.Texte(F), Ccnint: rd.Texte(F + 1), Ccdtsa: rd.Entier(F + 2), Ccdtss: rd.Entier(F + 3),
        Cctxco: rd.Nombre(F + 4), Ccat: rd.Texte(F + 5), Ccscat: rd.Texte(F + 6), Ccprof: rd.Texte(F + 7),
        Ccecal: rd.Texte(F + 8), Ccport: rd.Texte(F + 9), Ccmod: rd.Texte(F + 10), LibReglement: rd.Texte(F + 11),
        Ccech: rd.Entier(F + 12), Ccle: rd.Entier(F + 13), Ccart: rd.Entier(F + 14), Cccdev: rd.Texte(F + 15),
        Ccop11: rd.Texte(F + 16), Ccop10: rd.Texte(F + 17), Cgenf: rd.Entier(F + 18), Cclif: rd.Entier(F + 19),
        NomFactureEntete: rd.Texte(F + 20), Cctypv: rd.Texte(F + 41), Ccnaea: rd.Entier(F + 42), Ctxrm: rd.Entier(F + 43),
        Ccrcvd: rd.Texte(F + 44));

    private static ComplementLigneBrut LireComplementLigne(DbDataReader rd) => new(
        Dnrs: rd.Texte(F + 21), Dnpost: rd.Texte(F + 22), Dnvill: rd.Texte(F + 23), Dnliga: rd.Entier(F + 24),
        Dndatb: rd.Entier(F + 25), Dncomm: rd.Texte(F + 26), Dncom2: rd.Texte(F + 27), Dncom3: rd.Texte(F + 28),
        Dntxco: rd.Nombre(F + 29), Dntxre: rd.Entier(F + 30), Dnqtea: rd.Entier(F + 31), Dnqmax: rd.Entier(F + 32),
        Dnpxce: rd.Nombre(F + 33), Dndev: rd.Entier(F + 34), Dnvers: rd.Entier(F + 35), Dnhypo: rd.Entier(F + 36),
        Dngenf: rd.Entier(F + 37), Dnclif: rd.Entier(F + 38), NomFactureLigne: rd.Texte(F + 39), Demp: rd.Texte(F + 40));

    // BL de la commande : élément EXP010 du dossier, en-tête EXP009 (date d'expédition, adresse), lignes EXP015
    private static async Task<List<LivraisonCommande>> LivraisonsAsync(
        System.Data.Odbc.OdbcConnection cnx, string b, int societe, CleCommande cle, CancellationToken ct)
    {
        var sql = $"""
            SELECT x.HNOBL, h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HRS, h.HCP, h.HVILLE, x.HBLFAC, x.HNOFAC, x.HDAFAA, x.HDAFAM, x.HDAFAJ,
                   l.DNUMLC, l.DART, l.DIND, l.DDES, l.DQTE
            FROM {b}.EXP010 x
            LEFT OUTER JOIN {b}.EXP009 h ON h.RSOC = x.RSOC AND h.HNOBL = x.HNOBL
            LEFT OUTER JOIN {b}.EXP015 l ON l.RSOC = x.RSOC AND l.DNBL = x.HNOBL AND l.DNUM = x.HNUM
            WHERE x.RSOC = ? AND x.HNODOS = ? AND x.HCLEF = ?
            ORDER BY x.HNOBL DESC, l.DNUMLC, l.DNLIG
            """;
        var parBl = new Dictionary<int, (LivraisonCommande Bl, List<LigneLivree> Lignes)>();
        await using var cmd = Commande(cnx, sql, societe, cle.Numero, cle.Cle);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var numero = rd.Entier(0);
            if (!parBl.TryGetValue(numero, out var bl))
            {
                var adresse = string.Join(" ", new[] { rd.Texte(4), rd.Texte(5), rd.Texte(6) }.Where(s => s != ""));
                var lignesBl = new List<LigneLivree>();
                bl = (new LivraisonCommande(numero, BaseDb2.Date(rd.Entier(1), rd.Entier(2), rd.Entier(3)), adresse,
                    rd.Texte(7) == "O", rd.Entier(8), BaseDb2.Date(rd.Entier(9), rd.Entier(10), rd.Entier(11)), lignesBl), lignesBl);
                parBl.Add(numero, bl);
            }
            if (!rd.IsDBNull(16))
            {
                var article = rd.Texte(14) != "" ? $"{rd.Texte(13)} {rd.Texte(14)}" : rd.Texte(13);
                bl.Lignes.Add(new LigneLivree(rd.Entier(12), article, rd.Texte(15), rd.Entier(16)));
            }
        }
        return parBl.Values.Select(v => v.Bl).ToList();
    }

    // Factures de la commande (FAC090) : une ligne par facture, quantités et montants cumulés sur ses éléments ;
    // date = la plus récente des éléments, prise en entier (et non année, mois, jour maximum chacun de leur côté)
    private static async Task<List<FactureCommande>> FacturesAsync(
        System.Data.Odbc.OdbcConnection cnx, string b, int societe, CleCommande cle, CancellationToken ct)
    {
        var sql = $"""
            SELECT RNFAC, MAX(FEANNE * 10000 + FEMOIS * 100 + FEJOUR), SUM(FEQTE), SUM(FEVEN)
            FROM {b}.FAC090
            WHERE RSOC = ? AND FEDOSS = ? AND FECLE = ?
            GROUP BY RNFAC
            ORDER BY RNFAC DESC
            """;
        var factures = new List<FactureCommande>();
        await using var cmd = Commande(cnx, sql, societe, cle.Numero, cle.Cle);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            factures.Add(new FactureCommande(rd.Entier(0), BaseDb2.DateAAAAMMJJ(rd.Entier(1)), rd.Entier(2), rd.Nombre(3)));
        return factures;
    }

    // Journal des lignes (NEG100E, écrit par MAJ520), du plus récent au plus ancien
    private static async Task<List<EvenementLigne>> JournalAsync(
        System.Data.Odbc.OdbcConnection cnx, string b, int societe, CleCommande cle, CancellationToken ct)
    {
        var sql = $"""
            SELECT DEDTMO, DEDTHE, DETYMO, DNNLIG, DEPROF, ANCART, ANCND, DNART, DNIND, ANCQTE, DNQTEC, ANCPUS, DNPUS
            FROM {b}.NEG100E
            WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?
            ORDER BY DEDTMO DESC, DEDTHE DESC, DNNLIG
            FETCH FIRST {MaxJournal + 1} ROWS ONLY
            """;
        var journal = new List<EvenementLigne>();
        await using var cmd = Commande(cnx, sql, societe, cle.Numero, cle.Cle);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            static string Article(string a, string i) => i != "" ? $"{a} {i}" : a;
            journal.Add(new EvenementLigne(DateHeure(rd.Entier(0), rd.Entier(1)), rd.Texte(2), rd.Entier(3), rd.Texte(4),
                Article(rd.Texte(5), rd.Texte(6)), Article(rd.Texte(7), rd.Texte(8)), rd.Entier(9), rd.Entier(10),
                rd.Nombre(11), rd.Nombre(12)));
        }
        return journal;
    }

    // Historique du dossier (DOS040)
    private static async Task<List<HistoriqueDossier>> HistoriqueAsync(
        System.Data.Odbc.OdbcConnection cnx, string b, int societe, CleCommande cle, CancellationToken ct)
    {
        var sql = $"""
            SELECT HIDTMA, HIDTMM, HIDTMJ, HIHEUR, HITYPM, HIINAM
            FROM {b}.DOS040
            WHERE RSOC = ? AND RDOSS = ? AND HICLE = ?
            ORDER BY HIDTMA DESC, HIDTMM DESC, HIDTMJ DESC, HIHEUR DESC
            FETCH FIRST {MaxHistorique + 1} ROWS ONLY
            """;
        var historique = new List<HistoriqueDossier>();
        await using var cmd = Commande(cnx, sql, societe, cle.Numero, cle.Cle);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var date = BaseDb2.Date(rd.Entier(0), rd.Entier(1), rd.Entier(2));
            historique.Add(new HistoriqueDossier(date is { } d ? d.ToDateTime(Heure(rd.Entier(3))) : null, rd.Texte(4), rd.Texte(5)));
        }
        return historique;
    }

    // Date AAAAMMJJ + heure HHMMSS
    internal static DateTime? DateHeure(int aaaammjj, int hhmmss) =>
        BaseDb2.DateAAAAMMJJ(aaaammjj) is { } d ? d.ToDateTime(Heure(hhmmss)) : null;

    private static TimeOnly Heure(int hhmmss)
    {
        int h = hhmmss / 10000, m = hhmmss / 100 % 100, s = hhmmss % 100;
        return h < 24 && m < 60 && s < 60 ? new TimeOnly(h, m, s) : TimeOnly.MinValue;
    }
}
