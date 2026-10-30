using System.Data.Odbc;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Lectures DB2 de la consultation des devis.
// Requêtes reprises de FEN_ConsultationDevis (BTN_Chargement, InitVarGlobales) et de FEN_DetailDevis
// (InitAtelier, InitElt, InitPapier, InitMatiere, InitStr, InitRub, InitOutil), en SQL paramétré.
// Écarts volontaires avec le WinDev (signalés « ÉCART ») : uniquement pour corriger une erreur.
// =====================================================================
internal sealed class ConsultationDevisDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // ---------- Restriction représentant (InitVarGlobales) ----------
    public async Task<int?> RepresentantImposeAsync(int societe, string profil, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = cnx.CreateCommand();
        cmd.CommandText = $"SELECT RREPR, ILIMIT FROM {Bib}.B000F WHERE RSOC = ? AND IPROF = ? AND ITAG <> '*' FETCH FIRST 1 ROW ONLY";
        cmd.Parameters.Valeur(societe);
        cmd.Parameters.Valeur(profil.ToUpperInvariant());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return rd.Texte(1) == "O" ? rd.Entier(0) : null;
    }

    // ---------- Liste (BTN_Chargement) ----------
    public async Task<ResultatRecherche<LigneListeBrute>> RechercherAsync(int societe, CriteresDevis c, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = cnx.CreateCommand();
        var sql = new StringBuilder($"""
            SELECT a.RGEN, a.RCLI, a.RPROSP, b.CNOM, c.DRS, a.DEDATA, a.DEDATM, a.DEDATJ, a.DEQTED, a.RDEV, a.RVERS, a.RHYPO,
                   a.DEDES1, a.DEDES2, a.DELRFF, a.DEHAFF, a.DE3CFF, a.DELRFA, a.DEHAFA, a.DENCAH, a.DENORM, a.DEREPR,
                   a.DEINAT, a.DECONT, a.DEPROF, a.DEPVM, a.DEPVC, a.DEPVR, a.DEDTPA, a.DEDTPM, a.DEDTPJ, a.DECAT, a.DESCAT,
                   a.DEMODP, d.RDOSS, d.CCDCLE, a.DEDOSP, a.DECLEP, a.DEETUD, x.F6DATEE, x.F6MOTIF, a.DETAG, y.INOM, z.ACNOM,
                   aa.TLIB1, ab.TLIB1,
                   (SELECT COUNT(*) FROM {b}.OFFRE o WHERE o.RSOC = a.RSOC AND o.RDEV = a.RDEV AND o.RVERS = a.RVERS),
                   a.DEUNIT, a.RCOUT,
                   (SELECT COUNT(*) FROM {b}.D50008 g WHERE g.RSOC = a.RSOC AND g.RDEV = a.RDEV),
                   a.DEOBSP, e.TNUM, a.DERFIM, a.DEDDEM
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA0003 b ON a.RSOC = b.RSOC AND a.RGEN = b.RGEN AND a.RCLI = b.RCLI AND b.CTAG <> '*'
            LEFT OUTER JOIN {b}.AX00F c ON a.RSOC = c.RSOC AND a.RPROSP = c.RPROSP AND c.DTAG <> '*'
            LEFT OUTER JOIN {b}.AB001 d ON a.RSOC = d.RSOC AND a.RDEV = d.CCDEV AND a.RVERS = d.CCVERS AND a.RHYPO = d.CCHYP
            LEFT OUTER JOIN {b}.D50006 x ON a.RSOC = x.RSOC AND a.RDEV = x.RDEV
            LEFT OUTER JOIN {b}.B000F y ON a.RSOC = y.RSOC AND a.DEREPR = y.RREPR AND y.ITAG <> '*'
            LEFT OUTER JOIN {b}.AI00F z ON a.DEINAT = z.ACINI AND z.ACFAB = 'O'
            LEFT OUTER JOIN {b}.AA0009 aa ON a.RSOC = aa.RSOC AND a.DECAT = aa.RCAT AND aa.TTAG <> '*'
            LEFT OUTER JOIN {b}.AE0003 ab ON a.RSOC = ab.RSOC AND a.DECAT = ab.RCAT AND a.DESCAT = ab.RSCAT AND ab.TTAG <> '*'
            LEFT OUTER JOIN {b}.AA00083 e ON a.RSOC = e.RSOC AND a.DEUNIT = e.RARG AND e.RTABLE = 'UV' AND e.TTAG <> '*'
            WHERE a.RSOC = ?
            """);
        // ÉCART : le WinDev lisait l'offre par « (select rdev from offre ...) », qui échoue s'il existe une offre
        // en plusieurs langues pour la même version ; on compte les offres.
        cmd.Parameters.Valeur(societe);

        void Et(string condition, params object?[] valeurs)
        {
            sql.Append(" AND ").Append(condition);
            foreach (var v in valeurs) cmd.Parameters.Valeur(v);
        }
        // Sous-requête « au moins un élément / une ligne du devis qui... » (le WinDev écrivait « valeur in (select ...) »)
        string Element(string condition) =>
            $"EXISTS (SELECT 1 FROM {b}.D60001 e1 WHERE e1.RSOC = a.RSOC AND e1.RDEV = a.RDEV AND e1.RVERS = a.RVERS " +
            $"AND e1.RHYPO = a.RHYPO AND e1.RCOUT = a.RCOUT AND {condition})";
        string Ligne(string type, string condition) =>
            $"EXISTS (SELECT 1 FROM {b}.D60002 l1 WHERE l1.RSOC = a.RSOC AND l1.RDEV = a.RDEV AND l1.RVERS = a.RVERS " +
            $"AND l1.RHYPO = a.RHYPO AND l1.RCOUT = a.RCOUT AND l1.DDTYPL = '{type}' AND l1.DDRSQT > 0 AND {condition})";

        if (c.Numero is > 0)
        {
            // N° de devis saisi : seuls le représentant imposé et le type de devis s'ajoutent
            Et("a.RDEV = ?", c.Numero);
            if (c.Representant is > 0) Et("a.DEREPR = ?", c.Representant);
            if (c.TypeDevis is "T" or "C") Et("a.RCOUT = ?", c.TypeDevis);
        }
        else
        {
            if (c.Etude is > 0) Et("a.DEETUD = ?", c.Etude);
            if (c.CompteClient is > 0) Et("a.RGEN = ? AND a.RCLI = ?", c.CompteGeneral ?? 0, c.CompteClient);
            if (c.Prospect is > 0) Et("a.RPROSP = ?", c.Prospect);
            if (Rempli(c.Profil)) Et("a.DEPROF = ?", c.Profil!.Trim().ToUpperInvariant());
            if (Rempli(c.Categorie)) Et("a.DECAT = ?", c.Categorie!.Trim().ToUpperInvariant());
            if (Rempli(c.SousCategorie)) Et("a.DESCAT = ?", c.SousCategorie!.Trim().ToUpperInvariant());
            if (Rempli(c.Fabricant)) Et("a.DEINAT = ?", c.Fabricant!.Trim().ToUpperInvariant());
            if (c.Representant is > 0) Et("a.DEREPR = ?", c.Representant);
            if (Rempli(c.Norme)) Et("a.DENORM = ?", c.Norme!.Trim().ToUpperInvariant());
            if (c.NbElements is > 0) Et("a.DENCAH = ?", c.NbElements);
            if (c.Modele is > 0) Et("a.DEMODP = ?", c.Modele);
            if (c.Dossier is > 0) Et("d.RDOSS = ?", c.Dossier);
            if (c.DossierPrecedent is > 0) Et("a.DEDOSP = ?", c.DossierPrecedent);

            if (c.Papier is > 0) Et(Element("e1.DDPAPI = ?"), c.Papier);
            if (Rempli(c.Sorte)) Et(Element("e1.DDSORT = ?"), c.Sorte!.Trim().ToUpperInvariant());
            if (Rempli(c.SousSorte)) Et(Element("e1.DDSSOR = ?"), c.SousSorte!.Trim().ToUpperInvariant());
            if (c.Grammage is > 0) Et(Element("e1.DDGRAM = ?"), c.Grammage);
            if (c.LargeurPapier is > 0) Et(Element("e1.DDLRFP = ?"), c.LargeurPapier);
            if (c.HauteurPapier is > 0) Et(Element("e1.DDHAFP = ?"), c.HauteurPapier);
            if (Rempli(c.Coloris)) Et(Element("e1.DDCOUL = ?"), c.Coloris!.Trim().ToUpperInvariant());
            if (Rempli(c.MachineTirage)) Et(Element("e1.DDSECT = ?"), c.MachineTirage!.Trim().ToUpperInvariant());
            if (c.TirageLargeur is > 0) Et(Element("e1.DDLRFT = ?"), c.TirageLargeur);
            if (c.TirageHauteur is > 0) Et(Element("e1.DDHAFT = ?"), c.TirageHauteur);
            if (c.NbPoses is > 0) Et(Element("e1.DDPOSE = ?"), c.NbPoses);
            if (c.Outil is > 0)
                Et($"EXISTS (SELECT 1 FROM {b}.D6001C o1 WHERE o1.RSOC = a.RSOC AND o1.RDEV = a.RDEV AND o1.RVERS = a.RVERS " +
                   "AND o1.RHYPO = a.RHYPO AND o1.RCOUT = a.RCOUT AND (o1.DDNOUT = ? OR o1.DDNOU1 = ? OR o1.DDNOU2 = ?))",
                   c.Outil, c.Outil, c.Outil);
            if (Rempli(c.Section)) Et(Ligne("O", "SUBSTR(l1.DDREF, 1, 3) = ?"), c.Section!.Trim().ToUpperInvariant());
            if (c.Operation is > 0) Et(Ligne("O", "SUBSTR(l1.DDREF, 4, 3) = ?"), c.Operation.Value.ToString("D3"));
            if (c.Matiere is > 0) Et(Ligne("M", "l1.DDREF = ?"), c.Matiere.Value.ToString("D6"));
            if (c.FournisseurSousTraitance is > 0)
                Et(Ligne("S", "SUBSTR(l1.DDREF, 1, 3) = ?"), c.FournisseurSousTraitance.Value.ToString("D3"));
            if (c.OperationSousTraitance is > 0)
                Et(Ligne("S", "SUBSTR(l1.DDREF, 4, 3) = ?"), c.OperationSousTraitance.Value.ToString("D3"));
            if (c.Epaisseur is > 0)
            {
                Et(Element("e1.DDEPAIS = ?"), c.Epaisseur);
                Et(Element("e1.DDUNEPAI = ?"), c.UniteEpaisseur);
            }

            const string dateDevis = "(a.DEDATA * 10000 + a.DEDATM * 100 + a.DEDATJ)";
            if (c.DateDu is { } du) Et($"{dateDevis} >= ?", BaseDb2.AAAAMMJJ(du));
            if (c.DateAu is { } au) Et($"{dateDevis} <= ?", BaseDb2.AAAAMMJJ(au));
            if (c.QuantiteDe is > 0) Et("a.DEQTED >= ?", c.QuantiteDe);
            if (c.QuantiteA is > 0) Et("a.DEQTED <= ?", c.QuantiteA);
            if (c.NbElementsExemplaire is > 0) Et("a.DENCAH = ?", c.NbElementsExemplaire);
            if (Rempli(c.MotifEchec)) Et("x.F6MOTIF = ?", c.MotifEchec!.Trim().ToUpperInvariant());
            if (c.EchecDu is { } edu) Et("x.F6DATEE >= ?", BaseDb2.AAAAMMJJ(edu));
            if (c.EchecAu is { } eau) Et("x.F6DATEE <= ?", BaseDb2.AAAAMMJJ(eau));
            if (c.FiniLargeur is > 0) Et("a.DELRFF = ?", c.FiniLargeur);
            if (c.FiniHauteur is > 0) Et("a.DEHAFF = ?", c.FiniHauteur);
            if (c.FiniTroisiemeCote is > 0) Et("a.DE3CFF = ?", c.FiniTroisiemeCote);
            if (c.OuvertLargeur is > 0) Et("a.DELRFA = ?", c.OuvertLargeur);
            if (c.OuvertHauteur is > 0) Et("a.DEHAFA = ?", c.OuvertHauteur);

            if (c.EtatDevis == "1") Et("a.DETAG = ' '");
            if (c.EtatDevis == "2") Et("a.DETAG = '*'");
            if (c.TypeDevis is "T" or "C") Et("a.RCOUT = ?", c.TypeDevis);

            if (Rempli(c.Designation))
                foreach (var mot in c.Designation!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var motif = $"%{mot.ToUpperInvariant()}%";
                    Et("(UPPER(a.DEDES1) LIKE ? OR UPPER(a.DEDES2) LIKE ?)", motif, motif);
                }
            if (Rempli(c.ReferenceImprime)) Et("a.DERFIM LIKE ?", c.ReferenceImprime!.Trim() + "%");
        }

        sql.Append($" ORDER BY a.RDEV DESC, a.RVERS DESC, a.RHYPO DESC FETCH FIRST {LimitesRecherche.Maximum + 1} ROWS ONLY");
        cmd.CommandText = sql.ToString();

        var lignes = new List<LigneListeBrute>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            if (lignes.Count == LimitesRecherche.Maximum) return new(lignes, Tronque: true);
            lignes.Add(new LigneListeBrute(
                Rgen: rd.Entier(0), Rcli: rd.Entier(1), Rprosp: rd.Entier(2), Cnom: rd.Texte(3), Drs: rd.Texte(4),
                Dedata: rd.Entier(5), Dedatm: rd.Entier(6), Dedatj: rd.Entier(7), Deqted: rd.Entier(8),
                Rdev: rd.Entier(9), Rvers: rd.Entier(10), Rhypo: rd.Entier(11),
                Dedes1: rd.Texte(12), Dedes2: rd.Texte(13), Delrff: rd.Nombre(14), Dehaff: rd.Nombre(15), De3cff: rd.Nombre(16),
                Delrfa: rd.Nombre(17), Dehafa: rd.Nombre(18), Dencah: rd.Entier(19), Denorm: rd.Texte(20), Derepr: rd.Entier(21),
                Deinat: rd.Texte(22), Decont: rd.Texte(23), Deprof: rd.Texte(24), Depvm: rd.Nombre(25), Depvc: rd.Nombre(26),
                Depvr: rd.Nombre(27), Dedtpa: rd.Entier(28), Dedtpm: rd.Entier(29), Dedtpj: rd.Entier(30),
                Decat: rd.Texte(31), Descat: rd.Texte(32), Demodp: rd.Entier(33), Rdoss: rd.Entier(34), Ccdcle: rd.Texte(35),
                Dedosp: rd.Entier(36), Declep: rd.Texte(37), Deetud: rd.Entier(38), F6datee: rd.Entier(39), F6motif: rd.Texte(40),
                Detag: rd.Texte(41), Inom: rd.Texte(42), Acnom: rd.Texte(43), LibCat: rd.Texte(44), LibSousCat: rd.Texte(45),
                NbOffres: rd.Entier(46), Deunit: rd.Texte(47), Rcout: rd.Texte(48), NbDocuments: rd.Entier(49),
                Deobsp: rd.Texte(50), Tnum: rd.Entier(51), Derfim: rd.Texte(52), Deddem: rd.Entier(53)));
        }
        return new(lignes, Tronque: false);
    }

    // ---------- Détail (FEN_DetailDevis) ----------
    public async Task<DetailBrut?> LireDetailAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);

        // 1. En-tête (déclaration de la fenêtre)
        EnteteBrute? entete = null;
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RGEN, a.RCLI, a.RPROSP, b.CNOM, c.DRS, a.DEDES1, a.DEDES2, a.DEUNIT, d.TNUM, a.DENCAH, a.DELRFF, a.DEHAFF,
                   a.DE3CFF, a.DELRFA, a.DEHAFA, a.DEQTED, a.DETXGA, a.DECAT, e.TTYPDT, a.DEREPR
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA0003 b ON a.RSOC = b.RSOC AND a.RGEN = b.RGEN AND a.RCLI = b.RCLI AND b.CTAG <> '*'
            LEFT OUTER JOIN {b}.AX00F c ON a.RSOC = c.RSOC AND a.RPROSP = c.RPROSP AND c.DTAG <> '*'
            LEFT OUTER JOIN {b}.AA00083 d ON a.RSOC = d.RSOC AND a.DEUNIT = d.RARG AND d.RTABLE = 'UV' AND d.TTAG <> '*'
            LEFT OUTER JOIN {b}.AA0009 e ON a.RSOC = e.RSOC AND a.DECAT = e.RCAT AND e.TTAG <> '*'
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ?
            """, societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct))
                entete = new EnteteBrute(rd.Entier(0), rd.Entier(1), rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Texte(5),
                    rd.Texte(6), rd.Texte(7), rd.Entier(8), rd.Entier(9), rd.Nombre(10), rd.Nombre(11), rd.Nombre(12),
                    rd.Nombre(13), rd.Nombre(14), rd.Entier(15), rd.Entier(16), rd.Texte(17), rd.Texte(18), rd.Entier(19));
        if (entete is null) return null;

        // 2. Perte de fin de bobine (AA0002.TPPERB) : type de produit + catégorie, à défaut catégorie à blanc
        decimal perteBobine = 0;
        await using (var cmd = Commande(cnx, $"""
            SELECT TPPERB FROM {b}.AA0002 WHERE RSOC = ? AND RTYPD = ? AND RCAT = ?
            UNION ALL
            SELECT TPPERB FROM {b}.AA0002 WHERE RSOC = ? AND RTYPD = ? AND RCAT = ' '
            """, societe, entete.Ttypdt, entete.Decat, societe, entete.Ttypdt))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var p = rd.Nombre(0);
                if (p > 0) { perteBobine = p; break; }
            }

        var parametresDevis = new object?[] { societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout };

        // 3. Lignes de fabrication (InitAtelier, InitElt, InitMatiere, InitStr) : toutes les lignes à résultat > 0
        var lignes = new List<LigneFabBrute>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.DDREF, a.DDBASE, a.DDTPS, a.DDCADH, a.DDDIV, a.DDRSQT, a.DDPRMA, a.DDVALO, a.DDRSMP, a.DDCOMF, a.DDQTED,
                   a.DDPXFP, b.RSLIB, c.OLIB, CASE WHEN d.GNOM IS NULL THEN 'Matière inexistante' ELSE d.GNOM END,
                   a.DDTYPL, a.DDATEL, e.TLIB1, a.RELMT, f.DDDESI, a.DDPRMO, a.DDNBR, a.DDLBRF, f.DDSENS, a.DDSEQ,
                   a.DDPXF, a.DDQP
            FROM {b}.D60002 a
            LEFT OUTER JOIN {b}.C800F b ON a.RSOC = b.RSOC AND SUBSTR(a.DDREF, 1, 3) = b.RSECT AND b.RSTAG <> '*'
            LEFT OUTER JOIN {b}.D000F c ON a.RSOC = c.RSOC AND SUBSTR(a.DDREF, 4, 3) = DIGITS(c.ROPE) AND c.OTAG <> '*'
            LEFT OUTER JOIN {b}.C000F d ON a.RSOC = d.RSOC AND a.DDREF = DIGITS(d.RMATIE) AND d.GTAG <> '*'
            LEFT OUTER JOIN {b}.AA00081 e ON a.RSOC = e.RSOC AND e.RTABLE = 'AT' AND a.DDATEL = e.RARG AND e.TTAG <> '*'
            LEFT OUTER JOIN {b}.D60001 f ON a.RSOC = f.RSOC AND a.RDEV = f.RDEV AND a.RVERS = f.RVERS AND a.RHYPO = f.RHYPO
                 AND a.RCOUT = f.RCOUT AND a.RELMT = f.RELMT
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ? AND a.DDRSQT > 0
            ORDER BY a.DDTYPL, a.DDATEL, a.RELMT, a.DDSEQ, a.DDREF
            """, parametresDevis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                lignes.Add(new LigneFabBrute(
                    Ref: rd.Texte(0), Base: rd.Nombre(1), Temps: rd.Nombre(2), Cadence: rd.Entier(3), Diviseur: rd.Entier(4),
                    Resultat: rd.Nombre(5), PrixMachine: rd.Nombre(6), Valeur: rd.Nombre(7), ResultatMillePlus: rd.Nombre(8),
                    Commentaire: rd.Texte(9), Quantite: rd.Nombre(10), PrixProportionnel: rd.Nombre(11), LibSection: rd.Texte(12),
                    LibOperation: rd.Texte(13), LibMatiere: rd.Texte(14), TypeLigne: rd.Texte(15), Atelier: rd.Texte(16),
                    LibAtelier: rd.Texte(17), Element: rd.Entier(18), DesignationElement: rd.Texte(19), PrixMainOeuvre: rd.Nombre(20),
                    Nombre: rd.Entier(21), LibelleLibre: rd.Texte(22), Sens: rd.Texte(23), Sequence: rd.Entier(24),
                    PrixFixe: rd.Nombre(25), QuantiteProportionnelle: rd.Entier(26)));

        // 4. Éléments D60001 (InitPapier, InitElt) + outils D6001C + nombre de lignes de l'élément
        var elements = new List<ElementBrut>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RELMT, a.DDSORT, a.DDSSOR, a.DDGRAM, a.DDLRFP, a.DDHAFP, a.DDCOUL, a.DDLRFT, a.DDHAFT, a.DDPAGE, a.DDCRO,
                   a.DDCVO, a.DDCBAS, a.DDNTRG, a.DDGACV, a.DDNTR, a.DDPDSP, a.DDNML, a.DDPA,
                   CASE WHEN a.DDUA = 'KG' THEN a.DDPDSP * a.DDPA
                        ELSE {b}.CONVUP(CHAR('KG'), CHAR(a.DDUA), CHAR(a.DDFOUB), a.DDLRFP, a.DDHAFP, a.DDGRAM, a.DDPDSP) * a.DDPA END,
                   b.TLIB1, c.TLIB1, a.DDUA, a.DDPROV, a.DDDESI, a.DDPOSE, a.DDCON, a.DDEPAIS, a.DDUNEPAI, a.DDQTEE, c.TNATSU,
                   d.TLIB1, c.TNORM, a.DDFOUB, g.TLIB1, a.DDNBFT, a.DDPAPI,
                   a.DDLRFF, a.DDHAFF, a.DD3CFF, a.DDLRFO, a.DDHAFO, a.DDSENS, o.DDNOUT, o.DDNOU1, o.DDNOU2,
                   (SELECT COUNT(*) FROM {b}.D60002 l WHERE l.RSOC = a.RSOC AND l.RDEV = a.RDEV AND l.RVERS = a.RVERS
                        AND l.RHYPO = a.RHYPO AND l.RCOUT = a.RCOUT AND l.RELMT = a.RELMT),
                   (SELECT COUNT(*) FROM {b}.D60004 t WHERE t.RSOC = a.RSOC AND t.RDEV = a.RDEV AND t.RVERS = a.RVERS
                        AND t.RHYPO = a.RHYPO AND t.RCOUT = a.RCOUT AND t.RELMT = a.RELMT)
            FROM {b}.D60001 a
            LEFT OUTER JOIN {b}.AA00081 b ON a.RSOC = b.RSOC AND a.DDSORT = b.RARG AND b.RTABLE = 'SP' AND b.TTAG <> '*'
            LEFT OUTER JOIN {b}.AE0004 c ON a.RSOC = c.RSOC AND a.DDSORT = c.RSORTE AND a.DDSSOR = c.RSSORT AND c.TTAG <> '*'
            LEFT OUTER JOIN {b}.AA00081 d ON c.RSOC = d.RSOC AND DIGITS(c.TNATSU) = d.RARG AND d.RTABLE = 'NT' AND d.TTAG <> '*'
            LEFT OUTER JOIN {b}.AA00081 g ON a.RSOC = g.RSOC AND a.DDCOUL = g.RARG AND g.RTABLE = 'KL' AND g.TTAG <> '*'
            LEFT OUTER JOIN {b}.D6001C o ON a.RSOC = o.RSOC AND a.RDEV = o.RDEV AND a.RVERS = o.RVERS AND a.RHYPO = o.RHYPO
                 AND a.RCOUT = o.RCOUT AND a.RELMT = o.RELMT
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ?
            ORDER BY a.RELMT
            """, parametresDevis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                elements.Add(new ElementBrut(
                    Element: rd.Entier(0), Sorte: rd.Texte(1), SousSorte: rd.Texte(2), Grammage: rd.Entier(3),
                    LargeurPapier: rd.Entier(4), HauteurPapier: rd.Entier(5), Coloris: rd.Texte(6), LargeurTirage: rd.Entier(7),
                    HauteurTirage: rd.Entier(8), Pages: rd.Entier(9), Recto: rd.Entier(10), Verso: rd.Entier(11),
                    Basculees: rd.Entier(12), GacheFixe: rd.Entier(13), GacheVariable: rd.Entier(14), Tours: rd.Entier(15),
                    Poids: rd.Nombre(16), Metrage: rd.Entier(17), Prix: rd.Nombre(18), Valeur: rd.Nombre(19),
                    LibSorte: rd.Texte(20), LibSousSorte: rd.Texte(21), UniteAchat: rd.Texte(22), Provenance: rd.Texte(23),
                    Designation: rd.Texte(24), Poses: rd.Entier(25), Conditionnement: rd.Entier(26), Epaisseur: rd.Entier(27),
                    UniteEpaisseur: rd.Texte(28), QuantiteElement: rd.Entier(29), NatureSupport: rd.Entier(30),
                    LibNature: rd.Texte(31), Norme: rd.Texte(32), FeuilleBobine: rd.Texte(33), LibColoris: rd.Texte(34),
                    FeuillesParTour: rd.Entier(35), Papier: rd.Entier(36), FiniLargeur: rd.Nombre(37), FiniHauteur: rd.Nombre(38),
                    FiniTroisiemeCote: rd.Nombre(39), OuvertLargeur: rd.Nombre(40), OuvertHauteur: rd.Nombre(41),
                    Sens: rd.Texte(42), Outil: rd.Entier(43), Outil1: rd.Entier(44), Outil2: rd.Entier(45),
                    NbLignes: rd.Entier(46), NbTextesLibres: rd.Entier(47)));

        // 5. Rubriques et réponses (InitRub), tous éléments
        var rubriques = new List<RubriqueBrute>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RELMT, a.DDRUBF, a.DDRES1, a.DDRES2, a.DDRES3, a.DDRES4, a.DDRES5,
                   b.TRLIBR, b.TRQUE1, b.TRQUE2, b.TRQUE3, b.TRQUE4, b.TRQUE5
            FROM {b}.D60002 a JOIN {b}.E300F b ON a.RSOC = b.RSOC AND a.DDRUBF = b.RRUB
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ? AND a.DDRUBF > 0
            GROUP BY a.RELMT, a.DDRUBF, a.DDRES1, a.DDRES2, a.DDRES3, a.DDRES4, a.DDRES5,
                     b.TRLIBR, b.TRQUE1, b.TRQUE2, b.TRQUE3, b.TRQUE4, b.TRQUE5
            ORDER BY a.RELMT, a.DDRUBF
            """, parametresDevis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                rubriques.Add(new RubriqueBrute(rd.Entier(0), rd.Texte(7),
                    [rd.Texte(8), rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Texte(12)],
                    [rd.Nombre(2), rd.Nombre(3), rd.Nombre(4), rd.Nombre(5), rd.Nombre(6)]));

        // 6. Transport (D50001)
        var transports = new List<LigneTransport>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.DEQTEL, a.DEDEP, a.DEBART, a.DETRSA, a.DEPDSL, a.DEMTP1, a.DEMTPM, b.MNOM, a.DECOMM,
                   CASE WHEN a.DEVILL <> ' ' THEN a.DEVILL WHEN a.DEPRIO > 0 THEN c.ALVILL WHEN ? > 0 THEN d.CVILLE
                        WHEN ? > 0 THEN e.DVILLE END,
                   CASE WHEN a.DEVILL <> ' ' THEN a.DERS WHEN a.DEPRIO > 0 THEN c.ALRS WHEN ? > 0 THEN d.CNOM
                        WHEN ? > 0 THEN e.DRS END
            FROM {b}.D50001 a
            LEFT OUTER JOIN {b}.B200F b ON a.RSOC = b.RSOC AND a.DETRSA = b.RTRANS
            LEFT OUTER JOIN {b}.A500F c ON a.RSOC = c.RSOC AND c.RGEN = ? AND c.RCLI = ? AND a.DEPRIO = c.ALNLIG
            LEFT OUTER JOIN {b}.AA0003 d ON a.RSOC = d.RSOC AND d.RGEN = ? AND d.RCLI = ?
            LEFT OUTER JOIN {b}.AX00F e ON a.RSOC = e.RSOC AND e.RPROSP = ?
            WHERE a.RSOC = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ? AND a.RCOUT = ?
            """,
            entete.Rcli, entete.Rprosp, entete.Rcli, entete.Rprosp, entete.Rgen, entete.Rcli, entete.Rgen, entete.Rcli,
            entete.Rprosp, societe, cle.Numero, cle.Version, cle.Hypothese, cle.TypeCout))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                transports.Add(new LigneTransport(rd.Entier(0), rd.Texte(1), rd.Entier(2), $"{rd.Entier(3)} {rd.Texte(7)}".Trim(),
                    rd.Entier(4), rd.Nombre(5), rd.Nombre(6), rd.Texte(9), rd.Texte(8), rd.Texte(10)));

        // 7. Frais annexes (D50005) : par devis et version (pas d'hypothèse, comme le WinDev)
        var frais = new List<LigneFraisAnnexes>();
        await using (var cmd = Commande(cnx, $"""
            SELECT F15ART, F15DES, F15QTE, F15PRU, F15PMA, F15PVT FROM {b}.D50005
            WHERE RSOC = ? AND RDEV = ? AND RVERS = ? AND RCOUT = ?
            """, societe, cle.Numero, cle.Version, cle.TypeCout))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                frais.Add(new LigneFraisAnnexes(rd.Texte(0), rd.Texte(1), rd.Entier(2), rd.Nombre(3), rd.Nombre(4), rd.Nombre(5)));

        // 8. Outils de découpe (FOU001P)
        var outils = new Dictionary<int, OutilElement>();
        foreach (var n in elements.Where(e => e.Outil != 0).Select(e => e.Outil).Distinct())
        {
            await using var cmd = Commande(cnx, $"""
                SELECT OFMTHA, OFMTLA, OCAC, OPAS, OPOLZE, OPODVL, ORAYON, OFORME FROM {b}.FOU001P WHERE RSOC = ? AND ONOUT = ?
                """, societe, n);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) continue;
            decimal avance = rd.Nombre(0), laize = rd.Nombre(1), cote = rd.Nombre(2), pas = rd.Nombre(3);
            int posesLaize = rd.Entier(4), posesDev = rd.Entier(5);
            var forme = rd.Texte(7) switch
            {
                "C" => "Carré", "D" => "Rond", "O" => "Ovale", "R" => "Rectangle", "S" => "Silhouette", "T" => "Triangle", _ => "",
            };
            outils[n] = new OutilElement(n, forme, laize, avance, cote, pas, posesLaize, posesDev, rd.Nombre(6),
                EcartLaize: pas - laize, EcartAvance: cote - avance, LaizeUtile: posesLaize * cote, AvanceUtile: posesDev * pas);
        }

        return new DetailBrut(entete, perteBobine, lignes, elements, rubriques, transports, frais, outils);
    }

    private static bool Rempli(string? s) => !string.IsNullOrWhiteSpace(s);

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, string sql, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v);
        return cmd;
    }
}

// ---------- Lignes brutes lues en base (noms des zones AS400 entre parenthèses quand utile) ----------
internal sealed record LigneListeBrute(
    int Rgen, int Rcli, int Rprosp, string Cnom, string Drs, int Dedata, int Dedatm, int Dedatj, int Deqted,
    int Rdev, int Rvers, int Rhypo, string Dedes1, string Dedes2, decimal Delrff, decimal Dehaff, decimal De3cff,
    decimal Delrfa, decimal Dehafa, int Dencah, string Denorm, int Derepr, string Deinat, string Decont, string Deprof,
    decimal Depvm, decimal Depvc, decimal Depvr, int Dedtpa, int Dedtpm, int Dedtpj, string Decat, string Descat,
    int Demodp, int Rdoss, string Ccdcle, int Dedosp, string Declep, int Deetud, int F6datee, string F6motif,
    string Detag, string Inom, string Acnom, string LibCat, string LibSousCat, int NbOffres, string Deunit,
    string Rcout, int NbDocuments, string Deobsp, int Tnum, string Derfim, int Deddem);

internal sealed record EnteteBrute(
    int Rgen, int Rcli, int Rprosp, string Cnom, string Drs, string Dedes1, string Dedes2, string Deunit, int Tnum,
    int Dencah, decimal Delrff, decimal Dehaff, decimal De3cff, decimal Delrfa, decimal Dehafa, int Deqted,
    int TauxGache, string Decat, string Ttypdt, int Representant);

internal sealed record LigneFabBrute(
    string Ref, decimal Base, decimal Temps, int Cadence, int Diviseur, decimal Resultat, decimal PrixMachine,
    decimal Valeur, decimal ResultatMillePlus, string Commentaire, decimal Quantite, decimal PrixProportionnel,
    string LibSection, string LibOperation, string LibMatiere, string TypeLigne, string Atelier, string LibAtelier,
    int Element, string DesignationElement, decimal PrixMainOeuvre, int Nombre, string LibelleLibre, string Sens,
    int Sequence, decimal PrixFixe, int QuantiteProportionnelle);

internal sealed record ElementBrut(
    int Element, string Sorte, string SousSorte, int Grammage, int LargeurPapier, int HauteurPapier, string Coloris,
    int LargeurTirage, int HauteurTirage, int Pages, int Recto, int Verso, int Basculees, int GacheFixe,
    int GacheVariable, int Tours, decimal Poids, int Metrage, decimal Prix, decimal Valeur, string LibSorte,
    string LibSousSorte, string UniteAchat, string Provenance, string Designation, int Poses, int Conditionnement,
    int Epaisseur, string UniteEpaisseur, int QuantiteElement, int NatureSupport, string LibNature, string Norme,
    string FeuilleBobine, string LibColoris, int FeuillesParTour, int Papier, decimal FiniLargeur, decimal FiniHauteur,
    decimal FiniTroisiemeCote, decimal OuvertLargeur, decimal OuvertHauteur, string Sens, int Outil, int Outil1,
    int Outil2, int NbLignes, int NbTextesLibres);

internal sealed record RubriqueBrute(int Element, string Libelle, string[] Questions, decimal[] Reponses);

internal sealed record DetailBrut(
    EnteteBrute Entete, decimal PerteBobine, List<LigneFabBrute> Lignes, List<ElementBrut> Elements,
    List<RubriqueBrute> Rubriques, List<LigneTransport> Transports, List<LigneFraisAnnexes> Frais,
    Dictionary<int, OutilElement> Outils);
