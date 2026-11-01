using System.Data;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Édition définitive d'une facture (FAC199 / FAC201 en type définitif, R1 R7 R9 R14 R15 R17) en une transaction : facture
// relue verrouillée (seulement « à éditer », FDEDIT = O), totaux recalculés sous verrou, puis :
//  - FAC080 : FDEDIT = E, FDEDT = O, date d'édition, HT, ports, escompte, TVA, échéance ;
//  - FAC090 : éléments édités (FEEDT = O) ;
//  - EXP010 : BL de la facture facturés (HBLFAC = O ; BL rattachés par HNOFAC et la date de facture, comme EXP010L7) ;
//  - AB001 : n° et date de la dernière facture, quantité facturée de l'élément (+ facture, − avoir) ;
//  - NEG100 : quantité facturée des lignes des éléments de type 2 et 3 (+ facture, − avoir).
//  - FAC070 / FAC072 : historique commercial (HistoriqueCommercial, comme FAC199 : vérifié sur 300 factures éditées par l'AS400).
// Pas repris ici : notifications TRTMSG. Une facture
// « supprimée » (FDEDIT = S) n'est plus effacée de FAC080 (R17) : elle est refusée.
// =====================================================================
internal sealed class EditionDefinitiveDb2(IConfiguration config, CalculFacturesDb2 calcul)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task<TotauxFacture> EditerAsync(int societe, int numero, DateOnly date, DateOnly aujourdhui, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        Task<Lecteur> Lire(string sql, params object?[] v) => Lecteur.ExecuterAsync(cnx, tx, sql.Replace("{b}", Bib), ct, v);
        async Task<int> Executer(string sql, params object?[] v)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql.Replace("{b}", Bib);
            foreach (var x in v) cmd.Parameters.Valeur(x is string t && t == "" ? " " : x);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
        var valide = false;
        try
        {
            bool avoir;
            await using (var rd = await Lire("""
                SELECT FDEDIT, FDTYPE FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, cle))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} n'existe plus.");
                var edit = rd.Texte(0);
                if (edit == "E") throw new RegleMetierException($"La facture {numero} est déjà éditée en définitif.");
                if (edit == "A") throw new RegleMetierException($"La facture {numero} est à contrôler : elle ne peut pas être éditée en définitif.");
                if (edit != "O") throw new RegleMetierException($"La facture {numero} ne peut pas être éditée (statut {edit}).");
                avoir = rd.Texte(1) == "A";
            }
            var f = await calcul.LireAsync(cnx, tx, societe, numero, date, ct)
                    ?? throw new RegleMetierException($"La facture {numero} n'existe plus.");
            if (f.Lignes.Count == 0) throw new RegleMetierException($"La facture {numero} n'a aucune ligne facturée.");
            var t = CalculFacture.Calculer(f.Lignes, f.Conditions);

            await Executer("""
                UPDATE {b}.FAC080 SET FDEDIT = 'E', FDEDT = 'O', FDEDA = ?, FDEDM = ?, FDEDJ = ?, FDMTN = ?, FDPOT = ?, FDPOE = ?, FDMES = ?, FDTVA = ?,
                       FDECHA = ?, FDECHM = ?, FDECHJ = ?
                WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                """, [aujourdhui.Year, aujourdhui.Month, aujourdhui.Day, t.Ht, t.PortTaxable, t.PortExonere, t.Escompte, t.TotalTva,
                    t.Echeance.Year, t.Echeance.Month, t.Echeance.Day, .. cle]);
            await Executer("UPDATE {b}.FAC090 SET FEEDT = 'O' WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?", cle);
            await Executer("UPDATE {b}.EXP010 SET HBLFAC = 'O' WHERE RSOC = ? AND HNOFAC = ? AND HDAFAA = ? AND HDAFAM = ? AND HDAFAJ = ?", cle);

            await QuantitesFactureesAsync(cnx, tx, Bib, cle, avoir ? -1 : 1, derniereFacture: true, ct);

            // Historique commercial (FAC199 HISTO / HISNEG) ; un historique resté d'une édition annulée est remplacé
            await Executer("DELETE FROM {b}.FAC072 WHERE RSOC = ? AND RNFAC = ? AND HNDTFA = ? AND HNDTFM = ? AND HNDTFJ = ?", cle);
            await Executer("DELETE FROM {b}.FAC070 WHERE RSOC = ? AND RNFAC = ? AND HFDTFA = ? AND HFDTFM = ? AND HFDTFJ = ?", cle);
            if (await HistoriqueCommercialDb2.LireAsync(cnx, tx, Bib, societe, numero, date, ct) is { } h)
            {
                var (fac070, fac072) = HistoriqueCommercial.Calculer(h.Entete, h.Parametres, h.Elements);
                await HistoriqueCommercialDb2.EcrireAsync(cnx, tx, Bib, "FAC072", fac072, ct);
                await HistoriqueCommercialDb2.EcrireAsync(cnx, tx, Bib, "FAC070", fac070, ct);
            }
            await tx.CommitAsync(ct);
            valide = true;
            return t;
        }
        finally
        {
            // Annulation sans masquer l'erreur d'origine (connexion perdue : la transaction est de toute façon abandonnée)
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }

    // Commandes des éléments (FAC199 SPTOT8) : quantité facturée de l'élément (AB001.CCQTEP) et des lignes négoce des éléments de type
    // 2 et 3 (NEG100.DNQTEF), ajoutée (signe 1) ou retirée (signe −1) ; à l'édition, n° et date de la dernière facture (AB001).
    // Partagé avec la remise « à éditer » (FAC166), pour que l'une soit toujours l'inverse exact de l'autre. cle : société, n°, date.
    internal static async Task QuantitesFactureesAsync(OdbcConnection cnx, OdbcTransaction tx, string bib, object[] cle, int signe,
        bool derniereFacture, CancellationToken ct)
    {
        async Task Executer(string sql, params object?[] v)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql.Replace("{b}", bib);
            foreach (var x in v) cmd.Parameters.Valeur(x is string t && t == "" ? " " : x);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var societe = cle[0];
        var elements = new List<(int Num, int Dossier, string Cle, int Type, decimal Qte)>();
        await using (var rd = await Lecteur.ExecuterAsync(cnx, tx, """
            SELECT FENUM, FEDOSS, FECLE, FETYP, FEQTE FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?
            AND FEDOSS <> 0 ORDER BY FENUM
            """.Replace("{b}", bib), ct, cle))
            while (await rd.ReadAsync(ct)) elements.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Nombre(4)));
        foreach (var el in elements)
        {
            if (derniereFacture)
                await Executer("""
                    UPDATE {b}.AB001 SET CCNOFA = ?, CCFCTA = ?, CCFCTM = ?, CCFCTJ = ?, CCQTEP = CCQTEP + ?
                    WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                    """, [.. cle[1..], signe * el.Qte, societe, el.Dossier, el.Cle]);
            else
                await Executer("UPDATE {b}.AB001 SET CCQTEP = CCQTEP + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", signe * el.Qte, societe, el.Dossier, el.Cle);
            if (el.Type is not (2 or 3)) continue;
            await Executer("""
                UPDATE {b}.NEG100 n SET DNQTEF = DNQTEF + ? * (SELECT COALESCE(SUM(l.FGQTE), 0) FROM {b}.FAC100 l
                    WHERE l.RSOC = ? AND l.RNFAC = ? AND l.FGANNE = ? AND l.FGMOIS = ? AND l.FGJOUR = ? AND l.FGNEL = ?
                      AND l.FGLIGD = n.DNNLIG AND l.FGART = n.DNART)
                WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ?
                  AND EXISTS (SELECT 1 FROM {b}.FAC100 l WHERE l.RSOC = ? AND l.RNFAC = ? AND l.FGANNE = ? AND l.FGMOIS = ? AND l.FGJOUR = ?
                      AND l.FGNEL = ? AND l.FGLIGD = n.DNNLIG AND l.FGART = n.DNART AND l.FGART NOT LIKE '999%')
                """, [signe, .. cle, el.Num, societe, el.Dossier, el.Cle, .. cle, el.Num]);
        }
    }
}
