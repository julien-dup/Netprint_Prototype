using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

internal sealed partial class SaisieBlDb2
{
    internal static string CleVerrouPreBl(int societe, int numero) => $"{societe:0000}{numero:000000}";

    // Types de stock des articles stockés du pré-BL (ATYPEA, ATYFAC), pour les contrôles de période et d'inventaire
    public async Task<List<(string TypeArticle, string TypeFacturation)>> TypesArticlesPreBlAsync(int societe, int numero, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, """
            SELECT DISTINCT COALESCE(s.ATYPEA, ''), COALESCE(s.ATYFAC, '') FROM {b}.EXP510 l
            JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
            WHERE l.RSOC = ? AND l.HNOBL = ? AND s.ASTK = 'O'
            """, ct, societe, numero);
        var r = new List<(string, string)>();
        while (await rd.ReadAsync(ct)) r.Add((rd.Texte(0), rd.Texte(1)));
        return r;
    }

    // =====================================================================
    // Transformation d'un pré-BL en BL (EXP540) en une transaction, sous verrous du pré-BL (PRTLCK « PREBL », comme le WinDev)
    // et de ses commandes : pré-BL et palettes relus verrouillés, stock contrôlé, BL du même n° (EXP009 / EXP010 / EXP015 /
    // EXP016 / EXP012), transfert TR vers le dépôt principal puis sortie EX, palettes expédiées (statut 5), commande (livré,
    // reste, soldé), pré-BL marqué « O ». Après : recalcul du stock (STOREVNAV), audit « CR ».
    // =====================================================================
    public async Task<List<string>> TransformerPreBlAsync(int societe, string profil, int numero, ParametresBl parametres, DateOnly dateControlee,
        CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var recalculs = new List<(string TypeStock, int Depot, string Article, string Indice)>();
        var poses = new List<string>();
        List<string> avertissements;
        await PoserVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, numero), profil, $"Le pré-BL {numero} est en cours de modification par", ct);
        try
        {
            await using (var rd = await Lire(cnx, "SELECT DISTINCT HNODOS, HCLEF FROM {b}.EXP510 WHERE RSOC = ? AND HNOBL = ? ORDER BY HNODOS, HCLEF", ct,
                             societe, numero))
                while (await rd.ReadAsync(ct)) poses.Add(CleVerrouDossier(societe, rd.Entier(0), rd.Texte(1)));
            for (var i = 0; i < poses.Count; i++)
            {
                try { await PoserVerrouAsync(cnx, "DOSSIER", poses[i], profil, "Ce dossier est en cours de modification par", ct); }
                catch { poses.RemoveRange(i, poses.Count - i); throw; }
            }

            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                var s = await SourcePreBlAsync(cnx, tx, societe, numero, parametres, ct)
                        ?? throw new RegleMetierException($"Le pré-BL {numero} n'existe pas.");
                if (ReglesTransformationPreBl.Refus(s) is { Length: > 0 } refus) throw new RegleMetierException(refus);
                if (s.Date != dateControlee)
                    throw new RegleMetierException("La date de départ du pré-BL vient d'être modifiée : rouvrez le pré-BL.");
                await using (var rd = await Lire(cnx, tx, "SELECT 1 FROM {b}.EXP009 WHERE RSOC = ? AND HNOBL = ?", ct, societe, numero))
                    if (await rd.ReadAsync(ct)) throw new RegleMetierException($"Le BL {numero} existe déjà : le pré-BL ne peut pas être transformé.");
                // Palettes d'un ancien BL du même n° (supprimé sans les détacher) : la suppression du nouveau BL les remettrait en stock
                await using (var rd = await Lire(cnx, tx, """
                    SELECT COUNT(*) FROM {b}.PROD_ENTITE e WHERE e.SOCIETE = ? AND e.NUMERO_BL = ?
                      AND e.NUMERO_SSCC NOT IN (SELECT p.DPSSCC FROM {b}.EXP517 p WHERE p.RSOC = ? AND p.DNBL = ?)
                    """, ct, societe, numero, societe, numero))
                {
                    await rd.ReadAsync(ct);
                    if (rd.Entier(0) > 0)
                        throw new RegleMetierException($"{rd.Entier(0)} palette(s) d'un ancien BL n° {numero}, supprimé, portent encore ce numéro : " +
                                                       "le pré-BL ne peut pas être transformé sans mélanger les deux BL.");
                }
                avertissements = ReglesTransformationPreBl.Avertissements(s);
                await ControlerStockPreBlAsync(cnx, tx, societe, s, ct);
                await EcrirePreBlAsync(cnx, tx, societe, profil, s, recalculs, ct);
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
            foreach (var d in poses) await LeverVerrouAsync(cnx, "DOSSIER", d, profil);
            await LeverVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, numero), profil);
        }

        foreach (var (typeStock, depot, article, indice) in recalculs.Distinct())
            await RecalculerStockAsync(cnx, societe, typeStock, depot, article, indice);
        await AuditAsync(cnx, societe, profil, numero, "CR");
        return avertissements;
    }

    // ---------- Lecture du pré-BL dans la transaction : en-tête et palettes verrouillés ----------
    private async Task<SourcePreBl?> SourcePreBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, ParametresBl parametres,
        CancellationToken ct)
    {
        SourcePreBl enTete;
        await using (var rd = await Lire(cnx, tx, """
            SELECT h.HENBL, h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HGENE, h.HCLIE, h.HNADRE, h.HOBS, h.HOBS2, h.HOBS3, h.HMENTI, h.HNPALT, h.HPDSTT,
                   h.HTRAN, h.HTYPL, h.HTYPP, h.HNBEPL, h.HBART, h.HSTOCKP,
                   COALESCE(f.ALFJ, ''), COALESCE(f.ALRS, ''), COALESCE(f.ALRUE1, ''), COALESCE(f.ALRUE2, ''), COALESCE(f.ALPOST, ''),
                   COALESCE(f.ALVILL, ''), COALESCE(f.ALDEP, ''), COALESCE(f.ALPAY, ''),
                   COALESCE(c.CFJ, ''), COALESCE(c.CNOM, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''),
                   COALESCE(c.CVILLE, ''), COALESCE(c.CDEPT, ''), COALESCE(c.CPAYS, ''),
                   COALESCE((SELECT MAX(x.FEOPT4) FROM {b}.EXP005 x WHERE x.RSOC = h.RSOC), '')
            FROM {b}.EXP509 h
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            LEFT OUTER JOIN {b}.A500F f ON f.RSOC = h.RSOC AND f.RGEN = h.HGENE AND f.RCLI = h.HCLIE AND f.ALNLIG = h.HNADRE AND h.HNADRE <> 0
            WHERE h.RSOC = ? AND h.HNOBL = ?
            WITH RS USE AND KEEP EXCLUSIVE LOCKS
            """, ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) return null;
            var date = BaseDb2.Date(rd.Entier(1), rd.Entier(2), rd.Entier(3)) ?? throw new RegleMetierException("Le pré-BL n'a pas de date de départ.");
            var adresse = rd.Texte(20) != ""
                ? new AdresseBl(rd.Entier(6), rd.Texte(19), rd.Texte(20), rd.Texte(21), rd.Texte(22), rd.Texte(23), rd.Texte(24), rd.Texte(25), rd.Texte(26))
                : new AdresseBl(0, rd.Texte(27), rd.Texte(28), rd.Texte(29), rd.Texte(30), rd.Texte(31), rd.Texte(32), rd.Texte(33), rd.Texte(34));
            enTete = new SourcePreBl(numero, rd.Texte(0), date, rd.Entier(4), rd.Entier(5), rd.Entier(6), adresse,
                [rd.Texte(7), rd.Texte(8), rd.Texte(9)], rd.Texte(10), rd.Entier(11), rd.Entier(12), rd.Entier(13), rd.Texte(14), rd.Texte(15),
                rd.Entier(16), rd.Entier(17), rd.Texte(18) == "O", rd.Texte(35) == "O", parametres.PoidsPalette, parametres.DepotPrincipal, [],
                new Dictionary<(int, string), CommandeSourcePreBl>(), []);
        }

        // Palettes : statut, dépôt, emplacement et lot relus verrouillés
        var palettes = new Dictionary<(int, int), List<PaletteSourcePreBl>>();
        var sscc = new List<(int Ligne, int Lot, string Sscc, int Quantite, bool Picking)>();
        await using (var rd = await Lire(cnx, tx, """
            SELECT DNUM, DNLIG, DPSSCC, DPQTEP, DPPICK FROM {b}.EXP517 WHERE RSOC = ? AND DNBL = ? ORDER BY DNUM, DNLIG, DPSSCC
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct)) sscc.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Texte(4) == "O"));
        foreach (var p in sscc)
        {
            await using var rd = await Lire(cnx, tx, """
                SELECT STATUT, DEPOT, EMPLACEMENT, NUMERO_OF, CLE_OF FROM {b}.PROD_ENTITE WHERE SOCIETE = ? AND NUMERO_SSCC = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, p.Sscc);
            var palette = await rd.ReadAsync(ct)
                ? new PaletteSourcePreBl(p.Sscc, p.Quantite, p.Picking, rd.Texte(0), rd.Entier(1) == 0 ? parametres.DepotPrincipal : rd.Entier(1),
                    rd.Texte(2), $"{rd.Entier(3):00000}{rd.Texte(4)}")
                : new PaletteSourcePreBl(p.Sscc, p.Quantite, p.Picking, "", parametres.DepotPrincipal, "", "");
            if (!palettes.TryGetValue((p.Ligne, p.Lot), out var l)) palettes[(p.Ligne, p.Lot)] = l = [];
            l.Add(palette);
        }

        var lots = new Dictionary<int, List<LotSourcePreBl>>();
        await using (var rd = await Lire(cnx, tx, "SELECT DNUM, DNLIG, DNTYPL, DLIBPAL FROM {b}.EXP516 WHERE RSOC = ? AND DNBL = ? ORDER BY DNUM, DNLIG", ct,
                         societe, numero))
            while (await rd.ReadAsync(ct))
            {
                if (!lots.TryGetValue(rd.Entier(0), out var l)) lots[rd.Entier(0)] = l = [];
                l.Add(new LotSourcePreBl(rd.Entier(1), rd.Texte(2) == "2", rd.Texte(3), palettes.GetValueOrDefault((rd.Entier(0), rd.Entier(1))) ?? []));
            }

        var lignes = new List<LigneSourcePreBl>();
        var commandes = new Dictionary<(int, string), CommandeSourcePreBl>();
        var colonnesTextes = string.Join(", ", Enumerable.Range(1, 19).Select(i => $"COALESCE(t.{(i < 10 ? $"DNTXT{i}" : $"DNTX{i}")}, '')"));
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT l.HNUM, l.HNODOS, l.HCLEF, l.HNUMLC, l.DART, l.DIND, l.DQTE, l.DOBS1, l.DOBS2, l.DOBS3, l.DAPPSOLD, l.DETALI, l.DENUC, l.DEEXMEC,
                   l.DEMBP1, l.DEEMBPE, l.DTYEMB, COALESCE(s.ASTK, ''), COALESCE(s.ATYPEA, ''), COALESCE(s.ATYFAC, ''), COALESCE(s.AFAM, ''),
                   COALESCE(s.ASFAM, ''), COALESCE(s.APDSU, 0), n.DNDESA, n.DNCLIG, n.DNGENF, n.DNCLIF,
                   a.CCENSE, a.RGEN, a.RCLI, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCREP, a.CCRCVD, COALESCE(e.FAAUTO, ''), COALESCE(c.CDOPT2, ''),
                   a.CGENF, a.CCLIF, {colonnesTextes}
            FROM {"{b}"}.EXP510 l
            JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.HNODOS AND n.DNDCLE = l.HCLEF AND n.DNNLIG = l.HNUMLC
            JOIN {"{b}"}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            LEFT OUTER JOIN {"{b}"}.AB001FA e ON e.RSOC = a.RSOC AND e.RDOSS = a.RDOSS AND e.CCDCLE = a.CCDCLE
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
            LEFT OUTER JOIN {"{b}"}.NEG100T t ON t.RSOC = n.RSOC AND t.RDOSS = n.RDOSS AND t.DNDCLE = n.DNDCLE AND t.DNNLIG = n.DNNLIG
            WHERE l.RSOC = ? AND l.HNOBL = ? ORDER BY l.HNODOS, l.HCLEF, l.HNUMLC, l.HNUM
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var (dossier, cle) = (rd.Entier(1), rd.Texte(2));
                lignes.Add(new LigneSourcePreBl(rd.Entier(0), dossier, cle, rd.Entier(3), rd.Texte(4), rd.Texte(5), rd.Entier(6),
                    [.. new[] { rd.Texte(7), rd.Texte(8), rd.Texte(9) }.Where(o => o != "")], rd.Texte(10) == "O", rd.Texte(11), rd.Entier(12),
                    rd.Entier(13), rd.Entier(14), rd.Entier(15), rd.Texte(16), rd.Texte(17) == "O", parametres.TypeStockArticle(rd.Texte(18), rd.Texte(19)),
                    rd.Texte(20), rd.Texte(21), rd.Nombre(22), rd.Texte(23), rd.Texte(24), [.. Enumerable.Range(40, 19).Select(rd.Texte).Where(t => t != "")],
                    rd.Entier(25), rd.Entier(26), lots.GetValueOrDefault(rd.Entier(0)) ?? []));
                var cdopt2 = int.TryParse(rd.Texte(37), out var o) ? o : 0;
                commandes.TryAdd((dossier, cle), new CommandeSourcePreBl(dossier, cle, rd.Texte(27), rd.Entier(28), rd.Entier(29), rd.Texte(30),
                    BaseDb2.Date(rd.Entier(31), rd.Entier(32), rd.Entier(33)), rd.Entier(34), rd.Texte(35),
                    rd.Texte(36) != "" ? rd.Texte(36) == "O" : cdopt2 is >= 1 and <= 8, rd.Entier(38), rd.Entier(39)));
            }
        // Lignes dont la commande n'existe plus : gardées sans commande (refusées par les règles)
        await using (var rd = await Lire(cnx, tx, """
            SELECT l.HNUM, l.HNODOS, l.HCLEF, l.DART FROM {b}.EXP510 l WHERE l.RSOC = ? AND l.HNOBL = ?
              AND NOT EXISTS (SELECT 1 FROM {b}.NEG100 n JOIN {b}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
                              WHERE n.RSOC = l.RSOC AND n.RDOSS = l.HNODOS AND n.DNDCLE = l.HCLEF AND n.DNNLIG = l.HNUMLC)
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
                lignes.Add(new LigneSourcePreBl(rd.Entier(0), rd.Entier(1), rd.Texte(2), 0, rd.Texte(3), "", 0, [], false, "", 0, 0, 0, 0, "", false, "",
                    "", "", 0, "", "", [], 0, 0, []));

        var pied = new List<string>();
        await using (var rd = await Lire(cnx, tx, "SELECT HTTXT FROM {b}.EXP512 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numero))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);
        return enTete with { Lignes = lignes, Commandes = commandes, Pied = pied };
    }

    // Stock : chaque lot sort du dépôt (et de l'emplacement) de ses palettes
    private Task ControlerStockPreBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, SourcePreBl s, CancellationToken ct) =>
        ControlerSortiesPalettesAsync(cnx, tx, societe, s.DepotPrincipal, s.Date,
            [.. s.Lignes.Where(l => l.Stocke).SelectMany(l => ReglesTransformationPreBl.Sorties(l)
                .Select(x => (l.TypeStock, x.Depot, l.Article, l.Indice, x.Lot, x.Emplacement, x.Quantite)))], ct);

    // ---------- Écritures du BL ----------
    private async Task EcrirePreBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, SourcePreBl s,
        List<(string, int, string, string)> recalculs, CancellationToken ct)
    {
        var elements = ReglesTransformationPreBl.Elements(s);
        var premiere = s.Commandes[elements[0].Key];
        var a = s.Adresse;
        var colis = s.Lignes.Sum(l => ReglesTransformationPreBl.ColisPoids(l).Colis);
        var maintenant = DateTime.Now;
        await Executer(cnx, tx, """
            INSERT INTO {b}.EXP009 (RSOC, HNOBL, HDEXAA, HDEXMM, HDEXJJ, HFJ, HRS, HRUE1, HRUE2, HCP, HVILLE, HOBS, HGENE, HCLIE,
                HNCOLT, HNPALT, HPDSTT, HTRAN, HTYPL, HTYPP, HNBEPL, HEDBL, HEDETP, HENS, HBART, HNOREC, HBLGRP, HPROF, HDPT, HREV,
                HNADRE, HPAYS, HOBSIN, HOBS2, HOBS3, HSTR, HMENTI)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'N', 'N', ?, ?, 0, 'N', ?, ?, ?, ?, ?, ' ', ?, ?, 'N', ?)
            """, ct, societe, s.Numero, s.Date.Year, s.Date.Month, s.Date.Day, a.FormeJuridique, a.RaisonSociale, a.Rue1, a.Rue2, a.CodePostal,
            a.Ville, s.Observations[0], s.CompteGeneral, s.CompteClient, Math.Min(colis, 99999), Math.Min(s.Palettes, 99),
            Math.Min(ReglesTransformationPreBl.PoidsTotal(s), 99999), s.Transporteur, s.TypeLivraison, s.TypePort, s.EtiquettesParPalette,
            premiere.Enseigne, s.Bareme, profil, a.Departement, premiere.Revendeur == "O" ? "O" : "N", s.NumeroAdresse, a.Pays, s.Observations[1],
            s.Observations[2], s.Mention);

        var element = 0;
        foreach (var el in elements)
        {
            element++;
            var c = s.Commandes[el.Key];
            var (genF, cliF) = ReglesTransformationPreBl.ClientFacture(el, c);
            await Executer(cnx, tx, """
                INSERT INTO {b}.EXP010 (RSOC, HNOBL, HNUM, HNODOS, HCLEF, HETABL, HQTELI, HNUC1, HQTUC1, HUCDT1, HNUC2, HQTUC2, HUCDT2, HNBPAL,
                    HPDST, HETALI, HTYPBL, HEDETC, HBLFAC, HGENE, HCLIE, HREFCD, HDCDEA, HDCDEM, HDCDEJ, HNODEV, HNOHYP, HNOMOD, HREFIM,
                    HREP, HNOFAC, HFACTU, HNOBLO, HNUMO, HGENF, HCLIF, HFACTO)
                VALUES (?, ?, ?, ?, ?, 'C', 0, 0, 0, 'C', 0, 0, ' ', 0, 0, ' ', 2, 'N', 'N', ?, ?, ?, ?, ?, ?, 0, 0, 0, ' ', ?, 0, ?, 0, 0, ?, ?, ?)
                """, ct, societe, s.Numero, element, c.Dossier, c.Cle, s.CompteGeneral, s.CompteClient, c.Reference, c.DateCommande?.Year ?? 0,
                c.DateCommande?.Month ?? 0, c.DateCommande?.Day ?? 0, c.Representant, s.StockPaye ? "N" : "O", genF, cliF,
                !s.StockPaye && c.FacturationAutomatique ? "O" : "N");
            var no = 0;
            var livreElement = 0;
            foreach (var l in el.OrderBy(x => x.LigneCommande).ThenBy(x => x.Numero))
            {
                var quantite = ReglesTransformationPreBl.Quantite(l);
                var textes = ReglesTransformationPreBl.Textes(l, s.SansNumerosPalettes);
                no++;
                var ligneArticle = no;
                await Executer(cnx, tx, """
                    INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                        DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0)
                    """, ct, societe, s.Numero, element, no, l.Article, Tronquer(l.Designation, 40), quantite, l.Indice, l.Uc, l.LigneCommande,
                    l.EtatLivraison, l.CodeLigne, Tronquer(textes.ElementAtOrDefault(0) ?? "", 40), Tronquer(textes.ElementAtOrDefault(1) ?? "", 40),
                    l.EmballageParExceptionnel > 0 ? l.EmballageParExceptionnel : l.EmballagePar, l.TypeEmballage, l.ExMecompte > 0 ? 1 : 0, l.ExMecompte);
                foreach (var groupe in textes.Skip(2).Chunk(3))
                {
                    no++;
                    await Executer(cnx, tx, """
                        INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                            DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                        VALUES (?, ?, ?, ?, ' ', ?, 0, ' ', 0, ?, ' ', ' ', ?, ?, 0, ' ', 0, 0, 0)
                        """, ct, societe, s.Numero, element, no, Tronquer(groupe.ElementAtOrDefault(0) ?? "", 40), l.LigneCommande,
                        Tronquer(groupe.ElementAtOrDefault(1) ?? "", 40), Tronquer(groupe.ElementAtOrDefault(2) ?? "", 40));
                }
                if (l.Stocke)
                    foreach (var x in ReglesTransformationPreBl.Sorties(l))
                    {
                        await Executer(cnx, tx, """
                            MERGE INTO {b}.EXP016 t
                            USING (VALUES (CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(6, 0)), CAST(? AS NUMERIC(3, 0)), CAST(? AS NUMERIC(3, 0)),
                                           CAST(? AS CHAR(6)), CAST(? AS CHAR(8)), CAST(? AS NUMERIC(8, 0))))
                                  AS v (RSOC, DNBL, DNUM, DNLIG, DNLOT, DNEMP, DNQTPL)
                            ON t.RSOC = v.RSOC AND t.DNBL = v.DNBL AND t.DNUM = v.DNUM AND t.DNLIG = v.DNLIG AND t.DNLOT = v.DNLOT AND t.DNEMP = v.DNEMP
                            WHEN MATCHED THEN UPDATE SET t.DNQTPL = t.DNQTPL + v.DNQTPL
                            WHEN NOT MATCHED THEN INSERT (RSOC, DNBL, DNUM, DNLIG, DNLOT, DNQTPL, DUC1, DUC2, DNEMP)
                                 VALUES (v.RSOC, v.DNBL, v.DNUM, v.DNLIG, v.DNLOT, v.DNQTPL, 0, 0, v.DNEMP)
                            """, ct, societe, s.Numero, element, ligneArticle, x.Lot, x.Emplacement, x.Quantite);
                        var mouvements = new List<(string Code, string Sens, int Depot, int Contrepartie)>();
                        if (x.Depot != s.DepotPrincipal)
                        {
                            mouvements.Add(("TR", "S", x.Depot, s.DepotPrincipal));
                            mouvements.Add(("TR", "E", s.DepotPrincipal, x.Depot));
                        }
                        mouvements.Add(("EX", "S", s.DepotPrincipal, 0));
                        foreach (var (code, sens, depot, contrepartie) in mouvements)
                            await Executer(cnx, tx, """
                                INSERT INTO {b}.A700F (RSOC, MVBON, MVAA, MVMM, MVJJ, MVTSTO, MVDEP, MVCMVT, MVSENS, MVLOT, MVDOS, MVDCLE, MVDEPX, MVREF,
                                    MVFAM, MVSFAM, MVQTUS, MVUS, MVPRO, MVIND, MVBL, MVELBL, MVLIGB, MVEMP, MVDATS, MVCHRO)
                                VALUES (?, ' ', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'UN', ?, ?, ?, ?, ?, ?, ?, ?)
                                """, ct, societe, s.Date.Year, s.Date.Month, s.Date.Day, l.TypeStock, depot, code, sens, x.Lot, c.Dossier, c.Cle,
                                contrepartie, l.Article, l.Famille, l.SousFamille, x.Quantite, profil, l.Indice, s.Numero, element, ligneArticle,
                                x.Emplacement, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant.Hour * 100 + maintenant.Minute);
                        foreach (var sscc in x.Palettes)
                            await Executer(cnx, tx, """
                                UPDATE {b}.PROD_ENTITE SET STATUT = '5', NUMERO_BL = ?, ELEMENT_BL = ?, NUMERO_LIGNE_BL = ?, PALETTE_GROUP = ' ',
                                       DATE_SOR_STOCK = CURRENT TIMESTAMP
                                WHERE SOCIETE = ? AND NUMERO_SSCC = ?
                                """, ct, s.Numero, element, ligneArticle, societe, sscc);
                        recalculs.Add((l.TypeStock, s.DepotPrincipal, l.Article, l.Indice));
                        if (x.Depot != s.DepotPrincipal) recalculs.Add((l.TypeStock, x.Depot, l.Article, l.Indice));
                    }
                livreElement += quantite;
                // Commande : livré, reste ; soldée si appel à solder ou plus rien à livrer (un solde antérieur est gardé)
                await Executer(cnx, tx, """
                    UPDATE {b}.NEG100 SET DNQTEL = DNQTEL + ?, DNQTER = MAX(0, DNQTEC - (DNQTEL + ?)),
                           DNSOLD = CASE WHEN ? = 'O' OR DNQTEC - (DNQTEL + ?) <= 0 THEN 'O' ELSE DNSOLD END
                    WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                    """, ct, quantite, quantite, l.AppelASolder ? "O" : "N", quantite, societe, c.Dossier, c.Cle, l.LigneCommande);
            }
            await Executer(cnx, tx, "UPDATE {b}.AB001 SET CCQTEL = CCQTEL + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC = '4'", ct,
                livreElement, societe, c.Dossier, c.Cle);
            await SolderCommandeSiToutLivreAsync(cnx, tx, societe, profil, c.Dossier, c.Cle, c.CompteGeneral, c.CompteClient, ct);
        }
        await EcrireTextePiedAsync(cnx, tx, societe, s.Numero, s.Pied, ct);
        await Executer(cnx, tx, "UPDATE {b}.EXP509 SET HENBL = 'O' WHERE RSOC = ? AND HNOBL = ?", ct, societe, s.Numero);
    }
}
