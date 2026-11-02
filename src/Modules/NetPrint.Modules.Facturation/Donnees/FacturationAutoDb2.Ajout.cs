using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Ajout de BL à une facture pas encore éditée (FAC080S option AE → FAC085 « regroupement sur une facture » → FAC150 mode A) :
// une transaction, en-tête verrouillé ; même donneur d'ordre et même type (facture ou avoir) que la facture ; une commande = un
// nouvel élément, numéroté à la suite (le RPG n'interdit pas une commande déjà présente : son contrôle est en commentaire) ;
// mêmes écritures que la création (BL, éléments en plus, compléments marqués facturés). En-tête : nombre d'éléments (R41) et
// statut ; date, conditions, client facturé et acompte inchangés (R4). « À contrôler » si les nouvelles lignes ont une anomalie ou
// un autre code vente que la facture.
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    public const int ElementsMaxi = 99;   // FENUM sur 2 chiffres

    // Renvoie vrai si la facture est « à contrôler » après l'ajout
    public async Task<bool> AjouterElementsAsync(int societe, int numero, DateOnly date, FacturePrevue f, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var valide = false;
        try
        {
            var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
            bool dejaAControler;
            string codeVente;
            await using (var rd = await Lire(cnx, tx, """
                SELECT FDEDIT, FDTYPE, FDGEND, FDCLID, FDVEN FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, cle))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe plus.");
                if (rd.Texte(0) is not ("O" or "A"))
                    throw new RegleMetierException($"La facture {numero} a été {(rd.Texte(0) == "S" ? "supprimée" : "éditée")} entre-temps : on ne peut plus lui ajouter de BL.");
                if ((rd.Texte(1) == "A") != f.Avoir)
                    throw new RegleMetierException(f.Avoir ? $"Des retours (avoir) ne s'ajoutent pas à la facture {numero}." : $"Des BL livrés ne s'ajoutent pas à l'avoir {numero}.");
                if ((rd.Entier(2), rd.Entier(3)) != (f.CompteGeneral, f.CompteClient))
                    throw new RegleMetierException($"Les BL ne sont pas du donneur d'ordre de la facture {numero} ({rd.Entier(2)} {rd.Entier(3)}).");
                dejaAControler = rd.Texte(0) == "A";
                codeVente = rd.Texte(4).Trim();
            }

            int decalage;
            await using (var rd = await Lire(cnx, tx,
                             "SELECT COALESCE(MAX(FENUM), 0) FROM {b}.FAC090 WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ?", ct, cle))
                decalage = await rd.ReadAsync(ct) ? rd.Entier(0) : 0;
            if (decalage + f.Elements.Count > ElementsMaxi)
                throw new RegleMetierException($"Une facture porte au plus {ElementsMaxi} éléments : la facture {numero} en a déjà {decalage}.");

            await VerrouillerBlsAsync(cnx, tx, societe, f, ct);
            await EcrireElementsAsync(cnx, tx, societe, numero, date, f, decalage, ct);

            var autreCodeVente = codeVente != "" && f.CodeVente.Trim() != "" && f.CodeVente.Trim() != codeVente;
            var aControler = dejaAControler || f.AControler || autreCodeVente;
            await ExecuterAsync(cnx, tx, """
                UPDATE {b}.FAC080 a SET FDPLEL = (SELECT COUNT(*) FROM {b}.FAC090 e WHERE e.RSOC = a.RSOC AND e.RNFAC = a.RNFAC
                        AND e.FEANNE = a.FDANNE AND e.FEMOIS = a.FDMOIS AND e.FEJOUR = a.FDJOUR),
                    FDEDIT = ?, FDVEN = ?
                WHERE a.RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                """, ct, [aControler ? "A" : "O", codeVente != "" ? codeVente : f.CodeVente, .. cle]);
            await tx.CommitAsync(ct);
            valide = true;
            return aControler;
        }
        finally
        {
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }
}
