using System.Data;
using System.Data.Odbc;
using NetPrint.Socle;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Copie d'une facture (FAC080S option 3 → FAC150 mode D) : nouvelle facture « à éditer » (ou « à contrôler » si l'originale l'est),
// datée du jour, n° pris au compteur, en une transaction. Toutes les zones de l'en-tête (FAC080), du pied (FAC081), des éléments
// (FAC090), des lignes (FAC100) et des textes d'élément (FAC105) sont reprises, sauf : clé, édition, comptabilisation, journaux,
// traite, envoi, Factur-X, totaux et acompte (remis à zéro, comme FAC150 en duplication : acompte 0, « à éditer »), échéance
// recalculée et profil. Colonnes lues dans le catalogue (QSYS2.SYSCOLUMNS) : une colonne ajoutée à la table est copiée aussi.
// Les BL, éléments en plus et compléments ne sont pas rattachés à la copie (les lignes gardent la référence du BL, comme le RPG).
// Comme le RPG : la copie éditée en définitif ajoute de nouveau ses quantités à la commande (AB001, NEG100) — elle refacture.
// Colonnes générées par la base (identité, horodatage…) laissées à la base.
// Avoir d'une facture éditée (FAC080S option SA : SpCreTot, SpCrePar) : même copie en type A, facture d'origine (FDFACO, FDFACA),
// « à éditer » ; avoir total = toute la facture, et ses BL (EXP010) et éléments en plus (EXP011) redeviennent à facturer ; avoir
// d'un BL = les éléments et lignes de ce BL (montants d'élément à zéro, recalculés en modification), et ce BL seul redevient à
// facturer : quantité d'élément = celle de ce BL (EXP010.HQTELI, retirée de la commande à l'édition), honoraires et pied non repris.
// Un deuxième avoir de la même facture (ou du même BL) est refusé tant que le premier n'est pas supprimé. Comme le RPG : compléments
// AB004 laissés facturés ; lignes de texte sans BL non reprises dans l'avoir d'un BL ; BL libérés dès la création de l'avoir, et pas
// rattachés de nouveau à la facture si l'avoir est supprimé.
// =====================================================================

// Avoir demandé : Bl null = avoir total
internal sealed record AvoirDemande(int? Bl);
internal sealed partial class FacturationAutoDb2
{
    // En-tête : zones remises à zéro (0 ou blanc selon le type)
    private static readonly HashSet<string> RemisesAZeroEnTete =
    [
        "FDCODA", "FDCODM", "FDCODJ", "FDJLPA", "FDJLPM", "FDJLPJ", "FDJLMA", "FDJLMM", "FDJLMJ", "FDTRTA", "FDTRTM", "FDTRTJ",
        "FDEDA", "FDEDM", "FDEDJ", "FDMTN", "FDPOT", "FDPOE", "FDMES", "FDTVA", "FDAMTN", "FDAPOT", "FDAPOE", "FDAMES", "FDATVA",
        "FDCPT", "FDACPT", "FDTAGE", "FDDATEE", "FDHEURE", "FDMANAU", "FDMETHE", "FDTYPEE", "FDIDFACTX", "FDSTAT", "FDFACSTAT",
    ];

    // Renvoie le n° de la copie et son statut (« à contrôler » si l'originale l'est) ; avoir : avoir de la facture (voir ci-dessus)
    public async Task<(int Numero, bool AControler)> CopierFactureAsync(int societe, string profil, NumerotationFactures numerotation, int numero, DateOnly origine,
        DateOnly date, DateOnly echeance, CancellationToken ct, AvoirDemande? avoir = null)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        Task<int> Executer(string sql, params object?[] v) => ExecuterAsync(cnx, tx, sql, ct, v);
        var valide = false;
        try
        {
            string statut;
            var cle = new object[] { societe, numero, origine.Year, origine.Month, origine.Day };
            await using (var rd = await Lire(cnx, tx, """
                SELECT FDEDIT, FDTYPE FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, cle))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {origine:dd/MM/yyyy} n'existe plus.");
                if (rd.Texte(0) == "S") throw new RegleMetierException($"La facture {numero} est supprimée : elle ne se copie pas.");
                // FAC080S option SA : avoir d'une facture (pas d'un avoir) éditée ; pas encore éditée, elle se modifie ou se supprime
                if (avoir is not null && rd.Texte(1) != "F") throw new RegleMetierException($"Le document {numero} est un avoir : on ne fait pas d'avoir d'un avoir.");
                if (avoir is not null && rd.Texte(0) != "E")
                    throw new RegleMetierException($"La facture {numero} n'est pas éditée : modifiez-la ou supprimez-la plutôt que d'en faire un avoir.");
                statut = avoir is null && rd.Texte(0) == "A" ? "A" : "O";
            }

            // Avoir déjà fait (non supprimé) : total, ou du même BL
            if (avoir is not null)
                await using (var rd = await Lire(cnx, tx, """
                    SELECT a.RNFAC, (SELECT COUNT(*) FROM {b}.FAC100 l WHERE l.RSOC = a.RSOC AND l.RNFAC = a.RNFAC AND l.FGANNE = a.FDANNE
                                     AND l.FGMOIS = a.FDMOIS AND l.FGJOUR = a.FDJOUR AND l.FGBL = ?),
                           (SELECT COUNT(DISTINCT l.FGBL) FROM {b}.FAC100 l WHERE l.RSOC = a.RSOC AND l.RNFAC = a.RNFAC AND l.FGANNE = a.FDANNE
                                     AND l.FGMOIS = a.FDMOIS AND l.FGJOUR = a.FDJOUR AND l.FGBL <> 0)
                    FROM {b}.FAC080 a WHERE a.RSOC = ? AND a.FDTYPE = 'A' AND a.FDFACO = ? AND a.FDFACA = ? AND a.FDEDIT <> 'S'
                    """, ct, avoir.Bl ?? -1, societe, numero, origine.Year))
                    while (await rd.ReadAsync(ct))
                        // avoir total demandé : tout avoir existant gêne ; avoir d'un BL : un avoir qui a des lignes de ce BL
                        if (avoir.Bl is null || rd.Entier(1) > 0)
                            throw new RegleMetierException($"L'avoir {rd.Entier(0)} existe déjà pour {(avoir.Bl is { } dejaBl ? $"le BL {dejaBl} de " : "")}la facture {numero} : "
                                + "supprimez-le ou modifiez-le plutôt que d'en faire un autre.");

            // Avoir d'un BL : éléments qui ont des lignes de ce BL
            var parBl = avoir?.Bl is not null;
            const string ElementsDuBl = "SELECT FGNEL FROM {b}.FAC100 WHERE RSOC = ? AND RNFAC = ? AND FGANNE = ? AND FGMOIS = ? AND FGJOUR = ? AND FGBL = ?";
            var elementsDuBl = 0;
            if (avoir?.Bl is { } b)
            {
                await using var rd = await Lire(cnx, tx, $"SELECT COUNT(DISTINCT FGNEL) FROM ({ElementsDuBl}) x", ct, [.. cle, b]);
                if (await rd.ReadAsync(ct)) elementsDuBl = rd.Entier(0);
                if (elementsDuBl == 0) throw new RegleMetierException($"Le BL {b} n'a aucune ligne sur la facture {numero}.");
            }

            var nouveau = await NumeroAsync(cnx, tx, Executer, societe, numerotation, date, ct);
            await using (var rd = await Lire(cnx, tx, "SELECT 1 FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
                             ct, societe, nouveau, date.Year, date.Month, date.Day))
                if (await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {nouveau} du {date:dd/MM/yyyy} existe déjà : vérifiez le compteur des factures.");

            foreach (var (table, colNumero, colAnnee, colMois, colJour) in DatesDeFacture.Take(5))   // FAC080, FAC081, FAC090, FAC100, FAC105
            {
                var valeurs = new Dictionary<string, object?>
                {
                    [colNumero] = nouveau, [colAnnee] = date.Year, [colMois] = date.Month, [colJour] = date.Day,
                };
                IReadOnlySet<string> aZero = new HashSet<string>();
                if (table == "FAC080")
                {
                    foreach (var c in new[] { "FDEDT", "FDCOMP", "FDBROU", "FDJALP", "FDJALM", "FDTRAT", "FDTAGR" }) valeurs[c] = "N";
                    valeurs["FDEDIT"] = statut;
                    valeurs["FDPROF"] = Tronquer(profil, 10);
                    (valeurs["FDECHA"], valeurs["FDECHM"], valeurs["FDECHJ"]) = (echeance.Year, echeance.Month, echeance.Day);
                    aZero = RemisesAZeroEnTete;
                    if (avoir is not null)
                    {
                        (valeurs["FDTYPE"], valeurs["FDFACO"], valeurs["FDFACA"]) = ("A", numero, origine.Year);
                        if (parBl) valeurs["FDPLEL"] = elementsDuBl;
                    }
                }
                if (table == "FAC090")
                {
                    valeurs["FEEDT"] = "N";
                    if (parBl) aZero = new HashSet<string> { "FECA", "FEVEN", "FECAA", "FEVENA", "FEMTO" };
                }
                if (table == "FAC081" && parBl) continue;   // pied de toute la facture : pas pour l'avoir d'un BL
                var condition = $"RSOC = ? AND {colNumero} = ? AND {colAnnee} = ? AND {colMois} = ? AND {colJour} = ?";
                object?[] parametres = cle;
                if (avoir?.Bl is { } blAvoir && table is "FAC090" or "FAC100" or "FAC105")
                {
                    condition += table switch
                    {
                        "FAC100" => " AND FGBL = ?",
                        "FAC090" => $" AND FENUM IN ({ElementsDuBl})",
                        _ => $" AND FTNEL IN ({ElementsDuBl})",
                    };
                    parametres = table == "FAC100" ? [.. cle, blAvoir] : [.. cle, .. cle, blAvoir];
                }
                await CopierLignesAsync(cnx, tx, table, valeurs, aZero, condition, parametres, ct);
            }

            // Avoir d'un BL : quantité de chaque élément = celle de ce BL (avant de libérer le BL)
            if (avoir?.Bl is { } blQte)
                await Executer("""
                    UPDATE {b}.FAC090 e SET FEQTE = (SELECT COALESCE(SUM(x.HQTELI), 0) FROM {b}.EXP010 x WHERE x.RSOC = e.RSOC AND x.HNOFAC = ?
                        AND x.HDAFAA = ? AND x.HDAFAM = ? AND x.HDAFAJ = ? AND x.HNOBL = ? AND x.HNOELT = e.FENUM)
                    WHERE e.RSOC = ? AND e.RNFAC = ? AND e.FEANNE = ? AND e.FEMOIS = ? AND e.FEJOUR = ?
                    """, numero, origine.Year, origine.Month, origine.Day, blQte, societe, nouveau, date.Year, date.Month, date.Day);

            // Avoir : BL (tous, ou celui demandé) et éléments en plus de la facture de nouveau à facturer
            if (avoir is not null)
            {
                var filtreBl = avoir.Bl is null ? "" : " AND HNOBL = ?";
                object?[] p = avoir.Bl is { } x ? [.. cle, x] : cle;
                await Executer($"""
                    UPDATE {"{b}"}.EXP010 SET HBLFAC = 'N', HNOFAC = 0, HNOELT = 0, HDAFAA = 0, HDAFAM = 0, HDAFAJ = 0
                    WHERE RSOC = ? AND HNOFAC = ? AND HDAFAA = ? AND HDAFAM = ? AND HDAFAJ = ?{filtreBl}
                    """, p);
                await Executer($"""
                    UPDATE {"{b}"}.EXP011 SET HPFACT = 'N', HPNFAC = 0, HPNELT = 0, HPDFAA = 0, HPDFAM = 0, HPDFAJ = 0
                    WHERE RSOC = ? AND HPNFAC = ? AND HPDFAA = ? AND HPDFAM = ? AND HPDFAJ = ?{filtreBl}
                    """, p);
            }
            await tx.CommitAsync(ct);
            valide = true;
            return (nouveau, statut == "A");
        }
        finally
        {
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }

    // INSERT … SELECT de la table sur elle-même : colonnes du catalogue, valeurs imposées (paramètres typés), remises à zéro, reste copié
    private async Task CopierLignesAsync(OdbcConnection cnx, OdbcTransaction tx, string table, IReadOnlyDictionary<string, object?> valeurs,
        IReadOnlySet<string> aZero, string condition, object?[] parametres, CancellationToken ct)
    {
        var colonnes = new List<(string Nom, string Type, int Longueur, int Echelle)>();
        await using (var rd = await Lire(cnx, tx, """
            SELECT COLUMN_NAME, DATA_TYPE, LENGTH, COALESCE(NUMERIC_SCALE, 0) FROM QSYS2.SYSCOLUMNS
            WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? AND HAS_DEFAULT IN ('N', 'Y') ORDER BY ORDINAL_POSITION
            """, ct, Bib.ToUpperInvariant(), table))
            while (await rd.ReadAsync(ct)) colonnes.Add((rd.Texte(0).Trim(), rd.Texte(1).Trim(), rd.Entier(2), rd.Entier(3)));
        if (colonnes.Count == 0) throw new InvalidOperationException($"Colonnes de {table} introuvables dans le catalogue.");

        static bool Numerique(string type) => type is "DECIMAL" or "NUMERIC" or "INTEGER" or "SMALLINT" or "BIGINT";
        static string TypeSql((string Nom, string Type, int Longueur, int Echelle) c) => c.Type switch
        {
            "DECIMAL" or "NUMERIC" => $"{c.Type}({c.Longueur}, {c.Echelle})",
            "CHAR" or "VARCHAR" or "GRAPHIC" or "VARGRAPHIC" => $"{c.Type}({c.Longueur})",
            _ => c.Type,
        };
        var selection = new List<string>();
        var valeursSelection = new List<object?>();
        foreach (var c in colonnes)
            if (valeurs.TryGetValue(c.Nom, out var v))
            {
                selection.Add($"CAST(? AS {TypeSql(c)})");
                valeursSelection.Add(v);
            }
            else if (aZero.Contains(c.Nom)) selection.Add(Numerique(c.Type) ? "0" : "' '");
            else selection.Add(c.Nom);
        await ExecuterAsync(cnx, tx, $"""
            INSERT INTO {"{b}"}.{table} ({string.Join(", ", colonnes.Select(c => c.Nom))})
            SELECT {string.Join(", ", selection)} FROM {"{b}"}.{table} WHERE {condition}
            """, ct, [.. valeursSelection, .. parametres]);
    }
}
