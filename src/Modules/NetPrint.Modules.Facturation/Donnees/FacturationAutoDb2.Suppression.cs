using System.Data;
using System.Data.Odbc;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Suppression d'une facture pas encore éditée ou d'un de ses éléments (FAC160 option S, FAC150 mode S → FAC165) et remise « à éditer »
// d'une facture éditée non comptabilisée (FAC080S option 2 → FAC166), chacune en une transaction sous verrou de l'en-tête.
// Suppression : BL (EXP010), éléments en plus (EXP011) et compléments (AB004) redeviennent à facturer ; lignes (FAC100), textes
// d'élément (FAC105) et éléments (FAC090) effacés ; l'en-tête reste, marqué supprimé (FDEDIT = S), son n° n'est pas réutilisé.
// Écarts au RPG : FAC165 n'efface pas les textes FAC105 (son saut d'étiquette les oublie) et efface le pied FAC081 de toute la
// facture quand il ne supprime qu'un élément ; ici FAC105 est effacé avec son élément et le pied ne l'est qu'avec la facture.
// Remise « à éditer » : exact inverse de l'édition définitive (EditionDefinitiveDb2, mêmes conditions que FAC199) ; FAC166 retire les
// quantités avec d'autres conditions (type de commande CCTYPC 1, ou 3 et 4) que celles du FAC199 qui les a ajoutées : non repris.
// Comme FAC165 / FAC166 : n° de dernière facture de la commande (AB001.CCNOFA) laissé ; montants en autre monnaie remis à 0.
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    // Renvoie vrai si toute la facture est supprimée (demandée entière, ou dernier élément supprimé)
    public async Task<bool> SupprimerFactureAsync(int societe, int numero, DateOnly date, int? element, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var valide = false;
        try
        {
            var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
            await using (var rd = await Lire(cnx, tx, """
                SELECT FDEDIT FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, cle))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe plus.");
                if (rd.Texte(0) == "S") throw new RegleMetierException($"La facture {numero} est déjà supprimée.");
                if (rd.Texte(0) is not ("O" or "A"))
                    throw new RegleMetierException($"La facture {numero} est éditée : elle ne se supprime plus (faites un avoir).");
            }
            if (element is { } el)
            {
                await using var rd = await Lire(cnx, tx,
                    "SELECT 1 FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ? AND FENUM = ?", ct, [.. cle, el]);
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"L'élément {el} de la facture {numero} n'existe plus.");
            }

            // Libération et effacement, pour un élément ou toute la facture ; colonnes : n° de facture, date (année, mois, jour), élément
            async Task LibererAsync(int? e)
            {
                Task<int> Executer(string sql, string colElement) => ExecuterAsync(cnx, tx,
                    e is null ? sql : $"{sql} AND {colElement} = ?", ct, e is { } n ? [.. cle, n] : cle);
                await Executer("""
                    UPDATE {b}.EXP010 SET HBLFAC = 'N', HNOFAC = 0, HNOELT = 0, HDAFAA = 0, HDAFAM = 0, HDAFAJ = 0
                    WHERE RSOC = ? AND HNOFAC = ? AND HDAFAA = ? AND HDAFAM = ? AND HDAFAJ = ?
                    """, "HNOELT");
                await Executer("""
                    UPDATE {b}.EXP011 SET HPFACT = 'N', HPNFAC = 0, HPNELT = 0, HPDFAA = 0, HPDFAM = 0, HPDFAJ = 0
                    WHERE RSOC = ? AND HPNFAC = ? AND HPDFAA = ? AND HPDFAM = ? AND HPDFAJ = ?
                    """, "HPNELT");
                await Executer("""
                    UPDATE {b}.AB004 SET F4FACT = 'N', F4NFAC = 0, F4NELT = 0, F4DFAA = 0, F4DFAM = 0, F4DFAJ = 0
                    WHERE RSOC = ? AND F4NFAC = ? AND F4DFAA = ? AND F4DFAM = ? AND F4DFAJ = ?
                    """, "F4NELT");
                await Executer("DELETE FROM {b}.FAC100 WHERE RSOC = ? AND RNFAC = ? AND FGANNE = ? AND FGMOIS = ? AND FGJOUR = ?", "FGNEL");
                await Executer("DELETE FROM {b}.FAC105 WHERE RSOC = ? AND RNFAC = ? AND FTANNE = ? AND FTMOIS = ? AND FTJOUR = ?", "FTNEL");
                await Executer("DELETE FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?", "FENUM");
            }
            await LibererAsync(element);

            var reste = 0;
            if (element is not null)
            {
                await using (var rd = await Lire(cnx, tx,
                                 "SELECT COUNT(*) FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?", ct, cle))
                    if (await rd.ReadAsync(ct)) reste = rd.Entier(0);
                // Dernier élément retiré : lignes et BL rattachés à aucun élément libérés aussi (la facture entière est supprimée)
                if (reste == 0) await LibererAsync(null);
            }
            if (reste == 0)
                await ExecuterAsync(cnx, tx, "DELETE FROM {b}.FAC081 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?", ct, cle);
            // Élément : nombre d'éléments mis à jour ; plus aucun élément : en-tête marqué supprimé (FDPLEL gardé pour la facture entière, comme FAC165)
            if (element is not null)
                await ExecuterAsync(cnx, tx, "UPDATE {b}.FAC080 SET FDPLEL = ? WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
                    ct, [reste, .. cle]);
            if (reste == 0)
                await ExecuterAsync(cnx, tx, "UPDATE {b}.FAC080 SET FDEDIT = 'S' WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?", ct, cle);
            await tx.CommitAsync(ct);
            valide = true;
            return reste == 0;
        }
        finally
        {
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }

    // FAC166 : facture éditée non comptabilisée remise « à éditer » : totaux, date d'édition et échéance remis à zéro, éléments non
    // édités, quantités facturées des commandes retirées, historique commercial (FAC070, FAC072) effacé. BL laissés facturés (ils le
    // sont dès la création de la facture).
    public async Task RemettreAEditerAsync(int societe, int numero, DateOnly date, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var valide = false;
        try
        {
            var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
            bool avoir;
            await using (var rd = await Lire(cnx, tx, """
                SELECT FDEDIT, FDCOMP, FDTYPE FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, cle))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe plus.");
                if (rd.Texte(0) != "E") throw new RegleMetierException($"La facture {numero} n'est pas éditée : rien à remettre « à éditer ».");
                if (rd.Texte(1) != "N") throw new RegleMetierException($"La facture {numero} est comptabilisée : elle ne se modifie plus (faites un avoir).");
                avoir = rd.Texte(2) == "A";
            }

            // Quantités facturées retirées par le même code que l'édition définitive qui les a ajoutées (signe inverse) ;
            // n° de dernière facture de la commande laissé, comme FAC166
            await EditionDefinitiveDb2.QuantitesFactureesAsync(cnx, tx, BaseDb2.Bibliotheque(config), cle, avoir ? 1 : -1, derniereFacture: false, ct);
            await ExecuterAsync(cnx, tx, "UPDATE {b}.FAC090 SET FEEDT = 'N' WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?", ct, cle);
            await ExecuterAsync(cnx, tx, "DELETE FROM {b}.FAC072 WHERE RSOC = ? AND RNFAC = ? AND HNDTFA = ? AND HNDTFM = ? AND HNDTFJ = ?", ct, cle);
            await ExecuterAsync(cnx, tx, "DELETE FROM {b}.FAC070 WHERE RSOC = ? AND RNFAC = ? AND HFDTFA = ? AND HFDTFM = ? AND HFDTFJ = ?", ct, cle);
            await ExecuterAsync(cnx, tx, """
                UPDATE {b}.FAC080 SET FDEDIT = 'O', FDEDT = 'N', FDEDA = 0, FDEDM = 0, FDEDJ = 0, FDMTN = 0, FDPOT = 0, FDPOE = 0, FDMES = 0,
                    FDTVA = 0, FDAMTN = 0, FDAPOT = 0, FDAMES = 0, FDATVA = 0, FDECHA = 0, FDECHM = 0, FDECHJ = 0
                WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                """, ct, cle);
            await tx.CommitAsync(ct);
            valide = true;
        }
        finally
        {
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }
}
