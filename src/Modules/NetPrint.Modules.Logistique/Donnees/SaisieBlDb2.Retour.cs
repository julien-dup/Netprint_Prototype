using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// BL d'origine d'un retour, tel qu'il est enregistré
internal sealed record OrigineRetour(
    int NumeroBl, DateOnly? Date, string Enseigne, bool SousTraitanceSynchronisee, int CompteGeneral, int CompteClient, string NomClient,
    int Transporteur, IReadOnlyList<ElementOrigine> Elements);

// Fabrication : HTYPBL = 1 (quantité de l'élément, plan de livraison EXP020 de la première ligne)
internal sealed record ElementOrigine(
    int Element, string Etat, int Dossier, string Cle, string TypeDossier, string Designation, bool Fabrication, bool SousTraite,
    int QuantiteLivree, int DejaRetourne, int NombreRetours, int CompteGeneralFacture, int CompteClientFacture, int LignePlan,
    IReadOnlyList<LigneOrigine> Lignes);

internal sealed record LigneOrigine(
    int LigneBl, int LigneCommande, string CodeLigne, string Article, string Indice, string Designation, string Designation2,
    string Designation3, int EmballagePar, string TypeEmballage, IReadOnlyList<LotOrigine> Lots);

// Lot livré : lot EXP016, lot des palettes (n° d'OF + clé) ou, sans lot (article non stocké), la ligne entière (Lot vide).
// Sortie d'origine (A700F EX) : type de stock, prix, famille ; Stocke = une sortie de stock existe.
internal sealed record LotOrigine(
    string Lot, string Emplacement, int Quantite, int DejaRetourne, bool Stocke, string TypeStock, decimal Prix, string Famille,
    string SousFamille, IReadOnlyList<PaletteOrigine> Palettes);

internal sealed record PaletteOrigine(string Sscc, int Quantite, int Of, string CleOf, bool Retournee);

// Retour calculé par le service : quantités par élément / ligne / lot / palette, dépôt et emplacement de retour
internal sealed record RetourAEcrire(
    OrigineRetour Origine, SaisieRetour Saisie, int DepotPrincipal, int DepotRetour, bool NumerotationParEnseigne,
    IReadOnlyList<ElementAEcrire> Elements);

internal sealed record ElementAEcrire(ElementOrigine Origine, int QuantiteFabrication, IReadOnlyList<LigneAEcrire> Lignes);

internal sealed record LigneAEcrire(LigneOrigine Origine, IReadOnlyList<LotAEcrire> Lots)
{
    public int Quantite => Lots.Sum(l => l.Quantite);
}

// Emplacement : emplacement de retour si le type de stock est géré par emplacement, sinon vide
internal sealed record LotAEcrire(LotOrigine Origine, int Quantite, string Emplacement, IReadOnlyList<(PaletteOrigine Palette, int Quantite)> Palettes);

// Retour enregistré, pour sa suppression
internal sealed record RetourEnregistre(int NumeroBl, int BlOrigine, bool Facture, IReadOnlyList<(int Element, int Dossier, string Cle, string TypeDossier,
    bool Fabrication, int Quantite, int LignePlan)> Elements, IReadOnlyList<string> PalettesNonDisponibles, IReadOnlyList<string> TypesStock);

internal sealed partial class SaisieBlDb2
{
    // ---------- Lecture du BL d'origine ----------
    public async Task<OrigineRetour?> OrigineRetourAsync(int societe, int numeroBl, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        OrigineRetour? entete = null;
        await using (var rd = await Lire(cnx, """
            SELECT h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HENS, h.HSTR, h.HGENE, h.HCLIE, COALESCE(c.CNOM, h.HRS), h.HTRAN
            FROM {b}.EXP009 h LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numeroBl))
        {
            if (!await rd.ReadAsync(ct)) return null;
            entete = new OrigineRetour(numeroBl, BaseDb2.Date(rd.Entier(0), rd.Entier(1), rd.Entier(2)), rd.Texte(3), rd.Texte(4) == "O",
                rd.Entier(5), rd.Entier(6), rd.Texte(7), rd.Entier(8), []);
        }

        var elements = new List<(int Element, string Etat, int Dossier, string Cle, string Type, string Designation, bool Fabrication,
            bool SousTraite, int Livree, int Retourne, int Retours, int GenF, int CliF, int LignePlan)>();
        await using (var rd = await Lire(cnx, """
            SELECT e.HNUM, e.HETABL, e.HNODOS, e.HCLEF, COALESCE(a.CCTYPC, ''), COALESCE(a.CCDES1, ''), e.HTYPBL, COALESCE(a.CCOP10, ''),
                   e.HQTELI, COALESCE((SELECT SUM(-r.HQTELI) FROM {b}.EXP010 r WHERE r.RSOC = e.RSOC AND r.HNOBLO = e.HNOBL
                                       AND r.HNUMO = e.HNUM AND r.HETABL = 'R'), 0),
                   (SELECT COUNT(*) FROM {b}.EXP010 r WHERE r.RSOC = e.RSOC AND r.HNOBLO = e.HNOBL AND r.HNUMO = e.HNUM AND r.HETABL = 'R'),
                   e.HGENF, e.HCLIF,
                   COALESCE((SELECT MIN(l.DNUMLC) FROM {b}.EXP015 l WHERE l.RSOC = e.RSOC AND l.DNBL = e.HNOBL AND l.DNUM = e.HNUM
                             AND l.DNUMLC <> 0), 0)
            FROM {b}.EXP010 e LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = e.RSOC AND a.RDOSS = e.HNODOS AND a.CCDCLE = e.HCLEF
            WHERE e.RSOC = ? AND e.HNOBL = ? ORDER BY e.HNUM
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                elements.Add((rd.Entier(0), rd.Texte(1), rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Entier(6) == 1, rd.Texte(7) == "O",
                    rd.Entier(8), rd.Entier(9), rd.Entier(10), rd.Entier(11), rd.Entier(12), rd.Entier(13)));

        var lignes = new List<(int Element, LigneOrigine Ligne, int Quantite)>();
        await using (var rd = await Lire(cnx, """
            SELECT DNUM, DNLIG, DNUMLC, DCLIG, DART, DIND, DDES, DDES2, DDES3, DEMBP1, DTYEMB, DQTE
            FROM {b}.EXP015 WHERE RSOC = ? AND DNBL = ? AND DART <> ' ' ORDER BY DNUM, DNLIG
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                lignes.Add((rd.Entier(0), new LigneOrigine(rd.Entier(1), rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7),
                    rd.Texte(8), rd.Entier(9), rd.Texte(10), []), rd.Entier(11)));

        var lots = new List<(int Element, int Ligne, string Lot, string Emplacement, int Quantite)>();
        await using (var rd = await Lire(cnx, "SELECT DNUM, DNLIG, DNLOT, DNEMP, DNQTPL FROM {b}.EXP016 WHERE RSOC = ? AND DNBL = ?", ct,
                         societe, numeroBl))
            while (await rd.ReadAsync(ct)) lots.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Entier(4)));

        // Sorties d'expédition d'origine : type de stock, prix (valorisation du retour), famille
        var sorties = new List<(int Element, int Ligne, string Lot, string TypeStock, decimal Prix, string Famille, string SousFamille)>();
        await using (var rd = await Lire(cnx, """
            SELECT MVELBL, MVLIGB, MVLOT, MVTSTO, MAX(MVPUUS), MAX(MVFAM), MAX(MVSFAM) FROM {b}.A700F
            WHERE RSOC = ? AND MVBL = ? AND MVCMVT = 'EX' AND MVSENS = 'S' GROUP BY MVELBL, MVLIGB, MVLOT, MVTSTO
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                sorties.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Nombre(4), rd.Texte(5), rd.Texte(6)));

        var palettes = new List<(int Element, int Ligne, PaletteOrigine Palette)>();
        await using (var rd = await Lire(cnx, """
            SELECT ELEMENT_BL, NUMERO_LIGNE_BL, NUMERO_SSCC, QUANTITE_EX, NUMERO_OF, CLE_OF, STATUT FROM {b}.PROD_ENTITE
            WHERE SOCIETE = ? AND NUMERO_BL = ? AND STATUT IN ('5', '7') ORDER BY NUMERO_SSCC
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                palettes.Add((rd.Entier(0), rd.Entier(1), new PaletteOrigine(rd.Texte(2), (int)rd.Nombre(3), rd.Entier(4), rd.Texte(5), rd.Texte(6) == "7")));

        // Déjà retourné, par élément d'origine / ligne de commande / article / lot (retours enregistrés : lots EXP016 ou lignes)
        var retoursLots = new List<(int Element, int LigneCommande, string Article, string Lot, int Quantite)>();
        await using (var rd = await Lire(cnx, """
            SELECT r.HNUMO, l.DNUMLC, l.DART, COALESCE(d.DNLOT, ''), SUM(-COALESCE(d.DNQTPL, l.DQTE))
            FROM {b}.EXP010 r
            JOIN {b}.EXP015 l ON l.RSOC = r.RSOC AND l.DNBL = r.HNOBL AND l.DNUM = r.HNUM AND l.DART <> ' '
            LEFT OUTER JOIN {b}.EXP016 d ON d.RSOC = l.RSOC AND d.DNBL = l.DNBL AND d.DNUM = l.DNUM AND d.DNLIG = l.DNLIG
            WHERE r.RSOC = ? AND r.HNOBLO = ? AND r.HETABL = 'R'
            GROUP BY r.HNUMO, l.DNUMLC, l.DART, COALESCE(d.DNLOT, '')
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) retoursLots.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Entier(4)));

        var resultat = new List<ElementOrigine>();
        foreach (var e in elements)
        {
            var lignesElement = new List<LigneOrigine>();
            foreach (var (_, l, quantiteLigne) in lignes.Where(x => x.Element == e.Element))
            {
                var deLaLigne = palettes.Where(p => p.Element == e.Element && p.Ligne == l.LigneBl).Select(p => p.Palette).ToList();
                var lotsLigne = new List<LotOrigine>();
                int Deja(string lot) => retoursLots.Where(x => x.Element == e.Element && x.LigneCommande == l.LigneCommande && x.Article == l.Article
                                                              && x.Lot == lot).Sum(x => x.Quantite);
                (bool Stocke, string TypeStock, decimal Prix, string Famille, string SousFamille) Sortie(string lot)
                {
                    var s = sorties.Where(x => x.Element == e.Element && x.Ligne == l.LigneBl).ToList();
                    var t = s.FirstOrDefault(x => x.Lot == lot);
                    if (t == default) t = s.FirstOrDefault();
                    return t == default ? (false, "", 0m, "", "") : (true, t.TypeStock, t.Prix, t.Famille, t.SousFamille);
                }
                if (deLaLigne.Count > 0)
                {
                    // Ligne livrée en palettes : un lot par n° d'OF + clé, retour palette par palette (ConsultationBL)
                    foreach (var g in deLaLigne.GroupBy(p => $"{p.Of:00000}{p.CleOf}"))
                    {
                        var s = Sortie(g.Key);
                        var emplacement = lots.FirstOrDefault(x => x.Element == e.Element && x.Ligne == l.LigneBl && x.Lot == g.Key).Emplacement ?? "";
                        lotsLigne.Add(new LotOrigine(g.Key, emplacement, g.Sum(p => p.Quantite), Deja(g.Key), s.Stocke, s.TypeStock, s.Prix, s.Famille,
                            s.SousFamille, g.ToList()));
                    }
                }
                else
                {
                    // Un lot livré sur plusieurs emplacements se retourne en une fois (le retour va à l'emplacement de retour)
                    foreach (var g in lots.Where(x => x.Element == e.Element && x.Ligne == l.LigneBl).GroupBy(x => x.Lot))
                    {
                        var s = Sortie(g.Key);
                        var emplacements = g.Select(x => x.Emplacement).Distinct().ToList();
                        lotsLigne.Add(new LotOrigine(g.Key, emplacements.Count == 1 ? emplacements[0] : "", g.Sum(x => x.Quantite), Deja(g.Key), s.Stocke,
                            s.TypeStock, s.Prix, s.Famille, s.SousFamille, []));
                    }
                    if (lotsLigne.Count == 0 && quantiteLigne > 0)
                        lotsLigne.Add(new LotOrigine("", "", quantiteLigne, Deja(""), false, "", 0, "", "", []));
                }
                lignesElement.Add(l with { Lots = lotsLigne });
            }
            resultat.Add(new ElementOrigine(e.Element, e.Etat, e.Dossier, e.Cle, e.Type, e.Designation, e.Fabrication, e.SousTraite, e.Livree,
                e.Retourne, e.Retours, e.GenF, e.CliF, e.LignePlan, lignesElement));
        }
        return entete with { Elements = resultat };
    }

    // Dépôt de retour (EXP005.FDEPRE, hors dépôt principal), son emplacement par défaut (B100F.DEMPDFT) et l'emplacement par
    // défaut du mouvement « RE » (AH00F.CEMPD)
    public async Task<(int DepotRetour, string EmplacementDepotRetour, string EmplacementRe)> ParametresRetourAsync(int societe, int depotPrincipal,
        CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var depot = 0;
        var emplacementDepot = "";
        var emplacementRe = "";
        await using (var rd = await Lire(cnx, """
            SELECT x.FDEPRE, COALESCE(d.DEMPDFT, '') FROM {b}.EXP005 x
            LEFT OUTER JOIN {b}.B100F d ON d.RSOC = x.RSOC AND d.RDEP = x.FDEPRE
            WHERE x.RSOC = ? FETCH FIRST 1 ROW ONLY
            """, ct, societe))
            if (await rd.ReadAsync(ct) && rd.Entier(0) != 0 && rd.Entier(0) != depotPrincipal) (depot, emplacementDepot) = (rd.Entier(0), rd.Texte(1));
        await using (var rd = await Lire(cnx, "SELECT CEMPD FROM {b}.AH00F WHERE RSOC = ? AND RCMVT = 'RE'", ct, societe))
            if (await rd.ReadAsync(ct)) emplacementRe = rd.Texte(0);
        return (depot, emplacementDepot, emplacementRe);
    }

    public async Task<bool> ClientExisteAsync(int societe, int general, int client, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT 1 FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", ct, societe, general, client);
        return await rd.ReadAsync(ct);
    }

    // ---------- Création du retour (EXP010S SPVAL / ConsultationBL Creation1BLTotal) ----------
    // Verrous BL d'origine et dossiers, puis une transaction : quantités et palettes relues, n° (compteur des BL, ou SYN005 en
    // sous-traitance synchronisée), EXP009 copié du BL d'origine, un élément EXP010 'R' par élément retourné (lien HNOBLO / HNUMO),
    // lignes EXP015 en négatif, lots EXP016, entrées A700F « RE » au dépôt principal valorisées au prix de la sortie d'origine puis
    // transfert « TD » vers le dépôt de retour, palettes (nouveau SSCC en stock, palette d'origine « retournée » 7), commande (NEG100)
    // et dossier désoldés, quantité livrée du dossier (fabrication, produit fini) et plan de livraison (fabrication). Après la
    // validation : recalcul du stock (STOREVNAV), audit « RM », message de synchronisation (sous-traitance synchronisée).
    public async Task<int> EnregistrerRetourAsync(int societe, string profil, RetourAEcrire r, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        var s = r.Saisie;
        var o = r.Origine;
        await using var cnx = await OuvrirAsync(ct);
        var verrouBl = CleVerrouBl(societe, o.NumeroBl);
        var dossiers = r.Elements.Where(e => e.Origine.Dossier != 0).Select(e => CleVerrouDossier(societe, e.Origine.Dossier, e.Origine.Cle))
            .Distinct().ToList();
        var poses = new List<string>();
        var recalculs = new List<(string TypeStock, int Depot, string Article, string Indice)>();
        int numero;
        await PoserVerrouAsync(cnx, "BL", verrouBl, profil, "Ce BL est en cours de modification par", ct);
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
                await VerifierRetourAsync(cnx, tx, societe, r, ct);
                numero = await NumeroRetourAsync(cnx, tx, societe, r, ct);
                var date = s.Date;
                await Executer(cnx, tx, """
                    INSERT INTO {b}.EXP009 (RSOC, HNOBL, HDEXAA, HDEXMM, HDEXJJ, HFJ, HRS, HRUE1, HRUE2, HCP, HVILLE, HOBS, HGENE, HCLIE,
                        HNCOLT, HNPALT, HPDSTT, HTRAN, HTYPL, HTYPP, HNBEPL, HEDBL, HDEDBA, HDEDBM, HDEDBJ, HEDETP, HDEEPA, HDEEPM, HDEEPJ,
                        HENS, HBART, HNOREC, HBLGRP, HPROF, HDPT, HREV, HNADRE, HPAYS, HOBSIN, HOBS2, HOBS3, HSTR, HMENTI)
                    SELECT RSOC, CAST(? AS INTEGER), CAST(? AS INTEGER), CAST(? AS INTEGER), CAST(? AS INTEGER), HFJ, HRS, HRUE1, HRUE2, HCP,
                        HVILLE, ' ', HGENE, HCLIE, CAST(? AS INTEGER), 0, CAST(? AS INTEGER), CAST(? AS INTEGER), ' ', ' ', 0, 'N', 0, 0, 0,
                        'N', 0, 0, 0, HENS, 1, 0, 'N', CAST(? AS VARCHAR(10)), HDPT, HREV, HNADRE, HPAYS, ' ', ' ', ' ', HSTR, ' '
                    FROM {b}.EXP009 WHERE RSOC = ? AND HNOBL = ?
                    """, ct, numero, date.Year, date.Month, date.Day, Math.Min(s.Colis, 99999), Math.Min(s.Poids, 99999), s.Transporteur, profil,
                    societe, o.NumeroBl);

                var element = 0;
                foreach (var e in r.Elements)
                {
                    element++;
                    await EcrireElementRetourAsync(cnx, tx, societe, profil, numero, element, r, e, ct);
                    recalculs.AddRange(e.Lignes.SelectMany(l => l.Lots.Where(x => x.Origine.Stocke && x.Quantite > 0)
                        .SelectMany(x => new[] { r.DepotPrincipal, r.DepotRetour }.Where(d => d != 0)
                            .Select(d => (x.Origine.TypeStock, d, l.Origine.Article, l.Origine.Indice)))));
                }
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
            await LeverVerrouAsync(cnx, "BL", verrouBl, profil);
        }

        foreach (var (typeStock, depot, article, indice) in recalculs.Distinct())
            await RecalculerStockAsync(cnx, societe, typeStock, depot, article, indice);
        await AuditAsync(cnx, societe, profil, numero, "RM");
        if (o.SousTraitanceSynchronisee)
            foreach (var e in r.Elements.Where(x => x.Origine.Dossier != 0 && x.Origine.TypeDossier is "3" or "4"))
                await MessageSynchronisationAsync(cnx, societe, e.Origine.Dossier, e.Origine.Cle, numero);
        return numero;
    }

    // Quantités relues sous verrou : un autre retour du même BL validé entre-temps, ou une palette déjà retournée
    private async Task VerifierRetourAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, RetourAEcrire r, CancellationToken ct)
    {
        foreach (var e in r.Elements)
        {
            // Retours de l'élément relus sous verrou : aucun nouveau depuis la lecture (sinon le contrôle des quantités est faux)
            await using var rd = await Lire(cnx, tx, """
                SELECT COALESCE(SUM(-r.HQTELI), 0), COUNT(*) FROM {b}.EXP010 r WHERE r.RSOC = ? AND r.HNOBLO = ? AND r.HNUMO = ? AND r.HETABL = 'R'
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, r.Origine.NumeroBl, e.Origine.Element);
            await rd.ReadAsync(ct);
            if (rd.Entier(0) != e.Origine.DejaRetourne || rd.Entier(1) != e.Origine.NombreRetours)
                throw new RegleMetierException("Un autre retour vient d'être enregistré sur ce BL : rouvrez le retour pour voir les quantités à jour.");
        }
        foreach (var (palette, _) in r.Elements.SelectMany(e => e.Lignes).SelectMany(l => l.Lots).SelectMany(x => x.Palettes))
        {
            await using var rd = await Lire(cnx, tx, """
                SELECT STATUT FROM {b}.PROD_ENTITE WHERE SOCIETE = ? AND NUMERO_SSCC = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, palette.Sscc);
            if (!await rd.ReadAsync(ct) || rd.Texte(0) != "5")
                throw new RegleMetierException($"La palette {palette.Sscc} n'est plus « expédiée » : elle a peut-être déjà été retournée.");
        }
    }

    // N° du retour : compteur de synchronisation (SYN005) si le BL d'origine est en sous-traitance synchronisée, sinon celui des BL
    private async Task<int> NumeroRetourAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, RetourAEcrire r, CancellationToken ct)
    {
        return r.Origine.SousTraitanceSynchronisee ? await NumeroSynchroAsync(cnx, tx, societe, ct)
            : await NumeroBlAsync(cnx, tx, societe, r.Origine.Enseigne, r.NumerotationParEnseigne, ct);
    }

    private async Task EcrireElementRetourAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, string profil, int numero, int element,
        RetourAEcrire r, ElementAEcrire e, CancellationToken ct)
    {
        var s = r.Saisie;
        var o = e.Origine;
        await Executer(cnx, tx, """
            INSERT INTO {b}.EXP010 (RSOC, HNOBL, HNUM, HNODOS, HCLEF, HETABL, HQTELI, HNUC1, HQTUC1, HUCDT1, HNUC2, HQTUC2, HUCDT2, HNBPAL,
                HPDST, HETALI, HNBEUC, HTYPBL, HEDETC, HDEECA, HDEECM, HDEECJ, HBLFAC, HGENE, HCLIE, HREFCD, HDCDEA, HDCDEM, HDCDEJ,
                HNODEV, HNOHYP, HNOMOD, HREFIM, HREP, HNOFAC, HNOELT, HDAFAA, HDAFAM, HDAFAJ, HTAG, HFOLAP, HFACTU, HNOBLO, HNUMO, HPDSU,
                HGENF, HCLIF, HFACTO)
            SELECT RSOC, CAST(? AS INTEGER), CAST(? AS INTEGER), HNODOS, HCLEF, 'R', CAST(? AS INTEGER), CAST(? AS INTEGER), 0, HUCDT1,
                0, 0, HUCDT2, 0, 0, ' ', 0, HTYPBL, 'N', 0, 0, 0, 'N', HGENE, HCLIE, HREFCD, HDCDEA, HDCDEM, HDCDEJ,
                HNODEV, HNOHYP, HNOMOD, HREFIM, HREP, 0, 0, 0, 0, 0, ' ', 0, CAST(? AS CHAR(1)), HNOBL, HNUM, 0,
                CAST(? AS INTEGER), CAST(? AS INTEGER), CAST(? AS CHAR(1))
            FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?
            """, ct, numero, element, -e.QuantiteFabrication, o.Fabrication && element == 1 ? -s.Colis : 0, s.Facturable ? "O" : "N",
            s.FacturationAutomatique ? s.CompteGeneralFacture : 0, s.FacturationAutomatique ? s.CompteClientFacture : 0,
            s.FacturationAutomatique ? "O" : "N", societe, r.Origine.NumeroBl, o.Element);

        // Texte : « Reprise Marchandises du BL n° … » (fabrication : date et quantités, comme EXP010S), ligne vide, motif
        var no = 0;
        if (o.Fabrication)
        {
            var d = r.Origine.Date;
            await LigneTexteRetourAsync(cnx, tx, societe, numero, element, ++no, $"Reprise Marchandises du BL n° {r.Origine.NumeroBl} du",
                $"{d:dd/MM/yy} pour une Qte livree de {o.QuantiteLivree}", $"Qte reprise : {e.QuantiteFabrication}, le {s.Date:dd/MM/yy}", ct);
        }
        else
            await LigneTexteRetourAsync(cnx, tx, societe, numero, element, ++no, $"Reprise Marchandises du BL n° {r.Origine.NumeroBl}", "", "", ct);
        await LigneTexteRetourAsync(cnx, tx, societe, numero, element, ++no, "", "", "", ct);
        var motif = s.Motif.Select(x => (x ?? "").Trim()).Where(x => x != "").ToList();
        if (motif.Count > 0)
            await LigneTexteRetourAsync(cnx, tx, societe, numero, element, ++no, motif.ElementAtOrDefault(0) ?? "", motif.ElementAtOrDefault(1) ?? "",
                motif.ElementAtOrDefault(2) ?? "", ct);

        if (o.Fabrication)
        {
            // Quantité livrée du dossier diminuée, dossier désoldé (il reste à livrer), échéance du plan de livraison diminuée et
            // désoldée ; pas de stock
            await Executer(cnx, tx, """
                UPDATE {b}.AB001 SET CCQTEL = CCQTEL - ?, CCTAGS = CASE WHEN CCTAGS = 'D' THEN ' ' ELSE CCTAGS END,
                       CCSLDA = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDA END, CCSLDM = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDM END,
                       CCSLDJ = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDJ END
                WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                """, ct, e.QuantiteFabrication, societe, o.Dossier, o.Cle);
            if (o.LignePlan != 0)
                await Executer(cnx, tx, """
                    UPDATE {b}.EXP020 SET PDQDLI = PDQDLI - ?, PDSOLD = ' ' WHERE RSOC = ? AND RDOSS = ? AND F4DCLE = ? AND F4NLIG = ?
                    """, ct, e.QuantiteFabrication, societe, o.Dossier, o.Cle, o.LignePlan);
            return;
        }

        var date = s.Date;
        var maintenant = DateTime.Now;
        var observation = Tronquer($"Ret du BL {r.Origine.NumeroBl} {r.Origine.NomClient}".Trim(), 25);
        foreach (var l in e.Lignes.Where(x => x.Quantite > 0))
        {
            var lo = l.Origine;
            no++;
            await Executer(cnx, tx, """
                INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                    DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ' ', ?, ?, ?, ?, ?, 0, 0, 0)
                """, ct, societe, numero, element, no, lo.Article, lo.Designation, -l.Quantite, lo.Indice, lo.LigneCommande, lo.CodeLigne,
                lo.Designation2, lo.Designation3, lo.EmballagePar, lo.TypeEmballage);

            foreach (var x in l.Lots.Where(x => x.Quantite > 0 && x.Origine.Lot != ""))
            {
                var lot = x.Origine;
                // Lot du retour, même sans sortie de stock d'origine : le déjà retourné se compte par lot
                await Executer(cnx, tx, """
                    INSERT INTO {b}.EXP016 (RSOC, DNBL, DNUM, DNLIG, DNLOT, DNQTPL, DUC1, DUC2, DNEMP) VALUES (?, ?, ?, ?, ?, ?, 0, 0, ?)
                    """, ct, societe, numero, element, no, lot.Lot, -x.Quantite, x.Emplacement);
                if (!lot.Stocke) continue;
                // Entrée au dépôt principal, puis transfert vers le dépôt de retour (sortie du principal, entrée au dépôt de retour)
                var mouvements = new List<(string Code, string Sens, int Depot, int Contrepartie)> { ("RE", "E", r.DepotPrincipal, 0) };
                if (r.DepotRetour != 0)
                {
                    mouvements.Add(("TD", "S", r.DepotPrincipal, r.DepotRetour));
                    mouvements.Add(("TD", "E", r.DepotRetour, r.DepotPrincipal));
                }
                foreach (var (code, sens, depot, contrepartie) in mouvements)
                    await Executer(cnx, tx, """
                        INSERT INTO {b}.A700F (RSOC, MVBON, MVAA, MVMM, MVJJ, MVTSTO, MVDEP, MVCMVT, MVSENS, MVLOT, MVOBS, MVDOS, MVDCLE, MVDEPX,
                            MVREF, MVFAM, MVSFAM, MVQTUS, MVUS, MVPUUS, MVPRO, MVIND, MVBL, MVELBL, MVLIGB, MVEMP, MVDATS, MVCHRO)
                        VALUES (?, ' ', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'UN', ?, ?, ?, ?, ?, ?, ?, ?, ?)
                        """, ct, societe, date.Year, date.Month, date.Day, lot.TypeStock, depot, code, sens, lot.Lot, observation, o.Dossier, o.Cle,
                        contrepartie, lo.Article, lot.Famille, lot.SousFamille, x.Quantite, lot.Prix, profil, lo.Indice, numero, element, no,
                        x.Emplacement, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant.Hour * 100 + maintenant.Minute);

                // Palettes : nouvelle palette en stock au dépôt de retour, palette d'origine « retournée »
                foreach (var (palette, quantite) in x.Palettes)
                {
                    string sscc;
                    await using (var rd = await Lire(cnx, tx, "SELECT {b}.GETSSCC(CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(5, 0)), CAST(? AS CHAR(1))) FROM SYSIBM.SYSDUMMY1",
                                     ct, societe, palette.Of, palette.CleOf))
                    {
                        await rd.ReadAsync(ct);
                        sscc = rd.Texte(0);
                    }
                    if (sscc == "") throw new RegleMetierException($"Pas de n° SSCC pour la palette retournée de l'OF {palette.Of}/{palette.CleOf}.");
                    await Executer(cnx, tx, """
                        INSERT INTO {b}.PROD_ENTITE (SOCIETE, NUMERO_SSCC, ARTICLE, INDICE, QUANTITE_EX, NUMERO_OF, CLE_OF, STATUT,
                            DATE_HEURE_FABRICATION, OPERATEUR_FABRICATION, DEPOT, EMPLACEMENT, OPERATEUR_MISE_EN_STOCK, OPERATEUR_SORTIE_STOCK,
                            NUMERO_OPR, NUMERO_LIGNE_OPR, NUMERO_COLISAGE, NUMERO_LIGNE_FABRICATION, NUMERO_LIGNE_BL, NUMERO_BL, ELEMENT_BL,
                            SSCC_ORIGINE, SSCC_RETOUR, BL_RETOUR, DATE_ENT_STOCK)
                        VALUES (?, ?, ?, ?, ?, ?, ?, '2', CURRENT TIMESTAMP, ?, ?, ?, ?, ' ', 0, 0, 0, 0, 0, 0, 0, ?, ' ', 0, CURRENT TIMESTAMP)
                        """, ct, societe, sscc, lo.Article, lo.Indice, quantite, palette.Of, palette.CleOf, numero.ToString(),
                        r.DepotRetour != 0 ? r.DepotRetour : r.DepotPrincipal, x.Emplacement, profil, palette.Sscc);
                    await Executer(cnx, tx, """
                        UPDATE {b}.PROD_ENTITE SET STATUT = '7', SSCC_RETOUR = ?, BL_RETOUR = ? WHERE SOCIETE = ? AND NUMERO_SSCC = ?
                        """, ct, sscc, numero, societe, palette.Sscc);
                }
            }

            // Ligne de commande : livré diminué, désoldée s'il reste à livrer
            if (lo.LigneCommande != 0 && o.Dossier != 0)
                await Executer(cnx, tx, """
                    UPDATE {b}.NEG100 SET DNQTEL = DNQTEL - ?, DNQTER = MAX(0, DNQTEC - (DNQTEL - ?)),
                           DNSOLD = CASE WHEN DNQTEC > DNQTEL - ? THEN ' ' ELSE 'O' END
                    WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                    """, ct, l.Quantite, l.Quantite, l.Quantite, societe, o.Dossier, o.Cle, lo.LigneCommande);
        }

        // Dossier désoldé ; produit fini : quantité livrée diminuée (comme la création du BL l'avait augmentée ; sans plancher, pour
        // que la suppression du retour rende exactement ce qui a été ôté)
        if (o.Dossier != 0)
            await Executer(cnx, tx, """
                UPDATE {b}.AB001 SET CCQTEL = CASE WHEN CCTYPC = '4' THEN CCQTEL - ? ELSE CCQTEL END,
                       CCTAGS = CASE WHEN CCTAGS = 'D' THEN ' ' ELSE CCTAGS END,
                       CCSLDA = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDA END,
                       CCSLDM = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDM END,
                       CCSLDJ = CASE WHEN CCTAGS = 'D' THEN 0 ELSE CCSLDJ END
                WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                """, ct, e.Lignes.Sum(l => l.Quantite), societe, o.Dossier, o.Cle);
    }

    private async Task LigneTexteRetourAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, int element, int ligne,
        string designation, string designation2, string designation3, CancellationToken ct) =>
        await Executer(cnx, tx, """
            INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
            VALUES (?, ?, ?, ?, ' ', ?, 0, ' ', 0, 0, ' ', ' ', ?, ?, 0, ' ', 0, 0, 0)
            """, ct, societe, numero, element, ligne, Tronquer(designation, 40), Tronquer(designation2, 40), Tronquer(designation3, 40));

    // Message de synchronisation de la sous-traitance (SYN100, action « BL »), comme EXP010S et ConsultationBL ; non bloquant
    private async Task MessageSynchronisationAsync(OdbcConnection cnx, int societe, int dossier, string cle, int numero)
    {
        try
        {
            await Executer(cnx, null, """
                INSERT INTO {b}.SYN100 (HTSOC, HTDOSS, HTDCLE, HTDATE, HTHEUR, HTSTAT, HTNBL, HTACT, HTART, HTIND, HTQTET, HTDEPO, HTDEPD)
                VALUES (?, ?, ?, ?, CURRENT TIMESTAMP, ' ', ?, 'BL', ' ', ' ', 0, 0, 0)
                """, CancellationToken.None, societe, dossier, cle, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(DateTime.Today)), numero);
        }
        catch (OdbcException)
        {
            // synchronisation rattrapée par l'exploitation
        }
    }

    // ---------- Suppression d'un retour (EXP010S SpSupRm) ----------
    public async Task<RetourEnregistre?> RetourEnregistreAsync(int societe, int numeroBl, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var elements = new List<(int, int, string, string, bool, int, int)>();
        var origine = 0;
        var facture = false;
        await using (var rd = await Lire(cnx, """
            SELECT e.HNUM, e.HNODOS, e.HCLEF, COALESCE(a.CCTYPC, ''), e.HTYPBL, e.HQTELI, e.HNOBLO, e.HBLFAC, e.HNOFAC, e.HETABL,
                   COALESCE((SELECT MIN(l.DNUMLC) FROM {b}.EXP015 l WHERE l.RSOC = o.RSOC AND l.DNBL = o.HNOBL AND l.DNUM = o.HNUM
                             AND l.DNUMLC <> 0), 0)
            FROM {b}.EXP010 e
            LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = e.RSOC AND a.RDOSS = e.HNODOS AND a.CCDCLE = e.HCLEF
            LEFT OUTER JOIN {b}.EXP010 o ON o.RSOC = e.RSOC AND o.HNOBL = e.HNOBLO AND o.HNUM = e.HNUMO
            WHERE e.RSOC = ? AND e.HNOBL = ? ORDER BY e.HNUM
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
            {
                if (rd.Texte(9) != "R") return null;
                elements.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Entier(4) == 1, -rd.Entier(5), rd.Entier(10)));
                origine = rd.Entier(6);
                facture |= rd.Texte(7) == "O" || rd.Entier(8) != 0;
            }
        if (elements.Count == 0) return null;
        var indisponibles = await PalettesNonDisponiblesAsync(cnx, null, societe, numeroBl, ct);
        var types = new List<string>();
        await using (var rd = await Lire(cnx, "SELECT DISTINCT MVTSTO FROM {b}.A700F WHERE RSOC = ? AND MVBL = ?", ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) types.Add(rd.Texte(0));
        return new RetourEnregistre(numeroBl, origine, facture, elements, indisponibles, types);
    }

    private const string PalettesRetourHorsStock = """
        SELECT n.NUMERO_SSCC FROM {b}.PROD_ENTITE o
        JOIN {b}.PROD_ENTITE n ON n.SOCIETE = o.SOCIETE AND n.NUMERO_SSCC = o.SSCC_RETOUR
        WHERE o.SOCIETE = ? AND o.BL_RETOUR = ? AND (n.STATUT <> '2' OR n.NUMERO_BL <> 0)
        """;

    // Palettes recréées par le retour qui ne sont plus en stock (déplacées, reprises sur un BL…)
    private async Task<List<string>> PalettesNonDisponiblesAsync(OdbcConnection cnx, OdbcTransaction? tx, int societe, int numeroBl, CancellationToken ct)
    {
        var r = new List<string>();
        await using var rd = tx is null ? await Lire(cnx, PalettesRetourHorsStock, ct, societe, numeroBl)
            : await Lire(cnx, tx, PalettesRetourHorsStock + " WITH RS USE AND KEEP EXCLUSIVE LOCKS", ct, societe, numeroBl);
        while (await rd.ReadAsync(ct)) r.Add(rd.Texte(0));
        return r;
    }

    // Verrous du retour et des dossiers, puis une transaction : stock contrôlé (les entrées du retour disparaissent), commande et
    // lignes de commande rétablies (re-soldées si tout est livré ; le dossier reste désoldé, comme SpSupRm), quantité livrée et plan de livraison (fabrication), mouvements, lots, lignes,
    // éléments et en-tête supprimés, palettes recréées supprimées et palettes d'origine remises « expédiées ». Après : recalcul du
    // stock, audit « SU ».
    public async Task SupprimerRetourAsync(int societe, string profil, RetourEnregistre r, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var verrouBl = CleVerrouBl(societe, r.NumeroBl);
        var dossiers = r.Elements.Where(e => e.Dossier != 0).Select(e => CleVerrouDossier(societe, e.Dossier, e.Cle)).Distinct().ToList();
        var poses = new List<string>();
        var recalculs = new List<(string TypeStock, int Depot, string Article, string Indice)>();
        await PoserVerrouAsync(cnx, "BL", verrouBl, profil, "Ce BL est en cours de modification par", ct);
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
                // Relu sous verrou : retour facturé ou palettes reprises entre-temps
                await using (var rd = await Lire(cnx, tx, """
                    SELECT COUNT(*) FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND (HBLFAC = 'O' OR HNOFAC <> 0)
                    WITH RS USE AND KEEP EXCLUSIVE LOCKS
                    """, ct, societe, r.NumeroBl))
                {
                    await rd.ReadAsync(ct);
                    if (rd.Entier(0) > 0) throw new RegleMetierException("Ce retour vient d'être facturé : il ne peut plus être supprimé.");
                }
                if ((await PalettesNonDisponiblesAsync(cnx, tx, societe, r.NumeroBl, ct)).Count > 0)
                    throw new RegleMetierException("Suppression impossible : certaines palettes du retour ne sont plus en stock.");

                // Entrées nettes du retour par position : elles disparaissent, le stock ne doit pas devenir négatif
                var entrees = new List<(string TypeStock, int Depot, string Article, string Indice, string Lot, string Emplacement, int Quantite)>();
                await using (var rd = await Lire(cnx, tx, """
                    SELECT MVTSTO, MVDEP, MVREF, MVIND, MVLOT, MVEMP, SUM(CASE WHEN MVSENS = 'E' THEN MVQTUS ELSE -MVQTUS END)
                    FROM {b}.A700F WHERE RSOC = ? AND MVBL = ? GROUP BY MVTSTO, MVDEP, MVREF, MVIND, MVLOT, MVEMP
                    """, ct, societe, r.NumeroBl))
                    while (await rd.ReadAsync(ct))
                        entrees.Add((rd.Texte(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), (int)rd.Nombre(6)));
                recalculs.AddRange(entrees.Select(x => (x.TypeStock, x.Depot, x.Article, x.Indice)));
                foreach (var (typeStock, depot, article, indice) in recalculs.Distinct()
                             .OrderBy(x => x.TypeStock, StringComparer.Ordinal).ThenBy(x => x.Depot).ThenBy(x => x.Article, StringComparer.Ordinal))
                    await Executer(cnx, tx, "UPDATE {b}.AK00F SET PRREF = PRREF WHERE RSOC = ? AND PRTSTO = ? AND PRDEP = ? AND PRREF = ? AND PRIND = ?",
                        ct, societe, typeStock, depot, article, indice);
                var parEmplacement = new Dictionary<string, bool>();
                foreach (var x in entrees.Where(x => x.Quantite > 0))
                {
                    if (!parEmplacement.TryGetValue(x.TypeStock, out var pe))
                    {
                        await using var ts = await Lire(cnx, tx, "SELECT TSEMP FROM {b}.AG00F WHERE RSOC = ? AND TSTSTO = ?", ct, societe, x.TypeStock);
                        parEmplacement[x.TypeStock] = pe = await ts.ReadAsync(ct) && ts.Texte(0) == "O";
                    }
                    var retour = await ControlerStockAsync(cnx, tx, societe, x.TypeStock, pe, x.Depot, x.Article, x.Indice, x.Lot, x.Emplacement,
                        DateOnly.FromDateTime(DateTime.Today), -x.Quantite, ct);
                    if (retour != 0) throw new RegleMetierException(MessageStock(x.Article, x.Lot, x.Emplacement, x.Depot, retour));
                }

                foreach (var e in r.Elements)
                {
                    if (e.Fabrication)
                    {
                        await Executer(cnx, tx, "UPDATE {b}.AB001 SET CCQTEL = CCQTEL + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                            e.Quantite, societe, e.Dossier, e.Cle);
                        if (e.LignePlan != 0)
                            await Executer(cnx, tx, """
                                UPDATE {b}.EXP020 SET PDQDLI = PDQDLI + ?, PDSOLD = CASE WHEN PDQDLI + ? >= PQTE THEN 'O' ELSE PDSOLD END
                                WHERE RSOC = ? AND RDOSS = ? AND F4DCLE = ? AND F4NLIG = ?
                                """, ct, e.Quantite, e.Quantite, societe, e.Dossier, e.Cle, e.LignePlan);
                        continue;
                    }
                    var lignes = new List<(int LigneCommande, int Quantite)>();
                    await using (var rd = await Lire(cnx, tx, """
                        SELECT DNUMLC, SUM(-DQTE) FROM {b}.EXP015 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? AND DART <> ' '
                        GROUP BY DNUMLC
                        """, ct, societe, r.NumeroBl, e.Element))
                        while (await rd.ReadAsync(ct)) lignes.Add((rd.Entier(0), rd.Entier(1)));
                    if (e.Dossier == 0) continue;
                    foreach (var (ligne, quantite) in lignes.Where(x => x.LigneCommande != 0))
                        await Executer(cnx, tx, """
                            UPDATE {b}.NEG100 SET DNQTEL = DNQTEL + ?, DNQTER = MAX(0, DNQTEC - (DNQTEL + ?)),
                                   DNSOLD = CASE WHEN DNQTEC - (DNQTEL + ?) <= 0 THEN 'O' ELSE ' ' END
                            WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                            """, ct, quantite, quantite, quantite, societe, e.Dossier, e.Cle, ligne);
                    var total = lignes.Sum(x => x.Quantite);   // toutes les lignes d'article, comme à la création
                    await Executer(cnx, tx, "UPDATE {b}.AB001 SET CCQTEL = CCQTEL + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTYPC = '4'",
                        ct, total, societe, e.Dossier, e.Cle);
                }

                await Executer(cnx, tx, """
                    DELETE FROM {b}.PROD_ENTITE n WHERE n.SOCIETE = ? AND n.NUMERO_SSCC IN
                        (SELECT o.SSCC_RETOUR FROM {b}.PROD_ENTITE o WHERE o.SOCIETE = ? AND o.BL_RETOUR = ?)
                    """, ct, societe, societe, r.NumeroBl);
                await Executer(cnx, tx, """
                    UPDATE {b}.PROD_ENTITE SET STATUT = '5', SSCC_RETOUR = ' ', BL_RETOUR = 0 WHERE SOCIETE = ? AND BL_RETOUR = ?
                    """, ct, societe, r.NumeroBl);
                await Executer(cnx, tx, "DELETE FROM {b}.A700F WHERE RSOC = ? AND MVBL = ?", ct, societe, r.NumeroBl);
                foreach (var table in new[] { "EXP016", "EXP015" })
                    await Executer(cnx, tx, $"DELETE FROM {{b}}.{table} WHERE RSOC = ? AND DNBL = ?", ct, societe, r.NumeroBl);
                foreach (var table in new[] { "EXP010", "EXP012", "EXP009" })
                    await Executer(cnx, tx, $"DELETE FROM {{b}}.{table} WHERE RSOC = ? AND HNOBL = ?", ct, societe, r.NumeroBl);
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
            await LeverVerrouAsync(cnx, "BL", verrouBl, profil);
        }
        foreach (var (typeStock, depot, article, indice) in recalculs.Distinct())
            await RecalculerStockAsync(cnx, societe, typeStock, depot, article, indice);
        await AuditAsync(cnx, societe, profil, r.NumeroBl, "SU");
    }
}
