using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Lot E : lectures et écritures DB2 de la décomposition analytique (ex-FEN_Decomposition).
// Requêtes reprises du WinDev (branche C:\Mes Projets\ConsultationDevis), en SQL paramétré.
// Écritures dans une transaction : D5000F, D50007, D60001, D60002 et D50001 sont journalisés (vérifié le 30/10/2026).
// =====================================================================
internal sealed class DecompositionDevisDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private const string Devis = "RSOC = ? AND RCOUT = ? AND RDEV = ? AND RVERS = ? AND RHYPO = ?";
    private static object[] CleSql(int societe, CleDevis cle) => [societe, cle.TypeCout, cle.Numero, cle.Version, cle.Hypothese];

    // Colonnes de ventilation : valeur et % par chapitre A à O. Papier : la valeur du chapitre A est DDDA1
    // (DDDA est la date d'achat du papier).
    private static string Colonnes(string prefixe, string alias = "") => string.Join(", ",
        LimitesDecomposition.Lettres.Select(l =>
            $"{alias}{prefixe}{l}{(prefixe == "DDD" && l == 'A' ? "1" : "")}, {alias}{prefixe}{l}PC"));

    private static (int[] Valeurs, int[] Pourcents) LireVentilation(DbDataReader rd, int debut)
    {
        var v = new int[LimitesDecomposition.Chapitres];
        var p = new int[LimitesDecomposition.Chapitres];
        for (var i = 0; i < LimitesDecomposition.Chapitres; i++)
        {
            v[i] = rd.Entier(debut + 2 * i);
            p[i] = rd.Entier(debut + 2 * i + 1);
        }
        return (v, p);
    }

    // ------------------------------------------------------------------
    // Lecture
    // ------------------------------------------------------------------
    public async Task<DonneesDecomposition?> LireAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        var devis = CleSql(societe, cle);
        await using var cnx = await OuvrirAsync(ct);

        // 1. Montants du devis, unité de vente, catégorie (perte de fin de bobine)
        EnteteDecompositionBrute? e = null;
        await using (var cmd = Commande(cnx, null, $"""
            SELECT a.DEQTED, a.DEPVR, a.DEUNIT, d.TNUM, a.DEPRA, a.DEPR, a.DEMCO, a.DEMES, a.DEMRI, a.DEMCR, a.DEMFF,
                   a.DEMFS, a.DEMFA, a.DEMFMA, a.DEPVM, a.DEPVMM, a.DEPVC, a.DEPVCM, a.DECAT, e.TTYPDT
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA00083 d ON a.RSOC = d.RSOC AND a.DEUNIT = d.RARG AND d.RTABLE = 'UV' AND d.TTAG <> '*'
            LEFT OUTER JOIN {b}.AA0009 e ON a.RSOC = e.RSOC AND a.DECAT = e.RCAT AND e.TTAG <> '*'
            WHERE a.RSOC = ? AND a.RCOUT = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ?
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct))
            {
                var m = new decimal[14];
                for (var i = 0; i < 14; i++) m[i] = rd.Nombre(4 + i);
                e = new EnteteDecompositionBrute(rd.Entier(0), rd.Nombre(1), rd.Texte(2), rd.Entier(3), m, rd.Texte(18), rd.Texte(19));
            }
        if (e is null) return null;

        // 2. Méthode de valorisation et frais généraux de la société
        var methode = ""; var fg = new decimal[5];
        await using (var cmd = Commande(cnx, null,
            $"SELECT CFTYPE, CKFG1, CKFG2, CKFG3, CKFG4, CKFG5 FROM {b}.AA000Z WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", societe))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct))
            {
                methode = rd.Texte(0);
                for (var i = 0; i < 5; i++) fg[i] = rd.Nombre(1 + i);
            }

        // 3. Unités de vente
        var unites = new List<UniteVente>();
        await using (var cmd = Commande(cnx, null, $"""
            SELECT RARG, TLIB1, TNUM FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = 'UV' AND TTAG <> '*' ORDER BY RARG
            """, societe))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) unites.Add(new UniteVente(rd.Texte(0), rd.Texte(1), rd.Entier(2)));

        // 4. Légende du devis (ligne 0 = titre du chapitre)
        var legende = new List<(string Chapitre, int Ligne, string Texte)>();
        await using (var cmd = Commande(cnx, null,
            $"SELECT F7CHAPI, F7LIGNE, F7TXT FROM {b}.D50007 WHERE {Devis} ORDER BY F7CHAPI, F7LIGNE", devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) legende.Add((rd.Texte(0), rd.Entier(1), rd.Texte(2)));

        // 5. Perte de fin de bobine (AA0002.TPPERB) : type de produit + catégorie, à défaut catégorie à blanc
        decimal perteBobine = 0;
        await using (var cmd = Commande(cnx, null, $"""
            SELECT TPPERB FROM {b}.AA0002 WHERE RSOC = ? AND RTYPD = ? AND RCAT = ?
            UNION ALL
            SELECT TPPERB FROM {b}.AA0002 WHERE RSOC = ? AND RTYPD = ? AND RCAT = ' '
            """, societe, e.TypeProduit, e.Categorie, societe, e.TypeProduit))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var p = rd.Nombre(0);
                if (p > 0) { perteBobine = p; break; }
            }

        // 6. Papier de chaque élément (valeur = poids converti par CONVUP × prix d'achat)
        var papiers = new List<PapierDecompositionBrut>();
        await using (var cmd = Commande(cnx, null, $"""
            SELECT RELMT, DDDESI, DDSORT, DDSSOR, DDGRAM, DDCOUL, DDLRFP, DDHAFP, DDPROV, DDFOUB, DDCON, DDHAFT, DDNTR, DDNTRG,
                   CASE WHEN DDUA = 'KG' THEN DDPDSP * DDPA
                        ELSE {b}.CONVUP(CHAR('KG'), CHAR(DDUA), CHAR(DDFOUB), DDLRFP, DDHAFP, DDGRAM, DDPDSP) * DDPA END,
                   {Colonnes("DDD")}
            FROM {b}.D60001
            WHERE {Devis}
            ORDER BY RELMT
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var (v, p) = LireVentilation(rd, 15);
                papiers.Add(new PapierDecompositionBrut(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4),
                    rd.Texte(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Nombre(10), rd.Nombre(11),
                    rd.Nombre(12), rd.Nombre(13), rd.Nombre(14), v, p));
            }

        // 7. Lignes de fabrication à résultat > 0 (opérations O, matières M, sous-traitance S), dans l'ordre du WinDev
        //    (type de ligne, référence) ; n° de ligne en dernier critère pour un ordre stable.
        var lignes = new List<LigneFabricationDecompositionBrute>();
        await using (var cmd = Commande(cnx, null, $"""
            SELECT a.RELMT, a.DDTYPL, a.DDREF, a.DDVALO, b.RSLIB,
                   CASE WHEN d.GNOM IS NULL THEN 'Matière inexistante' ELSE d.GNOM END, c.OLIB, c.ROPE, d.RMATIE, a.DDLBRF,
                   a.DDLIG, a.DDRSQT, a.DDPRMA, a.DDPRMO, a.DDCOMF, {Colonnes("DDE", "a.")}
            FROM {b}.D60002 a
            LEFT OUTER JOIN {b}.C800F b ON a.RSOC = b.RSOC AND SUBSTR(a.DDREF, 1, 3) = b.RSECT AND b.RSTAG <> '*'
            LEFT OUTER JOIN {b}.D000F c ON a.RSOC = c.RSOC AND SUBSTR(a.DDREF, 4, 3) = DIGITS(c.ROPE) AND c.OTAG <> '*'
            LEFT OUTER JOIN {b}.C000F d ON a.RSOC = d.RSOC AND a.DDREF = DIGITS(d.RMATIE) AND d.GTAG <> '*'
            WHERE a.RSOC = ? AND a.RCOUT = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.DDRSQT > 0
            ORDER BY a.RELMT, a.DDTYPL, a.DDREF, a.DDLIG
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var (v, p) = LireVentilation(rd, 15);
                lignes.Add(new LigneFabricationDecompositionBrute(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Nombre(3),
                    rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Entier(7), rd.Entier(8), rd.Texte(9), rd.Entier(10),
                    rd.Nombre(11), rd.Nombre(12), rd.Nombre(13), rd.Texte(14), v, p));
            }

        // 8. Transport : port total du devis et ventilation (commune à toutes les lignes de D50001)
        decimal port = 0;
        await using (var cmd = Commande(cnx, null, $"SELECT SUM(DEMTP1) FROM {b}.D50001 WHERE {Devis}", devis))
        {
            var r = await cmd.ExecuteScalarAsync(ct);
            if (r is not null and not DBNull) port = Convert.ToDecimal(r);
        }
        (int[] Valeurs, int[] Pourcents)? transport = null;
        await using (var cmd = Commande(cnx, null,
            $"SELECT {Colonnes("DDT")} FROM {b}.D50001 WHERE {Devis} FETCH FIRST 1 ROW ONLY", devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) transport = LireVentilation(rd, 0);

        return new DonneesDecomposition(e, methode, fg, unites, legende, perteBobine, papiers, lignes, port, transport);
    }

    // Prix actuels du devis (contrôle d'accès simultané, conversion des prix mini et conseillés)
    public async Task<PrixActuelsBruts?> LirePrixAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, null, $"""
            SELECT a.DEPVR, a.DEUNIT, d.TNUM, a.DEQTED, a.DEPVM, a.DEPVMM, a.DEPVC, a.DEPVCM
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA00083 d ON a.RSOC = d.RSOC AND a.DEUNIT = d.RARG AND d.RTABLE = 'UV' AND d.TTAG <> '*'
            WHERE a.RSOC = ? AND a.RCOUT = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ?
            """, CleSql(societe, cle));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new PrixActuelsBruts(rd.Nombre(0), rd.Texte(1), rd.Entier(2), rd.Entier(3), rd.Nombre(4), rd.Nombre(5),
                rd.Nombre(6), rd.Nombre(7))
            : null;
    }

    public async Task<List<UniteVente>> LireUnitesAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, null,
            $"SELECT RARG, TLIB1, TNUM FROM {Bib}.AA00083 WHERE RSOC = ? AND RTABLE = 'UV' AND TTAG <> '*'", societe);
        var unites = new List<UniteVente>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) unites.Add(new UniteVente(rd.Texte(0), rd.Texte(1), rd.Entier(2)));
        return unites;
    }

    // ------------------------------------------------------------------
    // Écriture (BTN_Valider), dans une transaction. false : le devis a changé depuis la lecture des prix
    // (D5000F non mis à jour, rien n'est écrit).
    // ------------------------------------------------------------------
    public async Task<bool> EnregistrerAsync(int societe, CleDevis cle, EcritureDecomposition ecriture, CancellationToken ct)
    {
        var b = Bib;
        var devis = CleSql(societe, cle);
        await using var cnx = await OuvrirAsync(ct);
        using var tx = cnx.BeginTransaction(IsolationLevel.ReadCommitted);

        // 1. Prix remis, unité, prix mini et conseillés convertis — seulement si les prix n'ont pas changé
        var a = ecriture.Attendu;
        await using (var cmd = Commande(cnx, tx, $"""
            UPDATE {b}.D5000F SET DEPVR = ?, DEUNIT = ?, DEPVM = ?, DEPVMM = ?, DEPVC = ?, DEPVCM = ?
            WHERE {Devis} AND DEPVR = ? AND DEUNIT = ? AND DEPVM = ? AND DEPVMM = ? AND DEPVC = ? AND DEPVCM = ?
            """,
            [ecriture.PrixRemis, ecriture.Unite, ecriture.PvMini, ecriture.PvMiniMillePlus, ecriture.PvConseille,
             ecriture.PvConseilleMillePlus, .. devis, a.Depvr, a.Deunit, a.Depvm, a.Depvmm, a.Depvc, a.Depvcm]))
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            {
                tx.Rollback();
                return false;
            }

        // 2. Légende : suppression puis réinsertion (ligne 0 = titre du chapitre)
        await using (var cmd = Commande(cnx, tx, $"DELETE FROM {b}.D50007 WHERE {Devis}", devis))
            await cmd.ExecuteNonQueryAsync(ct);
        for (var c = 0; c < ecriture.Legende.Count; c++)
        {
            var chapitre = ecriture.Legende[c];
            var lettre = LimitesDecomposition.Lettre(c);
            await InsererLegendeAsync(cnx, tx, devis, lettre, 0, chapitre.Titre, ct);
            for (var v = 0; v < chapitre.Valeurs.Count; v++)
                await InsererLegendeAsync(cnx, tx, devis, lettre, v + 1, chapitre.Valeurs[v], ct);
        }

        // 3. Ventilation de chaque feuille
        foreach (var l in ecriture.Lignes)
        {
            object[] ventilation = [.. Enumerable.Range(0, LimitesDecomposition.Chapitres)
                .SelectMany(i => new object[] { l.Valeurs[i], l.Pourcents[i] })];
            switch (l.Cible.Table)
            {
                case TableVentilation.Papier:
                    await ExecuterAsync(cnx, tx, $"UPDATE {b}.D60001 SET {Affectations("DDD")} WHERE {Devis} AND RELMT = ?",
                        [.. ventilation, .. devis, l.Cible.Element], ct);
                    break;
                case TableVentilation.Fabrication:
                    // ÉCART (A1, validé) : toutes les lignes fusionnées à l'affichage, pas seulement la première
                    foreach (var ligne in l.Cible.Lignes)
                        await ExecuterAsync(cnx, tx,
                            $"UPDATE {b}.D60002 SET {Affectations("DDE")} WHERE {Devis} AND RELMT = ? AND DDLIG = ?",
                            [.. ventilation, .. devis, l.Cible.Element, ligne], ct);
                    break;
                case TableVentilation.Transport:
                    await ExecuterAsync(cnx, tx, $"UPDATE {b}.D50001 SET {Affectations("DDT")} WHERE {Devis}",
                        [.. ventilation, .. devis], ct);
                    break;
            }
        }

        tx.Commit();
        return true;
    }

    private static string Affectations(string prefixe) => string.Join(", ",
        LimitesDecomposition.Lettres.Select(l =>
            $"{prefixe}{l}{(prefixe == "DDD" && l == 'A' ? "1" : "")} = ?, {prefixe}{l}PC = ?"));

    private async Task InsererLegendeAsync(OdbcConnection cnx, OdbcTransaction tx, object[] devis, string chapitre, int ligne,
        string texte, CancellationToken ct) =>
        await ExecuterAsync(cnx, tx, $"""
            INSERT INTO {Bib}.D50007 (RSOC, RCOUT, RDEV, RVERS, RHYPO, F7CHAPI, F7LIGNE, F7TXT) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """, [.. devis, chapitre, ligne, texte], ct);

    private static async Task ExecuterAsync(OdbcConnection cnx, OdbcTransaction tx, string sql, object?[] valeurs, CancellationToken ct)
    {
        await using var cmd = Commande(cnx, tx, sql, valeurs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v);
        return cmd;
    }
}

// Montants de D5000F, dans l'ordre : 0 DEPRA 1 DEPR 2 DEMCO 3 DEMES 4 DEMRI 5 DEMCR 6 DEMFF 7 DEMFS 8 DEMFA 9 DEMFMA
// 10 DEPVM 11 DEPVMM 12 DEPVC 13 DEPVCM
internal sealed record EnteteDecompositionBrute(
    int Quantite, decimal PrixRemis, string Unite, int Tnum, decimal[] M, string Categorie, string TypeProduit);

internal sealed record PapierDecompositionBrut(
    int Element, string Designation, string Sorte, string SousSorte, string Grammage, string Coloris, string Laize,
    string Hauteur, string Provenance, string FeuilleBobine, decimal Conditionnement, decimal HauteurTirage,
    decimal Tours, decimal GacheFixe, decimal Valeur, int[] Valeurs, int[] Pourcents);

internal sealed record LigneFabricationDecompositionBrute(
    int Element, string Type, string Ref, decimal Valeur, string LibSection, string LibMatiere, string LibOperation,
    int Operation, int Matiere, string LibelleRef, int Ligne, decimal Quantite, decimal PrixMachine, decimal PrixMainOeuvre,
    string Commentaire, int[] Valeurs, int[] Pourcents);

internal sealed record DonneesDecomposition(
    EnteteDecompositionBrute Entete, string Methode, decimal[] FraisGeneraux, List<UniteVente> Unites,
    List<(string Chapitre, int Ligne, string Texte)> Legende, decimal PerteBobine, List<PapierDecompositionBrut> Papiers,
    List<LigneFabricationDecompositionBrute> Lignes, decimal Port, (int[] Valeurs, int[] Pourcents)? Transport);

internal sealed record PrixActuelsBruts(
    decimal Depvr, string Deunit, int Tnum, int Quantite, decimal Depvm, decimal Depvmm, decimal Depvc, decimal Depvcm);

internal sealed record EcritureDecomposition(
    decimal PrixRemis, string Unite, decimal PvMini, decimal PvMiniMillePlus, decimal PvConseille, decimal PvConseilleMillePlus,
    PrixActuelsBruts Attendu, IReadOnlyList<ChapitreLegende> Legende, IReadOnlyList<VentilationLigne> Lignes);
