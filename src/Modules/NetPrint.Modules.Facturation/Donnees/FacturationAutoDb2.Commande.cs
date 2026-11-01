using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// =====================================================================
// Facture d'une commande sans BL (FAC085 clé dossier → FAC150, lignes reprises du dossier) : la commande est lue comme un
// élément de BL sans BL (n° 0), pour passer par le même moteur que les BL. Donneur d'ordre et client facturé : ceux de la
// commande (AB001 RGEN / RCLI, CGENF / CCLIF) ; conditions comme la lecture des BL (commande AB001.CCMOD, sinon client des
// conditions AB001FA.FACRC). Quantité : fabrication = commandée − déjà facturée (CCQTEC − CCQTEP) ; négoce (FAC150 R37) = lignes
// NEG100 pas entièrement facturées, pour leur reste (DNQTEC − DNQTEF), avec les lignes de texte qui les suivent. Compléments AB004
// non facturés. Rien à facturer : refusé. Les quantités facturées ne bougent qu'à l'édition définitive : une facture « à éditer »
// de la commande est signalée (FacturesEnCoursAsync).
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    public async Task<ElementBlAFacturer?> LireCommandeAsync(int societe, int numero, string cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        ElementBlAFacturer element;
        decimal resteFabrication;
        await using (var r = await Lire(cnx, null, """
            SELECT a.RGEN, a.RCLI, a.CGENF, a.CCLIF, COALESCE(o.CNOM, ''),
                   COALESCE(k.CMODE, ''), COALESCE(k.CNBJOU, 0), COALESCE(k.CJOULE, 0), COALESCE(k.CESCOM, 0), COALESCE(k.CARRET, 0),
                   a.CCMOD, a.CCECH, a.CCLE, a.CCESC, a.CCART, a.CCREP, a.CCTYPC, a.CCTYPV, COALESCE(o.CDCVD, ''), a.CCUNIT, a.CCDES1,
                   a.CCDES2, a.CCPXVE, a.CTXRM, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCAT, a.CCSCAT, a.CCDEV, a.CCMODL, a.CCTXCO,
                   a.CCMON, COALESCE(x.FACPT, 0), COALESCE(x.FAATT, ''), COALESCE(x.FAORTE, ''), COALESCE(x.FAEDTB, ''),
                   COALESCE(x.FATPC, ''), COALESCE(x.FAFJ, ''), COALESCE(x.FANOM, ''), COALESCE(x.FARUE1, ''), COALESCE(x.FARUE2, ''),
                   COALESCE(x.FAPOST, ''), COALESCE(x.FAVILL, ''), COALESCE(ad.ALFJ, ''), COALESCE(ad.ALRS, ''), COALESCE(ad.ALRUE1, ''),
                   COALESCE(ad.ALRUE2, ''), COALESCE(ad.ALPOST, ''), COALESCE(ad.ALVILL, ''), COALESCE(ad.ALPAY, ''),
                   COALESCE(o.CRELVE, ''), COALESCE(o.CDOPT7, ''), a.CCQTEC, a.CCQTEP, a.CCENSE
            FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.AB001FA x ON x.RSOC = a.RSOC AND x.RDOSS = a.RDOSS AND x.CCDCLE = a.CCDCLE
            LEFT OUTER JOIN {b}.AA0003 o ON o.RSOC = a.RSOC AND o.RGEN = a.RGEN AND o.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AA0003 k ON k.RSOC = a.RSOC
                 AND k.RGEN = CASE WHEN x.RSOC IS NULL OR x.FACRC = '1' OR a.CCLIF = 0 THEN a.RGEN ELSE a.CGENF END
                 AND k.RCLI = CASE WHEN x.RSOC IS NULL OR x.FACRC = '1' OR a.CCLIF = 0 THEN a.RCLI ELSE a.CCLIF END
            LEFT OUTER JOIN {b}.A500F ad ON ad.RSOC = a.RSOC AND ad.RGEN = a.RGEN AND ad.RCLI = a.RCLI AND ad.ALNLIG = x.FANAL AND x.FANAL <> 0
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ?
            """, ct, societe, numero, cle))
        {
            if (!await r.ReadAsync(ct)) return null;
            var conditions = r.Texte(10) != ""
                ? new ConditionsReglement(r.Texte(10), r.Entier(11), r.Entier(12), r.Nombre(13), r.Entier(14))
                : new ConditionsReglement(r.Texte(5), r.Entier(6), r.Entier(7), r.Nombre(8), r.Entier(9));
            var adresse = new AdresseEnvoi(r.Texte(39), r.Texte(40), r.Texte(41), r.Texte(42), r.Texte(43), r.Texte(44), "");
            if (adresse.Vide) adresse = new AdresseEnvoi(r.Texte(45), r.Texte(46), r.Texte(47), r.Texte(48), r.Texte(49), r.Texte(50), r.Texte(51));
            var d = new DossierAFacturer(numero, cle, r.Texte(16), r.Texte(17) != "" ? r.Texte(17) : r.Texte(18), r.Texte(19), r.Texte(20),
                r.Texte(21), r.Nombre(22), r.Nombre(23), r.Texte(24), BaseDb2.Date(r.Entier(25), r.Entier(26), r.Entier(27)), r.Texte(28),
                r.Texte(29), r.Entier(30), r.Entier(31), r.Nombre(32), r.Texte(33), r.Nombre(34), r.Texte(35), r.Texte(36), r.Texte(37), adresse,
                r.Texte(38) is "" or "1", r.Texte(52), r.Texte(53), []);
            resteFabrication = Math.Max(0, r.Nombre(54) - r.Nombre(55));
            element = new ElementBlAFacturer(0, 0, r.Entier(0), r.Entier(1), r.Entier(2), r.Entier(3), 6, conditions, AdresseEnvoi.Aucune,
                r.Entier(15), false, resteFabrication, d, [], [], r.Texte(4), null, r.Texte(56));
        }

        var complements = new List<ComplementDossier>();
        await using (var rd = await Lire(cnx, null, """
            SELECT F4LIG, F4CL, F4QTE, F4DES, F4UT, F4REM, F4PU FROM {b}.AB004
            WHERE RSOC = ? AND RDOSS = ? AND F4DCLE = ? AND F4FACT <> 'O' ORDER BY F4LIG
            """, ct, societe, numero, cle))
            while (await rd.ReadAsync(ct))
                complements.Add(new ComplementDossier(rd.Entier(0), rd.Texte(1), rd.Nombre(2), rd.Texte(3), rd.Texte(4), rd.Entier(5), rd.Nombre(6)));
        element = element with { Dossier = element.Dossier with { Complements = complements } };

        if (element.Dossier.Fabrication)
        {
            if (resteFabrication <= 0 && complements.Count == 0)
                throw new RegleMetierException($"La commande {numero}/{cle} est entièrement facturée : rien à facturer.");
            return element;
        }

        // Négoce : lignes au reste positif ; une ligne de texte suit la ligne qu'elle décrit (gardée si celle-ci l'est)
        var lignes = new List<LigneBl>();
        var garder = true;
        await using (var rd = await Lire(cnx, null, """
            SELECT DNNLIG, DNCLIG, DNART, DNIND, DNPUS, DNUV, DNTXCO, DNOPT2, DNOPT5, DNDESA, DNQTEC - DNQTEF
            FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? ORDER BY DNNLIG
            """, ct, societe, numero, cle))
            while (await rd.ReadAsync(ct))
            {
                var c = new LigneCommandeNegoce(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Nombre(4), rd.Texte(5), rd.Nombre(6),
                    rd.Texte(7) == "O", rd.Texte(8) == "O");
                var texte = c.CodeLigne == "9";
                if (!texte) garder = rd.Nombre(10) > 0;
                if (garder) lignes.Add(new LigneBl(rd.Entier(0), rd.Texte(2), rd.Texte(1), texte ? 0 : rd.Nombre(10), rd.Texte(9), "", "", c));
            }
        if (!lignes.Any(l => l.Commande!.CodeLigne != "9") && complements.Count == 0)
            throw new RegleMetierException($"Les lignes de la commande {numero}/{cle} sont entièrement facturées : rien à facturer.");
        return element with { Lignes = lignes };
    }

    // Factures pas encore éditées (à éditer, à contrôler) qui portent déjà un élément de la commande
    public async Task<IReadOnlyList<int>> FacturesEnCoursAsync(int societe, int numero, string cle, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var factures = new List<int>();
        await using var rd = await Lire(cnx, null, """
            SELECT DISTINCT e.RNFAC FROM {b}.FAC090 e
            JOIN {b}.FAC080 f ON f.RSOC = e.RSOC AND f.RNFAC = e.RNFAC AND f.FDANNE = e.FEANNE AND f.FDMOIS = e.FEMOIS AND f.FDJOUR = e.FEJOUR
            WHERE e.RSOC = ? AND e.FEDOSS = ? AND e.FECLE = ? AND f.FDEDIT IN ('O', 'A') ORDER BY e.RNFAC
            """, ct, societe, numero, cle);
        while (await rd.ReadAsync(ct)) factures.Add(rd.Entier(0));
        return factures;
    }
}
