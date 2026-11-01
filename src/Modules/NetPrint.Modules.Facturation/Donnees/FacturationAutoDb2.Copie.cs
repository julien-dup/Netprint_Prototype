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
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    // En-tête : zones remises à zéro (0 ou blanc selon le type)
    private static readonly HashSet<string> RemisesAZeroEnTete =
    [
        "FDCODA", "FDCODM", "FDCODJ", "FDJLPA", "FDJLPM", "FDJLPJ", "FDJLMA", "FDJLMM", "FDJLMJ", "FDTRTA", "FDTRTM", "FDTRTJ",
        "FDEDA", "FDEDM", "FDEDJ", "FDMTN", "FDPOT", "FDPOE", "FDMES", "FDTVA", "FDAMTN", "FDAPOT", "FDAPOE", "FDAMES", "FDATVA",
        "FDCPT", "FDACPT", "FDTAGE", "FDDATEE", "FDHEURE", "FDMANAU", "FDMETHE", "FDTYPEE", "FDIDFACTX", "FDSTAT", "FDFACSTAT",
    ];

    // Renvoie le n° de la copie et son statut (« à contrôler » si l'originale l'est)
    public async Task<(int Numero, bool AControler)> CopierFactureAsync(int societe, string profil, NumerotationFactures numerotation, int numero, DateOnly origine,
        DateOnly date, DateOnly echeance, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        Task<int> Executer(string sql, params object?[] v) => ExecuterAsync(cnx, tx, sql, ct, v);
        var valide = false;
        try
        {
            string statut;
            await using (var rd = await Lire(cnx, tx, "SELECT FDEDIT FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
                             ct, societe, numero, origine.Year, origine.Month, origine.Day))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {origine:dd/MM/yyyy} n'existe plus.");
                if (rd.Texte(0) == "S") throw new RegleMetierException($"La facture {numero} est supprimée : elle ne se copie pas.");
                statut = rd.Texte(0) == "A" ? "A" : "O";
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
                }
                if (table == "FAC090") valeurs["FEEDT"] = "N";
                await CopierLignesAsync(cnx, tx, table, valeurs, aZero,
                    $"RSOC = ? AND {colNumero} = ? AND {colAnnee} = ? AND {colMois} = ? AND {colJour} = ?",
                    [societe, numero, origine.Year, origine.Month, origine.Day], ct);
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
