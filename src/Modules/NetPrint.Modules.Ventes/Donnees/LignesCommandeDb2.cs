using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Données des lignes de commande (NEG520 / CTL520 / MAJ520) : lecture des tarifs d'un article pour un client
// (cascade SPRCPX), paramètres de commission et de maxi livrable.
// =====================================================================
internal sealed partial class LignesCommandeDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Tout ce dont la cascade de prix a besoin pour un article et un client, à la date du jour
    public async Task<MoteurPrixCommande.DonneesTarif?> DonneesTarifAsync(int societe, string article, string indice,
        int compteGeneral, int compteClient, int representant, DateOnly aujourdhui, CancellationToken ct)
    {
        var b = Bib;
        var jour = BaseDb2.AAAAMMJJ(aujourdhui);
        await using var cnx = await OuvrirAsync(ct);

        MoteurPrixCommande.Article a;
        await using (var rd = await Lire(cnx, $"""
            SELECT AAPPTP, AMODEL, APVENT, APRVT, APRCES, AUNITE, AEMBP, AOPT2, ALRFF, AHAFF
            FROM {b}.SPF010 WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?
            """, ct, societe, article, indice))
        {
            if (!await rd.ReadAsync(ct)) return null;
            a = new(rd.Texte(0) == "O", rd.Entier(1), rd.Nombre(2), rd.Nombre(3), rd.Nombre(4), rd.Texte(5), rd.Entier(6),
                rd.Texte(7) == "C", rd.Nombre(8), rd.Nombre(9));
        }

        var categorie = "";
        await using (var rd = await Lire(cnx, $"SELECT CTARIF FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", ct,
                         societe, compteGeneral, compteClient))
            if (await rd.ReadAsync(ct)) categorie = rd.Texte(0);

        // SPF011 : tarifs de catégorie, tranches « Q » (client et générales) et tarif « A » de l'article
        var tarifs = new List<MoteurPrixCommande.TarifDate>();
        await using (var rd = await Lire(cnx, $"""
            SELECT ATQST, ATGEN, ATCLI, ATTQT, ATPV1, ATDTA1, ATDTM1, ATDTJ1, ATPC1, ATPV2, ATDTA2, ATDTM2, ATDTJ2, ATPC2
            FROM {b}.SPF011
            WHERE RSOC = ? AND ATART = ? AND ATIND = ?
              AND (ATQST IN ('Q', 'A') OR ATQST = ?) AND ((ATGEN = 0 AND ATCLI = 0) OR (ATGEN = ? AND ATCLI = ?))
            """, ct, societe, article, indice, categorie == "" ? "?" : categorie, compteGeneral, compteClient))
            while (await rd.ReadAsync(ct))
                tarifs.Add(new(rd.Texte(0), rd.Entier(1), rd.Entier(2), rd.Texte(3),
                    rd.Nombre(4), BaseDb2.Date(rd.Entier(5), rd.Entier(6), rd.Entier(7)), rd.Nombre(8),
                    rd.Nombre(9), BaseDb2.Date(rd.Entier(10), rd.Entier(11), rd.Entier(12)), rd.Nombre(13)));

        var tranches = new Dictionary<string, long>();
        await using (var rd = await Lire(cnx, $"SELECT RARG, TNUM FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = 'TQ' AND TTAG <> '*'", ct, societe))
            while (await rd.ReadAsync(ct)) tranches[rd.Texte(0)] = Convert.ToInt64(rd.GetValue(1));

        // SPF020 : tarif quantitatif du client, à la dernière date d'effet ≤ aujourd'hui
        var quantitatif = await TranchesAsync(cnx, $"""
            SELECT d.CQTMN, d.CQTMX, d.CPRXVT, d.CPRXRV, d.CUNITE FROM {b}.SPF020D d
            WHERE d.RSOC = ? AND d.CART = ? AND d.CIND = ? AND d.CGEN = ? AND d.CCLI = ?
              AND d.CDATEA = (SELECT MAX(e.CDATEA) FROM {b}.SPF020E e WHERE e.RSOC = d.RSOC AND e.CART = d.CART AND e.CIND = d.CIND
                              AND e.CGEN = d.CGEN AND e.CCLI = d.CCLI AND e.CDATEA <= ?)
            ORDER BY d.CQTMN
            """, ct, societe, article, indice, compteGeneral, compteClient, jour);

        // MOD090 : tarif modèle du client, sinon général (articles AAPPTP = 'O')
        List<MoteurPrixCommande.TarifQuantite> modeleClient = [], modeleGeneral = [];
        bool prorata = false;
        decimal largeur = 0, hauteur = 0;
        if (a.TarifModele)
        {
            async Task<(List<MoteurPrixCommande.TarifQuantite>, bool)> Modele(int gen, int cli)
            {
                var lignes = await TranchesAsync(cnx, $"""
                    SELECT d.CQTMN, d.CQTMX, d.CPRXVT, d.CPRXRV, d.CUNITE FROM {b}.MOD090D d
                    WHERE d.RSOC = ? AND d.CMOD = ? AND d.CGEN = ? AND d.CCLI = ?
                      AND d.CDATEA = (SELECT MAX(e.CDATEA) FROM {b}.MOD090E e WHERE e.RSOC = d.RSOC AND e.CMOD = d.CMOD
                                      AND e.CGEN = d.CGEN AND e.CCLI = d.CCLI AND e.CDATEA <= ?)
                    ORDER BY d.CQTMN
                    """, ct, societe, a.Modele, gen, cli, jour);
                var avecProrata = false;
                await using var rd = await Lire(cnx, $"""
                    SELECT CPRORAT FROM {b}.MOD090E WHERE RSOC = ? AND CMOD = ? AND CGEN = ? AND CCLI = ? AND CDATEA <= ?
                    ORDER BY CDATEA DESC FETCH FIRST 1 ROW ONLY
                    """, ct, societe, a.Modele, gen, cli, jour);
                if (await rd.ReadAsync(ct)) avecProrata = rd.Texte(0) == "O";
                return (lignes, avecProrata);
            }
            (modeleClient, var prorataClient) = await Modele(compteGeneral, compteClient);
            (modeleGeneral, var prorataGeneral) = await Modele(0, 0);
            prorata = modeleClient.Count > 0 ? prorataClient : prorataGeneral;
            await using var rd = await Lire(cnx, $"SELECT MLRFF, MHAFF FROM {b}.MOD010 WHERE RSOC = ? AND RMODEL = ? FETCH FIRST 1 ROW ONLY",
                ct, societe, a.Modele);
            if (await rd.ReadAsync(ct)) (largeur, hauteur) = (rd.Nombre(0), rd.Nombre(1));
        }

        var mandataire = false;
        await using (var rd = await Lire(cnx, $"SELECT ITYPRP FROM {b}.B000F WHERE RSOC = ? AND RREPR = ?", ct, societe, representant))
            if (await rd.ReadAsync(ct)) mandataire = rd.Texte(0) == "M";

        return new MoteurPrixCommande.DonneesTarif(a, categorie, compteGeneral, compteClient, tarifs, tranches, quantitatif,
            modeleClient, modeleGeneral, prorata, largeur, hauteur, mandataire);
    }

    private static async Task<List<MoteurPrixCommande.TarifQuantite>> TranchesAsync(OdbcConnection cnx, string sql,
        CancellationToken ct, params object?[] valeurs)
    {
        var lignes = new List<MoteurPrixCommande.TarifQuantite>();
        await using var rd = await Lire(cnx, sql, ct, valeurs);
        while (await rd.ReadAsync(ct))
            lignes.Add(new(rd.Entier(0), rd.Entier(1), rd.Nombre(2), rd.Nombre(3), rd.Texte(4)));
        return lignes;
    }

    // ------------------------------------------------------------------
    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, null, sql, valeurs).ExecuteReaderAsync(ct);
}
