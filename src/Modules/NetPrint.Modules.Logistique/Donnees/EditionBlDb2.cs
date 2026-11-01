using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Données de l'édition d'un BL (EXP035P2) : tout ce que le document affiche, en lecture seule, puis la marque « édité ».
// =====================================================================

// En-tête (EXP009), client (AA0003), transporteur (B200F), pays (AA00081 'PY'), paramètres société (AA0007, EXP005)
internal sealed record EnteteEditionBl(
    int NumeroBl, string Enseigne, DateOnly? DateExpedition, string RaisonSociale, string Rue1, string Rue2, string CodePostal,
    string Ville, string Pays, IReadOnlyList<string> Observations, string TypePort, string Incoterm, string TypeLivraison,
    string Transporteur, string TransporteurVille, string TransporteurTelephone, string Mention,
    int PoidsTotal, int ColisTotal, int PalettesTotal, int CompteClient,
    string ClientNom, string ClientRue1, string ClientRue2, string ClientCodePostal, string ClientVille,
    string RefFournisseur, string RefExportClient, string RegimeClient, string RefExportSociete,
    bool ImprimerLots, bool AdresseClientEnTete, bool ReferenceClientDabord,
    bool Revendeur, string Presentation, string ClientTelephone, string ClientFax, string ClientEmail,
    string Langue, bool CodeBarreClient, bool CodesBarresSociete, bool DateFabSurLots);

// Élément (EXP010) et sa commande (AB001) ; normes et textes techniques déjà lus
internal sealed record ElementEditionBl(
    int Numero, string Etat, int TypeBl, int Dossier, string Cle, int QuantiteLivree, string EtatLivraison, int Poids,
    string RefImprime, string RefCommande, DateOnly? DateCommande, int NbUc1, int QteUc1, string Uc1, int NbUc2, int QteUc2, string Uc2,
    int Representant, string NomRepresentant, bool QuantiteEnUc, int EmballageCommande, string EnseigneCommande,
    string NormeCommande, IReadOnlyList<string> TextesTechniques, string RefCommandeClient, DateOnly? DateCommandeClient);

// Ligne (EXP015), article (SPF010, SPF022), ligne de commande (NEG100)
internal sealed record LigneEditionBl(
    int Element, int Ligne, string Article, string Indice, int Quantite, IReadOnlyList<string> Designations, string EtatLivraison,
    decimal PoidsColis, int Colis, int EmballagePar, int ColisMecompte, int ExemplairesMecompte, string TypeEmballage,
    bool Imprimee, bool QuantiteEnUc, int EmballageArticle, string RefImprime, string RefClient, string NumeroMarche,
    string UniteLots, string Appel, DateOnly? DateAppel, string Norme, bool CodeBarreArticle = false);

// Lot (EXP016), lot fournisseur (AL00F.PLLOTI) ; pour un lot « n° de dossier + clé » : quantité fabriquée du dossier
// (somme d'AB0020.LAQTEC) et date de sortie de fabrication (DOS010)
internal sealed record LotEditionBl(int Element, int Ligne, string Lot, int Quantite, string LotFournisseur,
    int? QuantiteFabriquee = null, DateOnly? SortieFabrication = null);

// Partiel : représentant limité, des éléments d'autres représentants sont écartés
internal sealed record DonneesEditionBl(
    bool Partiel, EnteteEditionBl Entete, IReadOnlyList<ElementEditionBl> Elements, IReadOnlyList<LigneEditionBl> Lignes,
    IReadOnlyList<LotEditionBl> Lots, IReadOnlyList<string> TextesPied, IReadOnlyDictionary<(string Enseigne, string Code), IReadOnlyList<string>> Normes,
    LibellesBl Libelles);

internal sealed partial class EditionBlDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // null si le BL n'existe pas ou si le représentant limité du profil n'en a aucun élément (comme la consultation)
    public async Task<DonneesEditionBl?> LireAsync(int societe, string profil, int numeroBl, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        var representant = await ConsultationBlDb2.RepresentantImposeAsync(cnx, b, societe, profil, ct);

        EnteteEditionBl entete;
        await using (var rd = await Lire(cnx, """
            SELECT h.HNOBL, h.HENS, h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HRS, h.HRUE1, h.HRUE2, h.HCP, h.HVILLE,
                   COALESCE((SELECT MIN(p.TLIB1) FROM {b}.AA00081 p WHERE p.RSOC = h.RSOC AND p.RTABLE = 'PY' AND p.RARG = h.HPAYS), ''),
                   h.HOBS, h.HOBS2, h.HOBS3, h.HTYPP, h.HOBSIN, h.HTYPL,
                   COALESCE(t.MNOM, ''), TRIM(COALESCE(t.MPOSTA, '')) || ' ' || TRIM(COALESCE(t.MVILLE, '')), COALESCE(t.MTELEP, ''),
                   h.HMENTI, h.HPDSTT, h.HNCOLT, h.HNPALT, h.HCLIE,
                   COALESCE(c.CNOM, ''), COALESCE(c.CRUE1, ''), COALESCE(c.CRUE2, ''), COALESCE(c.CPOSTA, ''), COALESCE(c.CVILLE, ''),
                   COALESCE(c.CREFFO, ''), COALESCE(c.CRFEXP, ''), COALESCE(c.CFREX, ''),
                   COALESCE((SELECT MIN(s.SRFEXP) FROM {b}.AA0007 s WHERE s.RSOC = h.RSOC), ''),
                   COALESCE((SELECT MIN(x.FEOPT1) FROM {b}.EXP005 x WHERE x.RSOC = h.RSOC), ''),
                   COALESCE((SELECT MIN(x.FEOPT2) FROM {b}.EXP005 x WHERE x.RSOC = h.RSOC), ''),
                   COALESCE((SELECT MIN(x.FEDART) FROM {b}.EXP005 x WHERE x.RSOC = h.RSOC), ''),
                   h.HREV, COALESCE(c.CENVBL, ''), COALESCE(c.CTELEP, ''), COALESCE(c.CTELEC, ''), COALESCE(c.CEMAIL, ''),
                   COALESCE(c.CDOP10, ''), COALESCE(c.CDCABB, ''), COALESCE(c.CDOP11, '')
            FROM {b}.EXP009 h
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            LEFT OUTER JOIN {b}.B200F t ON t.RSOC = h.RSOC AND t.RTRANS = h.HTRAN
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numeroBl))
        {
            if (!await rd.ReadAsync(ct)) return null;
            entete = new EnteteEditionBl(rd.Entier(0), rd.Texte(1), BaseDb2.Date(rd.Entier(2), rd.Entier(3), rd.Entier(4)),
                rd.Texte(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10),
                [rd.Texte(11), rd.Texte(12), rd.Texte(13)], rd.Texte(14), rd.Texte(15), rd.Texte(16),
                rd.Texte(17), rd.Texte(18), rd.Texte(19), rd.Texte(20), rd.Entier(21), rd.Entier(22), rd.Entier(23), rd.Entier(24),
                rd.Texte(25), rd.Texte(26), rd.Texte(27), rd.Texte(28), rd.Texte(29), rd.Texte(30), rd.Texte(31), rd.Texte(32),
                rd.Texte(33), rd.Texte(34) != "N", rd.Texte(35) == "O", rd.Texte(36) == "2",
                rd.Texte(37) == "O", rd.Texte(38), rd.Texte(39), rd.Texte(40), rd.Texte(41),
                rd.Texte(42), rd.Texte(43) == "O", rd.Texte(34) == "O", rd.Texte(44) == "O");
        }

        // Textes techniques de la commande imprimés sur le BL (AB0011.CCIBL = 'O')
        var textes = new Dictionary<(int, string), List<string>>();
        await using (var rd = await Lire(cnx, """
            SELECT x.RDOSS, x.CCDCLE, x.CCTXT1, x.CCTXT2, x.CCTXT3, x.CCTXT4, x.CCTXT5, x.CCTXT6, x.CCTXT7, x.CCTXT8
            FROM {b}.AB0011 x
            WHERE x.RSOC = ? AND x.CCIBL = 'O'
              AND EXISTS (SELECT 1 FROM {b}.EXP010 e WHERE e.RSOC = x.RSOC AND e.HNOBL = ? AND e.HNODOS = x.RDOSS AND e.HCLEF = x.CCDCLE)
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                textes[(rd.Entier(0), rd.Texte(1))] = Enumerable.Range(2, 8).Select(rd.Texte).Where(x => x != "").ToList();

        var liste = new List<ElementEditionBl>();
        var ecartes = false;
        await using (var rd = await Lire(cnx, """
            SELECT e.HNUM, e.HETABL, e.HTYPBL, e.HNODOS, e.HCLEF, e.HQTELI, e.HETALI, e.HPDST, e.HREFIM, e.HREFCD,
                   e.HDCDEA, e.HDCDEM, e.HDCDEJ, e.HNUC1, e.HQTUC1, e.HUCDT1, e.HNUC2, e.HQTUC2, e.HREP,
                   COALESCE((SELECT MIN(r.INOMEX) FROM {b}.B000F r WHERE r.RSOC = e.RSOC AND r.RREPR = e.HREP AND r.ITAG <> '*' AND e.HREP <> 0), ''),
                   COALESCE(a.CCOPT5, ''), COALESCE(a.CCEMBP, 0), COALESCE(a.CCENSE, ''), COALESCE(a.CCNORM, ''), e.HUCDT2,
                   COALESCE(a.CCREFC, ''), COALESCE(a.CCDENA, 0), COALESCE(a.CCDENM, 0), COALESCE(a.CCDENJ, 0)
            FROM {b}.EXP010 e
            LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = e.RSOC AND a.RDOSS = e.HNODOS AND a.CCDCLE = e.HCLEF
            WHERE e.RSOC = ? AND e.HNOBL = ? ORDER BY e.HNUM
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
            {
                if (representant is { } rep && rd.Entier(18) != rep)
                {
                    ecartes = true;
                    continue;
                }
                liste.Add(new ElementEditionBl(rd.Entier(0), rd.Texte(1), rd.Entier(2), rd.Entier(3), rd.Texte(4), rd.Entier(5), rd.Texte(6),
                    rd.Entier(7), rd.Texte(8), rd.Texte(9), BaseDb2.Date(rd.Entier(10), rd.Entier(11), rd.Entier(12)),
                    rd.Entier(13), rd.Entier(14), rd.Texte(15), rd.Entier(16), rd.Entier(17), rd.Texte(24), rd.Entier(18), rd.Texte(19),
                    rd.Texte(20) == "O", rd.Entier(21), rd.Texte(22), rd.Texte(23),
                    textes.GetValueOrDefault((rd.Entier(3), rd.Texte(4))) ?? [],
                    rd.Texte(25), BaseDb2.Date(rd.Entier(26), rd.Entier(27), rd.Entier(28))));
            }

        if (representant is not null && liste.Count == 0) return null;
        var visibles = liste.Select(e => e.Numero).ToHashSet();

        // Unité des lots : celle de l'article si son unité de vente compte à l'unité (AA00083 'UV', TNUM = 1), sinon « ex. »
        var lignes = new List<LigneEditionBl>();
        await using (var rd = await Lire(cnx, """
            SELECT l.DNUM, l.DNLIG, l.DART, l.DIND, l.DQTE, l.DDES, l.DDES2, l.DDES3, l.DETALI, l.DPDSU, l.DCOLIS, l.DEMBP1,
                   l.DCOLIM, l.DEXMEC, l.DTYEMB,
                   COALESCE(s.AOPT1, ''), COALESCE(s.AOPT3, ''), COALESCE(s.AEMBP, 0), COALESCE(s.ARFIMP, ''),
                   COALESCE((SELECT MIN(r.ARREF) FROM {b}.SPF022 r WHERE r.RSOC = l.RSOC AND r.AARTIC = l.DART AND r.AINDIC = l.DIND
                             AND r.ARGEN = h.HGENE AND r.ARCLI = h.HCLIE), ''),
                   COALESCE(s.ANOMARC, ''),
                   CASE WHEN (SELECT MIN(u.TNUM) FROM {b}.AA00083 u WHERE u.RSOC = l.RSOC AND u.RTABLE = 'UV' AND u.RARG = s.AUNITE
                              AND u.TTAG <> '*') = 1 THEN s.AUNITE ELSE '' END,
                   COALESCE(n.DNAPPE, ''), COALESCE(n.DNDTAC, 0), COALESCE(n.DNNORM, ''), COALESCE(s.ACABBL, '')
            FROM {b}.EXP015 l
            JOIN {b}.EXP009 h ON h.RSOC = l.RSOC AND h.HNOBL = l.DNBL
            JOIN {b}.EXP010 e ON e.RSOC = l.RSOC AND e.HNOBL = l.DNBL AND e.HNUM = l.DNUM
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.DART AND s.AINDIC = l.DIND AND l.DART <> ' '
            LEFT OUTER JOIN {b}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = e.HNODOS AND n.DNDCLE = e.HCLEF AND n.DNNLIG = l.DNUMLC
                 AND l.DNUMLC <> 0
            WHERE l.RSOC = ? AND l.DNBL = ? ORDER BY l.DNUM, l.DNLIG
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                if (visibles.Contains(rd.Entier(0)))
                    lignes.Add(new LigneEditionBl(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Entier(4),
                    new[] { rd.Texte(5), rd.Texte(6), rd.Texte(7) }.Where(x => x != "").ToList(), rd.Texte(8), rd.Nombre(9),
                    rd.Entier(10), rd.Entier(11), rd.Entier(12), rd.Entier(13), rd.Texte(14),
                    rd.Texte(15) != "N", rd.Texte(16) == "O", rd.Entier(17), rd.Texte(18), rd.Texte(19), rd.Texte(20), rd.Texte(21),
                    rd.Texte(22), BaseDb2.DateAAAAMMJJ(rd.Entier(23)), rd.Texte(24), rd.Texte(25) == "O"));

        // Lot fournisseur : AL00F, type de stock à blanc, premier dépôt (comme la clé partielle d'AL00L4).
        // Lot « n° de dossier (5 chiffres) + clé » de l'article : quantité fabriquée pour la commande (AB0020, clé AB0020L2
        // d'EXP035P2), lue seulement si un code-barres peut être imprimé ; date de sortie de fabrication (DOS010L3), seulement
        // si le client la demande.
        var codesBarres = entete.CodesBarresSociete && (entete.CodeBarreClient || lignes.Any(l => l.CodeBarreArticle));
        var quantiteFab = codesBarres ? """
            (SELECT SUM(a.LAQTEC) FROM {b}.AB0020 a WHERE a.RSOC = t.RSOC AND a.LAART = l.DART AND a.LAIND = l.DIND
              AND DIGITS(a.RDOSS) = SUBSTR(t.DNLOT, 1, 5) AND a.LADCLE = SUBSTR(t.DNLOT, 6, 1)
              AND a.LADOSC = e.HNODOS AND a.LACLDC = e.HCLEF)
            """ : "CAST(NULL AS DECIMAL(11, 0))";
        var sortieFab = entete.DateFabSurLots ? """
            (SELECT f.SFDSAA * 10000 + f.SFDSMM * 100 + f.SFDSJJ FROM {b}.DOS010 f WHERE f.RSOC = t.RSOC
              AND DIGITS(f.SFNDOS) = SUBSTR(t.DNLOT, 1, 5) AND f.SFCLEF = SUBSTR(t.DNLOT, 6, 1) AND f.SFART = l.DART AND f.SFIND = l.DIND
             ORDER BY f.SFNFAB FETCH FIRST 1 ROW ONLY)
            """ : "CAST(NULL AS DECIMAL(8, 0))";
        var lots = new List<LotEditionBl>();
        await using (var rd = await Lire(cnx, $$"""
            SELECT t.DNUM, t.DNLIG, t.DNLOT, t.DNQTPL,
                   COALESCE((SELECT MIN(f.PLLOTI) FROM {b}.AL00F f WHERE f.RSOC = t.RSOC AND f.PLTSTO = ' ' AND f.PLREF = l.DART
                             AND f.PLIND = l.DIND AND f.PLLOT = t.DNLOT), ''),
                   {{quantiteFab}}, {{sortieFab}}
            FROM {b}.EXP016 t
            JOIN {b}.EXP015 l ON l.RSOC = t.RSOC AND l.DNBL = t.DNBL AND l.DNUM = t.DNUM AND l.DNLIG = t.DNLIG
            JOIN {b}.EXP010 e ON e.RSOC = t.RSOC AND e.HNOBL = t.DNBL AND e.HNUM = t.DNUM
            WHERE t.RSOC = ? AND t.DNBL = ? ORDER BY t.DNUM, t.DNLIG, t.DNLOT, t.DNEMP
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                if (visibles.Contains(rd.Entier(0)))
                    lots.Add(new LotEditionBl(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Texte(4),
                        rd.Texte(5) == "" ? null : rd.Entier(5), BaseDb2.DateAAAAMMJJ(rd.Entier(6))));

        var pied = new List<string>();
        await using (var rd = await Lire(cnx, "SELECT HTTXT FROM {b}.EXP012 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);

        var demandes = liste.Where(e => e.NormeCommande != "").Select(e => (e.EnseigneCommande, e.NormeCommande))
            .Concat(lignes.Where(l => l.Norme != "").Select(l => (liste.First(e => e.Numero == l.Element).EnseigneCommande, l.Norme)));
        var normes = await NormesAsync(cnx, societe, demandes, entete.DateExpedition, ct);
        var libelles = await LibellesAsync(cnx, entete.Langue, ct);

        return new DonneesEditionBl(ecartes, entete, liste, lignes, lots, pied, normes, libelles);
    }

    // Mentions de norme (NOR010F) : enseigne de la commande, version la plus récente commencée à la date d'expédition
    // (SETLL date / READPE d'EXP035P2)
    private async Task<Dictionary<(string, string), IReadOnlyList<string>>> NormesAsync(OdbcConnection cnx, int societe,
        IEnumerable<(string Enseigne, string Code)> demandes, DateOnly? expedition, CancellationToken ct)
    {
        var normes = new Dictionary<(string, string), IReadOnlyList<string>>();
        var date = expedition is { } d ? BaseDb2.AAAAMMJJ(d) : 0;
        foreach (var (ens, code) in demandes.Distinct())
            await using (var rd = await Lire(cnx, """
                SELECT NOBL1, NOBL2, NOBL3, NOBL4 FROM {b}.NOR010F
                WHERE RSOC = ? AND NOENS = ? AND NOCOD = ? AND NODATED <= ?
                ORDER BY NODATED DESC, NODATE DESC FETCH FIRST 1 ROW ONLY
                """, ct, societe, ens, code, date))
                normes[(ens, code)] = await rd.ReadAsync(ct) ? Enumerable.Range(0, 4).Select(rd.Texte).Where(x => x != "").ToList() : [];
        return normes;
    }

    // Libellés dans la langue du client (PRT008 programme « BL », listes PRT009)
    private async Task<LibellesBl> LibellesAsync(OdbcConnection cnx, string langue, CancellationToken ct)
    {
        if (langue == "") return LibellesBl.EnFrancais;
        var traductions = new List<string>();
        var colonnes = string.Join(", ", Enumerable.Range(1, 50).Select(i => $"TRADUC{i}"));
        await using (var rd = await Lire(cnx, $"SELECT {colonnes} FROM {{b}}.PRT008 WHERE MLANGUE = ? AND MPGM = 'BL'", ct, langue))
            if (await rd.ReadAsync(ct)) traductions.AddRange(Enumerable.Range(0, 50).Select(rd.Texte));
        var listes = new Dictionary<(int, string), string>();
        await using (var rd = await Lire(cnx, """
            SELECT Y2LSNO, Y2EXVL, Y2CNTX FROM {b}.PRT009 WHERE LLANGUE = ? AND Y2LSNO IN (28, 30, 31, 32)
            """, ct, langue))
            while (await rd.ReadAsync(ct)) listes[(rd.Entier(0), rd.Texte(1))] = rd.Texte(2);
        return LibellesBl.Pour(traductions, listes);
    }

    // Marque « édité » d'EXP035P2 (seule écriture de l'édition) : HEDBL = 'N' et date d'édition
    public async Task MarquerEditeAsync(int societe, int numeroBl, DateOnly jour, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"UPDATE {Bib}.EXP009 SET HEDBL = 'N', HDEDBA = ?, HDEDBM = ?, HDEDBJ = ? WHERE RSOC = ? AND HNOBL = ?";
        foreach (var v in new object[] { jour.Year, jour.Month, jour.Day, societe, numeroBl }) cmd.Parameters.Valeur(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private Task<Lecteur> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs) =>
        Lecteur.ExecuterAsync(cnx, sql.Replace("{b}", Bib), ct, valeurs);
}
