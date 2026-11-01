using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Lectures de la saisie d'un pré-BL : valeurs par défaut (client, adresse A500F, EXP005, CLITXB), pré-BL existant, lignes de
// commande livrables, lots (AL00F) et palettes (PROD_ENTITE) disponibles. Conditionnement : SPF026 de l'adresse, sinon SPF026
// du client (adresse 0), sinon SPF010.
// =====================================================================
internal sealed partial class PreBlDb2
{
    private const string Conditionnement = """
        COALESCE(NULLIF(ca.AEMBP, 0), NULLIF(cc.AEMBP, 0), s.AEMBP, 0), COALESCE(NULLIF(ca.ANPAL, 0), NULLIF(cc.ANPAL, 0), s.ANPAL, 0),
        COALESCE(ca.ATYEMB, cc.ATYEMB, s.ATYEMB, '')
        """;

    private const string JointuresConditionnement = """
        LEFT OUTER JOIN {b}.SPF026 ca ON ca.RSOC = n.RSOC AND ca.AARTIC = n.DNART AND ca.AINDIC = n.DNIND AND ca.EAGEN = ? AND ca.EACLI = ? AND ca.EANLIG = ?
        LEFT OUTER JOIN {b}.SPF026 cc ON cc.RSOC = n.RSOC AND cc.AARTIC = n.DNART AND cc.AINDIC = n.DNIND AND cc.EAGEN = ? AND cc.EACLI = ? AND cc.EANLIG = 0
        """;

    // Client (non annulé), adresse de livraison (non annulée) et ses commentaires, paramètres d'expédition, pied du client
    public async Task<SaisiePreBl> NouveauAsync(int societe, int compteGeneral, int compteClient, int numeroAdresse, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var s = new SaisiePreBl { CompteGeneral = compteGeneral, CompteClient = compteClient, DateDepart = DateOnly.FromDateTime(DateTime.Today) };
        await using (var rd = await Lire(cnx, "SELECT CNOM, CTRANS FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND CTAG <> '*'", ct,
                         societe, compteGeneral, compteClient))
        {
            if (!await rd.ReadAsync(ct)) throw new RegleMetierException("Le client n'existe pas.");
            (s.NomClient, s.Transporteur) = (rd.Texte(0), rd.Entier(1));
        }
        await using (var rd = await Lire(cnx, """
            SELECT ALFJ, ALRS, ALRUE1, ALRUE2, ALPOST, ALVILL, ALDEP, ALPAY, ALCOMM, ALCOM2, ALCOM3 FROM {b}.A500F
            WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALNLIG = ? AND ALTAG <> '*'
            """, ct, societe, compteGeneral, compteClient, numeroAdresse))
        {
            if (numeroAdresse <= 0 || !await rd.ReadAsync(ct)) throw new RegleMetierException("Le n° d'adresse de livraison n'existe pas.");
            s.Adresse = new AdresseBl(numeroAdresse, rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7));
            s.Observations = [rd.Texte(8), rd.Texte(9), rd.Texte(10)];
        }
        await using (var rd = await Lire(cnx, "SELECT FETYPL, FEPORT, FEPREB FROM {b}.EXP005 WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", ct, societe))
            if (await rd.ReadAsync(ct)) (s.TypeLivraison, s.TypePort, s.SansPalette) = (rd.Texte(0), rd.Texte(1), rd.Texte(2) == "O");
        var pied = new List<string>();
        await using (var rd = await Lire(cnx, "SELECT CTTXT FROM {b}.CLITXB WHERE RSOC = ? AND RGEN = ? AND RCLI = ? ORDER BY CTNLIG", ct,
                         societe, compteGeneral, compteClient))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        s.Pied = string.Join("\n", pied).TrimEnd();
        return s;
    }

    // Variante sans palette de la société (EXP005.FEPREB, choisie par MenuNetprint)
    public async Task<bool> SansPaletteAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, "SELECT FEPREB FROM {b}.EXP005 WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", ct, societe);
        return await rd.ReadAsync(ct) && rd.Texte(0) == "O";
    }

    // ---------- Pré-BL existant ----------
    public async Task<SaisiePreBl?> OuvrirSaisieAsync(int societe, int numero, int? representant, CancellationToken ct)
    {
        var fiche = await LireAsync(societe, numero, representant, ct);
        if (fiche is null) return null;
        await using var cnx = await OuvrirAsync(ct);
        var s = new SaisiePreBl
        {
            Numero = numero, SansPalette = fiche.SansPalette, CompteGeneral = fiche.CompteGeneral, CompteClient = fiche.CompteClient,
            NomClient = fiche.NomClient, Adresse = fiche.Adresse, DateDepart = fiche.DateDepart ?? DateOnly.FromDateTime(DateTime.Today),
            Transporteur = fiche.Transporteur, TypeLivraison = fiche.TypeLivraison, TypePort = fiche.TypePort, StockPaye = fiche.StockPaye,
            Mention = fiche.Mention, Poids = fiche.Poids, Pied = string.Join("\n", fiche.Pied), Etat = fiche.Etat,
        };
        await using (var rd = await Lire(cnx, "SELECT HOBS, HOBS2, HOBS3, HNADRE FROM {b}.EXP509 WHERE RSOC = ? AND HNOBL = ?", ct, societe, numero))
            if (await rd.ReadAsync(ct))
            {
                s.Observations = [rd.Texte(0), rd.Texte(1), rd.Texte(2)];
                if (s.Adresse.Numero == 0) s.Adresse = s.Adresse with { Numero = rd.Entier(3) };
            }
        await using (var rd = await Lire(cnx, $"""
            SELECT l.HNUM, l.DOBS1, l.DOBS2, l.DOBS3, l.DAPPSOLD, l.DETALI, l.DEMBP1, l.DEEMBPE, l.DENPALE, l.DTYEMB,
                   COALESCE(n.DNQTEC - n.DNQTEL, 0), COALESCE(NULLIF(n.DNGENF, 0), a.CGENF, 0), COALESCE(NULLIF(n.DNCLIF, 0), a.CCLIF, 0),
                   COALESCE(s.ADES, n.DNDESA, ''), {Conditionnement}
            FROM {"{b}"}.EXP510 l
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.HNODOS AND n.DNDCLE = l.HCLEF AND n.DNNLIG = l.HNUMLC
            LEFT OUTER JOIN {"{b}"}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.HNODOS AND a.CCDCLE = l.HCLEF
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND
            {JointuresConditionnement}
            WHERE l.RSOC = ? AND l.HNOBL = ?
            """, ct, s.CompteGeneral, s.CompteClient, s.Adresse.Numero, s.CompteGeneral, s.CompteClient, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var f = fiche.Lignes.FirstOrDefault(x => x.Numero == rd.Entier(0))
                        ?? throw new RegleMetierException($"La ligne {rd.Entier(0)} du pré-BL est illisible : rouvrez le pré-BL.");
                s.Lignes.Add(new LigneSaisiePreBl
                {
                    Numero = f.Numero, Dossier = f.Dossier, Cle = f.Cle, LigneCommande = f.LigneCommande, ReferenceCommande = f.ReferenceCommande,
                    Appel = f.Appel, Article = f.Article, Indice = f.Indice, RefImprime = f.RefImprime, Designation = rd.Texte(13), Stocke = !f.Frais,
                    Quantite = f.Quantite, Observations = [rd.Texte(1), rd.Texte(2), rd.Texte(3)], AppelASolder = rd.Texte(4) == "O",
                    EtatLivraison = rd.Texte(5) == "" ? "T" : rd.Texte(5), EmballagePar = rd.Entier(14), UcParPalette = rd.Entier(15),
                    TypeEmballage = rd.Texte(16), EmballageParExceptionnel = rd.Entier(7), UcParPaletteExceptionnel = rd.Entier(8),
                    Reste = rd.Entier(10) + f.Quantite, CompteGeneralFacture = rd.Entier(11), CompteClientFacture = rd.Entier(12),
                    Lots = [.. f.Lots.Select(x => new LotSaisiePreBl
                    {
                        Numero = x.Numero, Lot = x.Lot, Depot = x.Depot, Quantite = x.Quantite,
                        Verrouille = x.AProduire || x.PalettesSscc.Any(p => p.Statut is "3" or "4") || (fiche.SansPalette && x.PalettesSscc.Count > 0),
                        Palettes = [.. x.PalettesSscc.Select(p => new PaletteSaisiePreBl { Sscc = p.Sscc, Quantite = p.Quantite, Picking = p.Picking, Statut = p.Statut })],
                    })],
                });
            }
        s.Lignes = [.. s.Lignes.OrderBy(l => l.Numero)];
        return s;
    }

    // ---------- Lignes de commande livrables (FEN_AjoutLigneCde, R6, R7) ----------
    public async Task<List<LigneCommandePreBl>> LignesCommandeAsync(int societe, SaisiePreBl saisie, int commande, string cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using (var rd = await Lire(cnx, "SELECT CCTYPC, RGEN, RCLI FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct, societe, commande, cle))
        {
            if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La commande {commande}/{cle} n'existe pas.");
            if (rd.Texte(0) is not ("3" or "4")) throw new RegleMetierException($"Le dossier {commande}/{cle} n'est pas une commande.");
            if (rd.Entier(1) != saisie.CompteGeneral || rd.Entier(2) != saisie.CompteClient)
                throw new RegleMetierException($"La commande {commande}/{cle} n'est pas du client du pré-BL.");
        }
        var r = new List<LigneCommandePreBl>();
        await using (var rd = await Lire(cnx, $"""
            SELECT n.DNNLIG, a.CCREFC, n.DNAPPE, n.DNART, n.DNIND, COALESCE(s.ARFIMP, ''), COALESCE(s.ADES, n.DNDESA), n.DNQTEC - n.DNQTEL, n.DNDLIV,
                   COALESCE(s.ASTK, ''), {Conditionnement}, COALESCE(NULLIF(n.DNGENF, 0), a.CGENF, 0), COALESCE(NULLIF(n.DNCLIF, 0), a.CCLIF, 0)
            FROM {"{b}"}.NEG100 n
            JOIN {"{b}"}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            LEFT OUTER JOIN {"{b}"}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            {JointuresConditionnement}
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNSTAT = '1' AND n.DNSOLD <> 'O' AND n.DNLIGA = ? AND n.DNART <> ' '
              AND (CAST(? AS CHAR(1)) = 'N' OR COALESCE(s.AOPT7, '') <> 'O')
              AND (COALESCE(s.ASTK, '') <> 'O' OR NOT EXISTS (SELECT 1 FROM {"{b}"}.EXP510 z JOIN {"{b}"}.EXP509 y ON y.RSOC = z.RSOC AND y.HNOBL = z.HNOBL
                   WHERE z.RSOC = n.RSOC AND z.HNODOS = n.RDOSS AND z.HCLEF = n.DNDCLE AND z.HNUMLC = n.DNNLIG AND y.HENBL <> 'O' AND y.HNOBL <> ?))
            ORDER BY n.DNDLIV, n.DNNLIG
            """, ct, saisie.CompteGeneral, saisie.CompteClient, saisie.Adresse.Numero, saisie.CompteGeneral, saisie.CompteClient,
                         societe, commande, cle, saisie.Adresse.Numero, saisie.StockPaye ? "O" : "N", saisie.Numero))
            while (await rd.ReadAsync(ct))
                r.Add(new LigneCommandePreBl(commande, cle, rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6),
                    rd.Entier(7), BaseDb2.DateAAAAMMJJ(rd.Entier(8)), rd.Texte(9) == "O", rd.Entier(10), rd.Entier(11), rd.Texte(12), rd.Entier(13),
                    rd.Entier(14)));
        return r;
    }

    // ---------- Lots (AL00F) et palettes (PROD_ENTITE) disponibles (FI_AjoutPalettes, FI_AjoutPalettesEXP517, R13, R14) ----------
    // Disponible d'une palette = quantité - quantités prises par les autres pré-BL non livrés
    public async Task<List<LotDisponiblePreBl>> LotsAsync(int societe, int numero, string article, string indice, bool stockPaye, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var palettes = new Dictionary<(string, int), List<PaletteDisponiblePreBl>>();
        await using (var rd = await Lire(cnx, """
            SELECT e.NUMERO_SSCC, e.QUANTITE_EX, e.STATUT, e.EMPLACEMENT,
                   COALESCE(YEAR(e.DATE_HEURE_FABRICATION) * 10000 + MONTH(e.DATE_HEURE_FABRICATION) * 100 + DAY(e.DATE_HEURE_FABRICATION), 0), e.DEPOT, DIGITS(e.NUMERO_OF) CONCAT e.CLE_OF,
                   COALESCE((SELECT SUM(p.DPQTEP) FROM {b}.EXP517 p JOIN {b}.EXP509 h ON h.RSOC = p.RSOC AND h.HNOBL = p.DNBL
                             WHERE p.RSOC = e.SOCIETE AND p.DPSSCC = e.NUMERO_SSCC AND p.DNBL <> ? AND h.HENBL <> 'O'), 0),
                   (SELECT COUNT(*) FROM {b}.EXP517 p WHERE p.RSOC = e.SOCIETE AND p.DPSSCC = e.NUMERO_SSCC AND p.DNBL = ?)
            FROM {b}.PROD_ENTITE e
            WHERE e.SOCIETE = ? AND e.ARTICLE = ? AND e.INDICE = ? AND e.STATUT IN ('1', '2', '3', '4') AND e.NUMERO_OPR = 0
            ORDER BY e.NUMERO_SSCC
            """, ct, numero, numero, societe, article, indice))
            while (await rd.ReadAsync(ct))
            {
                var (statut, dansCeluiCi) = (rd.Texte(2), rd.Entier(8) > 0);
                var disponible = (int)rd.Nombre(1) - rd.Entier(7);
                if (disponible <= 0 && !dansCeluiCi) continue;
                var cle = (rd.Texte(6), rd.Entier(5));
                if (!palettes.TryGetValue(cle, out var l)) palettes[cle] = l = [];
                l.Add(new PaletteDisponiblePreBl(rd.Texte(0), Math.Max(0, disponible), (int)rd.Nombre(1), statut, rd.Texte(3),
                    BaseDb2.DateAAAAMMJJ(rd.Entier(4)), statut is "1" or "2" || dansCeluiCi));
            }
        var r = new List<LotDisponiblePreBl>();
        await using (var rd = await Lire(cnx, """
            SELECT a.PLLOT, a.PLDEP, COALESCE(MAX(d.DLIB), ''), SUM(a.PLQENT) FROM {b}.AL00F a
            JOIN {b}.B100F d ON d.RSOC = a.RSOC AND d.RDEP = a.PLDEP AND d.DTAG <> '*' AND d.DFACTUR = ?
            WHERE a.RSOC = ? AND a.PLREF = ? AND a.PLIND = ? AND a.PLQENT > 0
            GROUP BY a.PLLOT, a.PLDEP ORDER BY a.PLLOT, a.PLDEP
            """, ct, stockPaye ? "O" : "N", societe, article, indice))
            while (await rd.ReadAsync(ct))
                r.Add(new LotDisponiblePreBl(rd.Texte(0), rd.Entier(1), rd.Texte(2), (int)rd.Nombre(3),
                    palettes.GetValueOrDefault((rd.Texte(0), rd.Entier(1))) ?? []));
        return r;
    }
}
