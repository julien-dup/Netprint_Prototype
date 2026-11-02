using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// Commande et paramètres utiles à la saisie d'une ligne
internal sealed record ContexteLigne(
    string TypeCommande, string TypeFacturation, int CompteGeneral, int CompteClient, string CodeVente, string Enlevement,
    int Representant, decimal TauxCommission, bool SousTraitee, bool NonModifiable, bool Soldee, string Enseigne,
    DateOnly? DateLivraison, DateOnly? DateDossier, int AdresseParDefaut, bool SansMaxi,
    bool PrixCessionObligatoire, bool NouvelleApplication, bool LivraisonDeLEnTete, int Depot,
    IReadOnlyDictionary<string, string> TypesStock, string TypeCommission, string SeuilsCommission, string TauxCommissions,
    IReadOnlyList<(int Seuil, decimal Pourcentage)> MaxiClient, IReadOnlyList<(int Seuil, decimal Pourcentage)> MaxiSociete,
    bool CalculMaxi, decimal PlafondFranco, string ModeMaxi = "");

// Article (SPF010) tel que CTL520 le lit
internal sealed record ArticleLigne(
    string Remplacement, string Type, string TypeFacturation, int CompteGeneral, int CompteClient, bool Stocke,
    bool TarifParEmballage, bool SaisieEnUc, bool StockControle, int Emballage, bool FractionnableCommande,
    bool FractionnableLivraison, string Unite, string Designation, string Norme, string Emplacement, decimal PrixRevient,
    decimal PrixCession, IReadOnlyList<string> Textes, bool Nomenclature);

// Contrôles d'existence et de calendrier d'une ligne
internal sealed record VerificationsLigne(
    string? TypeCodeVente, long? TnumUnite, bool UniteExiste, (decimal Entree, decimal Reserve)? Stock,
    bool DateAuCalendrier, string TypeJour, int JourSemaine, string? JoursAutorises, bool AdresseExiste,
    bool ClientFactureExiste, bool ClientEdi, bool EmplacementExiste, int? OprNonLivre, bool AppelsExistent,
    bool ReferenceFournisseurManquante, bool NormeExiste = true);

internal sealed partial class LignesCommandeDb2
{
    private const decimal TauxFranc = 6.55957m;

    public async Task<ContexteLigne?> ContexteAsync(int societe, CleCommande cle, int? representant, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        ContexteLigne? c = null;
        await using (var rd = await Lire(cnx, $"""
            SELECT a.CCTYPC, a.CCFACT, a.RGEN, a.RCLI, a.CCTYPV, a.CCECAL, a.CCREP, a.CCTXCO, a.CCOP10, a.CCOP11, a.CCTAGS, a.CCENSE,
                   a.CCDTLA, a.CCDTLM, a.CCDTLJ, a.CCDATA, a.CCDATM, a.CCDATJ, c.CDNAL, c.CDOPT4,
                   s.SOP15, s.SOP21, d.POPT03, k.PSDEPP, k.PSTSNE, k.PSTSP1, k.PSTSP2, k.PSTSP3, k.PSTSSF,
                   z.CFTYPE, z.TRP, z.TCM, p.PNMTPO, m.PMCALC
            FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AA0007 s ON s.RSOC = a.RSOC
            LEFT OUTER JOIN {b}.DOS005 d ON d.RSOC = a.RSOC
            LEFT OUTER JOIN {b}.STO005 k ON k.RSOC = a.RSOC
            LEFT OUTER JOIN {b}.AA000Z z ON z.RSOC = a.RSOC
            LEFT OUTER JOIN {b}.SPF002 p ON p.RSOC = a.RSOC
            LEFT OUTER JOIN {b}.DOS009 m ON m.RSOC = a.RSOC
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ? AND a.CCTYPC IN ('3', '4'){(representant is null ? "" : " AND a.CCREP = ?")}
            """, ct, representant is null ? [societe, cle.Numero, cle.Cle] : [societe, cle.Numero, cle.Cle, representant]))
        {
            if (!await rd.ReadAsync(ct)) return null;
            var types = new Dictionary<string, string>
            {
                ["NE"] = rd.Texte(24), ["P1"] = rd.Texte(25), ["P2"] = rd.Texte(26), ["P3"] = rd.Texte(27), ["SF"] = rd.Texte(28),
            };
            c = new ContexteLigne(
                TypeCommande: rd.Texte(0), TypeFacturation: rd.Texte(1), CompteGeneral: rd.Entier(2), CompteClient: rd.Entier(3),
                CodeVente: rd.Texte(4), Enlevement: rd.Texte(5), Representant: rd.Entier(6), TauxCommission: rd.Nombre(7),
                SousTraitee: rd.Texte(8) == "O", NonModifiable: rd.Texte(9) == "O", Soldee: rd.Texte(10) != "", Enseigne: rd.Texte(11),
                DateLivraison: BaseDb2.Date(rd.Entier(12), rd.Entier(13), rd.Entier(14)),
                DateDossier: BaseDb2.Date(rd.Entier(15), rd.Entier(16), rd.Entier(17)),
                AdresseParDefaut: rd.Entier(18), SansMaxi: rd.Texte(19) == "O",
                PrixCessionObligatoire: rd.Texte(20) == "O", NouvelleApplication: rd.Texte(21) == "O",
                LivraisonDeLEnTete: rd.Texte(22) == "O", Depot: rd.Entier(23), TypesStock: types,
                TypeCommission: rd.Texte(29), SeuilsCommission: rd.Texte(30), TauxCommissions: rd.Texte(31),
                MaxiClient: [], MaxiSociete: [], CalculMaxi: rd.Texte(33) != "", PlafondFranco: rd.Nombre(32), ModeMaxi: rd.Texte(33));
        }

        // Tolérances de livraison : client (PRT059), société (DOS009)
        async Task<List<(int, decimal)>> Tranches(string table, string q, string p, string where, params object[] v)
        {
            var liste = new List<(int, decimal)>();
            var colonnes = string.Join(", ", Enumerable.Range(1, 5).Select(i => $"{q}{i}, {p}{i}"));
            await using var rd = await Lire(cnx, $"SELECT {colonnes} FROM {b}.{table} WHERE {where}", ct, v);
            if (await rd.ReadAsync(ct))
                for (var i = 0; i < 5; i++) liste.Add((rd.Entier(2 * i), rd.Nombre(2 * i + 1)));
            return liste;
        }
        return c! with
        {
            MaxiClient = await Tranches("PRT059", "CMQTE", "CMPC", "RSOC = ? AND RGEN = ? AND RCLI = ?", societe, c!.CompteGeneral, c.CompteClient),
            MaxiSociete = await Tranches("DOS009", "PMQTE", "PMPC", "RSOC = ?", societe),
        };
    }

    public async Task<ArticleLigne?> ArticleAsync(int societe, string article, string indice, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        var textes = string.Join(", ", Enumerable.Range(1, 19).Select(i => i < 10 ? $"ADESC{i}" : $"ADES{i}"));
        await using var rd = await Lire(cnx, $"""
            SELECT AREMPL, ATYPEA, ATYFAC, AGEN, ACLI, ASTK, AOPT2, AOPT3, AOPT4, AEMBP, AFRACC, AFRACL, AUNITE, ADES, ACNORM,
                   ACEMPL, APRVT, APRCES, {textes},
                   (SELECT COUNT(*) FROM {b}.SPFNOM n WHERE n.RSOC = a.RSOC AND n.AARTIC = a.AARTIC AND n.AINDIC = a.AINDIC)
            FROM {b}.SPF010 a WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ?
            """, ct, societe, article, indice);
        if (!await rd.ReadAsync(ct)) return null;
        return new ArticleLigne(
            Remplacement: rd.Texte(0), Type: rd.Texte(1), TypeFacturation: rd.Texte(2), CompteGeneral: rd.Entier(3),
            CompteClient: rd.Entier(4), Stocke: rd.Texte(5) == "O", TarifParEmballage: rd.Texte(6) == "C",
            SaisieEnUc: rd.Texte(7) == "O", StockControle: rd.Texte(8) == "O", Emballage: rd.Entier(9),
            FractionnableCommande: rd.Texte(10) != "N", FractionnableLivraison: rd.Texte(11) != "N", Unite: rd.Texte(12),
            Designation: rd.Texte(13), Norme: rd.Texte(14), Emplacement: rd.Texte(15), PrixRevient: rd.Nombre(16),
            PrixCession: rd.Nombre(17), Textes: Enumerable.Range(18, 19).Select(rd.Texte).ToList(), Nomenclature: rd.Entier(37) > 0);
    }

    public async Task<VerificationsLigne> VerifierAsync(int societe, CleCommande cle, ContexteLigne c, LigneSaisie l,
        ArticleLigne? article, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        async Task<bool> Existe(string sql, params object?[] v)
        {
            await using var rd = await Lire(cnx, sql, ct, v);
            return await rd.ReadAsync(ct);
        }

        string? typeCode = null;
        if (l.CodeVente != "")
        {
            await using var rd = await Lire(cnx, $"SELECT LTYPE FROM {b}.FAC020 WHERE RSOC = ? AND RLIGNE = ?", ct, societe, l.CodeVente);
            if (await rd.ReadAsync(ct)) typeCode = rd.Texte(0);
        }
        long? tnum = null;
        if (l.Unite != "")
        {
            await using var rd = await Lire(cnx, $"SELECT TNUM FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = 'UV' AND RARG = ? AND TTAG <> '*'",
                ct, societe, l.Unite);
            if (await rd.ReadAsync(ct)) tnum = Convert.ToInt64(rd.GetValue(0));
        }

        (decimal, decimal)? stock = null;
        if (article is { StockControle: true } && TypeStock(article, c.TypesStock) is { } typeStock)
        {
            await using var rd = await Lire(cnx, $"""
                SELECT PRQENT, PRQRES FROM {b}.AK00F WHERE RSOC = ? AND PRTSTO = ? AND PRDEP = ? AND PRREF = ? AND PRIND = ?
                """, ct, societe, typeStock, c.Depot, l.Article, l.Indice);
            stock = await rd.ReadAsync(ct) ? (rd.Nombre(0), rd.Nombre(1)) : (0m, 0m);
        }

        bool auCalendrier = false;
        string typeJour = "";
        int jourSemaine = 0;
        string? joursAutorises = null;
        if (l.DateLivraison is { } d)
        {
            await using (var rd = await Lire(cnx, $"SELECT CATYPJ, CANOJR FROM {b}.D200F WHERE RSOC = ? AND RAN = ? AND RMOIS = ? AND RJOUR = ?",
                             ct, societe, d.Year, d.Month, d.Day))
                if (await rd.ReadAsync(ct)) (auCalendrier, typeJour, jourSemaine) = (true, rd.Texte(0), rd.Entier(1));
            await using (var rd = await Lire(cnx, $"""
                SELECT JLLUND || JLMARD || JLMERC || JLJEUD || JLVEND || JLSAME FROM {b}.PRT053F
                WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND RNAL = ?
                """, ct, societe, c.CompteGeneral, c.CompteClient, l.NumeroAdresse ?? 0))
                if (await rd.ReadAsync(ct)) joursAutorises = Convert.ToString(rd.GetValue(0)) ?? "";
        }

        var adresse = l.NumeroAdresse is not > 0 || await Existe(
            $"SELECT 1 FROM {b}.A500F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALNLIG = ?", societe, c.CompteGeneral, c.CompteClient, l.NumeroAdresse);
        var facture = l.CompteClientFacture is not > 0 || await Existe(
            $"SELECT 1 FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", societe, l.CompteGeneralFacture ?? 0, l.CompteClientFacture);
        var edi = await Existe($"SELECT 1 FROM {b}.PRT057F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND PFFMT LIKE 'EDI%'",
            societe, c.CompteGeneral, c.CompteClient);
        // Norme de l'enseigne de la commande (NOR010F, clé RSOC / NOENS / NOCOD comme CTL520)
        var norme = l.Norme == "" || await Existe($"SELECT 1 FROM {b}.NOR010F WHERE RSOC = ? AND NOENS = ? AND NOCOD = ?",
            societe, c.Enseigne, l.Norme);
        var emplacement = l.Emplacement == "" || await Existe(
            $"SELECT 1 FROM {b}.STO050 WHERE RSOC = ? AND ESDEP = ? AND ESEMP = ?", societe, c.Depot, l.Emplacement);

        int? opr = null;
        var appels = false;
        if (l.NumeroLigne > 0)
        {
            await using (var rd = await Lire(cnx, $"""
                SELECT ODNUM FROM {b}.OPR015 WHERE RSOC = ? AND ODNCDE = ? AND ODNCLE = ? AND ODDLIG = ? AND ODLIVR <> 'O'
                FETCH FIRST 1 ROW ONLY
                """, ct, societe, cle.Numero, cle.Cle, l.NumeroLigne))
                if (await rd.ReadAsync(ct)) opr = rd.Entier(0);
            appels = await Existe($"""
                SELECT 1 FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNSTAT = '1' AND DNLORI = ? AND DNNLIG <> ?
                """, societe, cle.Numero, cle.Cle, l.NumeroLigne, l.NumeroLigne);
        }

        // Commande sous-traitée à un autre site : un article stocké doit avoir une référence fournisseur (TAF010)
        var referenceManquante = false;
        if (c.SousTraitee && article is { Stocke: true })
        {
            string? fournisseur = null;
            await using (var rd = await Lire(cnx, $"SELECT PSSFOUR FROM {b}.SYN005 FETCH FIRST 1 ROW ONLY", ct))
                if (await rd.ReadAsync(ct)) fournisseur = Convert.ToString(rd.GetValue(0))?.Trim();
            if (fournisseur is not null)
                referenceManquante = !await Existe($"""
                    SELECT 1 FROM {b}.TAF010 WHERE RSOC = ? AND TFTYPA = 'A' AND TFREF = ? AND TFIND = ? AND TFFRN = ? AND TFREFF <> ' '
                    """, societe, l.Article, l.Indice, fournisseur);
        }

        return new VerificationsLigne(typeCode, tnum, tnum is not null, stock, auCalendrier, typeJour, jourSemaine, joursAutorises,
            adresse, facture, edi, emplacement, opr, appels, referenceManquante, norme);
    }

    // GETTYPSTOC (AXISRV R26-R27) : type de stock d'un article géré en stock, sinon null
    internal static string? TypeStock(ArticleLigne a, IReadOnlyDictionary<string, string> types)
    {
        if (!a.Stocke) return null;
        var cle = a.Type switch
        {
            "1" => "NE",
            "2" => a.TypeFacturation switch { "2" => "P2", "3" => "P3", _ => "P1" },
            "3" => "SF",
            _ => null,
        };
        return cle is not null && types.TryGetValue(cle, out var t) && t != "" ? t : null;
    }

    // ------------------------------------------------------------------
    // Lecture d'une ligne
    // ------------------------------------------------------------------
    public async Task<LigneSaisie?> LigneAsync(int societe, CleCommande cle, int numero, CancellationToken ct)
    {
        var b = Bib;
        var textes = string.Join(", ", Enumerable.Range(1, 19).Select(i => i < 10 ? $"t.DNTXT{i}" : $"t.DNTX{i}"));
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT n.DNCLIG, n.DNART, n.DNIND, n.DNDESA, n.DNSTAT, n.DNQTEC, n.DNUV, n.DNQMAX, n.DNPUNR, n.DNTXRE, n.DNPXCE,
                   n.DNOPT2, n.DNOPT5, n.DNTXCO, n.DNPUS, n.DNMTHT, n.DNDATB, n.DNDLIV, n.DNLIGA, n.DNRS, n.DNRUE1, n.DNRUE2,
                   n.DNPOST, n.DNVILL, n.DNPAY, n.DNAPPE, n.DNDTAC, n.DNCOMM, n.DNCOM2, n.DNCOM3, n.DNNORM, n.DEMP, n.DNOPT4,
                   n.DNOPT3, n.DNGENF, n.DNCLIF, n.DNQTEL, n.DNQTEF, n.DNSOLD, n.DNLORI, {textes}, n.DNQTEO,
                   n.DNDEV, n.DNVERS, n.DNHYPO
            FROM {b}.NEG100 n
            LEFT OUTER JOIN {b}.NEG100T t ON t.RSOC = n.RSOC AND t.RDOSS = n.RDOSS AND t.DNDCLE = n.DNDCLE AND t.DNNLIG = n.DNNLIG
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNNLIG = ?
            FETCH FIRST 1 ROW ONLY
            """, ct, societe, cle.Numero, cle.Cle, numero);
        if (!await rd.ReadAsync(ct)) return null;
        static int? Pos(int v) => v == 0 ? null : v;
        static decimal? PosD(decimal v) => v == 0 ? null : v;
        var confirmee = rd.Entier(17);
        var saisie = rd.Entier(16);
        var l = new LigneSaisie
        {
            NumeroLigne = numero, CodeVente = rd.Texte(0), Article = rd.Texte(1), Indice = rd.Texte(2), Designation = rd.Texte(3),
            Statut = rd.Texte(4), Quantite = rd.Entier(5), Unite = rd.Texte(6), MaxiLivrable = Pos(rd.Entier(7)),
            PrixBrut = rd.Nombre(8), PrixSaisi = true, Remise = PosD(rd.Nombre(9)), PrixCession = PosD(rd.Nombre(10)),
            Gratuite = rd.Texte(11) == "O", PrixADefinir = rd.Texte(12) == "O", TauxCommission = rd.Nombre(13),
            PrixNet = rd.Nombre(14), Montant = rd.Nombre(15),
            // MAJ520 : DNDATB = date saisie, DNDLIV = date confirmée ou, à défaut, date saisie
            DateLivraison = BaseDb2.DateAAAAMMJJ(saisie != 0 ? saisie : confirmee),
            DateConfirmee = confirmee != 0 && confirmee != saisie ? BaseDb2.DateAAAAMMJJ(confirmee) : null,
            NumeroAdresse = Pos(rd.Entier(18)), AdresseRaisonSociale = rd.Texte(19), AdresseRue1 = rd.Texte(20),
            AdresseRue2 = rd.Texte(21), AdresseCodePostal = rd.Texte(22), AdresseVille = rd.Texte(23), AdressePays = rd.Texte(24),
            NumeroAppel = rd.Texte(25), DateAppel = BaseDb2.DateAAAAMMJJ(rd.Entier(26)), Commentaire1 = rd.Texte(27),
            Commentaire2 = rd.Texte(28), Commentaire3 = rd.Texte(29), Norme = rd.Texte(30), Emplacement = rd.Texte(31),
            RegroupementOf = rd.Texte(32) == "O", NouvelleApplication = rd.Texte(33), CompteGeneralFacture = Pos(rd.Entier(34)),
            CompteClientFacture = Pos(rd.Entier(35)), QuantiteLivree = rd.Entier(36), QuantiteFacturee = rd.Entier(37),
            Soldee = rd.Texte(38) == "O", LigneOrigine = rd.Entier(39), QuantiteOrigine = rd.Entier(59),
            Devis = rd.Entier(60) > 0 ? new ReferenceDevis(rd.Entier(60), rd.Entier(61), rd.Entier(62)) : null,
            Texte = string.Join("\n", Enumerable.Range(40, 19).Select(rd.Texte)).TrimEnd('\n'),
        };
        l.AdresseSiege = l.NumeroAdresse is null && l.AdresseRaisonSociale == "" && l.AdresseVille == "";
        l.ArticleEnregistre = l.Article;
        l.IndiceEnregistre = l.Indice;
        l.ArticleComplete = $"{l.Article}|{l.Indice}";
        l.CommissionSaisie = true;                  // taux enregistré : conservé
        l.MaxiSaisi = l.MaxiLivrable is > 0;
        return l;
    }

    // ------------------------------------------------------------------
    // Écriture d'une ligne (MAJ520) et recalcul de l'en-tête (NEG520 SPSOR), en une transaction
    // ------------------------------------------------------------------
    public async Task<int> EnregistrerAsync(int societe, string profil, CleCommande cle, ContexteLigne c, LigneSaisie l,
        long tnum, Func<LigneSaisie, string> empreinte, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            // Verrou de la commande pendant la transaction (numérotation des lignes, totaux)
            await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                societe, cle.Numero, cle.Cle);

            var creation = l.NumeroLigne == 0;
            LigneSaisie? ancienne = null;
            if (!creation)
            {
                ancienne = await LigneDansTransactionAsync(cnx, tx, b, societe, cle, l.NumeroLigne, ct);
                if (ancienne is null || empreinte(ancienne) != l.Empreinte)
                    throw new RegleMetierException("La ligne a été modifiée par un autre utilisateur depuis son ouverture : rechargez la commande.");
            }
            else
            {
                await using var rd = await Lire(cnx, tx, $"SELECT COALESCE(MAX(DNNLIG), 0) + 1 FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?",
                    ct, societe, cle.Numero, cle.Cle);
                await rd.ReadAsync(ct);
                l.NumeroLigne = rd.Entier(0);
                if (l.NumeroLigne > 999) throw new RegleMetierException("Une commande ne peut pas dépasser 999 lignes.");
            }

            // Appel de livraison : l'origine doit toujours être une ligne « à la demande » du même article (verrouillée
            // jusqu'à la fin de la transaction) ; un appel créé sur une commande soldée « D » la remet en cours (NEG300NAV)
            var reactivee = false;
            if (l.EstAppel)
            {
                var origine = await LigneDansTransactionAsync(cnx, tx, b, societe, cle, l.LigneOrigine, ct, verrou: true);
                if (origine is null || origine.Statut != StatutsLigne.ALaDemande || origine.Article != l.Article || origine.Indice != l.Indice)
                    throw new RegleMetierException($"La ligne d'origine {l.LigneOrigine} n'est plus une ligne « à la demande » de cet article.");
                // Le dépassement a été contrôlé sur ce reste : un autre appel enregistré depuis l'a changé
                if (origine.Quantite != l.ResteOrigine) throw new RegleMetierException(MessagesLigne.OrigineModifiee);
                if (creation) reactivee = await ReactiverPourAppelAsync(cnx, tx, b, societe, cle, ct);
            }

            // DNPUT (prix théorique) est figé à la création
            var colonnes = ColonnesLigne(c, l, tnum, ancienne).Where(x => creation || x.Colonne != "DNPUT").ToList();
            if (creation)
                await Executer(cnx, tx,
                    $"INSERT INTO {b}.NEG100 (RSOC, RDOSS, DNDCLE, DNNLIG, {string.Join(", ", colonnes.Select(x => x.Colonne))}) " +
                    $"VALUES (?, ?, ?, ?, {string.Join(", ", colonnes.Select(_ => "?"))})", ct,
                    [societe, cle.Numero, cle.Cle, l.NumeroLigne, .. colonnes.Select(x => x.Valeur)]);
            else
                await Executer(cnx, tx,
                    $"UPDATE {b}.NEG100 SET {string.Join(", ", colonnes.Select(x => x.Colonne + " = ?"))} " +
                    "WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct,
                    [.. colonnes.Select(x => x.Valeur), societe, cle.Numero, cle.Cle, l.NumeroLigne]);

            await TexteAsync(cnx, tx, b, societe, cle, l.NumeroLigne, l.Texte, ct);

            // Journal NEG100E (C création, M si article, quantité, prix, unité ou cession changent)
            var prixNet = MoteurPrixCommande.PrixNet(l.PrixBrut ?? 0, l.Remise ?? 0);
            if (creation)
                await JournalAsync(cnx, tx, b, societe, cle, l.NumeroLigne, "C", null, l, prixNet, c, profil, ct);
            else if (ancienne!.Article != l.Article || ancienne.Indice != l.Indice || ancienne.Quantite != l.Quantite
                     || ancienne.PrixNet != prixNet || ancienne.Unite != l.Unite || (ancienne.PrixCession ?? 0) != (l.PrixCession ?? 0))
                await JournalAsync(cnx, tx, b, societe, cle, l.NumeroLigne, "M", ancienne, l, prixNet, c, profil, ct);

            // Date de dernière commande de l'article (création seulement)
            if (creation && c.DateDossier is { } dd)
                await Executer(cnx, tx, $"UPDATE {b}.SPF010 SET ADTCDE = ? WHERE RSOC = ? AND AARTIC = ? AND AINDIC = ? AND ADTCDE < ?", ct,
                    BaseDb2.AAAAMMJJ(dd), societe, l.Article, l.Indice, BaseDb2.AAAAMMJJ(dd));

            if (l.EstAppel) await RecalculerOrigineAsync(cnx, tx, b, societe, profil, cle, c, l.LigneOrigine, ct);
            await RecalculerEnTeteAsync(cnx, tx, b, societe, profil, cle, c, ct);
            await tx.CommitAsync(ct);
            validee = true;
            if (reactivee) await AuditDossierAsync(cnx, b, societe, profil, cle, "RA");
            return l.NumeroLigne;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Colonnes de NEG100 écrites par MAJ520 (création et modification ; en modification, quantités livrées et facturées,
    // solde et origine sont conservés, et le devis s'il n'est pas connu de la ligne saisie)
    internal static List<(string Colonne, object? Valeur)> ColonnesLigne(ContexteLigne c, LigneSaisie l, long tnum, LigneSaisie? ancienne)
    {
        var prixNet = MoteurPrixCommande.PrixNet(l.PrixBrut ?? 0, l.Remise ?? 0);
        var quantite = l.Quantite ?? 0;
        var montant = MoteurPrixCommande.Montant(prixNet, quantite, tnum);
        var prixFranc = Math.Round(prixNet * TauxFranc, 2, MidpointRounding.AwayFromZero);
        var cession = l.PrixCession ?? 0;
        var livree = ancienne?.QuantiteLivree ?? 0;
        var soldee = ancienne?.Soldee ?? false;
        var aLaDemande = l.Statut == StatutsLigne.ALaDemande;
        // Statut 3 : origine = la ligne elle-même, quantité d'origine = quantité ; appel : n° de sa ligne d'origine ;
        // ligne normale : 0 (le service remet LigneOrigine à 0 pour une ligne qui n'est pas un appel)
        var origine = aLaDemande ? l.NumeroLigne : l.LigneOrigine;
        var dateSaisie = l.DateLivraison is { } d ? BaseDb2.AAAAMMJJ(d) : 0;
        var dateConfirmee = l.DateConfirmee is { } dc ? BaseDb2.AAAAMMJJ(dc) : 0;
        var adresseLibre = !l.AdresseSiege && l.NumeroAdresse is not > 0;
        string Libre(string v) => adresseLibre ? v : "";
        return
        [
            ("DNCLIG", l.CodeVente), ("DNART", l.Article), ("DNIND", l.Indice), ("DNDESA", l.Designation), ("DNQTEC", quantite),
            ("DNQTER", soldee ? 0 : quantite - livree), ("DNPUNR", l.PrixBrut ?? 0), ("DNTXRE", l.Remise ?? 0), ("DNPUS", prixNet),
            ("DNPUSA", prixFranc), ("DNMTHT", montant), ("DNMTHA", MoteurPrixCommande.Montant(prixFranc, quantite, tnum)),
            ("DNPXCE", cession), ("DNPXCA", Math.Round(cession * TauxFranc, 2, MidpointRounding.AwayFromZero)),
            ("DNPUT", prixNet), ("DNUV", l.Unite), ("DNTXCO", l.TauxCommission ?? 0),
            ("DNEDTB", "O"), ("DNLIGA", l.NumeroAdresse ?? 0), ("DNRS", Libre(l.AdresseRaisonSociale)), ("DNRUE1", Libre(l.AdresseRue1)),
            ("DNRUE2", Libre(l.AdresseRue2)), ("DNPOST", Libre(l.AdresseCodePostal)), ("DNVILL", Libre(l.AdresseVille)),
            ("DNPAY", Libre(l.AdressePays)), ("DNCOMM", l.Commentaire1), ("DNCOM2", l.Commentaire2), ("DNCOM3", l.Commentaire3),
            // MAJ520 : DNDATB reçoit la date saisie, DNDLIV la date confirmée ou, à défaut, la date saisie
            ("DNDATB", dateSaisie), ("DNDLIV", dateConfirmee != 0 ? dateConfirmee : dateSaisie),
            ("DNSTAT", l.Statut), ("DNAPPE", l.NumeroAppel), ("DNDTAC", l.DateAppel is { } da ? BaseDb2.AAAAMMJJ(da) : 0),
            ("DNLORI", origine), ("DNQTEO", !aLaDemande ? 0 : ancienne is { Statut: StatutsLigne.ALaDemande } ? ancienne.QuantiteOrigine - (ancienne.Quantite ?? 0) + quantite : quantite), ("DNQMAX", l.MaxiLivrable ?? 0),
            ("DNOPT2", l.Gratuite ? "O" : "N"), ("DNOPT3", l.NouvelleApplication), ("DNOPT4", l.RegroupementOf ? "O" : "N"),
            ("DNOPT5", l.PrixADefinir ? "O" : "N"), ("DNNORM", l.Norme), ("DEMP", l.Emplacement),
            ("DNGENF", l.CompteClientFacture is > 0 ? l.CompteGeneralFacture ?? 0 : 0), ("DNCLIF", l.CompteClientFacture ?? 0),
            .. ancienne is null || l.Devis is not null
                ? new (string, object?)[] { ("DNDEV", l.Devis?.Numero ?? 0), ("DNVERS", l.Devis?.Version ?? 0), ("DNHYPO", l.Devis?.Hypothese ?? 0) }
                : [],
        ];
    }

    private static async Task TexteAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, CleCommande cle, int ligne,
        string texte, CancellationToken ct)
    {
        await Executer(cnx, tx, $"DELETE FROM {b}.NEG100T WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct,
            societe, cle.Numero, cle.Cle, ligne);
        var lignes = TexteEnLignes(texte);
        if (lignes.All(t => t == "")) return;
        var colonnes = string.Join(", ", Enumerable.Range(1, 19).Select(i => i < 10 ? $"DNTXT{i}" : $"DNTX{i}"));
        await Executer(cnx, tx, $"INSERT INTO {b}.NEG100T (RSOC, RDOSS, DNDCLE, DNNLIG, {colonnes}) VALUES (?, ?, ?, ?, {string.Join(", ", lignes.Select(_ => "?"))})",
            ct, [societe, cle.Numero, cle.Cle, ligne, .. lignes]);
    }

    // Texte libre : 19 lignes de 40 caractères au plus
    internal static List<string> TexteEnLignes(string texte)
    {
        var lignes = (texte ?? "").Replace("\r", "").Split('\n')
            .SelectMany(l => l.Length <= 40 ? [l.TrimEnd()] : Enumerable.Range(0, (l.Length + 39) / 40).Select(i => l.Substring(i * 40, Math.Min(40, l.Length - i * 40)).TrimEnd()))
            .Take(19).ToList();
        while (lignes.Count < 19) lignes.Add("");
        return lignes;
    }

    // Journal NEG100E (MAJ520) : date au format JJMMAAAA
    private static async Task JournalAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, CleCommande cle, int ligne,
        string type, LigneSaisie? avant, LigneSaisie? apres, decimal prixNetApres, ContexteLigne c, string profil, CancellationToken ct)
    {
        var maintenant = DateTime.Now;
        await Executer(cnx, tx, $"""
            INSERT INTO {b}.NEG100E (RSOC, RDOSS, DNDCLE, DNNLIG, ANCART, ANCND, ANCQTE, ANCPUS, ANCUV, ANCPXC, DNART, DNIND, DNQTEC,
                DNPUS, DNUV, DNPXCE, DEDTMO, DEDTHE, DETYMO, DETRAI, DEPROF, ANCREP, DNREP, ANCTYC, DNTYPC)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'N', ?, ?, ?, ?, ?)
            """, ct, societe, cle.Numero, cle.Cle, ligne,
            avant?.Article ?? "", avant?.Indice ?? "", avant?.Quantite ?? 0, avant?.PrixNet ?? 0, avant?.Unite ?? "", avant?.PrixCession ?? 0,
            apres?.Article ?? "", apres?.Indice ?? "", apres?.Quantite ?? 0, apres is null ? 0 : prixNetApres, apres?.Unite ?? "",
            apres?.PrixCession ?? 0, maintenant.Day * 1_000_000 + maintenant.Month * 10_000 + maintenant.Year,
            maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second, type, profil.ToUpperInvariant(),
            avant is null ? 0 : c.Representant, apres is null ? 0 : c.Representant, avant is null ? "" : c.TypeCommande,
            apres is null ? "" : c.TypeCommande);
    }

    // Recalcul de l'en-tête (SPSOR de NEG520) : totaux des lignes de vente (soldées comprises), unité LT,
    // solde automatique s'il ne reste aucune ligne active, franco sous le plafond SPF002, marge AB0016,
    // historique DOS040 « LV » pour un dossier antérieur au jour, demande de transfert SYN100 si sous-traitée
    private static async Task RecalculerEnTeteAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, string profil,
        CleCommande cle, ContexteLigne c, CancellationToken ct)
    {
        decimal total = 0, totalFranc = 0;
        int actives = 0, lignes = 0;
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT COALESCE(SUM(CASE WHEN f.LTYPE = 'V' THEN n.DNMTHT ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN f.LTYPE = 'V' THEN n.DNMTHA ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN n.DNART NOT LIKE '999%' AND n.DNSOLD <> 'O' THEN 1 ELSE 0 END), 0), COUNT(n.DNNLIG)
            FROM {b}.NEG100 n LEFT OUTER JOIN {b}.FAC020 f ON f.RSOC = n.RSOC AND f.RLIGNE = n.DNCLIG
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ?
            """, ct, societe, cle.Numero, cle.Cle))
            if (await rd.ReadAsync(ct)) (total, totalFranc, actives, lignes) = (rd.Nombre(0), rd.Nombre(1), rd.Entier(2), rd.Entier(3));

        // Solde automatique (sens unique, comme NEG520) : des lignes existent et toutes sont soldées. ÉCART : une commande dont
        // on vient de supprimer la dernière ligne n'est pas soldée (elle resterait bloquée), et un autre code de solde est conservé.
        var solder = lignes > 0 && actives == 0 ? 0 : 1;

        var aujourdhui = DateTime.Today;
        await Executer(cnx, tx, $"""
            UPDATE {b}.AB001 SET CCPXVE = ?, CCPAVE = ?, CCUNIT = 'LT',
                CCTAGS = CASE WHEN ? = 0 AND CCTAGS = ' ' THEN 'D' ELSE CCTAGS END,
                CCSLDA = CASE WHEN ? = 0 AND CCTAGS = ' ' THEN ? ELSE CCSLDA END,
                CCSLDM = CASE WHEN ? = 0 AND CCTAGS = ' ' THEN ? ELSE CCSLDM END,
                CCSLDJ = CASE WHEN ? = 0 AND CCTAGS = ' ' THEN ? ELSE CCSLDJ END,
                CCFOPO = CASE WHEN ? <= ? AND CCFOPO <> 'D' THEN 'O' ELSE CCFOPO END
            WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
            """, ct, total, totalFranc, solder, solder, aujourdhui.Year, solder, aujourdhui.Month, solder, aujourdhui.Day,
            total, c.PlafondFranco, societe, cle.Numero, cle.Cle);

        var marge = total > 0 ? 100m : 0m;   // le prix de revient cumulé n'est jamais alimenté par NEG520 : 100 % ou 0
        // ÉCART : le RPG lisait AB0016 par société et n° seulement et réécrivait F16CLE (celui d'un autre dossier de même n°
        // pouvait être écrasé) ; ici, la clé complète
        if (await Executer(cnx, tx, $"UPDATE {b}.AB0016 SET F16VA = ? WHERE RSOC = ? AND RDOSS = ? AND F16CLE = ?", ct,
                marge, societe, cle.Numero, cle.Cle) == 0)
            await Executer(cnx, tx, $"INSERT INTO {b}.AB0016 (RSOC, RDOSS, F16CLE, F16SUR, F16VA) VALUES (?, ?, ?, 0, ?)", ct,
                societe, cle.Numero, cle.Cle, marge);

        var maintenant = DateTime.Now;
        if (c.DateDossier is { } dd && dd < DateOnly.FromDateTime(aujourdhui))
        {
            var initiales = "";
            await using (var rd = await Lire(cnx, tx, $"SELECT ACINI FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil.ToUpperInvariant()))
                if (await rd.ReadAsync(ct)) initiales = rd.Texte(0);
            await Executer(cnx, tx, $"""
                INSERT INTO {b}.DOS040 (RSOC, RDOSS, HICLE, HIGEN, HICLI, HIINAM, HITYPM, HIDTMA, HIDTMM, HIDTMJ, HIHEUR)
                VALUES (?, ?, ?, ?, ?, ?, 'LV', ?, ?, ?, ?)
                """, ct, societe, cle.Numero, cle.Cle, c.CompteGeneral, c.CompteClient, initiales, maintenant.Year, maintenant.Month,
                maintenant.Day, maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
        }
        if (c.SousTraitee)
            await Executer(cnx, tx, $"""
                INSERT INTO {b}.SYN100 (HTSOC, HTDOSS, HTDCLE, HTDATE, HTHEUR, HTSTAT, HTNBL, HTACT) VALUES (?, ?, ?, ?, ?, ' ', 0, 'CDE')
                """, ct, societe, cle.Numero, cle.Cle, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)),
                maintenant);
    }

    // ------------------------------------------------------------------
    // Options de la liste : suppression (physique, MAJ520), solde, réactivation
    // ------------------------------------------------------------------
    // origine : n° de la ligne « à la demande » quand la ligne supprimée est un appel (son reste est recalculé)
    public async Task SupprimerAsync(int societe, string profil, CleCommande cle, ContexteLigne c, int ligne, CancellationToken ct,
        int origine = 0)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            // Verrou de la commande (totaux, reste à appeler de l'origine d'un appel)
            await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                societe, cle.Numero, cle.Cle);
            var avant = await LigneDansTransactionAsync(cnx, tx, b, societe, cle, ligne, ct)
                ?? throw new RegleMetierException("Cette ligne n'existe plus.");
            if (avant.Soldee || avant.QuantiteLivree != 0 || avant.QuantiteFacturee != 0)
                throw new RegleMetierException("Suppression impossible : la ligne a été soldée, livrée ou facturée entre-temps.");
            // Un appel a pu être créé sur cette ligne « à la demande » depuis le contrôle du service
            if (avant.Statut == StatutsLigne.ALaDemande)
                await using (var rd = await Lire(cnx, tx, $"""
                    SELECT 1 FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNSTAT = '1' AND DNLORI = ? AND DNNLIG <> ?
                    FETCH FIRST 1 ROW ONLY
                    """, ct, societe, cle.Numero, cle.Cle, ligne, ligne))
                    if (await rd.ReadAsync(ct)) throw new RegleMetierException("Suppression impossible : il existe des appels de livraison.");
            await Executer(cnx, tx, $"DELETE FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct, societe, cle.Numero, cle.Cle, ligne);
            await Executer(cnx, tx, $"DELETE FROM {b}.NEG100T WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct, societe, cle.Numero, cle.Cle, ligne);
            await Executer(cnx, tx, $"DELETE FROM {b}.NEG100S WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct, societe, cle.Numero, cle.Cle, ligne);
            // EVE105 n'a pas de colonne société : ses lignes sont celles des événements EVE100 de la ligne (comme MAJ520)
            await Executer(cnx, tx, $"""
                DELETE FROM {b}.EVE105 WHERE EVLIG IN (SELECT EVLIG FROM {b}.EVE100 WHERE RSOC = ? AND EVCDC = ? AND EVCDCC = ? AND EVCDCL = ?)
                """, ct, societe, cle.Numero, cle.Cle, ligne);
            await Executer(cnx, tx, $"DELETE FROM {b}.EVE100 WHERE RSOC = ? AND EVCDC = ? AND EVCDCC = ? AND EVCDCL = ?", ct, societe, cle.Numero, cle.Cle, ligne);
            await JournalAsync(cnx, tx, b, societe, cle, ligne, "S", avant, null, 0, c, profil, ct);
            if (origine > 0) await RecalculerOrigineAsync(cnx, tx, b, societe, profil, cle, c, origine, ct);
            await RecalculerEnTeteAsync(cnx, tx, b, societe, profil, cle, c, ct);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    public async Task SolderAsync(int societe, string profil, CleCommande cle, ContexteLigne c, int ligne, bool solder, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            // Solde : DNSOLD = 'O', reste à livrer non recalculé (comme NEG520) ; réactivation : DNSOLD à blanc
            await Executer(cnx, tx, $"UPDATE {b}.NEG100 SET DNSOLD = ? WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct,
                solder ? "O" : " ", societe, cle.Numero, cle.Cle, ligne);
            if (!solder)
                await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCTAGS = ' ', CCSLDA = 0, CCSLDM = 0, CCSLDJ = 0 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTAGS = 'D'",
                    ct, societe, cle.Numero, cle.Cle);
            await RecalculerEnTeteAsync(cnx, tx, b, societe, profil, cle, c, ct);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Réservations de stock recalculées par STOCK03 (NP11P), après la validation, pour un article géré en stock.
    // Paramètre de 256 caractères : société (4), type de stock (2), dépôt (3), article (15), indice (2).
    public async Task RecalculerStockAsync(int societe, string typeStock, int depot, string article, string indice, CancellationToken ct)
    {
        var parametre = $"{societe:0000}{typeStock,-2}{depot:000}{article,-15}{indice,-2}".PadRight(256);
        var commande = $"CALL PGM({BaseDb2.Bibliotheque(config["Db2:BibliothequePgm"] ?? "NP11P")}/STOCK03) PARM('{parametre.Replace("'", "''")}')";
        await using var cnx = await OuvrirAsync(ct);
        await Executer(cnx, null, "CALL QSYS2.QCMDEXC(?)", ct, commande);
    }

    // verrou : ligne verrouillée jusqu'à la fin de la transaction (ligne d'origine d'un appel)
    private static async Task<LigneSaisie?> LigneDansTransactionAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe,
        CleCommande cle, int ligne, CancellationToken ct, bool verrou = false)
    {
        await using var rd = await Lire(cnx, tx, $"""
            SELECT DNART, DNIND, DNQTEC, DNPUS, DNUV, DNPXCE, DNSTAT, DNLORI, DNQTEL, DNQTEF, DNSOLD, DNCLIG, DNDESA, DNPUNR, DNTXRE, DNQTEO
            FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ? FETCH FIRST 1 ROW ONLY
            {(verrou ? "WITH RS USE AND KEEP EXCLUSIVE LOCKS" : "")}
            """, ct, societe, cle.Numero, cle.Cle, ligne);
        if (!await rd.ReadAsync(ct)) return null;
        return new LigneSaisie
        {
            NumeroLigne = ligne, Article = rd.Texte(0), Indice = rd.Texte(1), Quantite = rd.Entier(2), PrixNet = rd.Nombre(3),
            Unite = rd.Texte(4), PrixCession = rd.Nombre(5), Statut = rd.Texte(6), LigneOrigine = rd.Entier(7),
            QuantiteLivree = rd.Entier(8), QuantiteFacturee = rd.Entier(9), Soldee = rd.Texte(10) == "O", CodeVente = rd.Texte(11),
            Designation = rd.Texte(12), PrixBrut = rd.Nombre(13), Remise = rd.Nombre(14), QuantiteOrigine = rd.Entier(15),
        };
    }

    private static async Task<int> Executer(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var cmd = Commande(cnx, tx, sql, valeurs);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<System.Data.Common.DbDataReader> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql,
        CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, tx, sql, valeurs).ExecuteReaderAsync(ct);
}
