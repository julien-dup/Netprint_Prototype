using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Écran de lancement : données de la fenêtre de lancement (commande, fiches articles, devis, modèles, dossiers précédents)
// et affectation de stock (option AF / SA de NEG515 : lien AB0020 de type « S », soldé, entre un OF ouvert et la commande ;
// SYN100 « AS » si la commande est sous-traitée à un autre site). Les OF eux-mêmes sont demandés à la production.
// =====================================================================
internal sealed partial class LancementCommandeDb2
{
    public async Task<PreparationLancement.Donnees?> LirePreparationAsync(int societe, CleCommande cle, IReadOnlyList<ReferenceArticle> references,
        CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);

        PreparationLancement.Commande commande;
        await using (var rd = await Lire(cnx, $"""
            SELECT RGEN, RCLI, CCENSE, CCREFC, CCNINT, CCDES1, CCDES2, CCDATA, CCDATM, CCDATJ, CCDENA, CCDENM, CCDENJ,
                   CCDTLA, CCDTLM, CCDTLJ, CCOP10
            FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC IN ('3', '4')
            """, ct, societe, cle.Numero, cle.Cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            commande = new(cle, rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6),
                BaseDb2.Date(rd.Entier(7), rd.Entier(8), rd.Entier(9)), BaseDb2.Date(rd.Entier(10), rd.Entier(11), rd.Entier(12)),
                BaseDb2.Date(rd.Entier(13), rd.Entier(14), rd.Entier(15)), rd.Texte(16) == "O");
        }
        var modeleParArticle = false;
        await using (var rd = await Lire(cnx, $"SELECT SOP22 FROM {b}.AA0007 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct)) modeleParArticle = rd.Texte(0) == "O";

        // Fiches des articles (dans l'ordre demandé), puis ce qui en découle pour les articles gérés en stock
        var articles = new List<PreparationLancement.Article>();
        foreach (var r in references)
        {
            var a = new PreparationLancement.Article(r.Article, r.Indice, false, "", "", "", "", null, null, null, 0, null, null);
            await using (var rd = await Lire(cnx, $"""
                SELECT ASTK, AFAM, ASFAM, ADES, ACNORM, ADRDOS, ADRCLE, AMODEL FROM {b}.SPF010 WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?
                """, ct, societe, r.Article, r.Indice))
                if (await rd.ReadAsync(ct))
                    a = a with
                    {
                        Stocke = rd.Texte(0) == "O", Famille = rd.Texte(1), SousFamille = rd.Texte(2), Designation = rd.Texte(3), Norme = rd.Texte(4),
                        DernierDossier = rd.Entier(5) > 0 ? new NumeroOf(rd.Entier(5), rd.Texte(6)) : null, Modele = rd.Entier(7),
                    };
            if (a.Stocke)
            {
                // Devis de la 1re ligne de commande de l'article
                await using (var rd = await Lire(cnx, $"""
                    SELECT DNDEV, DNVERS, DNHYPO FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNART = ? AND DNIND = ?
                    ORDER BY DNNLIG FETCH FIRST 1 ROW ONLY
                    """, ct, societe, cle.Numero, cle.Cle, r.Article, r.Indice))
                    if (await rd.ReadAsync(ct) && rd.Entier(0) > 0) a = a with { Devis = new DevisOrigine(rd.Entier(0), rd.Entier(1), rd.Entier(2)) };
                // Fabrication en cours de l'article (premier lien non soldé, AB0020L1)
                await using (var rd = await Lire(cnx, $"""
                    SELECT RDOSS, LADCLE FROM {b}.AB0020 WHERE RSOC = ? AND LAART = ? AND LAIND = ? AND LASOLD <> 'O'
                    ORDER BY RDOSS, LADCLE, LALIG FETCH FIRST 1 ROW ONLY
                    """, ct, societe, r.Article, r.Indice))
                    if (await rd.ReadAsync(ct)) a = a with { FabricationEnCours = new NumeroOf(rd.Entier(0), rd.Texte(1)) };
                if (modeleParArticle && a.Modele > 0)
                {
                    await using (var rd = await Lire(cnx, $"SELECT MDES1, MDES2 FROM {b}.MOD010 WHERE RSOC = ? AND RMODEL = ? FETCH FIRST 1 ROW ONLY",
                                     ct, societe, a.Modele))
                        if (await rd.ReadAsync(ct)) a = a with { DesignationModele = (rd.Texte(0), rd.Texte(1)) };
                    // Dernier dossier de fabrication du modèle (AB001LA lu à l'envers : modèle, année, mois, dossier)
                    await using (var rd = await Lire(cnx, $"""
                        SELECT RDOSS, CCDCLE FROM {b}.AB001 WHERE RSOC = ? AND CCMODL = ? AND CCTYPC = '2'
                        ORDER BY CCDATA DESC, CCDATM DESC, RDOSS DESC, CCDCLE DESC FETCH FIRST 1 ROW ONLY
                        """, ct, societe, a.Modele))
                        if (await rd.ReadAsync(ct)) a = a with { DernierDossierModele = new NumeroOf(rd.Entier(0), rd.Texte(1)) };
                }
            }
            articles.Add(a);
        }

        var devis = new Dictionary<DevisOrigine, PreparationLancement.Devis>();
        foreach (var dv in articles.Select(a => a.Devis).OfType<DevisOrigine>().Distinct())
            await using (var rd = await Lire(cnx, $"""
                SELECT DEDES1, DEDES2, DEDOSP, DECLEP, DECHEF FROM {b}.D5000F
                WHERE RSOC = ? AND RCOUT = 'C' AND RDEV = ? AND RVERS = ? ORDER BY CASE WHEN RHYPO = ? THEN 0 ELSE 1 END, RHYPO
                FETCH FIRST 1 ROW ONLY
                """, ct, societe, dv.Numero, dv.Version, dv.Hypothese))
                if (await rd.ReadAsync(ct))
                    devis[dv] = new(rd.Texte(0), rd.Texte(1), rd.Entier(2) > 0 ? new NumeroOf(rd.Entier(2), rd.Texte(3)) : null, rd.Texte(4));

        // Modèle et indice de composition des dossiers précédents possibles
        var dossiers = new Dictionary<NumeroOf, PreparationLancement.Dossier>();
        var candidats = articles.SelectMany(a => new[] { a.FabricationEnCours, a.DernierDossier, a.DernierDossierModele })
            .Concat(devis.Values.Select(d => d.DossierPrecedent)).OfType<NumeroOf>().Distinct();
        foreach (var of in candidats)
            await using (var rd = await Lire(cnx, $"SELECT CCMODL, CCINDC FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                             ct, societe, of.Numero, of.Cle))
                if (await rd.ReadAsync(ct)) dossiers[of] = new(rd.Entier(0), rd.Texte(1));

        return new(commande, modeleParArticle, articles, devis, dossiers);
    }

    // F10 : la commande a-t-elle déjà un lien vers un OF existant (ouvert ou à ouvrir) ? Les liens orphelins sont ignorés.
    public async Task<bool> OfDejaCreeAsync(int societe, CleCommande cle, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT COUNT(*) FROM {b}.AB0020 l WHERE l.RSOC = ? AND l.LADOSC = ? AND l.LACLDC = ?
              AND (EXISTS (SELECT 1 FROM {b}.AB001 o WHERE o.RSOC = l.RSOC AND o.RDOSS = l.RDOSS AND o.CCDCLE = l.LADCLE)
                OR EXISTS (SELECT 1 FROM {b}.AB001T t WHERE t.RSOC = l.RSOC AND t.RDOSS = l.RDOSS AND t.CCDCLE = l.LADCLE))
            """, ct, societe, cle.Numero, cle.Cle);
        return await rd.ReadAsync(ct) && rd.Entier(0) > 0;
    }

    // Affectation (AF, SPAB020) : lien de type « S » créé, ou quantité affectée remplacée ; date de fin de fabrication = date
    // de livraison de l'OF (NEG515 voulait la reprendre mais écrivait 0, anomalie A1).
    // ÉCART : un lien de fabrication entre l'OF et la commande n'est plus transformé en affectation (anomalie A2).
    public async Task AffecterAsync(int societe, CleCommande cle, NumeroOf of, ReferenceArticle article, int quantite, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            var sousTraitee = await VerrouillerCommandeAsync(cnx, tx, b, societe, cle, ct);
            // Verrou de l'OF : deux commandes n'affectent pas son disponible en même temps
            await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                societe, of.Numero, of.Cle);
            await using (var rd = await Lire(cnx, tx, $"SELECT 1 FROM {b}.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                             societe, of.Numero, of.Cle))
                if (await rd.ReadAsync(ct)) throw new RegleMetierException(PreparationLancement.Messages.AffectationImpossible);
            var (ofSousTraite, livraison) = (false, 0);
            await using (var rd = await Lire(cnx, tx, $"SELECT CCOP10, CCDTLA, CCDTLM, CCDTLJ FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                             ct, societe, of.Numero, of.Cle))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"Affectation impossible : l'OF {of.Libelle} n'existe pas.");
                (ofSousTraite, livraison) = (rd.Texte(0) == "O",
                    BaseDb2.Date(rd.Entier(1), rd.Entier(2), rd.Entier(3)) is { } d ? BaseDb2.AAAAMMJJ(d) : 0);
            }
            if (sousTraitee && !ofSousTraite) throw new RegleMetierException("Affectation impossible : cet OF n'est pas sous-traité.");

            var liens = await LiensAsync(cnx, tx, b, societe, cle, of, article, ct);
            if (liens.Any(l => l.Type != "S"))
                throw new RegleMetierException($"L'OF {of.Libelle} est lancé pour cette commande : la quantité se modifie dans le lancement.");
            // Revérifié sous verrou : affecté à la commande hors cet OF + quantité ≤ commandé (CDE0008)
            var (commande, affecteAilleurs) = (0, 0);
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT (SELECT COALESCE(SUM(DNQTEC), 0) FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNART = ? AND DNIND = ?),
                       (SELECT COALESCE(SUM(LAQTEA), 0) FROM {b}.AB0020 WHERE RSOC = ? AND LADOSC = ? AND LACLDC = ? AND LAART = ? AND LAIND = ?
                          AND NOT (RDOSS = ? AND LADCLE = ?))
                FROM SYSIBM.SYSDUMMY1
                """, ct, societe, cle.Numero, cle.Cle, article.Article, article.Indice, societe, cle.Numero, cle.Cle, article.Article, article.Indice,
                of.Numero, of.Cle))
                if (await rd.ReadAsync(ct)) (commande, affecteAilleurs) = (rd.Entier(0), rd.Entier(1));
            if (affecteAilleurs + quantite > commande) throw new DemandeOfInvalideException([new("quantite", PreparationLancement.Messages.AffecteSuperieurCommande)]);
            int ligne;
            if (liens.Count > 0)
            {
                ligne = liens[0].Ligne;
                await Executer(cnx, tx, $"""
                    UPDATE {b}.AB0020 SET LAQTEA = ?, LADTFF = ?, LASOLD = 'O' WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LALIG = ?
                    """, ct, quantite, livraison, societe, of.Numero, of.Cle, ligne);
                // Un seul lien d'affectation par OF, article et commande
                foreach (var l in liens.Skip(1))
                    await Executer(cnx, tx, $"DELETE FROM {b}.AB0020 WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LALIG = ?", ct,
                        societe, of.Numero, of.Cle, l.Ligne);
            }
            else
            {
                ligne = 1;
                await using (var rd = await Lire(cnx, tx, $"SELECT COALESCE(MAX(LALIG), 0) + 1 FROM {b}.AB0020 WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ?",
                                 ct, societe, of.Numero, of.Cle))
                    if (await rd.ReadAsync(ct)) ligne = rd.Entier(0);
                await Executer(cnx, tx, $"""
                    INSERT INTO {b}.AB0020 (RSOC, RDOSS, LADCLE, LALIG, LAART, LAIND, LAQTEC, LAQTEF, LADOSC, LACLDC, LADTFF, LASOLD, LAQTEA, LATYPE)
                    VALUES (?, ?, ?, ?, ?, ?, 0, 0, ?, ?, ?, 'O', ?, 'S')
                    """, ct, societe, of.Numero, of.Cle, ligne, article.Article, article.Indice, cle.Numero, cle.Cle, livraison, quantite);
            }
            if (sousTraitee) await TransfertAffectationAsync(cnx, tx, b, societe, of, article, ligne, ct);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Suppression de l'affectation (SA, SPSUPA) : liens « S » de l'article entre l'OF et la commande
    public async Task SupprimerAffectationAsync(int societe, CleCommande cle, NumeroOf of, ReferenceArticle article, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            var sousTraitee = await VerrouillerCommandeAsync(cnx, tx, b, societe, cle, ct);
            var liens = (await LiensAsync(cnx, tx, b, societe, cle, of, article, ct)).Where(l => l.Type == "S").ToList();
            if (liens.Count == 0) throw new RegleMetierException("Suppression impossible : aucune affectation de stock de cet OF à la commande.");
            foreach (var l in liens)
            {
                await Executer(cnx, tx, $"DELETE FROM {b}.AB0020 WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LALIG = ?", ct,
                    societe, of.Numero, of.Cle, l.Ligne);
                if (sousTraitee) await TransfertAffectationAsync(cnx, tx, b, societe, of, article, l.Ligne, ct);
            }
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------
    // Verrou de la commande (deux affectations de la même commande passent l'une après l'autre) ; renvoie CCOP10
    private static async Task<bool> VerrouillerCommandeAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, CleCommande cle,
        CancellationToken ct)
    {
        if (await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC IN ('3', '4')",
                ct, societe, cle.Numero, cle.Cle) == 0)
            throw new RegleMetierException($"La commande {cle.Libelle} n'existe plus.");
        await using var rd = await Lire(cnx, tx, $"SELECT CCOP10 FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
            societe, cle.Numero, cle.Cle);
        return await rd.ReadAsync(ct) && rd.Texte(0) == "O";
    }

    private static async Task<List<(int Ligne, string Type)>> LiensAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe,
        CleCommande cle, NumeroOf of, ReferenceArticle article, CancellationToken ct)
    {
        var liens = new List<(int, string)>();
        await using var rd = await Lire(cnx, tx, $"""
            SELECT LALIG, LATYPE FROM {b}.AB0020
            WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LAART = ? AND LAIND = ? AND LADOSC = ? AND LACLDC = ? ORDER BY LALIG
            """, ct, societe, of.Numero, of.Cle, article.Article, article.Indice, cle.Numero, cle.Cle);
        while (await rd.ReadAsync(ct)) liens.Add((rd.Entier(0), rd.Texte(1)));
        return liens;
    }

    // Commande sous-traitée à un autre site : demande de transfert SYN100 « AS » (la quantité porte le n° de ligne du lien,
    // lu ainsi par SYNAS, qui recopie le lien s'il existe encore, ou le supprime) ; HTHEUR est un horodatage
    private static async Task TransfertAffectationAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, NumeroOf of,
        ReferenceArticle article, int ligne, CancellationToken ct)
    {
        var maintenant = DateTime.Now;
        await Executer(cnx, tx, $"""
            INSERT INTO {b}.SYN100 (HTSOC, HTDOSS, HTDCLE, HTDATE, HTHEUR, HTSTAT, HTNBL, HTART, HTIND, HTQTET, HTACT)
            VALUES (?, ?, ?, ?, ?, ' ', 0, ?, ?, ?, 'AS')
            """, ct, societe, of.Numero, of.Cle, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant, article.Article, article.Indice, ligne);
    }

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static async Task<int> Executer(OdbcConnection cnx, OdbcTransaction tx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var cmd = Commande(cnx, tx, sql, valeurs);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, OdbcTransaction tx, string sql, CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, tx, sql, valeurs).ExecuteReaderAsync(ct);
}
