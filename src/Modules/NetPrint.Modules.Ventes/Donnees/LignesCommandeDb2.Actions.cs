using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Actions sur une commande (options SO, RE et 4 de DOS500S) : solde, réactivation, suppression.
// Les tables journalisées sont écrites en une transaction ; les tables satellites non journalisées
// (répartitions, coûts, façonnage, liens commande → OF, directives) sont vidées juste après la validation.
// Écarts volontaires (signalés « ÉCART ») :
//   - solde : les événements du dossier (EVE100 / EVE105) sont conservés (DOS500S les supprimait) ;
//   - suppression : textes de lignes (NEG100T), AB001FA et AB0016 supprimés aussi (le RPG les laissait orphelins).
// =====================================================================
internal sealed partial class LignesCommandeDb2
{
    // Articles des lignes de la commande, soldées comprises (réservations à recalculer après un solde ou une suppression)
    public async Task<IReadOnlyList<(string Article, string Indice)>> ArticlesAsync(int societe, CleCommande cle, CancellationToken ct)
    {
        var liste = new List<(string, string)>();
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"SELECT DISTINCT DNART, DNIND FROM {Bib}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?",
            ct, societe, cle.Numero, cle.Cle);
        while (await rd.ReadAsync(ct)) liste.Add((Socle.Donnees.BaseDb2.Texte(rd, 0), Socle.Donnees.BaseDb2.Texte(rd, 1)));
        return liste;
    }

    // BL et factures de la commande : la suppression est alors refusée (DOS500S R7)
    public async Task<(bool Bl, bool Facture)> PiecesAsync(int societe, CleCommande cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await PiecesAsync(cnx, null, Bib, societe, cle, ct);
    }

    private static async Task<(bool Bl, bool Facture)> PiecesAsync(OdbcConnection cnx, OdbcTransaction? tx, string b, int societe,
        CleCommande cle, CancellationToken ct)
    {
        bool bl, facture;
        await using (var rd = await Lire(cnx, tx, $"SELECT 1 FROM {b}.EXP010 WHERE RSOC = ? AND HNODOS = ? AND HCLEF = ? FETCH FIRST 1 ROW ONLY",
                         ct, societe, cle.Numero, cle.Cle))
            bl = await rd.ReadAsync(ct);
        await using (var rd = await Lire(cnx, tx, $"SELECT 1 FROM {b}.FAC090 WHERE RSOC = ? AND FEDOSS = ? AND FECLE = ? FETCH FIRST 1 ROW ONLY",
                         ct, societe, cle.Numero, cle.Cle))
            facture = await rd.ReadAsync(ct);
        return (bl, facture);
    }

    // Solde (option SO) : toutes les lignes soldées, en-tête « D » à la date du jour, historique SO si antidatée
    public async Task SolderCommandeAsync(int societe, string profil, CleCommande cle, ContexteLigne c, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            var jour = DateTime.Today;
            var n = await Executer(cnx, tx, $"""
                UPDATE {b}.AB001 SET CCTAGS = 'D', CCSLDA = ?, CCSLDM = ?, CCSLDJ = ?
                WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTAGS = ' '
                """, ct, jour.Year, jour.Month, jour.Day, societe, cle.Numero, cle.Cle);
            if (n == 0) throw new RegleMetierException("Cette commande est déjà soldée.");
            await Executer(cnx, tx, $"UPDATE {b}.NEG100 SET DNSOLD = 'O' WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNSOLD <> 'O'", ct,
                societe, cle.Numero, cle.Cle);
            // ÉCART : DOS500S supprimait ici les événements du dossier (EVE100 / EVE105) ; ils sont conservés
            await HistoriqueDossierAsync(cnx, tx, b, societe, profil, cle, c, "SO", ct);
            await TransfertAsync(cnx, tx, b, societe, cle, c, ct);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
        await AuditDossierAsync(cnx, b, societe, profil, cle, "SO");
    }

    // Réactivation (option RE, SPACT) d'une commande soldée (« D ») ; les lignes soldées le restent.
    // Un autre état de l'en-tête n'est pas touché.
    public async Task ReactiverCommandeAsync(int societe, string profil, CleCommande cle, ContexteLigne c, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using (var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var validee = false;
            try
            {
                var n = await Executer(cnx, tx, $"""
                    UPDATE {b}.AB001 SET CCTAGS = ' ', CCSLDA = 0, CCSLDM = 0, CCSLDJ = 0
                    WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTAGS = 'D'
                    """, ct, societe, cle.Numero, cle.Cle);
                if (n == 0) throw new RegleMetierException("Cette commande n'est pas soldée : elle ne peut pas être réactivée ici.");
                await TransfertAsync(cnx, tx, b, societe, cle, c, ct);
                await tx.CommitAsync(ct);
                validee = true;
            }
            finally
            {
                if (!validee) await tx.RollbackAsync(CancellationToken.None);
            }
        }
        await AuditDossierAsync(cnx, b, societe, profil, cle, "RA");
    }

    // Commande sous-traitée à un autre site : demande de transfert SYN100 « CDE »
    private static async Task TransfertAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, CleCommande cle,
        ContexteLigne c, CancellationToken ct)
    {
        if (!c.SousTraitee) return;
        var maintenant = DateTime.Now;
        await Executer(cnx, tx, $"""
            INSERT INTO {b}.SYN100 (HTSOC, HTDOSS, HTDCLE, HTDATE, HTHEUR, HTSTAT, HTNBL, HTACT) VALUES (?, ?, ?, ?, ?, ' ', 0, 'CDE')
            """, ct, societe, cle.Numero, cle.Cle, maintenant.Year * 10000 + maintenant.Month * 100 + maintenant.Day,
            maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
    }

    // Suppression (option 4, SPSUP / SPneg100) d'une commande sans BL ni facture, contrôlés dans la transaction.
    // Renvoie les tables satellites qui n'ont pas pu être vidées (verrou...), pour le signaler.
    public async Task<IReadOnlyList<string>> SupprimerCommandeAsync(int societe, string profil, CleCommande cle, ContexteLigne c,
        Func<bool, bool, string?> refus, CancellationToken ct)
    {
        var b = Bib;
        object?[] k = [societe, cle.Numero, cle.Cle];
        await using var cnx = await OuvrirAsync(ct);
        await using (var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var validee = false;
            try
            {
                // Verrou de l'en-tête, puis plus de BL ni de facture (ils pourraient avoir été créés depuis l'ouverture)
                await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct, k);
                var (bl, facture) = await PiecesAsync(cnx, tx, b, societe, cle, ct);
                if (refus(bl, facture) is { } message) throw new RegleMetierException(message);

                // Journal : une suppression « S » par ligne (SPneg100)
                var maintenant = DateTime.Now;
                await Executer(cnx, tx, $"""
                    INSERT INTO {b}.NEG100E (RSOC, RDOSS, DNDCLE, DNNLIG, ANCART, ANCND, ANCQTE, ANCPUS, ANCUV, ANCPXC, DNART, DNIND, DNQTEC,
                        DNPUS, DNUV, DNPXCE, DEDTMO, DEDTHE, DETYMO, DETRAI, DEPROF, ANCREP, DNREP, ANCTYC, DNTYPC)
                    SELECT RSOC, RDOSS, DNDCLE, DNNLIG, DNART, DNIND, DNQTEC, DNPUS, DNUV, DNPXCE, ' ', ' ', 0, 0, ' ', 0,
                        CAST(? AS NUMERIC(8, 0)), CAST(? AS NUMERIC(6, 0)), 'S', 'N', CAST(? AS CHAR(10)), CAST(? AS NUMERIC(3, 0)), 0,
                        CAST(? AS CHAR(1)), ' '
                    FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?
                    """, ct, maintenant.Day * 1_000_000 + maintenant.Month * 10_000 + maintenant.Year,
                    maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second, profil.ToUpperInvariant(), c.Representant,
                    c.TypeCommande, societe, cle.Numero, cle.Cle);
                // Événements des lignes (comme la suppression d'une ligne par MAJ520)
                await Executer(cnx, tx, $"""
                    DELETE FROM {b}.EVE105 WHERE EVLIG IN (SELECT EVLIG FROM {b}.EVE100 WHERE RSOC = ? AND EVCDC = ? AND EVCDCC = ?)
                    """, ct, k);
                await Executer(cnx, tx, $"DELETE FROM {b}.EVE100 WHERE RSOC = ? AND EVCDC = ? AND EVCDCC = ?", ct, k);
                foreach (var table in new[] { "NEG100T", "NEG100S", "NEG100" })
                    await Executer(cnx, tx, $"DELETE FROM {b}.{table} WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?", ct, k);
                await Executer(cnx, tx, $"DELETE FROM {b}.EXP020 WHERE RSOC = ? AND RDOSS = ? AND F4DCLE = ?", ct, k);
                // ÉCART : AB001FA et AB0016 supprimés (orphelins dans le RPG)
                await Executer(cnx, tx, $"DELETE FROM {b}.AB001FA WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct, k);
                await Executer(cnx, tx, $"DELETE FROM {b}.AB0016 WHERE RSOC = ? AND RDOSS = ? AND F16CLE = ?", ct, k);
                if (await Executer(cnx, tx, $"DELETE FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC IN ('3', '4')", ct, k) == 0)
                    throw new RegleMetierException("Cette commande n'existe plus.");
                await TransfertAsync(cnx, tx, b, societe, cle, c, ct);
                await tx.CommitAsync(ct);
                validee = true;
            }
            finally
            {
                if (!validee) await tx.RollbackAsync(CancellationToken.None);
            }
        }

        // Tables satellites non journalisées (normalement vides pour une commande de négoce ou de produit fini)
        var echecs = new List<string>();
        foreach (var (table, dossier, cleCol) in new[]
                 {
                     ("ABREPE", "RDOSS", "LADCLE"), ("ABREPD", "RDOSS", "LADCLE"), ("ABCOUTE", "CODOSS", "CODCLE"),
                     ("ABCOUTD", "CODOSS", "CODCLE"), ("ABCOUTB", "CODOSS", "CODCLE"), ("ABDIRL", "RDOSS", "CCDCLE"),
                     ("ABDIRP", "RDOSS", "CCDCLE"), ("DIR100", "RDOSS", "CCDCLE"),
                 })
            await SupprimerSansTransactionAsync(cnx, table, $"DELETE FROM {b}.{table} WHERE RSOC = ? AND {dossier} = ? AND {cleCol} = ?", k, echecs);
        await SupprimerSansTransactionAsync(cnx, "AFC020", $"DELETE FROM {b}.AFC020 WHERE RSOC = ? AND FCDOSS = ? AND FCDCLE = ?", k, echecs);
        // Liens commande → OF (AB0020, commande d'origine LADOSC / LACLDC)
        await SupprimerSansTransactionAsync(cnx, "AB0020", $"DELETE FROM {b}.AB0020 WHERE RSOC = ? AND LADOSC = ? AND LACLDC = ?", k, echecs);
        await AuditDossierAsync(cnx, b, societe, profil, cle, "SU");
        return echecs;
    }

    // Table ou colonne absente selon l'installation (SQLSTATE 42704 / 42703) : ignoré ; toute autre erreur (verrou...) est signalée
    private static async Task SupprimerSansTransactionAsync(OdbcConnection cnx, string table, string sql, object?[] valeurs, List<string> echecs)
    {
        try
        {
            await Executer(cnx, null, sql, CancellationToken.None, valeurs);
        }
        catch (OdbcException ex) when (ex.Errors.Count > 0 && ex.Errors[0].SQLState is "42704" or "42703")
        {
        }
        catch (OdbcException)
        {
            echecs.Add(table);
        }
    }

    // Historique du dossier (DOS040) pour un dossier antérieur au jour, initiales de l'utilisateur
    private static async Task HistoriqueDossierAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, string profil,
        CleCommande cle, ContexteLigne c, string type, CancellationToken ct)
    {
        if (c.DateDossier is not { } dd || dd >= DateOnly.FromDateTime(DateTime.Today)) return;
        var initiales = "";
        await using (var rd = await Lire(cnx, tx, $"SELECT ACINI FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil.ToUpperInvariant()))
            if (await rd.ReadAsync(ct)) initiales = Socle.Donnees.BaseDb2.Texte(rd, 0);
        var maintenant = DateTime.Now;
        await Executer(cnx, tx, $"""
            INSERT INTO {b}.DOS040 (RSOC, RDOSS, HICLE, HIGEN, HICLI, HIINAM, HITYPM, HIDTMA, HIDTMM, HIDTMJ, HIHEUR)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, ct, societe, cle.Numero, cle.Cle, c.CompteGeneral, c.CompteClient, initiales == "" ? " " : initiales, type,
            maintenant.Year, maintenant.Month, maintenant.Day, maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
    }

    // Audit EVE200 (EVE400), hors transaction (table non journalisée) ; n° de ligne MAX + 1 comme EVE400, nouvel essai en cas
    // de collision avec un autre poste ; jamais d'exception
    private static async Task AuditDossierAsync(OdbcConnection cnx, string b, int societe, string profil, CleCommande cle, string type)
    {
        for (var essai = 0; essai < 3; essai++)
        try
        {
            await Executer(cnx, null, $"""
                INSERT INTO {b}.EVE200 (AMLIG, RSOC, AMTYPE, AMDATE, AMPROF, AMDOSS, AMDCLE)
                SELECT COALESCE(MAX(AMLIG), 0) + 1, CAST(? AS NUMERIC(4, 0)), CAST(? AS CHAR(2)), CURRENT TIMESTAMP,
                       CAST(? AS CHAR(10)), CAST(? AS NUMERIC(5, 0)), CAST(? AS CHAR(1)) FROM {b}.EVE200
                """, CancellationToken.None, societe, type, profil.ToUpperInvariant(), cle.Numero, cle.Cle);
            return;
        }
        catch (Exception)
        {
            // audit non bloquant
        }
    }
}
