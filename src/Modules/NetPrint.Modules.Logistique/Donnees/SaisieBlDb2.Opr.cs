using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

internal sealed partial class SaisieBlDb2
{
    // =====================================================================
    // BL d'un OPR (FEN_CreationBL) en une transaction, sous verrous de l'OPR et des commandes : palettes relues (toujours
    // conditionnées), stock contrôlé, n° de BL (compteur des BL, ou SYN005 en sous-traitance synchronisée), EXP009 / EXP010 /
    // EXP015 / EXP016 / EXP012, mouvements (transfert TR vers le dépôt principal si la palette est ailleurs, puis sortie EX),
    // palettes expédiées (statut 5), cartons rattachés au BL et sortis du stock d'emballage (SS) s'il y en a assez, commande
    // (livré, reste, soldé ; frais comptés comme livrés), lignes d'OPR livrées (ODQTEX), OPR livré quand tout l'est.
    // Après : recalcul du stock (STOREVNAV), audit « CR », message de synchronisation de la sous-traitance.
    // =====================================================================
    public async Task<(List<BlGenereOpr> Bls, List<string> Avertissements)> EnregistrerBlOprAsync(int societe, string profil, SourceBlOpr source,
        IReadOnlyList<BlPlanifieOpr> plan, GenerationBlOpr g, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var dossiers = source.Commandes.Keys.Select(k => CleVerrouDossier(societe, k.Item1, k.Item2)).Distinct().ToList();
        var poses = new List<string>();
        var recalculs = new List<(string TypeStock, int Depot, string Article, string Indice)>();
        var resultat = new List<BlGenereOpr>();
        var avertissements = new List<string>();
        var synchronisation = new List<(int Dossier, string Cle, int Bl)>();
        await VerrousApplicatifs.PoserAsync(cnx, Bib, "OPR", OprDb2.CleVerrou(societe, source.Numero), profil,
            $"L'OPR {source.Numero} est en cours de modification par", ct);
        try
        {
            foreach (var d in dossiers)
            {
                await PoserVerrouAsync(cnx, "DOSSIER", d, profil, "Ce dossier est en cours de modification par", ct);
                poses.Add(d);
            }
            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                await VerifierPalettesOprAsync(cnx, tx, societe, source, ct);
                var livreCumule = new Dictionary<(int, string, int), int>();      // livré par les BL précédents de la génération
                var cartonsSortis = new Dictionary<int, int>();                     // cartons d'emballage déjà sortis, par matière
                await ControlerStockOprAsync(cnx, tx, societe, source, g.Date, ct);
                var maintenant = DateTime.Now;
                foreach (var bl in plan)
                {
                    var premiere = source.Commandes[(bl.Elements[0].Dossier, bl.Elements[0].Cle)];
                    var synchro = bl.Elements.Any(e => source.Commandes[(e.Dossier, e.Cle)].SousTraitanceSynchronisee);
                    var numero = synchro ? await NumeroSynchroAsync(cnx, tx, societe, ct)
                        : await NumeroBlAsync(cnx, tx, societe, bl.Enseigne, source.NumerotationParEnseigne, ct);
                    var a = source.Adresse;
                    await Executer(cnx, tx, """
                        INSERT INTO {b}.EXP009 (RSOC, HNOBL, HDEXAA, HDEXMM, HDEXJJ, HFJ, HRS, HRUE1, HRUE2, HCP, HVILLE, HOBS, HGENE, HCLIE,
                            HNCOLT, HNPALT, HPDSTT, HTRAN, HTYPL, HTYPP, HNBEPL, HEDBL, HEDETP, HENS, HBART, HNOREC, HBLGRP, HPROF, HDPT, HREV,
                            HNADRE, HPAYS, HOBSIN, HOBS2, HOBS3, HSTR, HMENTI)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?, 0, 'N', 'N', ?, 0, 0, 'N', ?, ?, ?, ?, ?, ?, ?, ?, ?, ' ')
                        """, ct, societe, numero, g.Date.Year, g.Date.Month, g.Date.Day, a.FormeJuridique, a.RaisonSociale, a.Rue1, a.Rue2, a.CodePostal,
                        a.Ville, Tronquer(g.Observations.ElementAtOrDefault(0) ?? "", 40), premiere.CompteGeneral, premiere.CompteClient,
                        Math.Min(bl.Colis, 99999), Math.Min(bl.PoidsCartons, 99999), g.Transporteur, g.ModeLivraison, g.TypePort, bl.Enseigne, profil,
                        a.Departement, premiere.Revendeur == "O" ? "O" : "N", premiere.NumeroAdresse, a.Pays, Tronquer(g.Incoterm, 40),
                        Tronquer(g.Observations.ElementAtOrDefault(1) ?? "", 40), Tronquer(g.Observations.ElementAtOrDefault(2) ?? "", 40),
                        synchro ? "O" : "N");
                    var element = 0;
                    foreach (var el in bl.Elements)
                    {
                        element++;
                        var c = source.Commandes[(el.Dossier, el.Cle)];
                        if (c.SousTraitanceSynchronisee) synchronisation.Add((el.Dossier, el.Cle, numero));
                        var genF = el.CompteGeneralFacture != 0 && el.CompteClientFacture != 0 ? el.CompteGeneralFacture : c.CompteGeneral;
                        var cliF = el.CompteGeneralFacture != 0 && el.CompteClientFacture != 0 ? el.CompteClientFacture : c.CompteClient;
                        await Executer(cnx, tx, """
                            INSERT INTO {b}.EXP010 (RSOC, HNOBL, HNUM, HNODOS, HCLEF, HETABL, HQTELI, HNUC1, HQTUC1, HUCDT1, HNUC2, HQTUC2, HUCDT2, HNBPAL,
                                HPDST, HETALI, HTYPBL, HEDETC, HBLFAC, HGENE, HCLIE, HREFCD, HDCDEA, HDCDEM, HDCDEJ, HNODEV, HNOHYP, HNOMOD, HREFIM,
                                HREP, HNOFAC, HFACTU, HNOBLO, HNUMO, HGENF, HCLIF, HFACTO)
                            VALUES (?, ?, ?, ?, ?, 'C', 0, 0, 0, 'C', 0, 0, ' ', 0, 0, ' ', 2, 'N', 'N', ?, ?, ?, ?, ?, ?, 0, 0, 0, ' ', ?, 0, 'O', 0, 0, ?, ?, ?)
                            """, ct, societe, numero, element, el.Dossier, el.Cle, c.CompteGeneral, c.CompteClient, c.Reference, c.DateCommande?.Year ?? 0,
                            c.DateCommande?.Month ?? 0, c.DateCommande?.Day ?? 0, c.Representant, genF, cliF, c.FacturationAutomatique ? "O" : "N");
                        var no = 0;
                        foreach (var l in el.Lignes)
                        {
                            var lc = source.Lignes[(el.Dossier, el.Cle, l.LigneCommande)];
                            var (colis, emballagePar, colisMecompte, exMecompte) = ReglesGenerationBlOpr.Conditionnement(l.QuantitesPalettes, lc.TypeEmballage);
                            no++;
                            await Executer(cnx, tx, """
                                INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                                    DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0)
                                """, ct, societe, numero, element, no, l.Article, Tronquer(lc.Designation, 40), l.Quantite, l.Indice, colis, l.LigneCommande,
                                ReglesGenerationBlOpr.EtatLivraison(lc.Livre + livreCumule.GetValueOrDefault((el.Dossier, el.Cle, l.LigneCommande)), l.Quantite,
                                    lc.Commande, lc.Acompte), lc.CodeLigne,
                                lc.Textes.ElementAtOrDefault(0) ?? "", lc.Textes.ElementAtOrDefault(1) ?? "", emballagePar, lc.TypeEmballage, colisMecompte, exMecompte);
                            var ligneArticle = no;
                            // Textes suivants de la ligne de commande, par trois, sur des lignes de texte
                            foreach (var groupe in lc.Textes.Skip(2).Chunk(3))
                            {
                                no++;
                                await Executer(cnx, tx, """
                                    INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                                        DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                                    VALUES (?, ?, ?, ?, ' ', ?, 0, ' ', 0, ?, ' ', ' ', ?, ?, 0, ' ', 0, 0, 0)
                                    """, ct, societe, numero, element, no, groupe.ElementAtOrDefault(0) ?? "", l.LigneCommande, groupe.ElementAtOrDefault(1) ?? "",
                                    groupe.ElementAtOrDefault(2) ?? "");
                            }
                            foreach (var lot in l.Lots)
                            {
                                await Executer(cnx, tx, """
                                    INSERT INTO {b}.EXP016 (RSOC, DNBL, DNUM, DNLIG, DNLOT, DNQTPL, DUC1, DUC2, DNEMP) VALUES (?, ?, ?, ?, ?, ?, 0, 0, ?)
                                    """, ct, societe, numero, element, ligneArticle, lot.Lot, lot.Quantite, lot.Emplacement);
                                var mouvements = new List<(string Code, string Sens, int Depot, int Contrepartie)>();
                                if (lot.Depot != source.DepotPrincipal)
                                {
                                    mouvements.Add(("TR", "S", lot.Depot, source.DepotPrincipal));
                                    mouvements.Add(("TR", "E", source.DepotPrincipal, lot.Depot));
                                }
                                mouvements.Add(("EX", "S", source.DepotPrincipal, 0));
                                foreach (var (code, sens, depot, contrepartie) in mouvements)
                                    await Executer(cnx, tx, """
                                        INSERT INTO {b}.A700F (RSOC, MVBON, MVAA, MVMM, MVJJ, MVTSTO, MVDEP, MVCMVT, MVSENS, MVLOT, MVDOS, MVDCLE, MVDEPX, MVREF,
                                            MVFAM, MVSFAM, MVQTUS, MVUS, MVPRO, MVIND, MVBL, MVELBL, MVLIGB, MVEMP, MVDATS, MVCHRO)
                                        VALUES (?, ' ', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'UN', ?, ?, ?, ?, ?, ?, ?, ?)
                                        """, ct, societe, g.Date.Year, g.Date.Month, g.Date.Day, lc.TypeStock, depot, code, sens, lot.Lot, el.Dossier, el.Cle,
                                        contrepartie, l.Article, lc.Famille, lc.SousFamille, lot.Quantite, profil, l.Indice, numero, element, ligneArticle,
                                        lot.Emplacement, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant.Hour * 100 + maintenant.Minute);
                                foreach (var sscc in lot.Palettes)
                                    await Executer(cnx, tx, """
                                        UPDATE {b}.PROD_ENTITE SET STATUT = '5', NUMERO_BL = ?, ELEMENT_BL = ?, NUMERO_LIGNE_BL = ?, DATE_SOR_STOCK = CURRENT TIMESTAMP
                                        WHERE SOCIETE = ? AND NUMERO_SSCC = ?
                                        """, ct, numero, element, ligneArticle, societe, sscc);
                                recalculs.Add((lc.TypeStock, source.DepotPrincipal, l.Article, l.Indice));
                                if (lot.Depot != source.DepotPrincipal) recalculs.Add((lc.TypeStock, lot.Depot, l.Article, l.Indice));
                            }
                            livreCumule[(el.Dossier, el.Cle, l.LigneCommande)] = livreCumule.GetValueOrDefault((el.Dossier, el.Cle, l.LigneCommande)) + l.Quantite;
                            // Commande : livré, reste, soldé (les frais comptent comme livrés, décision du 31/10)
                            await Executer(cnx, tx, """
                                UPDATE {b}.NEG100 SET DNQTEL = DNQTEL + ?, DNQTER = MAX(0, DNQTEC - (DNQTEL + ?)),
                                       DNSOLD = CASE WHEN DNQTEC - (DNQTEL + ?) <= 0 THEN 'O' ELSE DNSOLD END
                                WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                                """, ct, l.Quantite, l.Quantite, l.Quantite, societe, el.Dossier, el.Cle, l.LigneCommande);
                        }
                        // Lignes d'OPR livrées par cet élément : quantité de leurs palettes livrées ici (frais : quantité de la ligne), n° du BL
                        var livrees = el.Lignes.SelectMany(li => li.Lots).SelectMany(lo => lo.Palettes).ToHashSet();
                        foreach (var ligneOpr in el.Lignes.SelectMany(li => li.LignesOpr).Distinct())
                        {
                            var quantite = source.Palettes.Where(p => p.LigneOpr == ligneOpr && (p.Frais ? el.Lignes.Any(li => li.Frais && li.LignesOpr.Contains(ligneOpr))
                                : livrees.Contains(p.Sscc))).Sum(p => p.Quantite);
                            await Executer(cnx, tx, """
                                UPDATE {b}.OPR015 SET ODLIVR = 'O', ODBL = ?, ODQTEX = ODQTEX + ? WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?
                                """, ct, numero, quantite, societe, source.Numero, ligneOpr);
                        }
                        // Cartons de l'élément : rattachés au BL, puis sortis du stock d'emballage s'il y en a assez
                        foreach (var (colisage, _, _) in el.Cartons.Where(x => x.Colisage != 0))
                            await Executer(cnx, tx, """
                                UPDATE {b}.PROD_COLISAGE SET NUMERO_BL = ?, ELEMENT_BL = ? WHERE SOCIETE = ? AND NUMERO_OPR = ? AND NUMERO_COLISAGE = ?
                                """, ct, numero, element, societe, source.Numero, colisage);
                        foreach (var matiere in el.Cartons.Where(x => x.Colisage != 0 && x.Matiere != 0).GroupBy(x => x.Matiere))
                            if (await SortirCartonsAsync(cnx, tx, societe, profil, source.DepotPrincipal, matiere.Key, matiere.Count(),
                                    cartonsSortis.GetValueOrDefault(matiere.Key), numero, element, g.Date, maintenant, ct) is { } recalcul)
                            {
                                recalculs.Add(recalcul);
                                cartonsSortis[matiere.Key] = cartonsSortis.GetValueOrDefault(matiere.Key) + matiere.Count();
                            }
                            else
                                avertissements.Add($"BL {numero} : stock de cartons d'emballage {matiere.Key} insuffisant ({matiere.Count()} carton(s)), " +
                                                   "ils ne sont pas sortis du stock.");
                        // Produit fini : quantité livrée du dossier (comme la saisie des BL ; la suppression du BL la diminue)
                        await Executer(cnx, tx, "UPDATE {b}.AB001 SET CCQTEL = CCQTEL + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC = '4'", ct,
                            el.Lignes.Sum(li => li.Quantite), societe, el.Dossier, el.Cle);
                        await SolderCommandeSiToutLivreAsync(cnx, tx, societe, profil, c, ct);
                    }
                    // Texte de pied du client (CLITXB)
                    for (var i = 0; i < source.TextePied.Count; i++)
                        await Executer(cnx, tx, "INSERT INTO {b}.EXP012 (RSOC, HNOBL, HTNLIG, HTTXT) VALUES (?, ?, ?, ?)", ct, societe, numero, i + 1,
                            Tronquer(source.TextePied[i], 40));
                    resultat.Add(new BlGenereOpr(numero, bl.Enseigne, bl.Colis, bl.PoidsCartons, source.TextePied));
                }
                await Executer(cnx, tx, """
                    UPDATE {b}.OPR010 o SET OESTAT = '4' WHERE o.RSOC = ? AND o.OENUM = ? AND o.OESTAT <> '4'
                      AND NOT EXISTS (SELECT 1 FROM {b}.OPR015 l WHERE l.RSOC = o.RSOC AND l.ODNUM = o.OENUM AND l.ODLIVR <> 'O')
                    """, ct, societe, source.Numero);
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
            await VerrousApplicatifs.LeverAsync(cnx, Bib, "OPR", OprDb2.CleVerrou(societe, source.Numero), profil);
        }

        foreach (var (typeStock, depot, article, indice) in recalculs.Distinct())
            await RecalculerStockAsync(cnx, societe, typeStock, depot, article, indice);
        foreach (var bl in resultat) await AuditAsync(cnx, societe, profil, bl.NumeroBl, "CR");
        foreach (var (dossier, cle, bl) in synchronisation.Distinct()) await MessageSynchronisationAsync(cnx, societe, dossier, cle, bl);
        return (resultat, avertissements);
    }

    // Palettes relues verrouillées : toujours conditionnées et sur l'OPR ; aucune autre palette conditionnée ni en préparation sur
    // les lignes non livrées (situation changée depuis la lecture) ; lignes toujours non livrées
    private async Task VerifierPalettesOprAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, SourceBlOpr source, CancellationToken ct)
    {
        var attendues = source.Palettes.Where(p => !p.Frais).Select(p => p.Sscc).ToHashSet();
        await using (var rd = await Lire(cnx, tx, """
            SELECT p.NUMERO_SSCC, p.STATUT FROM {b}.PROD_ENTITE p JOIN {b}.OPR015 l ON l.RSOC = p.SOCIETE AND l.ODNUM = p.NUMERO_OPR
                 AND l.ODNLIG = p.NUMERO_LIGNE_OPR AND l.ODLIVR <> 'O'
            WHERE p.SOCIETE = ? AND p.NUMERO_OPR = ? AND p.STATUT IN ('3', '4')
            """, ct, societe, source.Numero))
            while (await rd.ReadAsync(ct))
                if (rd.Texte(1) == "3" || !attendues.Contains(rd.Texte(0)))
                    throw new RegleMetierException("La préparation de l'OPR a changé depuis l'ouverture (palette conditionnée ou en préparation) : " +
                                                   "rouvrez la situation de l'OPR.");
        foreach (var p in source.Palettes.Where(p => !p.Frais))
        {
            await using var rd = await Lire(cnx, tx, """
                SELECT STATUT, NUMERO_OPR FROM {b}.PROD_ENTITE WHERE SOCIETE = ? AND NUMERO_SSCC = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, p.Sscc);
            if (!await rd.ReadAsync(ct) || rd.Texte(0) != "4" || rd.Entier(1) != source.Numero)
                throw new RegleMetierException($"La palette {p.Sscc} n'est plus conditionnée sur cet OPR : rouvrez la situation de l'OPR.");
        }
        foreach (var ligne in source.Palettes.Select(p => p.LigneOpr).Distinct())
        {
            await using var rd = await Lire(cnx, tx, "SELECT ODLIVR FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                ct, societe, source.Numero, ligne);
            if (!await rd.ReadAsync(ct) || rd.Texte(0) == "O")
                throw new RegleMetierException($"La ligne {ligne} de l'OPR {source.Numero} vient d'être livrée : rouvrez la situation de l'OPR.");
        }
    }

    // Stock : chaque lot sort du dépôt de sa palette (transfert puis sortie, ou sortie directe au dépôt principal)
    private async Task ControlerStockOprAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, SourceBlOpr source, DateOnly date, CancellationToken ct)
    {
        var lots = source.Palettes.Where(p => !p.Frais)
            .Select(p => (Type: source.Lignes[(p.Dossier, p.Cle, p.LigneCommande)].TypeStock, p.Depot, p.Article, p.Indice, p.Lot, p.Emplacement, p.Quantite))
            .GroupBy(x => (x.Type, x.Depot, x.Article, x.Indice, x.Lot, x.Emplacement)).ToList();
        foreach (var (typeStock, depot, article, indice) in lots.Select(x => (x.Key.Type, x.Key.Depot, x.Key.Article, x.Key.Indice))
                     .Concat(lots.Select(x => (x.Key.Type, source.DepotPrincipal, x.Key.Article, x.Key.Indice))).Distinct()
                     .OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Item2).ThenBy(x => x.Article, StringComparer.Ordinal))
            await Executer(cnx, tx, "UPDATE {b}.AK00F SET PRREF = PRREF WHERE RSOC = ? AND PRTSTO = ? AND PRDEP = ? AND PRREF = ? AND PRIND = ?", ct,
                societe, typeStock, depot, article, indice);
        var parEmplacement = new Dictionary<string, bool>();
        foreach (var x in lots)
        {
            if (!parEmplacement.TryGetValue(x.Key.Type, out var pe))
            {
                await using var ts = await Lire(cnx, tx, "SELECT TSEMP FROM {b}.AG00F WHERE RSOC = ? AND TSTSTO = ?", ct, societe, x.Key.Type);
                parEmplacement[x.Key.Type] = pe = await ts.ReadAsync(ct) && ts.Texte(0) == "O";
            }
            var retour = await ControlerStockAsync(cnx, tx, societe, x.Key.Type, pe, x.Key.Depot, x.Key.Article, x.Key.Indice, x.Key.Lot, x.Key.Emplacement,
                date, -x.Sum(p => p.Quantite), ct);
            if (retour != 0) throw new RegleMetierException(MessageStock(x.Key.Article, x.Key.Lot, x.Key.Emplacement, x.Key.Depot, retour));
        }
    }

    private async Task<int> NumeroSynchroAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, CancellationToken ct)
    {
        if (await Executer(cnx, tx, "UPDATE {b}.SYN005 SET PSSNUMB = PSSNUMB + 1 WHERE PSSSOCS = ?", ct, societe) == 0)
            throw new RegleMetierException("Pas de compteur de BL de sous-traitance synchronisée (SYN005) pour cette société.");
        await using var rd = await Lire(cnx, tx, "SELECT PSSNUMB FROM {b}.SYN005 WHERE PSSSOCS = ? FETCH FIRST 1 ROW ONLY", ct, societe);
        await rd.ReadAsync(ct);
        return rd.Entier(0);
    }

    // Cartons d'emballage (matière C000F) sortis du stock d'emballage (emplacement EMBAL du dépôt principal), imputés à l'élément
    // du BL ; null si le stock ne suffit pas (rien n'est sorti)
    private async Task<(string, int, string, string)?> SortirCartonsAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, int depot,
        int matiere, int nombre, int dejaSortis, int numero, int element, DateOnly date, DateTime maintenant, CancellationToken ct)
    {
        string typeStock, unite, famille, sousFamille;
        await using (var rd = await Lire(cnx, tx, "SELECT GTSTO, GUS, GFAMIL, GSSFAM FROM {b}.C000F WHERE RSOC = ? AND RMATIE = ?", ct, societe, matiere))
        {
            if (!await rd.ReadAsync(ct)) return null;
            (typeStock, unite, famille, sousFamille) = (rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3));
        }
        var reference = matiere.ToString("000000");
        await using (var rd = await Lire(cnx, tx, """
            SELECT COALESCE(SUM(PEQTE), 0) FROM {b}.AP00F WHERE RSOC = ? AND PETSTO = ? AND PEDEP = ? AND PEEMP = 'EMBAL' AND PEREF = ?
            """, ct, societe, typeStock, depot, reference))
        {
            await rd.ReadAsync(ct);
            if (rd.Nombre(0) - dejaSortis < nombre) return null;   // positions recalculées après la validation : sorties de la génération déduites
        }
        await Executer(cnx, tx, """
            INSERT INTO {b}.A700F (RSOC, MVBON, MVAA, MVMM, MVJJ, MVTSTO, MVDEP, MVCMVT, MVSENS, MVLOT, MVOBS, MVDOS, MVDCLE, MVREF, MVFAM, MVSFAM,
                MVQTUS, MVUS, MVPRO, MVIND, MVBL, MVELBL, MVLIGB, MVEMP, MVDATS, MVCHRO)
            VALUES (?, ' ', ?, ?, ?, ?, ?, 'SS', 'S', ' ', ?, 0, ' ', ?, ?, ?, ?, ?, ?, ' ', ?, ?, 0, 'EMBAL', ?, ?)
            """, ct, societe, date.Year, date.Month, date.Day, typeStock, depot, $"sortie BL N° {numero}", reference, famille, sousFamille, nombre,
            unite, profil, numero, element, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant.Hour * 100 + maintenant.Minute);
        return (typeStock, depot, reference, "");
    }

    // Commande soldée (AB001.CCTAGS = 'D') quand toutes ses lignes le sont, comme SoldeCommande du WinDev
    private async Task SolderCommandeSiToutLivreAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, CommandeBlOpr c, CancellationToken ct)
    {
        await using (var rd = await Lire(cnx, tx, """
            SELECT COUNT(*) FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNART <> ' ' AND DNART NOT LIKE '999%' AND DNSOLD <> 'O'
            """, ct, societe, c.Dossier, c.Cle))
        {
            await rd.ReadAsync(ct);
            if (rd.Entier(0) > 0) return;
        }
        var maintenant = DateTime.Now;
        if (await Executer(cnx, tx, """
                UPDATE {b}.AB001 SET CCTAGS = 'D', CCSLDA = ?, CCSLDM = ?, CCSLDJ = ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTAGS <> 'D'
                """, ct, maintenant.Year, maintenant.Month, maintenant.Day, societe, c.Dossier, c.Cle) == 0) return;
        var initiales = "";
        await using (var rd = await Lire(cnx, tx, "SELECT ACINI FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil))
            if (await rd.ReadAsync(ct)) initiales = rd.Texte(0);
        await Executer(cnx, tx, """
            INSERT INTO {b}.DOS040 (RSOC, RDOSS, HICLE, HIGEN, HICLI, HIINAM, HITYPM, HIDTMA, HIDTMM, HIDTMJ, HIHEUR)
            VALUES (?, ?, ?, ?, ?, ?, 'SO', ?, ?, ?, ?)
            """, ct, societe, c.Dossier, c.Cle, c.CompteGeneral, c.CompteClient, initiales, maintenant.Year, maintenant.Month, maintenant.Day,
            maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
    }

    // Finalisation (FEN_DetailBL) : palettes, poids (cartons + palettes × poids d'une palette) et texte de pied de chaque BL
    public async Task FinaliserBlOprAsync(int societe, int numeroOpr, IReadOnlyList<FinalisationBlOpr> bls, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var valide = false;
        try
        {
            foreach (var f in bls)
            {
                // BL issu de cet OPR (une de ses lignes ou palettes) et non facturé
                await using (var rd = await Lire(cnx, tx, """
                    SELECT (SELECT COUNT(*) FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ? AND ODBL = ?)
                         + (SELECT COUNT(*) FROM {b}.PROD_ENTITE WHERE SOCIETE = ? AND NUMERO_OPR = ? AND NUMERO_BL = ?),
                           (SELECT COUNT(*) FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND (HBLFAC = 'O' OR HNOFAC <> 0))
                    FROM SYSIBM.SYSDUMMY1
                    """, ct, societe, numeroOpr, f.NumeroBl, societe, numeroOpr, f.NumeroBl, societe, f.NumeroBl))
                {
                    await rd.ReadAsync(ct);
                    if (rd.Entier(0) == 0) throw new RegleMetierException($"Le BL {f.NumeroBl} ne vient pas de l'OPR {numeroOpr}.");
                    if (rd.Entier(1) > 0) throw new RegleMetierException($"Le BL {f.NumeroBl} est facturé : il ne peut plus être modifié.");
                }
                int poidsCartons;
                await using (var rd = await Lire(cnx, tx, """
                    SELECT COALESCE(CEILING(SUM(g.POIDS)), 0) FROM {b}.PROD_COLISAGE g WHERE g.SOCIETE = ? AND g.NUMERO_BL = ?
                    """, ct, societe, f.NumeroBl))
                {
                    await rd.ReadAsync(ct);
                    poidsCartons = (int)rd.Nombre(0);
                }
                if (await Executer(cnx, tx, "UPDATE {b}.EXP009 SET HNPALT = ?, HPDSTT = ? WHERE RSOC = ? AND HNOBL = ?", ct, Math.Min(f.Palettes, 99),
                        Math.Min(99999, poidsCartons + f.Palettes * f.PoidsPalette), societe, f.NumeroBl) == 0)
                    throw new RegleMetierException($"Le BL {f.NumeroBl} n'existe plus.");
                await EcrireTextePiedAsync(cnx, tx, societe, f.NumeroBl, f.TextePied, ct);
            }
            await tx.CommitAsync(ct);
            valide = true;
        }
        finally
        {
            if (!valide) await tx.RollbackAsync(CancellationToken.None);
        }
    }
}
