using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Lot pris pour une ligne (EXP016, mouvement A700F)
internal sealed record LotPris(int Depot, string Lot, string Emplacement, int Quantite);

// Ligne du BL telle qu'elle sera écrite (EXP015), avec ses lots ; ligne de texte : article vide, quantité 0
internal sealed record LigneEcriteBl(
    int LigneCommande, string CodeLigne, string Article, string Indice, string Designation, string Designation2, string Designation3,
    int Quantite, int Colis, int EmballagePar, string TypeEmballage, int ColisIncomplet, int Mecompte, decimal PoidsUc, string Etat,
    bool Solder, bool GereEnStock, string TypeStock, bool ParEmplacement, string Famille, string SousFamille, IReadOnlyList<LotPris> Lots);

// En-tête et élément du BL calculés par le service
// Soldes : lignes de commande soldées sans être livrées (quantité 0, « soldé » coché)
internal sealed record BlAEcrire(
    SaisieBl Saisie, DossierBl Dossier, ParametresBl Parametres, string TypeBl, IReadOnlyList<LigneEcriteBl> Lignes, int Poids, int Colis,
    IReadOnlyList<int> Soldes, bool DepassementAccepte);

internal sealed partial class SaisieBlDb2
{
    // Création d'un BL (cahier des charges § 8) : verrou du dossier (PRTLCK, hors transaction, le temps de la validation),
    // puis une transaction : contrôle du stock de tout le BL (CTRPOSSTK / CTRPOSEMP), n° de BL, EXP009, EXP010, EXP012,
    // EXP015, EXP016, mouvements A700F, NEG100, OPR015 / OPR010, AB001 / DOS040. Après la validation : recalcul du stock
    // (STOREVNAV) et audit EVE200 (table non journalisée).
    public async Task<int> CreerAsync(int societe, string profil, BlAEcrire bl, CancellationToken ct)
    {
        var b = Bib;
        var s = bl.Saisie;
        var d = bl.Dossier;
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var verrou = CleVerrouDossier(societe, d.Dossier, d.Cle);
        await PoserVerrouAsync(cnx, verrou, profil, ct);
        int numero;
        try
        {
            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                await VerifierResteAsync(cnx, tx, societe, bl, ct);
                await ControlerStockBlAsync(cnx, tx, societe, s.DateExpedition, bl.Lignes, ct);
                numero = await NumeroBlAsync(cnx, tx, societe, s.Enseigne, bl.Parametres.NumerotationParEnseigne, ct);
                var recepisse = s.Enseigne == "" ? await CompteurAsync(cnx, tx, societe, "TR", ct) : 0;
                await EcrireEnteteAsync(cnx, tx, societe, profil, numero, recepisse, bl, ct);
                await EcrireLignesAsync(cnx, tx, societe, profil, numero, bl, ct);
                await MettreAJourCommandeAsync(cnx, tx, societe, profil, numero, bl, ct);
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
            await LeverVerrouAsync(cnx, verrou, profil);
        }

        // Après la validation : recalcul du stock de chaque article / type de stock (dépôt principal) et audit
        foreach (var (typeStock, article, indice) in bl.Lignes.Where(l => l.GereEnStock && l.Lots.Count > 0)
                     .Select(l => (l.TypeStock, l.Article, l.Indice)).Distinct())
            await RecalculerStockAsync(cnx, societe, typeStock, bl.Parametres.DepotPrincipal, article, indice);
        await AuditAsync(cnx, societe, profil, numero, "CR");
        return numero;
    }

    // ---------- Verrou applicatif du dossier (PRTLCK), vu par les écrans AS/400 ----------
    // Clé unique de PRTLCK (PLFIC, PLCLE) : l'insertion échoue si le verrou est déjà posé, même par une validation simultanée
    private async Task PoserVerrouAsync(OdbcConnection cnx, string cle, string profil, CancellationToken ct)
    {
        try
        {
            await Executer(cnx, null, "INSERT INTO {b}.PRTLCK (PLFIC, PLCLE, PLPROF) VALUES ('DOSSIER', ?, ?)", ct, cle, profil);
        }
        catch (OdbcException ex) when (ex.Errors.Count > 0 && ex.Errors[0].SQLState == "23505")
        {
            var detenteur = "un autre poste";
            await using (var rd = await Lire(cnx, "SELECT PLPROF FROM {b}.PRTLCK WHERE PLFIC = 'DOSSIER' AND PLCLE = ?", ct, cle))
                if (await rd.ReadAsync(ct)) detenteur = rd.Texte(0);
            throw new RegleMetierException($"Ce dossier est en cours de modification par {detenteur}.");
        }
    }

    // Reste à livrer relu dans la transaction (lignes verrouillées) : diminué depuis l'ouverture et dépassé → à confirmer
    private async Task VerifierResteAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, BlAEcrire bl, CancellationToken ct)
    {
        if (bl.DepassementAccepte) return;
        var d = bl.Dossier;
        var messages = new List<string>();
        foreach (var l in bl.Saisie.Lignes.Where(x => !x.Texte && x.Quantite > 0 && x.NumeroLigneCommande > 0))
        {
            await using var rd = await Lire(cnx, tx, """
                SELECT DNQTEC - DNQTEL FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, d.Dossier, d.Cle, l.NumeroLigneCommande);
            if (!await rd.ReadAsync(ct)) continue;
            var reste = Math.Max(0, rd.Entier(0));
            if (reste < l.Reste && l.Quantite > reste)
                messages.Add($"Article {l.Article} : le reste à livrer est passé de {l.Reste:#,##0} à {reste:#,##0} (autre BL validé entre-temps), " +
                             $"vous livrez {l.Quantite:#,##0}.");
        }
        if (messages.Count > 0) throw new DepassementResteException(string.Join("\n", messages));
    }

    private async Task LeverVerrouAsync(OdbcConnection cnx, string cle, string profil)
    {
        try
        {
            await Executer(cnx, null, "DELETE FROM {b}.PRTLCK WHERE PLFIC = 'DOSSIER' AND PLCLE = ? AND PLPROF = ?", CancellationToken.None,
                cle, profil);
        }
        catch (OdbcException)
        {
            // verrou orphelin : se supprime comme ceux des écrans AS/400
        }
    }

    // ---------- Stock : contrôle de la variation totale du BL par lot / emplacement, dans la transaction (§ 8.1) ----------
    // Les positions article (AK00F, journalisée, clé unique) sont d'abord verrouillées, dans un ordre fixe : deux BL qui prennent
    // le même article passent l'un après l'autre, le second contrôle voit les sorties du premier.
    private async Task ControlerStockBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, DateOnly date,
        IReadOnlyList<LigneEcriteBl> lignes, CancellationToken ct)
    {
        foreach (var (typeStock, depot, article, indice) in lignes.Where(l => l.GereEnStock)
                     .SelectMany(l => l.Lots.Select(p => (l.TypeStock, p.Depot, l.Article, l.Indice))).Distinct()
                     .OrderBy(x => x.TypeStock, StringComparer.Ordinal).ThenBy(x => x.Depot).ThenBy(x => x.Article, StringComparer.Ordinal)
                     .ThenBy(x => x.Indice, StringComparer.Ordinal))
            await Executer(cnx, tx, """
                UPDATE {b}.AK00F SET PRREF = PRREF WHERE RSOC = ? AND PRTSTO = ? AND PRDEP = ? AND PRREF = ? AND PRIND = ?
                """, ct, societe, typeStock, depot, article, indice);

        var variations = lignes.Where(l => l.GereEnStock)
            .SelectMany(l => l.Lots.Select(p => (l.TypeStock, l.ParEmplacement, p.Depot, l.Article, l.Indice, p.Lot, p.Emplacement, p.Quantite)))
            .GroupBy(x => (x.TypeStock, x.ParEmplacement, x.Depot, x.Article, x.Indice, x.Lot, x.Emplacement));
        foreach (var v in variations)
        {
            var k = v.Key;
            var retour = await ControlerStockAsync(cnx, tx, societe, k.TypeStock, k.ParEmplacement, k.Depot, k.Article, k.Indice, k.Lot,
                k.Emplacement, date, -v.Sum(x => x.Quantite), ct);
            if (retour != 0)
                throw new RegleMetierException(MessageStock(k.Article, k.Lot, k.Emplacement, k.Depot, retour));
        }
    }

    internal static string MessageStock(string article, string lot, string emplacement, int depot, int retour) =>
        retour < 0
            ? $"Contrôle du stock impossible : article {article}, lot {lot}."
            : $"Stock insuffisant : article {article}, lot {lot}{(emplacement == "" ? "" : $", emplacement {emplacement}")}, dépôt {depot} — " +
              $"le stock deviendrait négatif le {BaseDb2.DateAAAAMMJJ(retour):dd/MM/yyyy}.";

    // ---------- N° de BL (§ 8.2) : incrément dans la transaction (PRT320F par enseigne, sinon AA00083 « NB ») ; un n° déjà
    // utilisé (compteur en retard sur les données) est sauté ----------
    private async Task<int> NumeroBlAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string enseigne, bool parEnseigne,
        CancellationToken ct)
    {
        for (var essai = 0; essai < 1000; essai++)
        {
            int numero;
            if (parEnseigne)
            {
                if (await Executer(cnx, tx, "UPDATE {b}.PRT320F SET ENDNBL = ENDNBL + 1 WHERE RSOC = ? AND RENSEI = ?", ct, societe, enseigne) == 0)
                    throw new RegleMetierException($"Pas de compteur de BL pour l'enseigne « {enseigne} » (PRT320F).");
                await using var rd = await Lire(cnx, tx, "SELECT ENDNBL FROM {b}.PRT320F WHERE RSOC = ? AND RENSEI = ?", ct, societe, enseigne);
                await rd.ReadAsync(ct);
                numero = rd.Entier(0);
            }
            else numero = await CompteurAsync(cnx, tx, societe, "NB", ct);
            if (numero > 999999) throw new RegleMetierException("Le compteur des BL a atteint 999999.");
            await using var existe = await Lire(cnx, tx, "SELECT 1 FROM {b}.EXP009 WHERE RSOC = ? AND HNOBL = ?", ct, societe, numero);
            if (!await existe.ReadAsync(ct)) return numero;
        }
        throw new RegleMetierException("Aucun n° de BL libre trouvé : vérifiez le compteur des BL.");
    }

    private async Task<int> CompteurAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string table, CancellationToken ct)
    {
        if (await Executer(cnx, tx, "UPDATE {b}.AA00083 SET TNUM = TNUM + 1 WHERE RSOC = ? AND RTABLE = ? AND RARG = ' '", ct, societe, table) == 0)
            throw new RegleMetierException($"Pas de compteur « {table} » dans la table AA00083.");
        await using var rd = await Lire(cnx, tx, "SELECT TNUM FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = ? AND RARG = ' '", ct, societe, table);
        await rd.ReadAsync(ct);
        return (int)rd.Nombre(0);
    }

    // ---------- EXP009 (en-tête), EXP010 (élément), EXP012 (texte de pied) ----------
    private async Task EcrireEnteteAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, int numero, int recepisse,
        BlAEcrire bl, CancellationToken ct)
    {
        var s = bl.Saisie;
        var d = bl.Dossier;
        var a = s.Adresse;
        var p = bl.Parametres;
        await Executer(cnx, tx, """
            INSERT INTO {b}.EXP009 (RSOC, HNOBL, HDEXAA, HDEXMM, HDEXJJ, HFJ, HRS, HRUE1, HRUE2, HCP, HVILLE, HOBS, HGENE, HCLIE,
                HNCOLT, HNPALT, HPDSTT, HTRAN, HTYPL, HTYPP, HNBEPL, HEDBL, HEDETP, HENS, HBART, HNOREC, HBLGRP, HPROF, HDPT, HREV,
                HNADRE, HPAYS, HOBSIN, HOBS2, HOBS3, HSTR, HMENTI)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?, ?, 'N', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, ct, societe, numero, s.DateExpedition.Year, s.DateExpedition.Month, s.DateExpedition.Day, a.FormeJuridique,
            a.RaisonSociale, a.Rue1, a.Rue2, a.CodePostal, a.Ville, s.Observation1, d.CompteGeneral, d.CompteClient, bl.Colis,
            s.Palettes, bl.Poids, s.Transporteur, s.TypeLivraison, s.TypePort, Oui(p.EditionBl, "N"), Oui(p.EditionPalettes, "N"),
            s.Enseigne, s.Bareme, recepisse, profil, a.Departement, d.Revendeur == "O" ? "O" : "N", a.Numero, a.Pays, s.Incoterm,
            s.Observation2, s.Observation3, d.SousTraitanceSynchronisee ? "O" : "N", s.Mention);

        await Executer(cnx, tx, """
            INSERT INTO {b}.EXP010 (RSOC, HNOBL, HNUM, HNODOS, HCLEF, HETABL, HQTELI, HNUC1, HQTUC1, HUCDT1, HNUC2, HQTUC2, HUCDT2, HNBPAL,
                HPDST, HETALI, HTYPBL, HEDETC, HBLFAC, HGENE, HCLIE, HREFCD, HDCDEA, HDCDEM, HDCDEJ, HNODEV, HNOHYP, HNOMOD, HREFIM,
                HREP, HNOFAC, HFACTU, HNOBLO, HNUMO, HGENF, HCLIF, HFACTO)
            VALUES (?, ?, 1, ?, ?, 'C', 0, 0, 0, 'C', 0, 0, 'P', ?, ?, 'T', ?, ?, 'N', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, 0, 0, ?, ?, ?)
            """, ct, societe, numero, d.Dossier, d.Cle, s.Palettes, bl.Poids, bl.TypeBl, Oui(p.EditionEtiquettes, "N"), d.CompteGeneral,
            d.CompteClient, s.ReferenceCommande, s.DateCommande?.Year ?? 0, s.DateCommande?.Month ?? 0, s.DateCommande?.Day ?? 0, d.Devis,
            d.Hypothese, d.Modele, d.RefImprime, s.Representant, s.Facturable ? "O" : "N", s.CompteGeneralFacture, s.CompteClientFacture,
            s.FacturationAutomatique ? "O" : "N");

        // Texte de pied : lignes non vides, en gardant les lignes vides intermédiaires
        var pied = s.TextePied.Select(x => x.Trim()).ToList();
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);
        for (var i = 0; i < pied.Count; i++)
            await Executer(cnx, tx, "INSERT INTO {b}.EXP012 (RSOC, HNOBL, HTNLIG, HTTXT) VALUES (?, ?, ?, ?)", ct, societe, numero, i + 1,
                Tronquer(pied[i], 40));
    }

    // ---------- EXP015 (lignes), EXP016 (lots), A700F (sorties d'expédition, sans prix : valorisées par le recalcul) ----------
    private async Task EcrireLignesAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, int numero, BlAEcrire bl,
        CancellationToken ct)
    {
        var s = bl.Saisie;
        var d = bl.Dossier;
        var maintenant = DateTime.Now;
        var no = 0;
        foreach (var l in bl.Lignes)
        {
            no++;
            await Executer(cnx, tx, """
                INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                    DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                VALUES (?, ?, 1, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """, ct, societe, numero, no, l.Article, Tronquer(l.Designation, 40), l.Quantite, l.Indice, l.Colis, l.LigneCommande,
                l.Article == "" ? "" : l.Etat, l.CodeLigne, Tronquer(l.Designation2, 40), Tronquer(l.Designation3, 40), l.EmballagePar,
                l.TypeEmballage, l.ColisIncomplet, l.Mecompte, Math.Round(l.PoidsUc, 2));

            if (!l.GereEnStock) continue;
            // EXP016 : un enregistrement par lot / emplacement (clé unique)
            foreach (var g in l.Lots.GroupBy(x => (x.Lot, x.Emplacement)))
                await Executer(cnx, tx, """
                    INSERT INTO {b}.EXP016 (RSOC, DNBL, DNUM, DNLIG, DNLOT, DNQTPL, DUC1, DUC2, DNEMP) VALUES (?, ?, 1, ?, ?, ?, 0, 0, ?)
                    """, ct, societe, numero, no, g.Key.Lot, g.Sum(x => x.Quantite), g.Key.Emplacement);
            foreach (var p in l.Lots)
                await Executer(cnx, tx, """
                    INSERT INTO {b}.A700F (RSOC, MVBON, MVAA, MVMM, MVJJ, MVTSTO, MVDEP, MVCMVT, MVSENS, MVLOT, MVDOS, MVDCLE, MVREF, MVFAM,
                        MVSFAM, MVQTUS, MVUS, MVPRO, MVIND, MVBL, MVELBL, MVLIGB, MVEMP, MVDATS, MVCHRO)
                    VALUES (?, ' ', ?, ?, ?, ?, ?, 'EX', 'S', ?, ?, ?, ?, ?, ?, ?, 'UN', ?, ?, ?, 1, ?, ?, ?, ?)
                    """, ct, societe, s.DateExpedition.Year, s.DateExpedition.Month, s.DateExpedition.Day, l.TypeStock, p.Depot, p.Lot,
                    d.Dossier, d.Cle, l.Article, l.Famille, l.SousFamille, p.Quantite, profil, l.Indice, numero, no, p.Emplacement,
                    BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant.Hour * 100 + maintenant.Minute);
        }
    }

    // ---------- Commande (§ 8.5) : NEG100 (livré, reste, soldé), OPR015 / OPR010, AB001 (quantité livrée, solde) ----------
    private async Task MettreAJourCommandeAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, int numero,
        BlAEcrire bl, CancellationToken ct)
    {
        var d = bl.Dossier;
        // Lignes soldées sans livraison
        foreach (var ligne in bl.Soldes)
            await Executer(cnx, tx, "UPDATE {b}.NEG100 SET DNSOLD = 'O' WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct,
                societe, d.Dossier, d.Cle, ligne);
        foreach (var l in bl.Lignes.Where(x => x.LigneCommande > 0 && x.Article != ""))
        {
            await Executer(cnx, tx, """
                UPDATE {b}.NEG100 SET DNQTEL = DNQTEL + ?, DNQTER = DNQTEC - (DNQTEL + ?),
                       DNSOLD = CASE WHEN ? = 'O' OR DNQTEC - (DNQTEL + ?) <= 0 THEN 'O' ELSE ' ' END
                WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                """, ct, l.Quantite, l.Quantite, l.Solder ? "O" : "N", l.Quantite, societe, d.Dossier, d.Cle, l.LigneCommande);
            // Ligne d'OPR traitée : livrée, même partiellement (décision du 15/10)
            await Executer(cnx, tx, """
                UPDATE {b}.OPR015 SET ODQTEX = ODQTEX + ?, ODBL = ?, ODLIVR = 'O'
                WHERE RSOC = ? AND ODNCDE = ? AND ODNCLE = ? AND ODDLIG = ? AND ODSTAT = '3' AND ODLIVR <> 'O'
                """, ct, l.Quantite, numero, societe, d.Dossier, d.Cle, l.LigneCommande);
        }
        // OPR entièrement livrés
        await Executer(cnx, tx, """
            UPDATE {b}.OPR010 o SET OESTAT = '4'
            WHERE o.RSOC = ? AND o.OESTAT <> '4'
              AND EXISTS (SELECT 1 FROM {b}.OPR015 x WHERE x.RSOC = o.RSOC AND x.ODNUM = o.OENUM AND x.ODBL = ?)
              AND NOT EXISTS (SELECT 1 FROM {b}.OPR015 x WHERE x.RSOC = o.RSOC AND x.ODNUM = o.OENUM AND x.ODLIVR <> 'O')
            """, ct, societe, numero);

        var total = bl.Lignes.Where(x => x.Article != "").Sum(x => x.Quantite);
        if (d.Type == "4")
            await Executer(cnx, tx, "UPDATE {b}.AB001 SET CCQTEL = CCQTEL + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct, total, societe,
                d.Dossier, d.Cle);

        // Dossier soldé si toutes ses lignes de commande (articles réels, hors « 999… ») le sont
        int restantes;
        await using (var rd = await Lire(cnx, tx, """
            SELECT COUNT(*) FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNART <> ' ' AND DNART NOT LIKE '999%'
              AND DNSOLD <> 'O'
            """, ct, societe, d.Dossier, d.Cle))
        {
            await rd.ReadAsync(ct);
            restantes = rd.Entier(0);
        }
        if (restantes > 0) return;
        var maintenant = DateTime.Now;
        if (await Executer(cnx, tx, """
                UPDATE {b}.AB001 SET CCTAGS = 'D', CCSLDA = ?, CCSLDM = ?, CCSLDJ = ?
                WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTAGS <> 'D'
                """, ct, maintenant.Year, maintenant.Month, maintenant.Day, societe, d.Dossier, d.Cle) == 0) return;
        var initiales = "";
        await using (var rd = await Lire(cnx, tx, "SELECT ACINI FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil))
            if (await rd.ReadAsync(ct)) initiales = rd.Texte(0);
        await Executer(cnx, tx, """
            INSERT INTO {b}.DOS040 (RSOC, RDOSS, HICLE, HIGEN, HICLI, HIINAM, HITYPM, HIDTMA, HIDTMM, HIDTMJ, HIHEUR)
            VALUES (?, ?, ?, ?, ?, ?, 'SO', ?, ?, ?, ?)
            """, ct, societe, d.Dossier, d.Cle, d.CompteGeneral, d.CompteClient, initiales, maintenant.Year, maintenant.Month, maintenant.Day,
            maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
    }

    // ---------- Après la validation ----------

    // Recalcul du stock par STOREVNAV (MAJPMP, STOCK04, STOCK03), comme les applications OPR et PreBL. Paramètre de 256 car. :
    // société (4), type de stock (2), dépôt (3), article (15), indice (2). Ne fait pas échouer un BL déjà validé.
    private async Task RecalculerStockAsync(OdbcConnection cnx, int societe, string typeStock, int depot, string article, string indice)
    {
        var parametre = $"{societe:0000}{typeStock,-2}{depot:000}{article,-15}{indice,-2}".PadRight(256);
        var commande = $"CALL PGM({BaseDb2.Bibliotheque(config["Db2:BibliothequePgm"] ?? "NP11P")}/STOREVNAV) PARM('{parametre.Replace("'", "''")}')";
        try
        {
            await Executer(cnx, null, "CALL QSYS2.QCMDEXC(?)", CancellationToken.None, commande);
        }
        catch (OdbcException)
        {
            // positions recalculées au prochain passage des programmes de stock
        }
    }

    // Audit EVE200 (EVE400, type de donnée « BL ») : CR création, AE ajout de dossier, MO modification, SU suppression
    private async Task AuditAsync(OdbcConnection cnx, int societe, string profil, int numero, string type)
    {
        for (var essai = 0; essai < 3; essai++)
            try
            {
                await Executer(cnx, null, """
                    INSERT INTO {b}.EVE200 (AMLIG, RSOC, AMTYPE, AMDATE, AMPROF, AMBL)
                    SELECT COALESCE(MAX(AMLIG), 0) + 1, CAST(? AS NUMERIC(4, 0)), CAST(? AS CHAR(2)), CURRENT TIMESTAMP,
                           CAST(? AS CHAR(10)), CAST(? AS NUMERIC(6, 0)) FROM {b}.EVE200
                    """, CancellationToken.None, societe, type, profil, numero);
                return;
            }
            catch (OdbcException)
            {
                // audit non bloquant
            }
    }

    private async Task<int> Executer(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql.Replace("{b}", Bib);
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Oui(string valeur, string defaut) => valeur == "" ? defaut : valeur;
    private static string Tronquer(string v, int n) => v.Length <= n ? v : v[..n];
}
