using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

internal sealed partial class SaisieBlDb2
{
    // Ligne de lot enregistrée (EXP516) : ce que la saisie garde quand le lot ne change pas
    private sealed record LotEnregistrePreBl(int Ligne, int Numero, LotSaisiePreBl Lot, int PaletteMixte, string LibellePalettes, string Cloture,
        string Observation1, string Observation2, bool Verrouille);

    // =====================================================================
    // Enregistrement d'un pré-BL (FEN_SaisiePREBL.BTN_Valider) en une transaction, sous verrou PRTLCK « PREBL » en modification.
    // Création : n° pris sur le compteur des BL (celui du BL que deviendra le pré-BL). Modification : n° de ligne et de lot
    // gardés (les palettes scannées de la variante sans palette y sont rattachées), lots inchangés gardés tels quels (récap
    // palettes, préparation clôturée), état du pointage (HENBL) gardé ; lots en préparation non modifiables. Lignes de commande
    // et palettes relues verrouillées : même client et adresse, pas dans un autre pré-BL non livré, palette libre et disponible.
    // =====================================================================
    public async Task<int> EnregistrerPreBlAsync(int societe, string profil, SaisiePreBl s, bool numerotationParEnseigne, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var numero = s.Numero;
        if (numero > 0)
            await PoserVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, numero), profil, $"Le pré-BL {numero} est en cours de modification par", ct);
        try
        {
            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                var enseigne = await VerifierLignesPreBlAsync(cnx, tx, societe, s, ct);
                var a = s.Adresse;
                var lotsChanges = false;
                if (numero == 0)
                {
                    numero = await NumeroBlAsync(cnx, tx, societe, enseigne, numerotationParEnseigne, ct);
                    await Executer(cnx, tx, """
                        INSERT INTO {b}.EXP509 (RSOC, HNOBL, HDEXAA, HDEXMM, HDEXJJ, HOBS, HGENE, HCLIE, HNCOLT, HNPALT, HPDSTT, HTRAN, HTYPL, HTYPP,
                            HNBEPL, HENS, HBART, HPROF, HNADRE, HOBS2, HOBS3, HMENTI, HENBL, HDCREA, HSTOCKP)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, 0, ?, ?, ?, ?, 0, ' ', 0, ?, ?, ?, ?, ?, ' ', ?, ?)
                        """, ct, societe, numero, s.DateDepart.Year, s.DateDepart.Month, s.DateDepart.Day, Tronquer(s.Observations[0] ?? "", 40),
                        s.CompteGeneral, s.CompteClient, s.Poids, s.Transporteur, s.TypeLivraison, s.TypePort, profil, a.Numero,
                        Tronquer(s.Observations[1] ?? "", 40), Tronquer(s.Observations[2] ?? "", 40), Tronquer(s.Mention ?? "", 40),
                        BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(DateTime.Today)), s.StockPaye ? "O" : "N");
                }
                else
                {
                    await using (var rd = await Lire(cnx, tx, "SELECT HENBL, HGENE, HCLIE FROM {b}.EXP509 WHERE RSOC = ? AND HNOBL = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                                     ct, societe, numero))
                    {
                        if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"Le pré-BL {numero} n'existe plus.");
                        if (rd.Texte(0) == "O") throw new RegleMetierException($"Le pré-BL {numero} est transformé en BL : il ne peut plus être modifié.");
                        if (rd.Entier(1) != s.CompteGeneral || rd.Entier(2) != s.CompteClient) throw new RegleMetierException("Le client d'un pré-BL ne change pas.");
                    }
                    lotsChanges = await RemplacerLignesPreBlAsync(cnx, tx, societe, numero, s, ct);
                    // Numérotation des palettes à la suite sur tous les lots : un lot changé la rend fausse partout (récap à refaire)
                    if (lotsChanges)
                        await Executer(cnx, tx, "UPDATE {b}.EXP516 SET DNPALM = 0, DLIBPAL = ' ' WHERE RSOC = ? AND DNBL = ?", ct, societe, numero);
                    await Executer(cnx, tx, """
                        UPDATE {b}.EXP509 SET HDEXAA = ?, HDEXMM = ?, HDEXJJ = ?, HOBS = ?, HOBS2 = ?, HOBS3 = ?, HPDSTT = ?, HTRAN = ?, HTYPL = ?,
                               HTYPP = ?, HNADRE = ?, HMENTI = ?, HSTOCKP = ?, HNPALT = CASE WHEN ? = 'O' THEN 0 ELSE HNPALT END
                        WHERE RSOC = ? AND HNOBL = ?
                        """, ct, s.DateDepart.Year, s.DateDepart.Month, s.DateDepart.Day, Tronquer(s.Observations[0] ?? "", 40),
                        Tronquer(s.Observations[1] ?? "", 40), Tronquer(s.Observations[2] ?? "", 40), s.Poids, s.Transporteur, s.TypeLivraison,
                        s.TypePort, a.Numero, Tronquer(s.Mention ?? "", 40), s.StockPaye ? "O" : "N", lotsChanges ? "O" : "N", societe, numero);
                }
                if (s.Numero == 0) await RemplacerLignesPreBlAsync(cnx, tx, societe, numero, s, ct);
                await EcrireTextePiedAsync(cnx, tx, societe, numero, (s.Pied ?? "").Replace("\r", "").Split('\n'), ct, "EXP512");
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
            if (s.Numero > 0) await LeverVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, s.Numero), profil);
        }
        return numero;
    }

    // Lignes de commande relues verrouillées (R6, R7) ; rend l'enseigne de la première commande (numérotation du BL)
    private async Task<string> VerifierLignesPreBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, SaisiePreBl s, CancellationToken ct)
    {
        var enseigne = "";
        foreach (var l in s.Lignes.OrderBy(x => x.Dossier).ThenBy(x => x.Cle, StringComparer.Ordinal).ThenBy(x => x.LigneCommande))
        {
            await using var rd = await Lire(cnx, tx, """
                SELECT a.CCTYPC, a.RGEN, a.RCLI, n.DNLIGA, n.DNSTAT, n.DNSOLD, n.DNART, n.DNIND, a.CCENSE, COALESCE(s.ASTK, ''),
                       (SELECT COUNT(*) FROM {b}.EXP510 z JOIN {b}.EXP509 y ON y.RSOC = z.RSOC AND y.HNOBL = z.HNOBL
                        WHERE z.RSOC = n.RSOC AND z.HNODOS = n.RDOSS AND z.HCLEF = n.DNDCLE AND z.HNUMLC = n.DNNLIG AND y.HENBL <> 'O' AND y.HNOBL <> ?)
                FROM {b}.NEG100 n
                JOIN {b}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
                LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
                WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNNLIG = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, s.Numero, societe, l.Dossier, l.Cle, l.LigneCommande);
            var nom = $"Ligne {l.LigneCommande} de la commande {l.Dossier}/{l.Cle}";
            if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"{nom} : elle n'existe plus.");
            if (rd.Texte(0) is not ("3" or "4") || rd.Entier(1) != s.CompteGeneral || rd.Entier(2) != s.CompteClient)
                throw new RegleMetierException($"{nom} : la commande n'est pas une commande du client du pré-BL.");
            if (rd.Entier(3) != s.Adresse.Numero) throw new RegleMetierException($"{nom} : elle n'est pas livrée à l'adresse du pré-BL.");
            if (rd.Texte(4) != "1") throw new RegleMetierException($"{nom} : elle n'est plus une ligne normale (annulée ?).");
            if (rd.Texte(5) == "O") throw new RegleMetierException($"{nom} : elle est soldée.");
            if (rd.Texte(6) != l.Article || rd.Texte(7) != l.Indice) throw new RegleMetierException($"{nom} : l'article a changé.");
            if ((rd.Texte(9) == "O") != l.Stocke) throw new RegleMetierException($"{nom} : la gestion en stock de l'article a changé, rouvrez le pré-BL.");
            if (l.Stocke && rd.Entier(10) > 0) throw new RegleMetierException($"{nom} : elle est déjà dans un autre pré-BL non livré.");
            if (enseigne == "") enseigne = rd.Texte(8);
        }
        return enseigne;
    }

    // Lignes, lots et palettes : n° gardés, lots inchangés gardés ; rend vrai si un lot a été ajouté, changé ou retiré
    private async Task<bool> RemplacerLignesPreBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, SaisiePreBl s,
        CancellationToken ct)
    {
        // Existant
        var lignes = new Dictionary<(int, string, int), int>();
        await using (var rd = await Lire(cnx, tx, "SELECT HNUM, HNODOS, HCLEF, HNUMLC FROM {b}.EXP510 WHERE RSOC = ? AND HNOBL = ?", ct, societe, numero))
            while (await rd.ReadAsync(ct)) lignes[(rd.Entier(1), rd.Texte(2), rd.Entier(3))] = rd.Entier(0);
        var palettes = new Dictionary<(int, int), List<PaletteSaisiePreBl>>();
        await using (var rd = await Lire(cnx, tx, """
            SELECT p.DNUM, p.DNLIG, p.DPSSCC, p.DPQTEP, p.DPPICK, COALESCE(e.STATUT, '') FROM {b}.EXP517 p
            LEFT OUTER JOIN {b}.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC
            WHERE p.RSOC = ? AND p.DNBL = ?
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                if (!palettes.TryGetValue((rd.Entier(0), rd.Entier(1)), out var l)) palettes[(rd.Entier(0), rd.Entier(1))] = l = [];
                l.Add(new PaletteSaisiePreBl { Sscc = rd.Texte(2), Quantite = rd.Entier(3), Picking = rd.Texte(4) == "O", Statut = rd.Texte(5) });
            }
        var lots = new List<LotEnregistrePreBl>();
        await using (var rd = await Lire(cnx, tx, """
            SELECT DNUM, DNLIG, DNLOT, DNDEP, DNQTPL, DNPALM, DLIBPAL, DCLOTU, DDOBS1, DDOBS2, DNTYPL FROM {b}.EXP516 WHERE RSOC = ? AND DNBL = ?
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var pl = palettes.GetValueOrDefault((rd.Entier(0), rd.Entier(1))) ?? [];
                lots.Add(new LotEnregistrePreBl(rd.Entier(0), rd.Entier(1),
                    new LotSaisiePreBl { Numero = rd.Entier(1), Lot = rd.Texte(2), Depot = rd.Entier(3), Quantite = rd.Entier(4), Palettes = pl },
                    rd.Entier(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9),
                    rd.Texte(10) == "2" || pl.Any(p => p.Statut is "3" or "4") || (s.SansPalette && pl.Count > 0)));
            }

        // N° des lignes : gardés, nouveaux après le plus grand
        var prochaine = lignes.Count == 0 ? 1 : lignes.Values.Max() + 1;
        var numeros = s.Lignes.ToDictionary(l => l, l => lignes.TryGetValue((l.Dossier, l.Cle, l.LigneCommande), out var n) ? n : prochaine++);
        var change = false;

        // Lignes et lots retirés : refusés s'ils sont en préparation
        foreach (var lot in lots.Where(x => !s.Lignes.Any(l => numeros[l] == x.Ligne && l.Lots.Any(y => y.Numero == x.Numero))))
        {
            if (lot.Verrouille)
                throw new RegleMetierException($"Le lot {lot.Lot.Lot} de la ligne {lot.Ligne} est en préparation : il ne peut pas être retiré.");
            change = true;
        }
        await Executer(cnx, tx, "DELETE FROM {b}.EXP510 WHERE RSOC = ? AND HNOBL = ?", ct, societe, numero);

        foreach (var l in s.Lignes)
        {
            var hnum = numeros[l];
            var parLot = new List<(LotSaisiePreBl Lot, int Numero, LotEnregistrePreBl? Avant, ConditionnementLotPreBl Cond)>();
            var prochainLot = lots.Where(x => x.Ligne == hnum).Select(x => x.Numero).DefaultIfEmpty(0).Max() + 1;
            foreach (var lot in l.Lots)
            {
                var avant = lot.Numero > 0 ? lots.FirstOrDefault(x => x.Ligne == hnum && x.Numero == lot.Numero) : null;
                var no = avant?.Numero ?? prochainLot++;
                var inchange = avant is not null && (s.SansPalette ? avant.Lot.Lot == lot.Lot && avant.Lot.Depot == lot.Depot && avant.Lot.Quantite == lot.Quantite
                    : ReglesSaisiePreBl.LotInchange(avant.Lot, lot));
                if (avant is { Verrouille: true } && !inchange)
                    throw new RegleMetierException($"Le lot {avant.Lot.Lot} de la ligne {hnum} est en préparation : il ne peut pas être modifié.");
                if (!inchange) change = true;
                parLot.Add((lot, no, inchange ? avant : null, ReglesSaisiePreBl.Conditionnement(l, lot, s.SansPalette)));
            }
            await Executer(cnx, tx, """
                INSERT INTO {b}.EXP510 (RSOC, HNOBL, HNUM, HNODOS, HCLEF, HNUMLC, DART, DIND, DQTE, DOBS1, DOBS2, DOBS3, DAPPSOLD, DETALI, DENPAL,
                    DENUC, DEEXMEC, DEEMBPE, DTYEMB, DEMBP1, DENPALE)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """, ct, societe, numero, hnum, l.Dossier, l.Cle, l.LigneCommande, l.Article, l.Indice, l.Quantite, Tronquer(l.Observations[0] ?? "", 40),
                Tronquer(l.Observations[1] ?? "", 40), Tronquer(l.Observations[2] ?? "", 40), l.AppelASolder ? "O" : "N", l.EtatLivraison,
                parLot.Sum(x => x.Cond.Palettes), parLot.Sum(x => x.Cond.Colis), parLot.Sum(x => x.Cond.ExMecompte), l.EmballageParExceptionnel,
                l.TypeEmballage, l.EmballagePar, l.UcParPaletteExceptionnel);

            // Lots : réécrits (ceux qui n'ont pas changé gardent récap, clôture et observations) ; palettes des lots changés réécrites
            foreach (var (lot, no, avant, cond) in parLot)
            {
                if (avant is not null) continue;   // lot inchangé : ligne EXP516 et palettes gardées telles quelles
                await Executer(cnx, tx, "DELETE FROM {b}.EXP516 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DNLIG = ?", ct, societe, numero, hnum, no);
                await Executer(cnx, tx, """
                    INSERT INTO {b}.EXP516 (RSOC, DNBL, DNUM, DNLIG, DNTYPL, DNLOT, DNDEP, DNQTPL, DNPAL, DNPALM, DNUC, DEXMEC, DCOLIM, DPALINCOM, DLIBPAL,
                        DDOBS1, DDOBS2, DCLOTU)
                    VALUES (?, ?, ?, ?, '1', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """, ct, societe, numero, hnum, no, lot.Lot, lot.Depot, lot.Quantite, cond.Palettes, 0, cond.Colis,
                    cond.ExMecompte, cond.ExMecompte > 0 ? 1 : 0, cond.PaletteIncomplete ? "O" : "N", "", "", "", "");
                if (s.SansPalette) continue;
                await Executer(cnx, tx, "DELETE FROM {b}.EXP517 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DNLIG = ?", ct, societe, numero, hnum, no);
                foreach (var p in lot.Palettes)
                {
                    var picking = await VerifierPalettePreBlAsync(cnx, tx, societe, numero, l, lot, p, ct);
                    await Executer(cnx, tx, """
                        INSERT INTO {b}.EXP517 (RSOC, DNBL, DNUM, DNLIG, DPSSCC, DPQTEP, DPPICK, DPOBS) VALUES (?, ?, ?, ?, ?, ?, ?, ' ')
                        """, ct, societe, numero, hnum, no, p.Sscc, p.Quantite, picking ? "O" : "N");
                }
            }
        }
        // Lots et palettes des lignes et lots retirés
        foreach (var lot in lots.Where(x => !s.Lignes.Any(l => numeros[l] == x.Ligne && l.Lots.Any(y => y.Numero == x.Numero))))
        {
            await Executer(cnx, tx, "DELETE FROM {b}.EXP516 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DNLIG = ?", ct, societe, numero, lot.Ligne, lot.Numero);
            await Executer(cnx, tx, "DELETE FROM {b}.EXP517 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DNLIG = ?", ct, societe, numero, lot.Ligne, lot.Numero);
        }
        return change;
    }

    // Palette relue verrouillée : de l'article, du lot et du dépôt, hors OPR, libre (1, 2) ou déjà dans ce pré-BL, quantité
    // disponible (moins les autres pré-BL non livrés) ; rend vrai si elle est prise en partie (picking)
    private async Task<bool> VerifierPalettePreBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, LigneSaisiePreBl l,
        LotSaisiePreBl lot, PaletteSaisiePreBl p, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, tx, """
            SELECT e.ARTICLE, e.INDICE, DIGITS(e.NUMERO_OF) CONCAT e.CLE_OF, e.DEPOT, e.STATUT, e.NUMERO_OPR, e.QUANTITE_EX,
                   COALESCE((SELECT SUM(x.DPQTEP) FROM {b}.EXP517 x JOIN {b}.EXP509 h ON h.RSOC = x.RSOC AND h.HNOBL = x.DNBL
                             WHERE x.RSOC = e.SOCIETE AND x.DPSSCC = e.NUMERO_SSCC AND x.DNBL <> ? AND h.HENBL <> 'O'), 0),
                   (SELECT COUNT(*) FROM {b}.EXP517 x WHERE x.RSOC = e.SOCIETE AND x.DPSSCC = e.NUMERO_SSCC AND x.DNBL = ?)
            FROM {b}.PROD_ENTITE e WHERE e.SOCIETE = ? AND e.NUMERO_SSCC = ?
            WITH RS USE AND KEEP EXCLUSIVE LOCKS
            """, ct, numero, numero, societe, p.Sscc);
        var nom = $"Palette {p.Sscc}";
        if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"{nom} : elle n'existe pas.");
        if (rd.Texte(0) != l.Article || rd.Texte(1) != l.Indice || rd.Texte(2) != lot.Lot || rd.Entier(3) != lot.Depot)
            throw new RegleMetierException($"{nom} : elle n'est pas de l'article {l.Article}, lot {lot.Lot}, dépôt {lot.Depot}.");
        if (rd.Entier(5) != 0) throw new RegleMetierException($"{nom} : elle est sur un ordre de préparation.");
        if (rd.Texte(4) is not ("1" or "2") && !(rd.Texte(4) is "3" or "4" && rd.Entier(8) > 0))
            throw new RegleMetierException($"{nom} : elle n'est plus disponible (statut {rd.Texte(4)}).");
        var disponible = (int)rd.Nombre(6) - rd.Entier(7);
        if (p.Quantite > disponible)
            throw new RegleMetierException($"{nom} : {p.Quantite:#,##0} demandés, {Math.Max(0, disponible):#,##0} disponibles (prise par un autre pré-BL ?).");
        return p.Quantite < disponible;
    }

    // Suppression (FEN_GestionPreBL, R26) : refusée pour un pré-BL transformé ou dont des palettes sont en préparation ou
    // scannées ; texte de pied supprimé aussi (oublié par le WinDev). Le n° de BL réservé n'est pas rendu.
    public async Task SupprimerPreBlAsync(int societe, string profil, int numero, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        await PoserVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, numero), profil, $"Le pré-BL {numero} est en cours de modification par", ct);
        try
        {
            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                await using (var rd = await Lire(cnx, tx, """
                    SELECT h.HENBL, COALESCE((SELECT FEPREB FROM {b}.EXP005 x WHERE x.RSOC = h.RSOC FETCH FIRST 1 ROW ONLY), ''),
                           (SELECT COUNT(*) FROM {b}.EXP517 p JOIN {b}.PROD_ENTITE e ON e.SOCIETE = p.RSOC AND e.NUMERO_SSCC = p.DPSSCC
                            WHERE p.RSOC = h.RSOC AND p.DNBL = h.HNOBL AND e.STATUT IN ('3', '4')),
                           (SELECT COUNT(*) FROM {b}.EXP517 p WHERE p.RSOC = h.RSOC AND p.DNBL = h.HNOBL)
                    FROM {b}.EXP509 h WHERE h.RSOC = ? AND h.HNOBL = ?
                    WITH RS USE AND KEEP EXCLUSIVE LOCKS
                    """, ct, societe, numero))
                {
                    if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"Le pré-BL {numero} n'existe plus.");
                    if (rd.Texte(0) == "O") throw new RegleMetierException($"Le pré-BL {numero} est transformé en BL : supprimez le BL d'abord.");
                    if (rd.Entier(2) > 0) throw new RegleMetierException("Suppression impossible : des palettes du pré-BL sont en préparation.");
                    if (rd.Texte(1) == "O" && rd.Entier(3) > 0)
                        throw new RegleMetierException("Suppression impossible : des palettes ont déjà été scannées pour ce pré-BL.");
                }
                foreach (var table in new[] { ("EXP517", "DNBL"), ("EXP516", "DNBL"), ("EXP510", "HNOBL"), ("EXP512", "HNOBL"), ("EXP509", "HNOBL") })
                    await Executer(cnx, tx, $"DELETE FROM {{b}}.{table.Item1} WHERE RSOC = ? AND {table.Item2} = ?", ct, societe, numero);
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
            await LeverVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, numero), profil);
        }
    }

    // Récapitulatif palettes (FEN_RecapPalettes.BTN_Valider) : n° de palette mixte et libellé des n° de palettes de chaque lot,
    // nombre de palettes du pré-BL ; refusé pour un pré-BL transformé ou dont les lots ont changé depuis le calcul
    public async Task EnregistrerRecapPreBlAsync(int societe, string profil, RecapPalettesPreBl recap, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        await PoserVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, recap.Numero), profil, $"Le pré-BL {recap.Numero} est en cours de modification par", ct);
        try
        {
            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                await using (var rd = await Lire(cnx, tx, "SELECT HENBL FROM {b}.EXP509 WHERE RSOC = ? AND HNOBL = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS", ct,
                                 societe, recap.Numero))
                {
                    if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"Le pré-BL {recap.Numero} n'existe plus.");
                    if (rd.Texte(0) == "O") throw new RegleMetierException($"Le pré-BL {recap.Numero} est transformé en BL.");
                }
                // Lots relus : les mêmes que ceux du calcul (lot, dépôt, quantité, palettes complètes, incomplet), aucun ajouté ni retiré
                var lus = new List<(int, int, string, int, int, int, bool)>();
                await using (var rd = await Lire(cnx, tx, """
                    SELECT d.DNUM, d.DNLIG, d.DNLOT, d.DNDEP, d.DNQTPL, d.DNPAL, d.DPALINCOM FROM {b}.EXP516 d
                    JOIN {b}.EXP510 l ON l.RSOC = d.RSOC AND l.HNOBL = d.DNBL AND l.HNUM = d.DNUM
                    JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND AND s.ASTK = 'O'
                    WHERE d.RSOC = ? AND d.DNBL = ?
                    """, ct, societe, recap.Numero))
                    while (await rd.ReadAsync(ct)) lus.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Entier(4), rd.Entier(5), rd.Texte(6) == "O"));
                if (!lus.OrderBy(x => x.Item1).ThenBy(x => x.Item2).SequenceEqual(recap.Lignes
                        .Select(l => (l.Ligne, l.Lot, l.NumeroLot, l.Depot, l.Quantite, l.PalettesCompletes, l.Incomplete)).OrderBy(x => x.Ligne).ThenBy(x => x.Lot)))
                    throw new RegleMetierException("Le pré-BL a été modifié depuis l'ouverture du récapitulatif : rouvrez-le.");
                foreach (var l in recap.Lignes)
                    await Executer(cnx, tx, "UPDATE {b}.EXP516 SET DNPALM = ?, DLIBPAL = ? WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DNLIG = ?",
                        ct, l.PaletteMixte, Tronquer(l.Libelle, 25), societe, recap.Numero, l.Ligne, l.Lot);
                await Executer(cnx, tx, "UPDATE {b}.EXP509 SET HNPALT = ? WHERE RSOC = ? AND HNOBL = ?", ct, recap.NombrePalettes, societe, recap.Numero);
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
            await LeverVerrouAsync(cnx, "PREBL", CleVerrouPreBl(societe, recap.Numero), profil);
        }
    }
}
