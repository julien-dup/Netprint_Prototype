using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Données de l'ARC d'une commande (ARC105P), en lecture seule : en-tête AB001 et client, lignes NEG100 et leurs
// compléments, canevas ARC010F / ARC015F, texte complémentaire AB0011, paramètres DOS005, traductions PRT008.
// L'édition n'écrit rien (comme ARC105P).
// =====================================================================
internal sealed class ArcCommandeDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // representant : restriction du profil (B000F.ILIMIT) ; null si la commande n'existe pas ou n'est pas accessible
    public async Task<DonneesArc?> LireAsync(int societe, CleCommande cle, int? representant, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);

        // ---------- En-tête et client donneur d'ordre ----------
        EnteteArc e;
        int adresseArc, compteGeneralFacture;
        string modeCommande, paysClient;
        int echCommande, leCommande, arreteCommande;
        await using (var rd = await Lire(cnx, $"""
            SELECT a.RGEN, a.RCLI, a.CCENSE, a.CCAT, a.CCSCAT, a.CCNAEA, a.CGENF, a.CCLIF, a.CCMOD, a.CCECH, a.CCLE, a.CCART,
                   a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCDATA, a.CCDATM, a.CCDATJ, a.CCNINT, a.CCREP, a.CCINAT, a.CCPROF,
                   a.CCDES1, a.CCDES2, a.CCRFIM, a.CCECAL, a.CCPORT, a.CCDTLA, a.CCDTLM, a.CCDTLJ, a.CCDTSA, a.CCDTSS, a.CCFACT,
                   a.CCUNIT, a.CCMON, a.CTXRM,
                   c.CFJ, c.CNOM, c.CRUE1, c.CRUE2, c.CPOSTA, c.CVILLE, c.CPAYS, c.CTELEP, c.CTELEC, c.CREFFO, c.CMODE, c.CNBJOU,
                   c.CJOULE, c.CARRET, c.CDOP10, c.CEMAIL, t.TTYPDT
            FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AA0009 t ON t.RSOC = a.RSOC AND t.RCAT = a.CCAT
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ? AND a.CCTYPC IN ('3', '4'){(representant is null ? "" : " AND a.CCREP = ?")}
            """, ct, representant is null ? [societe, cle.Numero, cle.Cle] : [societe, cle.Numero, cle.Cle, representant]))
        {
            if (!await rd.ReadAsync(ct)) return null;
            adresseArc = rd.Entier(5);
            compteGeneralFacture = rd.Entier(6);
            (modeCommande, echCommande, leCommande, arreteCommande) = (rd.Texte(8), rd.Entier(9), rd.Entier(10), rd.Entier(11));
            paysClient = rd.Texte(43);
            var avecMode = modeCommande != "";
            e = new EnteteArc(
                Cle: cle, CompteGeneral: rd.Entier(0), CompteClient: rd.Entier(1), Langue: rd.Texte(51), Enseigne: rd.Texte(2),
                TypeProduit: rd.Texte(53), Categorie: rd.Texte(3), SousCategorie: rd.Texte(4),
                Client: new AdresseArc(rd.Texte(37), rd.Texte(38), rd.Texte(39), rd.Texte(40), rd.Texte(41), rd.Texte(42)),
                ClientFacture: null, CompteClientFacture: rd.Entier(7), Telephone: rd.Texte(44), Fax: rd.Texte(45),
                ReferenceFournisseur: rd.Texte(46), ModeReglement: avecMode ? modeCommande : rd.Texte(47),
                JoursEcheance: avecMode ? echCommande : rd.Entier(48), JourEcheance: avecMode ? leCommande : rd.Entier(49),
                Arrete: avecMode ? arreteCommande : rd.Entier(50), ReferenceCommande: rd.Texte(12),
                DateCommandeClient: BaseDb2.Date(rd.Entier(13), rd.Entier(14), rd.Entier(15)),
                DateDossier: BaseDb2.Date(rd.Entier(16), rd.Entier(17), rd.Entier(18)), Interlocuteur: rd.Texte(19),
                Representant: rd.Entier(20), NomRepresentant: "", NomRepresentantEtendu: "", InitialesFabricant: rd.Texte(21),
                NomFabricant: "", Profil: rd.Texte(22), NomProfil: "", Designation1: rd.Texte(23), Designation2: rd.Texte(24),
                ReferenceImprime: rd.Texte(25), Enlevement: rd.Texte(26), Port: rd.Texte(27),
                DateLivraison: BaseDb2.Date(rd.Entier(28), rd.Entier(29), rd.Entier(30)),
                SemaineLivraison: rd.Entier(32) > 0 ? $"semaine {rd.Entier(32)}/{rd.Entier(31)}" : "",
                TypeFacturation: rd.Texte(33), Unite: rd.Texte(34), Monnaie: rd.Texte(35), TauxRemisePied: rd.Nombre(36),
                Email: rd.Texte(52));
        }
        var langue = e.Langue;

        // Adresse d'envoi de l'ARC (AB001.CCNAEA → A500F) : remplace l'adresse du client
        if (adresseArc > 0)
            await using (var rd = await Lire(cnx, $"""
                SELECT ALFJ, ALRS, ALRUE1, ALRUE2, ALPOST, ALVILL, ALPAY, ALTELP, ALTELC FROM {b}.A500F
                WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALNLIG = ?
                """, ct, societe, e.CompteGeneral, e.CompteClient, adresseArc))
                if (await rd.ReadAsync(ct))
                {
                    paysClient = rd.Texte(6) != "" ? rd.Texte(6) : paysClient;
                    e = e with
                    {
                        Client = new AdresseArc(rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5)),
                        Telephone = rd.Texte(7) != "" ? rd.Texte(7) : e.Telephone, Fax = rd.Texte(8) != "" ? rd.Texte(8) : e.Fax,
                    };
                }

        async Task<string> Table(string table, string code, string arg)
        {
            if (arg == "") return "";
            // Libellé dans la langue du client (argument = code + langue, comme ARC105P), sinon le libellé de base
            foreach (var a in langue == "" ? [arg] : new[] { arg + langue, arg })
            {
                await using var rd = await Lire(cnx, $"SELECT TLIB1 FROM {b}.{table} WHERE RSOC = ? AND RTABLE = ? AND RARG = ?", ct, societe, code, a);
                if (await rd.ReadAsync(ct)) return rd.Texte(0);
            }
            return arg;   // pas de libellé : le code
        }
        e = e with
        {
            ModeReglement = await Table("AA00081", "RG", e.ModeReglement),
            Client = e.Client with { Pays = paysClient is "" or "FR" or "F" ? "" : await Table("AA00081", "PY", paysClient) },
        };

        // Client facturé (AB001.CGENF / CCLIF)
        if (e.CompteClientFacture != 0)
            await using (var rd = await Lire(cnx, $"SELECT CFJ, CNOM, CRUE1, CRUE2, CPOSTA, CVILLE FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
                             ct, societe, compteGeneralFacture, e.CompteClientFacture))
                if (await rd.ReadAsync(ct))
                    e = e with { ClientFacture = new AdresseArc(rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5)) };

        // Représentant (B000F), fabricant et profil (AI00F)
        if (e.Representant != 0)
            await using (var rd = await Lire(cnx, $"SELECT INOM, INOMEX FROM {b}.B000F WHERE RSOC = ? AND RREPR = ?", ct, societe, e.Representant))
                if (await rd.ReadAsync(ct)) e = e with { NomRepresentant = rd.Texte(0), NomRepresentantEtendu = rd.Texte(1) };
        if (e.InitialesFabricant != "")
            await using (var rd = await Lire(cnx, $"SELECT ACNOM FROM {b}.AI00F WHERE ACINI = ? FETCH FIRST 1 ROW ONLY", ct, e.InitialesFabricant))
                if (await rd.ReadAsync(ct)) e = e with { NomFabricant = rd.Texte(0) };
        if (e.Profil != "")
            await using (var rd = await Lire(cnx, $"SELECT ACNOM FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, e.Profil))
                if (await rd.ReadAsync(ct)) e = e with { NomProfil = rd.Texte(0) };

        // ---------- Lignes ----------
        var textes = string.Join(", ", Enumerable.Range(1, 19).Select(i => i < 10 ? $"t.DNTXT{i}" : $"t.DNTX{i}"));
        var brutes = new List<(LigneArc Ligne, string Norme, int Liga, AdresseArc? Saisie)>();
        await using (var rd = await Lire(cnx, $"""
            SELECT n.DNNLIG, f.LTYPE, n.DNSTAT, n.DNART, n.DNIND, n.DNDESA, n.DNQTEC, n.DNQTEL, n.DNSOLD, n.DNPUS, n.DNPUNR, n.DNTXRE,
                   n.DNMTHT, COALESCE(u.TNUM, 0), COALESCE(ul.TLIB1, u.TLIB1, n.DNUV), n.DNOPT2, s.AOPT3, s.AEMBP, s.ATYEMB, n.DNDLIV,
                   n.DNLIGA, n.DNFJ, n.DNRS, n.DNRUE1, n.DNRUE2, n.DNPOST, n.DNVILL, n.DNAPPE, n.DNDTAC, n.DNNORM,
                   COALESCE(r.ARREF, ''), s.ARFIMP, s.ANOMARC, n.DNGENF, n.DNCLIF, COALESCE(cf.CFJ, ''), COALESCE(cf.CNOM, ''),
                   {textes}
            FROM {b}.NEG100 n
            LEFT OUTER JOIN {b}.FAC020 f ON f.RSOC = n.RSOC AND f.RLIGNE = n.DNCLIG
            LEFT OUTER JOIN {b}.AA00083 u ON u.RSOC = n.RSOC AND u.RTABLE = 'UV' AND u.RARG = n.DNUV
            LEFT OUTER JOIN {b}.AA00083 ul ON ul.RSOC = n.RSOC AND ul.RTABLE = 'UV' AND ul.RARG = TRIM(n.DNUV) || CAST(? AS CHAR(1))
                AND CAST(? AS CHAR(1)) <> ' '
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = n.RSOC AND s.AARTIC = n.DNART AND s.AINDIC = n.DNIND
            LEFT OUTER JOIN {b}.SPF022 r ON r.RSOC = n.RSOC AND r.AARTIC = n.DNART AND r.AINDIC = n.DNIND AND r.ARGEN = ? AND r.ARCLI = ?
            LEFT OUTER JOIN {b}.AA0003 cf ON cf.RSOC = n.RSOC AND cf.RGEN = n.DNGENF AND cf.RCLI = n.DNCLIF AND n.DNCLIF <> 0
            LEFT OUTER JOIN {b}.NEG100T t ON t.RSOC = n.RSOC AND t.RDOSS = n.RDOSS AND t.DNDCLE = n.DNDCLE AND t.DNNLIG = n.DNNLIG
            WHERE n.RSOC = ? AND n.RDOSS = ? AND n.DNDCLE = ?
            ORDER BY n.DNNLIG
            """, ct, langue == "" ? " " : langue, langue == "" ? " " : langue, e.CompteGeneral, e.CompteClient, societe, cle.Numero, cle.Cle))
            while (await rd.ReadAsync(ct))
            {
                var saisie = rd.Texte(22) != "" || rd.Texte(26) != ""
                    ? new AdresseArc(rd.Texte(21), rd.Texte(22), rd.Texte(23), rd.Texte(24), rd.Texte(25), rd.Texte(26)) : null;
                var clientFacture = rd.Entier(34) != 0 && (rd.Entier(33) != e.CompteGeneral || rd.Entier(34) != e.CompteClient)
                    ? $"{rd.Texte(35)} {rd.Texte(36)} ({rd.Entier(33)} {rd.Entier(34)})".Trim() : "";
                var reference = rd.Texte(30) != "" ? rd.Texte(30) : rd.Texte(31);
                var ligne = new LigneArc(
                    Numero: rd.Entier(0), TypeLigne: rd.Texte(1), ALaDemande: rd.Texte(2) == StatutsLigne.ALaDemande, Article: rd.Texte(3),
                    Indice: rd.Texte(4), Designation: rd.Texte(5), QuantiteCommandee: rd.Entier(6), QuantiteLivree: rd.Entier(7),
                    Soldee: rd.Texte(8) == "O", PrixNet: rd.Nombre(9), PrixBrut: rd.Nombre(10), Remise: rd.Nombre(11), Montant: rd.Nombre(12),
                    Tnum: Convert.ToInt64(rd.GetValue(13)), LibelleUnite: rd.Texte(14), Gratuite: rd.Texte(15) == "O",
                    ParConditionnement: rd.Texte(16) == "O", Emballage: rd.Entier(17), TypeEmballage: TypeEmballage(rd.Texte(18)),
                    DateLivraison: BaseDb2.DateAAAAMMJJ(rd.Entier(19)), AdresseLivraison: null, NumeroAppel: rd.Texte(27),
                    DateAppel: BaseDb2.DateAAAAMMJJ(rd.Entier(28)),
                    Textes: Enumerable.Range(37, 19).Select(rd.Texte).Reverse().SkipWhile(x => x == "").Reverse().ToList(),
                    MentionsNorme: [], ReferenceClient: reference, Marche: reference != "" ? rd.Texte(32) : "",
                    ClientFactureLigne: clientFacture);
                brutes.Add((ligne, rd.Texte(29), rd.Entier(20), saisie));
            }

        // Adresses de livraison des lignes (A500F), sinon adresse saisie, sinon client
        var adresses = new Dictionary<int, AdresseArc>();
        var numeros = brutes.Select(x => x.Liga).Where(x => x > 0).Distinct().ToList();
        if (numeros.Count > 0)
            await using (var rd = await Lire(cnx, $"""
                SELECT ALNLIG, ALFJ, ALRS, ALRUE1, ALRUE2, ALPOST, ALVILL FROM {b}.A500F
                WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALNLIG IN ({string.Join(", ", numeros.Select(_ => "?"))})
                """, ct, [societe, e.CompteGeneral, e.CompteClient, .. numeros.Cast<object?>()]))
                while (await rd.ReadAsync(ct))
                    adresses[rd.Entier(0)] = new AdresseArc(rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6));
        var siege = await AdresseClientAsync(cnx, b, societe, e.CompteGeneral, e.CompteClient, ct);

        // Mentions de norme pour l'ARC (NOR010F), par code norme : enseigne de la commande puis toutes enseignes
        var normes = new Dictionary<string, List<string>>();
        var codes = brutes.Select(x => x.Norme).Where(x => x != "").Distinct().ToList();
        if (codes.Count > 0)
            await using (var rd = await Lire(cnx, $"""
                SELECT NOCOD, NOENS, NOARC1, NOARC2, NOARC3, NOARC4 FROM {b}.NOR010F
                WHERE RSOC = ? AND NOENS IN (?, ' ') AND NOCOD IN ({string.Join(", ", codes.Select(_ => "?"))})
                ORDER BY NOENS
                """, ct, [societe, e.Enseigne == "" ? " " : e.Enseigne, .. codes.Cast<object?>()]))
                // Enseigne de la commande prioritaire sur « toutes enseignes » (lue en dernier)
                while (await rd.ReadAsync(ct))
                    if (rd.Texte(1) != "" || !normes.ContainsKey(rd.Texte(0)))
                        normes[rd.Texte(0)] = Enumerable.Range(2, 4).Select(rd.Texte).Where(x => x != "").ToList();

        var lignes = brutes.Select(x => x.Ligne with
        {
            AdresseLivraison = x.Liga > 0 && adresses.TryGetValue(x.Liga, out var a) ? a : x.Saisie ?? siege,
            MentionsNorme = normes.GetValueOrDefault(x.Norme) ?? [],
        }).ToList();

        // ---------- Canevas (chapitres 1, 2 et 5), texte complémentaire, paramètres, traductions ----------
        var canevas = new List<LigneCanevas>();
        await using (var rd = await Lire(cnx, $"""
            SELECT RTYPD, RCAT, RSCAT, RCHAP, ARLANG, ARNLIG, ARTXT, ARDB FROM {b}.ARC015F
            WHERE RSOC = ? AND RCHAP IN ('1', '2', '5', 'A') AND ARLANG IN (' ', ?)
            ORDER BY RCHAP, ARNLIG
            """, ct, societe, langue == "" ? " " : langue))
            while (await rd.ReadAsync(ct))
                canevas.Add(new LigneCanevas(rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Entier(5),
                    rd.IsDBNull(6) ? "" : Convert.ToString(rd.GetValue(6))!.TrimEnd(), rd.Texte(7) == "O"));

        var complement = new List<string>();
        await using (var rd = await Lire(cnx, $"""
            SELECT CCTXT1, CCTXT2, CCTXT3, CCTXT4, CCTXT5, CCTXT6, CCTXT7, CCTXT8 FROM {b}.AB0011
            WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCIARC = 'O'
            """, ct, societe, cle.Numero, cle.Cle))
            if (await rd.ReadAsync(ct))
                complement = Enumerable.Range(0, 8).Select(rd.Texte).Reverse().SkipWhile(x => x == "").Reverse().ToList();

        bool soldees = true, butoir = false, cadre = true;
        var position = "B";
        await using (var rd = await Lire(cnx, $"SELECT PLGARC, PDPRTR, PGRES1 FROM {b}.DOS005 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct))
            {
                soldees = rd.Texte(0) != "N";
                var options = rd.Texte(1).PadRight(8);
                cadre = options[4] != 'N';
                position = options[7] == 'H' ? "H" : "B";
                butoir = rd.Texte(2) == "O";
            }

        var traductions = new List<string>();
        if (langue != "")
        {
            var colonnes = string.Join(", ", Enumerable.Range(1, 33).Select(i => $"TRADUC{i}"));
            await using var rd = await Lire(cnx, $"SELECT {colonnes} FROM {b}.PRT008 WHERE MLANGUE = ? AND MPGM = 'ARC'", ct, langue);
            if (await rd.ReadAsync(ct)) traductions = Enumerable.Range(0, 33).Select(rd.Texte).ToList();
        }

        return new DonneesArc(e, lignes, canevas, complement, position, soldees, butoir, cadre, traductions);
    }

    // Lignes soldées par défaut (DOS005.PLGARC) et client de la commande, pour la fenêtre d'édition
    public async Task<(bool AvecSoldees, int CompteGeneral, int CompteClient)?> OptionsAsync(int societe, CleCommande cle, int? representant,
        CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT COALESCE(d.PLGARC, 'O'), a.RGEN, a.RCLI
            FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.DOS005 d ON d.RSOC = a.RSOC
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ? AND a.CCTYPC IN ('3', '4'){(representant is null ? "" : " AND a.CCREP = ?")}
            """, ct, representant is null ? [societe, cle.Numero, cle.Cle] : [societe, cle.Numero, cle.Cle, representant]);
        return await rd.ReadAsync(ct) ? (rd.Texte(0) != "N", rd.Entier(1), rd.Entier(2)) : null;
    }

    private static async Task<AdresseArc?> AdresseClientAsync(OdbcConnection cnx, string b, int societe, int gen, int cli, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, $"SELECT CFJ, CNOM, CRUE1, CRUE2, CPOSTA, CVILLE FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?",
            ct, societe, gen, cli);
        return await rd.ReadAsync(ct) ? new AdresseArc(rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5)) : null;
    }

    // Liste fixe 28 de DDVLLSL0 (unités de conditionnement)
    internal static string TypeEmballage(string code) => code switch
    {
        "B" => "Bobine(s)", "C" => "Carton(s)", "F" => "Filtainer", "P" => "Paquet(s)", "Q" => "Palette(s)", "R" => "Rouleau(x)",
        "T" => "Barquettes", _ => "",
    };

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return await cmd.ExecuteReaderAsync(ct);
    }
}
