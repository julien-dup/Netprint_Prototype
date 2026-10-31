using System.Data;
using System.Data.Odbc;
using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Donnees;

// Élément de BL tel qu'il est enregistré (contrôles avant suppression ou modification)
internal sealed record ElementEnregistre(
    int NumeroBl, int Element, string Etat, int TypeBl, int Dossier, string Cle, bool Facture, bool SousTraitanceSynchronisee,
    int NombreElements, bool AvecRetour, IReadOnlyList<string> TypesStock);

internal sealed partial class SaisieBlDb2
{
    public async Task<ElementEnregistre?> ElementAsync(int societe, int numeroBl, int element, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, """
            SELECT e.HETABL, e.HTYPBL, e.HNODOS, e.HCLEF, e.HBLFAC, e.HNOFAC, h.HSTR,
                   (SELECT COUNT(*) FROM {b}.EXP010 x WHERE x.RSOC = e.RSOC AND x.HNOBL = e.HNOBL),
                   (SELECT COUNT(*) FROM {b}.EXP010 r WHERE r.RSOC = e.RSOC AND r.HNOBLO = e.HNOBL)
            FROM {b}.EXP010 e JOIN {b}.EXP009 h ON h.RSOC = e.RSOC AND h.HNOBL = e.HNOBL
            WHERE e.RSOC = ? AND e.HNOBL = ? AND e.HNUM = ?
            """, ct, societe, numeroBl, element);
        if (!await rd.ReadAsync(ct)) return null;
        var types = new List<string>();
        await using (var ts = await Lire(cnx, "SELECT DISTINCT MVTSTO FROM {b}.A700F WHERE RSOC = ? AND MVBL = ? AND MVELBL = ?", ct,
                         societe, numeroBl, element))
            while (await ts.ReadAsync(ct)) types.Add(ts.Texte(0));
        return new ElementEnregistre(numeroBl, element, rd.Texte(0), rd.Entier(1), rd.Entier(2), rd.Texte(3),
            rd.Texte(4) == "O" || rd.Entier(5) != 0, rd.Texte(6) == "O", rd.Entier(7), rd.Entier(8) > 0, types);
    }

    // Suppression d'un élément de BL (cahier des charges § 10) : verrous BL et dossier (PRTLCK), puis une transaction qui
    // annule tout ce que l'élément avait mis à jour (commande, OPR, dossier, stock, palettes) et supprime ses lignes ; le BL
    // entier disparaît avec son dernier élément (pré-BL remis « non basculé »), sinon ses totaux sont recalculés et son texte
    // de pied conservé. Après la validation : recalcul du stock (STOREVNAV) et audit « SU ». Rend true si le BL a disparu.
    public async Task<bool> SupprimerElementAsync(int societe, string profil, ElementEnregistre el, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var verrouBl = CleVerrouBl(societe, el.NumeroBl);
        var verrouDossier = el.Dossier == 0 ? null : CleVerrouDossier(societe, el.Dossier, el.Cle);
        await PoserVerrouAsync(cnx, "BL", verrouBl, profil, "Ce BL est en cours de modification par", ct);
        var recalculs = new List<(string TypeStock, int Depot, string Article, string Indice)>();
        bool blSupprime;
        try
        {
            if (verrouDossier is not null) await PoserVerrouAsync(cnx, "DOSSIER", verrouDossier, profil, "Ce dossier est en cours de modification par", ct);
            try
            {
                await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var valide = false;
                try
                {
                    await using (var rd = await Lire(cnx, tx, """
                        SELECT DISTINCT MVTSTO, MVDEP, MVREF, MVIND FROM {b}.A700F WHERE RSOC = ? AND MVBL = ? AND MVELBL = ?
                        """, ct, societe, el.NumeroBl, el.Element))
                        while (await rd.ReadAsync(ct)) recalculs.Add((rd.Texte(0), rd.Entier(1), rd.Texte(2), rd.Texte(3)));
                    await AnnulerElementAsync(cnx, tx, societe, el, ct);
                    blSupprime = await SupprimerLignesElementAsync(cnx, tx, societe, el, ct);
                    await tx.CommitAsync(ct);
                    valide = true;
                }
                finally
                {
                    if (!valide) await tx.RollbackAsync(CancellationToken.None);
                }
            }
            finally
            {
                if (verrouDossier is not null) await LeverVerrouAsync(cnx, "DOSSIER", verrouDossier, profil);
            }
        }
        finally
        {
            await LeverVerrouAsync(cnx, "BL", verrouBl, profil);
        }
        foreach (var (typeStock, depot, article, indice) in recalculs)
            await RecalculerStockAsync(cnx, societe, typeStock, depot, article, indice);
        await AuditAsync(cnx, societe, profil, el.NumeroBl, "SU");
        return blSupprime;
    }

    internal static string CleVerrouBl(int societe, int numeroBl) => $"{societe:0000}{numeroBl:000000}";

    // Annule ce que l'élément avait mis à jour : NEG100 (livré, reste, soldé), OPR015 / OPR010, AB001 (quantité livrée, solde),
    // mouvements A700F (sorties EX et transferts TR de l'élément), palettes PROD_ENTITE détachées du BL
    private async Task AnnulerElementAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, ElementEnregistre el, CancellationToken ct)
    {
        var lignes = new List<(int LigneCommande, int Quantite)>();
        await using (var rd = await Lire(cnx, tx, """
            SELECT DNUMLC, SUM(DQTE) FROM {b}.EXP015 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DART <> ' ' AND DNUMLC <> 0 GROUP BY DNUMLC
            """, ct, societe, el.NumeroBl, el.Element))
            while (await rd.ReadAsync(ct)) lignes.Add((rd.Entier(0), rd.Entier(1)));

        if (el.Dossier != 0 && el.Etat == "C")
        {
            foreach (var (ligne, quantite) in lignes)
            {
                await Executer(cnx, tx, """
                    UPDATE {b}.NEG100 SET DNQTEL = MAX(0, DNQTEL - ?), DNQTER = DNQTEC - MAX(0, DNQTEL - ?), DNSOLD = ' '
                    WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                    """, ct, quantite, quantite, societe, el.Dossier, el.Cle, ligne);
                await Executer(cnx, tx, """
                    UPDATE {b}.OPR015 SET ODLIVR = 'N', ODQTEX = MAX(0, ODQTEX - ?), ODBL = 0
                    WHERE RSOC = ? AND ODNCDE = ? AND ODNCLE = ? AND ODDLIG = ? AND ODBL = ?
                    """, ct, quantite, societe, el.Dossier, el.Cle, ligne, el.NumeroBl);
            }
            await Executer(cnx, tx, """
                UPDATE {b}.OPR010 o SET OESTAT = '3'
                WHERE o.RSOC = ? AND o.OESTAT = '4'
                  AND EXISTS (SELECT 1 FROM {b}.OPR015 x WHERE x.RSOC = o.RSOC AND x.ODNUM = o.OENUM AND x.ODNCDE = ? AND x.ODNCLE = ?
                              AND x.ODLIVR = 'N' AND x.ODSTAT = '3')
                """, ct, societe, el.Dossier, el.Cle);
            var total = lignes.Sum(x => x.Quantite);
            await Executer(cnx, tx, """
                UPDATE {b}.AB001 SET CCQTEL = CASE WHEN CCTYPC = '4' THEN MAX(0, CCQTEL - ?) ELSE CCQTEL END,
                       CCTAGS = CASE WHEN CCTAGS = 'D' THEN ' ' ELSE CCTAGS END,
                       CCSLDA = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDA END,
                       CCSLDM = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDM END,
                       CCSLDJ = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDJ END
                WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                """, ct, total, societe, el.Dossier, el.Cle);
        }

        await Executer(cnx, tx, "DELETE FROM {b}.A700F WHERE RSOC = ? AND MVBL = ? AND MVELBL = ?", ct, societe, el.NumeroBl, el.Element);
        await Executer(cnx, tx, """
            UPDATE {b}.PROD_ENTITE SET NUMERO_BL = 0, ELEMENT_BL = 0, NUMERO_LIGNE_BL = 0, STATUT = '4'
            WHERE SOCIETE = ? AND NUMERO_BL = ? AND ELEMENT_BL = ?
            """, ct, societe, el.NumeroBl, el.Element);
    }

    // Supprime les lignes de l'élément puis l'élément ; le BL entier s'il n'en reste aucun, sinon ses totaux recalculés
    private async Task<bool> SupprimerLignesElementAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, ElementEnregistre el, CancellationToken ct)
    {
        foreach (var table in new[] { "EXP016", "EXP018D", "EXP018E", "EXP015" })
            await Executer(cnx, tx, $"DELETE FROM {{b}}.{table} WHERE RSOC = ? AND DNBL = ? AND DNUM = ?", ct, societe, el.NumeroBl, el.Element);
        await Executer(cnx, tx, "DELETE FROM {b}.EXP011 WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?", ct, societe, el.NumeroBl, el.Element);
        if (el.Dossier != 0)
            await Executer(cnx, tx, "DELETE FROM {b}.A20005 WHERE RSOC = ? AND RDOSS = ? AND S5DCLE = ? AND S5NOBL = ?", ct, societe,
                el.Dossier, el.Cle, el.NumeroBl);
        await Executer(cnx, tx, "DELETE FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?", ct, societe, el.NumeroBl, el.Element);

        int restants;
        await using (var rd = await Lire(cnx, tx, "SELECT COUNT(*) FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ?", ct, societe, el.NumeroBl))
        {
            await rd.ReadAsync(ct);
            restants = rd.Entier(0);
        }
        if (restants == 0)
        {
            foreach (var table in new[] { "EXP012", "EXP013", "EXP009" })
                await Executer(cnx, tx, $"DELETE FROM {{b}}.{table} WHERE RSOC = ? AND HNOBL = ?", ct, societe, el.NumeroBl);
            await Executer(cnx, tx, "UPDATE {b}.EXP509 SET HENBL = 'E' WHERE RSOC = ? AND HNOBL = ? AND HENBL = 'O'", ct, societe, el.NumeroBl);
            return true;
        }
        await RecalculerTotauxAsync(cnx, tx, societe, el.NumeroBl, ct);
        return false;
    }

    // Totaux du BL (EXP009) = somme de ses éléments : poids et palettes des éléments, colis et colis incomplets des lignes
    private async Task RecalculerTotauxAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numeroBl, CancellationToken ct) =>
        await Executer(cnx, tx, """
            UPDATE {b}.EXP009 h SET
                HPDSTT = MIN(99999, (SELECT COALESCE(SUM(e.HPDST), 0) FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL)),
                HNPALT = MIN(99, (SELECT COALESCE(SUM(e.HNBPAL), 0) FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL)),
                HNCOLT = MIN(99999, (SELECT COALESCE(SUM(l.DCOLIS + l.DCOLIM), 0) FROM {b}.EXP015 l WHERE l.RSOC = h.RSOC AND l.DNBL = h.HNOBL))
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numeroBl);
}
