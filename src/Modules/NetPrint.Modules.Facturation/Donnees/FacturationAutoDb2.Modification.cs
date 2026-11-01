using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Élément d'une facture enregistrée : commande (et sa monnaie AB001.CCMON), BL facturés sur cet élément, lignes FAC100
internal sealed record ElementEnregistre(int Numero, int Commande, string Cle, string Designation, string Monnaie, IReadOnlyList<int> Bls,
    IReadOnlyList<LignePrevue> Lignes);

// Facture enregistrée (FAC080) relue pour la modification ; Edition : FDEDIT (O à éditer, A à contrôler, E éditée, S supprimée)
internal sealed record FactureEnregistree(bool Avoir, string Edition, int CompteGeneral, int CompteClient, string NomClient,
    int CompteGeneralFacture, int CompteClientFacture, ConditionsReglement Conditions, int Representant, string Reference, string AlAttention,
    decimal Acompte, string Enseigne, IReadOnlyList<ElementEnregistre> Elements);

// Élément modifié : chiffre d'affaires, ventes et lignes réécrites
internal sealed record ElementModifie(int Numero, decimal ChiffreAffaires, decimal Ventes, IReadOnlyList<LignePrevue> Lignes);

// Modification à enregistrer ; ClientFactureChange : l'adresse figée de l'ancien client facturé est effacée (celle de la fiche client
// sera reprise à l'édition)
internal sealed record ModificationFacture(int Numero, DateOnly Ancienne, DateOnly Nouvelle, int CompteGeneralFacture, int CompteClientFacture,
    bool ClientFactureChange, ConditionsReglement Conditions, DateOnly Echeance, int Representant, string Reference, string AlAttention,
    string CodeVente, bool AControler, IReadOnlyList<ElementModifie> Elements);

// =====================================================================
// Modification d'une facture pas encore éditée (FAC150 mode M) : relecture, puis enregistrement en une transaction sous verrou de
// l'en-tête. Changement de date reporté sur tous les fichiers qui portent la date de la facture (décision de David, 01/11 ; voir
// l'avoir 20501 aux lignes restées à l'ancienne date) : FAC080, FAC081, FAC090, FAC100, FAC105, FAC070, FAC072, FAC073, BL facturés
// (EXP010), éléments en plus (EXP011), compléments de commande (AB004). Lignes de chaque élément supprimées puis réécrites (R44).
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    public async Task<FactureEnregistree?> LireFactureAsync(int societe, int numero, DateOnly date, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
        FactureEnregistree? f;
        await using (var rd = await Lire(cnx, null, """
            SELECT a.FDTYPE, a.FDEDIT, a.FDGEND, a.FDCLID, COALESCE(c.CNOM, ''), a.FDGENF, a.FDCLIF, a.FDRGLT, a.FDNECH, a.FDNLE, a.FDESC,
                   a.FDART, a.FDREP, a.FDREF, a.FDATT, a.FDCPT, a.FDENS
            FROM {b}.FAC080 a
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.FDGEND AND c.RCLI = a.FDCLID
            WHERE a.RSOC = ? AND a.RNFAC = ? AND a.FDANNE = ? AND a.FDMOIS = ? AND a.FDJOUR = ?
            """, ct, cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            f = new FactureEnregistree(rd.Texte(0) == "A", rd.Texte(1), rd.Entier(2), rd.Entier(3), rd.Texte(4), rd.Entier(5), rd.Entier(6),
                new ConditionsReglement(rd.Texte(7), rd.Entier(8), rd.Entier(9), rd.Nombre(10), rd.Entier(11)), rd.Entier(12), rd.Texte(13),
                rd.Texte(14), rd.Nombre(15), rd.Texte(16), []);
        }
        var lignes = new Dictionary<int, List<LignePrevue>>();
        await using (var rd = await Lire(cnx, null, """
            SELECT FGNEL, FGNLG, FGCDL, FGQTE, FGDES, FGPU, FGPER, FGUN, FGART, FGIND, FGMTN, FGTCOL, FGLBL, FGMON, FGLIGD, FGBL, FGBLEL, FGBLLG,
                   FGCTC, FGNBR, FGPUA, FGMTNA
            FROM {b}.FAC100 WHERE RSOC = ? AND RNFAC = ? AND FGANNE = ? AND FGMOIS = ? AND FGJOUR = ? ORDER BY FGNEL, FGNLG
            """, ct, cle))
            while (await rd.ReadAsync(ct))
                Liste(lignes, rd.Entier(0)).Add(new LignePrevue(rd.Entier(1), rd.Texte(2), rd.Nombre(3), rd.Texte(4), rd.Nombre(5), rd.Entier(6),
                    rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Nombre(10), rd.Nombre(11), rd.Texte(12), rd.Texte(13), rd.Entier(14), rd.Entier(15),
                    rd.Entier(16), rd.Entier(17),
                    Conserve: new ZonesConservees(rd.Texte(18), rd.Nombre(19), rd.Nombre(20), rd.Nombre(21), rd.Nombre(10))));
        var bls = new Dictionary<int, List<int>>();
        await using (var rd = await Lire(cnx, null, """
            SELECT DISTINCT HNOELT, HNOBL FROM {b}.EXP010
            WHERE RSOC = ? AND HNOFAC = ? AND HDAFAA = ? AND HDAFAM = ? AND HDAFAJ = ? ORDER BY HNOELT, HNOBL
            """, ct, cle))
            while (await rd.ReadAsync(ct)) Liste(bls, rd.Entier(0)).Add(rd.Entier(1));
        var elements = new List<ElementEnregistre>();
        await using (var rd = await Lire(cnx, null, """
            SELECT e.FENUM, e.FEDOSS, e.FECLE, COALESCE(d.CCDES1, ''), COALESCE(d.CCMON, '')
            FROM {b}.FAC090 e
            LEFT OUTER JOIN {b}.AB001 d ON d.RSOC = e.RSOC AND d.RDOSS = e.FEDOSS AND d.CCDCLE = e.FECLE
            WHERE e.RSOC = ? AND e.RNFAC = ? AND e.FEANNE = ? AND e.FEMOIS = ? AND e.FEJOUR = ? ORDER BY e.FENUM
            """, ct, cle))
            while (await rd.ReadAsync(ct))
                elements.Add(new ElementEnregistre(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), bls.GetValueOrDefault(rd.Entier(0)) ?? [],
                    lignes.GetValueOrDefault(rd.Entier(0)) ?? []));
        return f with { Elements = elements };
    }

    // Fichiers qui portent la date de la facture : table, colonne du n° de facture, colonnes année / mois / jour
    private static readonly (string Table, string Numero, string Annee, string Mois, string Jour)[] DatesDeFacture =
    [
        ("FAC080", "RNFAC", "FDANNE", "FDMOIS", "FDJOUR"),
        ("FAC081", "RNFAC", "FDANNE", "FDMOIS", "FDJOUR"),
        ("FAC090", "RNFAC", "FEANNE", "FEMOIS", "FEJOUR"),
        ("FAC100", "RNFAC", "FGANNE", "FGMOIS", "FGJOUR"),
        ("FAC105", "RNFAC", "FTANNE", "FTMOIS", "FTJOUR"),
        ("FAC070", "RNFAC", "HFDTFA", "HFDTFM", "HFDTFJ"),
        ("FAC072", "RNFAC", "HNDTFA", "HNDTFM", "HNDTFJ"),
        ("FAC073", "RNFAC", "HRANNE", "HRMOIS", "HRJOUR"),
        ("EXP010", "HNOFAC", "HDAFAA", "HDAFAM", "HDAFAJ"),
        ("EXP011", "HPNFAC", "HPDFAA", "HPDFAM", "HPDFAJ"),
        ("AB004", "F4NFAC", "F4DFAA", "F4DFAM", "F4DFAJ"),
    ];

    public async Task ModifierFactureAsync(int societe, ModificationFacture m, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var valide = false;
        try
        {
            var (a, n) = (m.Ancienne, m.Nouvelle);
            await using (var rd = await Lire(cnx, tx, """
                SELECT FDEDIT FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, m.Numero, a.Year, a.Month, a.Day))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {m.Numero} du {a:dd/MM/yyyy} n'existe plus.");
                if (rd.Texte(0) is not ("O" or "A"))
                    throw new RegleMetierException($"La facture {m.Numero} a été {(rd.Texte(0) == "E" ? "éditée" : "supprimée")} entre-temps : elle n'est plus modifiable.");
            }
            if (n != a)
            {
                await using (var rd = await Lire(cnx, tx, "SELECT 1 FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
                                 ct, societe, m.Numero, n.Year, n.Month, n.Day))
                    if (await rd.ReadAsync(ct)) throw new RegleMetierException($"Une facture {m.Numero} du {n:dd/MM/yyyy} existe déjà.");
                foreach (var (table, numero, annee, mois, jour) in DatesDeFacture)
                    await ExecuterAsync(cnx, tx, $"""
                        UPDATE {"{b}"}.{table} SET {annee} = ?, {mois} = ?, {jour} = ?
                        WHERE RSOC = ? AND {numero} = ? AND {annee} = ? AND {mois} = ? AND {jour} = ?
                        """, ct, n.Year, n.Month, n.Day, societe, m.Numero, a.Year, a.Month, a.Day);
            }

            var cle = new object[] { societe, m.Numero, n.Year, n.Month, n.Day };
            foreach (var e in m.Elements)
            {
                await ExecuterAsync(cnx, tx, "DELETE FROM {b}.FAC100 WHERE RSOC = ? AND RNFAC = ? AND FGANNE = ? AND FGMOIS = ? AND FGJOUR = ? AND FGNEL = ?",
                    ct, [.. cle, e.Numero]);
                foreach (var l in e.Lignes) await InsererLigneAsync(cnx, tx, cle, e.Numero, l, ct);
                await ExecuterAsync(cnx, tx, """
                    UPDATE {b}.FAC090 SET FECA = ?, FEVEN = ?
                    WHERE RSOC = ? AND RNFAC = ? AND FEANNE = ? AND FEMOIS = ? AND FEJOUR = ? AND FENUM = ?
                    """, ct, [e.ChiffreAffaires, e.Ventes, .. cle, e.Numero]);
            }

            var c = m.Conditions;
            await ExecuterAsync(cnx, tx, """
                UPDATE {b}.FAC080 SET FDGENF = ?, FDCLIF = ?, FDRGLT = ?, FDNECH = ?, FDNLE = ?, FDART = ?, FDESC = ?, FDECHA = ?, FDECHM = ?,
                    FDECHJ = ?, FDREP = ?, FDREF = ?, FDATT = ?, FDVEN = ?, FDEDIT = ?
                WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                """, ct, [m.CompteGeneralFacture, m.CompteClientFacture, c.Mode, c.NombreJours, c.Le, c.Arrete, c.Escompte, m.Echeance.Year,
                    m.Echeance.Month, m.Echeance.Day, m.Representant, m.Reference, m.AlAttention, m.CodeVente, m.AControler ? "A" : "O", .. cle]);
            if (m.ClientFactureChange)
                await ExecuterAsync(cnx, tx, """
                    UPDATE {b}.FAC080 SET FDFJ = ' ', FDNOM = ' ', FDRUE1 = ' ', FDRUE2 = ' ', FDPOST = ' ', FDVILL = ' ', FDPAYS = ' '
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
