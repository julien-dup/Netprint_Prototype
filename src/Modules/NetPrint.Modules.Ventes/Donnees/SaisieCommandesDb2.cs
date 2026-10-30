using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Données de la saisie de l'en-tête de commande (DOS505). Lectures de paramètres et de références,
// écriture de la commande en une transaction (tables journalisées dans NP11JRN/JRN).
// Écarts volontaires avec le RPG (signalés « ÉCART ») : transaction, AA0003.CDDCLE reçoit la clé définitive,
// seules les colonnes de l'en-tête sont écrites (le RPG pouvait recopier celles d'un autre dossier lu avant).
// =====================================================================
internal interface ISourceSaisieCommandes
{
    Task<ParametresSaisie> ParametresAsync(int societe, CancellationToken ct);
    Task<ListesSaisie> ListesAsync(int societe, CancellationToken ct);
    Task<ClientSaisie?> ClientAsync(int societe, int compteGeneral, int compteClient, CancellationToken ct);
    Task<int?> CompteGeneralEnseigneAsync(int societe, string enseigne, CancellationToken ct);   // null : enseigne absente
    Task<DefautsSaisie> DefautsAsync(int societe, string profil, ClientSaisie client, int compteGeneral, int compteClient,
        string categorie, CancellationToken ct);
    Task<ReferencesEntete> VerifierAsync(int societe, EnteteSaisie s, CancellationToken ct);
    Task<CleCommande> CreerAsync(int societe, string profil, EnteteSaisie s, ClientSaisie client, ParametresSaisie p,
        (int Annee, int Semaine) semaine, CancellationToken ct);
    Task<EnteteSaisie?> LireAsync(int societe, CleCommande cle, int? representant, CancellationToken ct);
    Task<bool> ModifierAsync(int societe, string profil, CleCommande cle, EnteteSaisie s, (int Annee, int Semaine) semaine,
        Func<EnteteSaisie, string> empreinte, CancellationToken ct);
}

internal sealed record ParametresSaisie(
    bool Enseignes,          // AA0007.SOP03
    string FabricantDefaut,  // AA0007.SOP13 : '2' ou '3' = initiales du profil connecté, sinon fabricant du client
    bool NumeroParEnseigne,  // SOP17 (avec SOP03)
    bool GarderNumero,       // DOS005.POPT02
    bool ForcerCle9,         // DOS005.POPT05
    bool SousTraitance,      // DOS005.PSTRIS
    string EditionDetailBl); // EXP005.FEDEBL

internal sealed record ListesSaisie(
    IReadOnlyList<CodeLibelle> Enseignes, IReadOnlyList<CodeLibelle> ModesReglement, IReadOnlyList<CodeLibelle> CodesVente);

internal sealed record ClientSaisie(
    string Nom, string Qualite, int DateFermeture, int Representant, decimal TauxCommission, string ModeTransport,
    string TypePort, string CodeVente, string Revendeur, int CompteGeneralRegroupement, int CompteClientRegroupement,
    string Fabricant, string Monnaie, bool InterlocuteurObligatoire, string FacturationAuto, int AdresseFactureAuto,
    string TypePieceFacture, string ControleEncours, decimal DecouvertMaison, decimal DecouvertSfac,
    decimal EncoursComptable, decimal EncoursCommercial);

internal sealed record DefautsSaisie(decimal? TauxRepresentant, string InitialesProfil, string Interlocuteur, int AdresseArc,
    bool CategorieExiste, bool SousCategorieExiste);

// Résultat des contrôles d'existence de l'écran 2
internal sealed record ReferencesEntete(
    (int Annee, int Semaine)? SemaineDeLaDate, bool SemaineExiste, bool AdresseArcExiste, bool ClientFactureExiste,
    bool RepresentantExiste, bool FabricantExiste, string? TypeCodeVente, bool ModeReglementExiste);

internal sealed class SaisieCommandesDb2(IConfiguration config) : ISourceSaisieCommandes
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task<ParametresSaisie> ParametresAsync(int societe, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        string sop03 = "", sop13 = "", sop17 = "", popt02 = "N", popt05 = "", pstris = "", fedebl = "";
        await using (var rd = await Lire(cnx, $"SELECT SOP03, SOP13, SOP17 FROM {b}.AA0007 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct)) (sop03, sop13, sop17) = (rd.Texte(0), rd.Texte(1), rd.Texte(2));
        await using (var rd = await Lire(cnx, $"SELECT POPT02, POPT05, PSTRIS FROM {b}.DOS005 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct)) (popt02, popt05, pstris) = (rd.Texte(0), rd.Texte(1), rd.Texte(2));
        await using (var rd = await Lire(cnx, $"SELECT FEDEBL FROM {b}.EXP005 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct)) fedebl = rd.Texte(0);
        return new ParametresSaisie(sop03 == "O", sop13, sop03 == "O" && sop17 == "O", popt02 == "O", popt05 == "O",
            pstris == "O", fedebl);
    }

    public async Task<ListesSaisie> ListesAsync(int societe, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        return new ListesSaisie(
            await CodesAsync(cnx, $"SELECT RENSEI, ENRS FROM {b}.PRT320F WHERE RSOC = ? AND ENTAG <> '*' ORDER BY RENSEI", societe, ct),
            await CodesAsync(cnx, $"SELECT RARG, TLIB1 FROM {b}.AA00081 WHERE RSOC = ? AND RTABLE = 'RG' AND TTAG <> '*' ORDER BY RARG", societe, ct),
            await CodesAsync(cnx, $"SELECT RLIGNE, LLIB FROM {b}.FAC020 WHERE RSOC = ? AND LTYPE = 'V' ORDER BY RLIGNE", societe, ct));
    }

    private static async Task<List<CodeLibelle>> CodesAsync(OdbcConnection cnx, string sql, int societe, CancellationToken ct)
    {
        var liste = new List<CodeLibelle>();
        await using var rd = await Lire(cnx, sql, ct, societe);
        while (await rd.ReadAsync(ct)) liste.Add(new CodeLibelle(rd.Texte(0), rd.Texte(1)));
        return liste;
    }

    public async Task<ClientSaisie?> ClientAsync(int societe, int compteGeneral, int compteClient, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT c.CNOM, c.CQUACL, c.CDFERM, c.CREPRE, c.CTXCO, c.CMODTR, c.CTYPPO, c.CDCVD, c.CREVEN, c.CGENR, c.CAUXR,
                   c.CINAT, c.CMON, c.CDOPT6, c.CDOPT2, c.CDNAF, c.CDRETF, c.CGSFAC, c.CDECMA, c.CSFAFC, p.RECCP, p.RECCO
            FROM {Bib}.AA0003 c
            LEFT OUTER JOIN {Bib}.PRT055F p ON p.RSOC = c.RSOC AND p.RGEN = c.RGEN AND p.RCLI = c.RCLI
            WHERE c.RSOC = ? AND c.RGEN = ? AND c.RCLI = ?
            """, ct, societe, compteGeneral, compteClient);
        if (!await rd.ReadAsync(ct)) return null;
        return new ClientSaisie(
            Nom: rd.Texte(0), Qualite: rd.Texte(1), DateFermeture: rd.Entier(2), Representant: rd.Entier(3),
            TauxCommission: rd.Nombre(4), ModeTransport: rd.Texte(5), TypePort: rd.Texte(6), CodeVente: rd.Texte(7),
            Revendeur: rd.Texte(8), CompteGeneralRegroupement: rd.Entier(9), CompteClientRegroupement: rd.Entier(10),
            Fabricant: rd.Texte(11), Monnaie: rd.Texte(12), InterlocuteurObligatoire: rd.Texte(13) == "O",
            FacturationAuto: rd.Texte(14), AdresseFactureAuto: rd.Entier(15), TypePieceFacture: rd.Texte(16),
            ControleEncours: rd.Texte(17), DecouvertMaison: rd.Nombre(18), DecouvertSfac: rd.Nombre(19),
            EncoursComptable: rd.Nombre(20), EncoursCommercial: rd.Nombre(21));
    }

    public async Task<int?> CompteGeneralEnseigneAsync(int societe, string enseigne, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"SELECT EGENC FROM {Bib}.PRT320F WHERE RSOC = ? AND RENSEI = ?", ct, societe, enseigne);
        return await rd.ReadAsync(ct) ? rd.Entier(0) : null;
    }

    public async Task<DefautsSaisie> DefautsAsync(int societe, string profil, ClientSaisie client, int compteGeneral,
        int compteClient, string categorie, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        decimal? taux = null;
        await using (var rd = await Lire(cnx, $"SELECT ITXCOM FROM {b}.B000F WHERE RSOC = ? AND RREPR = ?", ct, societe, client.Representant))
            if (await rd.ReadAsync(ct)) taux = rd.Nombre(0);
        var initiales = "";
        await using (var rd = await Lire(cnx, $"SELECT ACINI FROM {b}.AI00F WHERE RPRF = ? FETCH FIRST 1 ROW ONLY", ct, profil.ToUpperInvariant()))
            if (await rd.ReadAsync(ct)) initiales = rd.Texte(0);
        // Interlocuteur : premier contact « destinataire ARC par défaut » (civilité + nom)
        var interlocuteur = "";
        await using (var rd = await Lire(cnx, $"""
            SELECT CNCIV, CNNOM FROM {b}.PRT051F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND CNARCD = 'O' AND CNTAG <> '*'
            ORDER BY CNLIG FETCH FIRST 1 ROW ONLY
            """, ct, societe, compteGeneral, compteClient))
            if (await rd.ReadAsync(ct)) interlocuteur = $"{rd.Texte(0)} {rd.Texte(1)}".Trim();
        // Adresse d'envoi d'ARC : la seule adresse « envoi ARC » du client (0 s'il y en a plusieurs ou aucune)
        var adresses = new List<int>();
        await using (var rd = await Lire(cnx, $"""
            SELECT ALNLIG FROM {b}.A500F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALTAG <> '*' AND ALEARC = 'O'
            FETCH FIRST 2 ROWS ONLY
            """, ct, societe, compteGeneral, compteClient))
            while (await rd.ReadAsync(ct)) adresses.Add(rd.Entier(0));
        bool categorieExiste, sousCategorieExiste;
        await using (var rd = await Lire(cnx, $"SELECT 1 FROM {b}.AA0009 WHERE RSOC = ? AND RCAT = ?", ct, societe, categorie))
            categorieExiste = await rd.ReadAsync(ct);
        await using (var rd = await Lire(cnx, $"SELECT 1 FROM {b}.AE0003 WHERE RSOC = ? AND RCAT = ? AND RSCAT = ?", ct, societe, categorie, categorie))
            sousCategorieExiste = await rd.ReadAsync(ct);
        return new DefautsSaisie(taux, initiales, interlocuteur, adresses.Count == 1 ? adresses[0] : 0, categorieExiste, sousCategorieExiste);
    }

    public async Task<ReferencesEntete> VerifierAsync(int societe, EnteteSaisie s, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        async Task<bool> Existe(string sql, params object?[] v)
        {
            await using var rd = await Lire(cnx, sql, ct, v);
            return await rd.ReadAsync(ct);
        }

        // Semaine d'une date : la semaine D300F qui commence au plus tard ce jour-là
        (int, int)? semaineDate = null;
        if (s.DateLivraison is { } d)
        {
            await using var rd = await Lire(cnx, $"""
                SELECT RAN, RSEM FROM {b}.D300F WHERE RSOC = ? AND (SEMAD * 10000 + SEMMD * 100 + SEMJD) <= ?
                ORDER BY SEMAD DESC, SEMMD DESC, SEMJD DESC FETCH FIRST 1 ROW ONLY
                """, ct, societe, BaseDb2.AAAAMMJJ(d));
            if (await rd.ReadAsync(ct)) semaineDate = (rd.Entier(0), rd.Entier(1));
        }
        var semaineExiste = s.SemaineLivraison is not > 0 || await Existe(
            $"SELECT 1 FROM {b}.D300F WHERE RSOC = ? AND RAN = ? AND RSEM = ?", societe, s.AnneeLivraison ?? 0, s.SemaineLivraison);
        var adresse = s.AdresseArc is not > 0 || await Existe(
            $"SELECT 1 FROM {b}.A500F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALNLIG = ? AND ALTAG <> '*'",
            societe, s.CompteGeneral, s.CompteClient, s.AdresseArc);
        var facture = s.CompteClientFacture is not > 0 || await Existe(
            $"SELECT 1 FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", societe, s.CompteGeneralFacture ?? 0, s.CompteClientFacture);
        var representant = await Existe($"SELECT 1 FROM {b}.B000F WHERE RSOC = ? AND RREPR = ?", societe, s.Representant ?? 0);
        var fabricant = s.Fabricant == "" || await Existe($"SELECT 1 FROM {b}.AI00F WHERE ACINI = ?", s.Fabricant);
        string? typeCode = null;
        if (s.CodeVente != "")
        {
            await using var rd = await Lire(cnx, $"SELECT LTYPE FROM {b}.FAC020 WHERE RSOC = ? AND RLIGNE = ?", ct, societe, s.CodeVente);
            if (await rd.ReadAsync(ct)) typeCode = rd.Texte(0);
        }
        var mode = s.ModeReglement == "" || await Existe(
            $"SELECT 1 FROM {b}.AA00081 WHERE RSOC = ? AND RTABLE = 'RG' AND RARG = ? AND TTAG <> '*'", societe, s.ModeReglement);
        return new ReferencesEntete(semaineDate, semaineExiste, adresse, facture, representant, fabricant, typeCode, mode);
    }

    // ------------------------------------------------------------------
    // Création (SPVAL de DOS505), en une transaction
    // ------------------------------------------------------------------
    public async Task<CleCommande> CreerAsync(int societe, string profil, EnteteSaisie s, ClientSaisie client,
        ParametresSaisie p, (int Annee, int Semaine) semaine, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // N° définitif : GETDOSSIER 'D' (le compteur est mis à jour par le RPG, hors transaction : un n° peut se perdre)
            string retour;
            await using (var cmd = Commande(cnx, tx, $"""
                VALUES {b}.GETDOSSIER(CAST(? AS NUMERIC(4, 0)), CAST(? AS CHAR(1)), CAST(? AS CHAR(2)),
                                      CAST(? AS NUMERIC(6, 0)), CAST(? AS NUMERIC(7, 0)), 'D')
                """, societe, s.Enseigne == "" ? " " : s.Enseigne, s.Categorie, s.CompteGeneral, s.CompteClient))
                retour = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "";
            var (numero, cleGetDossier) = NumerotationCommande.Decouper(retour);
            var cle = NumerotationCommande.Cle(p.GarderNumero, p.ForcerCle9, cleGetDossier);

            // Clé déjà prise pour ce n° (DOS505 R11) : chiffres 1 à 9 puis 0
            var prises = new HashSet<string>();
            // Dossiers ouverts et OF à ouvrir (AB001T) : un OF demandé garde sa clé jusqu'à son ouverture
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT CCDCLE FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ?
                UNION SELECT CCDCLE FROM {b}.AB001T WHERE RSOC = ? AND RDOSS = ?
                """, ct, societe, numero, societe, numero))
                while (await rd.ReadAsync(ct)) prises.Add(rd.Texte(0));
            cle = NumerotationCommande.CleLibre(cle, prises)
                ?? throw new Socle.RegleMetierException($"Toutes les clés du dossier n° {numero} sont déjà utilisées.");

            var colonnes = ColonnesEntete(s, semaine);
            colonnes.InsertRange(0,
            [
                ("RSOC", societe), ("RDOSS", numero), ("CCDCLE", cle), ("RGEN", s.CompteGeneral), ("RCLI", s.CompteClient),
                ("CCAT", s.Categorie), ("CCSCAT", s.SousCategorie), ("CCTYPC", s.TypeCommande), ("CCENSE", s.Enseigne),
                ("CCDATA", s.DateDossier.Year), ("CCDATM", s.DateDossier.Month), ("CCDATJ", s.DateDossier.Day),
                ("CCPROF", profil.ToUpperInvariant()), ("CCADEV", "N"), ("CCMON", s.Monnaie),
            ]);
            await using (var cmd = Commande(cnx, tx,
                $"INSERT INTO {b}.AB001 ({string.Join(", ", colonnes.Select(c => c.Colonne))}) VALUES ({string.Join(", ", colonnes.Select(_ => "?"))})",
                [.. colonnes.Select(c => c.Valeur)]))
                await cmd.ExecuteNonQueryAsync(ct);

            // Paramètres de facturation automatique (AB001FA), valeurs par défaut du client.
            // Comme le RPG (écriture ou mise à jour) : un AB001FA peut rester d'un ancien dossier de même n° et clé.
            object?[] fa = [NumerotationCommande.FacturationAutomatique(client.FacturationAuto) ? "O" : "N", client.AdresseFactureAuto,
                p.EditionDetailBl == "" ? " " : p.EditionDetailBl, client.TypePieceFacture == "" ? "1" : client.TypePieceFacture];
            int misAJour;
            await using (var cmd = Commande(cnx, tx, $"""
                UPDATE {b}.AB001FA SET FAAUTO = ?, FANAL = ?, FAEDTB = ?, FACRC = '2', FATPC = ?, FAFJ = ' ', FANOM = ' ',
                    FARUE1 = ' ', FARUE2 = ' ', FAPOST = ' ', FAVILL = ' ', FAATT = ' ', FAORTE = ' ', FACPT = 0, FAACPT = 0
                WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                """, [.. fa, societe, numero, cle]))
                misAJour = await cmd.ExecuteNonQueryAsync(ct);
            if (misAJour == 0)
                await using (var cmd = Commande(cnx, tx, $"""
                    INSERT INTO {b}.AB001FA (FAAUTO, FANAL, FAEDTB, FATPC, RSOC, RDOSS, CCDCLE, FACRC) VALUES (?, ?, ?, ?, ?, ?, ?, '2')
                    """, [.. fa, societe, numero, cle]))
                    await cmd.ExecuteNonQueryAsync(ct);

            // Fiche client (le client facturé s'il y en a un) : première et dernière commande, dernier dossier.
            // ÉCART : CDDCLE reçoit la clé définitive (le RPG gardait celle d'avant le contrôle des doublons).
            var (genMaj, cliMaj) = s.CompteClientFacture is > 0 ? (s.CompteGeneralFacture ?? 0, s.CompteClientFacture.Value)
                : (s.CompteGeneral, s.CompteClient);
            var dateDossier = BaseDb2.AAAAMMJJ(s.DateDossier);
            await using (var cmd = Commande(cnx, tx, $"""
                UPDATE {b}.AA0003 SET CDDTPC = CASE WHEN CDDTPC = 0 THEN ? ELSE CDDTPC END, CDDTDC = ?, CDDDOS = ?, CDDCLE = ?
                WHERE RSOC = ? AND RGEN = ? AND RCLI = ?
                """, dateDossier, dateDossier, numero, cle, societe, genMaj, cliMaj))
                await cmd.ExecuteNonQueryAsync(ct);

            // Historique DOS040 (type EN) : seulement pour un dossier antidaté, comme DOS505
            if (s.DateDossier < DateOnly.FromDateTime(DateTime.Today))
                await HistoriqueAsync(cnx, tx, b, societe, numero, cle, s, "EN", ct);
            await TexteComplementaireAsync(cnx, tx, b, societe, numero, cle, s, null, ct);

            await tx.CommitAsync(ct);
            await AuditAsync(cnx, b, societe, profil, numero, cle, "CR");
            return new CleCommande(numero, cle);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // Colonnes de l'écran 2 (écrites à la création et à la modification)
    private static List<(string Colonne, object? Valeur)> ColonnesEntete(EnteteSaisie s, (int Annee, int Semaine) semaine)
    {
        var livrer = s.EnlevementOuLivraison == "A";
        return
        [
            ("CCFACT", s.TypeFacturation), ("CCDES1", s.Designation1), ("CCDES2", s.Designation2),
            ("CCDTLA", s.DateLivraison?.Year ?? 0), ("CCDTLM", s.DateLivraison?.Month ?? 0), ("CCDTLJ", s.DateLivraison?.Day ?? 0),
            ("CCDTSA", semaine.Semaine == 0 ? 0 : semaine.Annee), ("CCDTSS", semaine.Semaine),
            ("CCNAEA", s.AdresseArc ?? 0), ("CCNINT", s.Interlocuteur), ("CCREFC", s.ReferenceCommandeClient),
            ("CCDENA", s.DateCommandeClient?.Year ?? 0), ("CCDENM", s.DateCommandeClient?.Month ?? 0),
            ("CCDENJ", s.DateCommandeClient?.Day ?? 0),
            ("CGENF", s.CompteGeneralFacture ?? 0),
            ("CCLIF", s.CompteClientFacture ?? 0), ("CCRCVD", s.Revendeur ? "O" : "N"), ("CCREP", s.Representant ?? 0),
            ("CCTXCO", s.TauxCommission ?? 0m), ("CCINAT", s.Fabricant), ("CCTYPV", s.CodeVente),
            ("CCOP10", s.SousTraitee ? "O" : "N"), ("CCECAL", s.EnlevementOuLivraison),
            // Enlèvement : ni port, ni lieux, ni adresse de facturation (DOS505 l. 1341-1343)
            ("CCPORT", livrer ? s.Port : " "), ("CCNBLV", livrer ? s.NbLieux ?? 0 : 0), ("CCLFAC", livrer ? s.AdresseFacturation : " "),
            ("CCMOD", s.ModeReglement), ("CCECH", s.Echeance ?? 0), ("CCLE", s.Le ?? 0), ("CCART", s.Arrete ?? 0),
            ("CCESC", s.Escompte ?? 0m), ("CCREL", s.Releve), ("CCNOMH", s.NomHonoraires), ("CCMTHO", s.MontantHonoraires ?? 0m),
            ("CCTXHO", s.TauxHonoraires ?? 0m), ("CCCDEG", s.CommandesGroupees ?? 0), ("CCCDET", s.TexteCommandesGroupees),
            ("CCEDIQ", s.EditionEtiquettes ? "O" : "N"),
        ];
    }

    // Texte complémentaire (DOS560 SPVAL) : AB0011 mise à jour, ou créée s'il y a un texte ou une case cochée.
    // Le texte arrive déjà contrôlé (8 lignes de 40 au plus). Renvoie vrai si le texte ou ses cases ont changé.
    private static async Task<bool> TexteComplementaireAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, int numero,
        string cle, EnteteSaisie s, EnteteSaisie? avant, CancellationToken ct)
    {
        var lignes = (s.TexteComplementaire == "" ? [] : s.TexteComplementaire.Split('\n')).Take(8).ToList();
        while (lignes.Count < 8) lignes.Add("");
        static string ON(bool v) => v ? "O" : "N";
        object?[] valeurs = [.. lignes.Select(l => l == "" ? " " : l), ON(s.TexteSurArc), ON(s.TexteSurBl), ON(s.TexteSurFacture)];
        var change = avant is null
            ? s.TexteComplementaire != "" || s.TexteSurArc || s.TexteSurBl || s.TexteSurFacture
            : avant.TexteComplementaire != s.TexteComplementaire || avant.TexteSurArc != s.TexteSurArc
              || avant.TexteSurBl != s.TexteSurBl || avant.TexteSurFacture != s.TexteSurFacture;
        if (!change) return false;
        var misAJour = 0;
        // Création : la ligne ne peut pas exister (sauf reste d'un ancien dossier de même n° : mise à jour, comme AB001FA)
        await using (var cmd = Commande(cnx, tx, $"""
            UPDATE {b}.AB0011 SET CCTXT1 = ?, CCTXT2 = ?, CCTXT3 = ?, CCTXT4 = ?, CCTXT5 = ?, CCTXT6 = ?, CCTXT7 = ?, CCTXT8 = ?,
                CCIARC = ?, CCIBL = ?, CCIFAC = ?
            WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
            """, [.. valeurs, societe, numero, cle]))
            misAJour = await cmd.ExecuteNonQueryAsync(ct);
        if (misAJour == 0)
            await using (var cmd = Commande(cnx, tx, $"""
                INSERT INTO {b}.AB0011 (CCTXT1, CCTXT2, CCTXT3, CCTXT4, CCTXT5, CCTXT6, CCTXT7, CCTXT8, CCIARC, CCIBL, CCIFAC,
                    RSOC, RDOSS, CCDCLE)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """, [.. valeurs, societe, numero, cle]))
                await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static async Task HistoriqueAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, int numero,
        string cle, EnteteSaisie s, string type, CancellationToken ct)
    {
        var maintenant = DateTime.Now;
        await using var cmd = Commande(cnx, tx, $"""
            INSERT INTO {b}.DOS040 (RSOC, RDOSS, HICLE, HIGEN, HICLI, HIINAM, HITYPM, HIDTMA, HIDTMM, HIDTMJ, HIHEUR)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, societe, numero, cle, s.CompteGeneral, s.CompteClient, s.Fabricant == "" ? " " : s.Fabricant, type,
            maintenant.Year, maintenant.Month, maintenant.Day, maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Audit EVE200 (programme EVE400) : CR création, MO modification.
    // EVE200 n'est pas journalisée : écrite après la validation, hors transaction ; une erreur est ignorée, comme dans EVE400.
    // Jamais d'exception ni d'annulation : appelé après la validation, il ne doit pas faire croire à un échec.
    private static async Task AuditAsync(OdbcConnection cnx, string b, int societe, string profil,
        int numero, string cle, string type)
    {
        await using var cmd = Commande(cnx, null, $"""
            INSERT INTO {b}.EVE200 (AMLIG, RSOC, AMTYPE, AMDATE, AMPROF, AMDOSS, AMDCLE)
            SELECT COALESCE(MAX(AMLIG), 0) + 1, CAST(? AS NUMERIC(4, 0)), CAST(? AS CHAR(2)), CURRENT TIMESTAMP,
                   CAST(? AS CHAR(10)), CAST(? AS NUMERIC(5, 0)), CAST(? AS CHAR(1)) FROM {b}.EVE200
            """, societe, type, profil.ToUpperInvariant(), numero, cle);
        try
        {
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // audit non bloquant
        }
    }

    // ------------------------------------------------------------------
    // Lecture pour modification et modification (DOS505 COPT 02)
    // ------------------------------------------------------------------
    private const string ColonnesLecture = """
        a.RGEN, a.RCLI, c.CNOM, a.CCENSE, a.CCDATA, a.CCDATM, a.CCDATJ, a.CCTYPC, a.CCAT, a.CCSCAT, a.CCFACT, a.CCDES1, a.CCDES2,
        a.CCDTLA, a.CCDTLM, a.CCDTLJ, a.CCDTSA, a.CCDTSS, a.CCNAEA, a.CCNINT, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ,
        a.CGENF, a.CCLIF, a.CCRCVD, a.CCREP, a.CCTXCO, a.CCINAT, a.CCTYPV, a.CCMON, a.CCOP10, a.CCECAL, a.CCPORT, a.CCNBLV,
        a.CCLFAC, a.CCMOD, a.CCECH, a.CCLE, a.CCART, a.CCESC, a.CCREL, a.CCNOMH, a.CCMTHO, a.CCTXHO, a.CCCDEG, a.CCCDET,
        a.CCEDIQ, a.CCTAGS, a.CCOP11,
        (SELECT COUNT(*) FROM {b}.NEG100 n WHERE n.RSOC = a.RSOC AND n.RDOSS = a.RDOSS AND n.DNDCLE = a.CCDCLE),
        t.CCTXT1, t.CCTXT2, t.CCTXT3, t.CCTXT4, t.CCTXT5, t.CCTXT6, t.CCTXT7, t.CCTXT8, t.CCIARC, t.CCIBL, t.CCIFAC
        """;

    public async Task<EnteteSaisie?> LireAsync(int societe, CleCommande cle, int? representant, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await LireEnteteAsync(cnx, null, Bib, societe, cle, representant, ct);
    }

    private static async Task<EnteteSaisie?> LireEnteteAsync(OdbcConnection cnx, OdbcTransaction? tx, string b, int societe,
        CleCommande cle, int? representant, CancellationToken ct)
    {
        var sql = $"""
            SELECT {ColonnesLecture.Replace("{b}", b)}
            FROM {b}.AB001 a
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.AB0011 t ON t.RSOC = a.RSOC AND t.RDOSS = a.RDOSS AND t.CCDCLE = a.CCDCLE
            WHERE a.RSOC = ? AND a.RDOSS = ? AND a.CCDCLE = ? AND a.CCTYPC IN ('3', '4'){(representant is null ? "" : " AND a.CCREP = ?")}
            """;
        object?[] valeurs = representant is null ? [societe, cle.Numero, cle.Cle] : [societe, cle.Numero, cle.Cle, representant];
        await using var rd = await Lire(cnx, tx, sql, ct, valeurs);
        if (!await rd.ReadAsync(ct)) return null;
        static int? Pos(int v) => v == 0 ? null : v;
        static decimal? PosD(decimal v) => v == 0 ? null : v;
        return new EnteteSaisie
        {
            CompteGeneral = rd.Entier(0), CompteClient = rd.Entier(1), NomClient = rd.Texte(2), Enseigne = rd.Texte(3),
            DateDossier = BaseDb2.Date(rd.Entier(4), rd.Entier(5), rd.Entier(6)) ?? DateOnly.MinValue,
            TypeCommande = rd.Texte(7), Categorie = rd.Texte(8), SousCategorie = rd.Texte(9), TypeFacturation = rd.Texte(10),
            Designation1 = rd.Texte(11), Designation2 = rd.Texte(12),
            DateLivraison = BaseDb2.Date(rd.Entier(13), rd.Entier(14), rd.Entier(15)),
            AnneeLivraison = Pos(rd.Entier(16)), SemaineLivraison = Pos(rd.Entier(17)), AdresseArc = Pos(rd.Entier(18)),
            Interlocuteur = rd.Texte(19), ReferenceCommandeClient = rd.Texte(20),
            DateCommandeClient = BaseDb2.Date(rd.Entier(21), rd.Entier(22), rd.Entier(23)),
            CompteGeneralFacture = Pos(rd.Entier(24)), CompteClientFacture = Pos(rd.Entier(25)), Revendeur = rd.Texte(26) == "O",
            Representant = Pos(rd.Entier(27)), TauxCommission = PosD(rd.Nombre(28)), Fabricant = rd.Texte(29),
            CodeVente = rd.Texte(30), Monnaie = rd.Texte(31), SousTraitee = rd.Texte(32) == "O",
            EnlevementOuLivraison = rd.Texte(33), Port = rd.Texte(34), NbLieux = Pos(rd.Entier(35)), AdresseFacturation = rd.Texte(36),
            ModeReglement = rd.Texte(37), Echeance = Pos(rd.Entier(38)), Le = Pos(rd.Entier(39)), Arrete = Pos(rd.Entier(40)),
            Escompte = PosD(rd.Nombre(41)), Releve = rd.Texte(42), NomHonoraires = rd.Texte(43),
            MontantHonoraires = PosD(rd.Nombre(44)), TauxHonoraires = PosD(rd.Nombre(45)), CommandesGroupees = Pos(rd.Entier(46)),
            TexteCommandesGroupees = rd.Texte(47), EditionEtiquettes = rd.Texte(48) == "O",
            Soldee = rd.Texte(49) != "", Bloquee = rd.Texte(50) == "O", AvecLignes = rd.Entier(51) > 0,
            // Blancs de début conservés (texte aligné par l'utilisateur), blancs et lignes vides de fin retirés
            TexteComplementaire = string.Join("\n", Enumerable.Range(52, 8)
                .Select(i => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.TrimEnd())).TrimEnd('\n'),
            TexteSurArc = rd.Texte(60) == "O", TexteSurBl = rd.Texte(61) == "O", TexteSurFacture = rd.Texte(62) == "O",
        };
    }

    // Mise à jour de l'en-tête si personne ne l'a modifié depuis sa lecture (empreinte identique) ; false sinon
    public async Task<bool> ModifierAsync(int societe, string profil, CleCommande cle, EnteteSaisie s,
        (int Annee, int Semaine) semaine, Func<EnteteSaisie, string> empreinte, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Verrou de la ligne pour la durée de la transaction, puis relecture et comparaison avec la lecture de l'écran
            await using (var cmd = Commande(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                societe, cle.Numero, cle.Cle))
                await cmd.ExecuteNonQueryAsync(ct);
            var actuel = await LireEnteteAsync(cnx, tx, b, societe, cle, null, ct);
            if (actuel is null || empreinte(actuel) != s.Empreinte)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            var colonnes = ColonnesEntete(s, semaine);
            await using (var cmd = Commande(cnx, tx,
                $"UPDATE {b}.AB001 SET {string.Join(", ", colonnes.Select(c => c.Colonne + " = ?"))} WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                [.. colonnes.Select(c => c.Valeur), societe, cle.Numero, cle.Cle]))
                await cmd.ExecuteNonQueryAsync(ct);

            // Changement de représentant : une ligne de journal NEG100E par ligne de commande (DOS505 R19)
            if (actuel.Representant != s.Representant)
            {
                var maintenant = DateTime.Now;
                await using var cmd = Commande(cnx, tx, $"""
                    INSERT INTO {b}.NEG100E (RSOC, RDOSS, DNDCLE, DNNLIG, ANCART, ANCND, ANCQTE, ANCPUS, ANCUV, ANCPXC,
                        DNART, DNIND, DNQTEC, DNPUS, DNUV, DNPXCE, DEDTMO, DEDTHE, DETYMO, DETRAI, DEPROF, ANCREP, DNREP,
                        ANCTYC, DNTYPC)
                    SELECT RSOC, RDOSS, DNDCLE, DNNLIG, DNART, DNIND, DNQTEC, DNPUS, DNUV, DNPXCE,
                        DNART, DNIND, DNQTEC, DNPUS, DNUV, DNPXCE, CAST(? AS NUMERIC(8, 0)), CAST(? AS NUMERIC(6, 0)), 'M', 'N',
                        CAST(? AS CHAR(10)), CAST(? AS NUMERIC(3, 0)), CAST(? AS NUMERIC(3, 0)), CAST(? AS CHAR(1)), CAST(? AS CHAR(1))
                    FROM {b}.NEG100 WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ?
                    """, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)), maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second,
                    profil.ToUpperInvariant(), actuel.Representant ?? 0, s.Representant ?? 0, actuel.TypeCommande, s.TypeCommande,
                    societe, cle.Numero, cle.Cle);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var texteChange = await TexteComplementaireAsync(cnx, tx, b, societe, cle.Numero, cle.Cle, s, actuel, ct);
            // Historique d'un dossier antérieur au jour : une seule ligne par enregistrement, la clé de DOS040 (dossier, date,
            // heure à la seconde) étant unique. EN (DOS505) si l'en-tête change, TT (DOS560) si seul le texte change.
            if (s.DateDossier < DateOnly.FromDateTime(DateTime.Today))
            {
                var enteteChange = !ColonnesEntete(actuel, semaine).Select(c => Convert.ToString(c.Valeur))
                    .SequenceEqual(colonnes.Select(c => Convert.ToString(c.Valeur)));
                if (enteteChange || !texteChange)
                    await HistoriqueAsync(cnx, tx, b, societe, cle.Numero, cle.Cle, s, "EN", ct);
                else
                    await HistoriqueAsync(cnx, tx, b, societe, cle.Numero, cle.Cle, s, "TT", ct);
            }
            await tx.CommitAsync(ct);
            await AuditAsync(cnx, b, societe, profil, cle.Numero, cle.Cle, "MO");
            return true;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------
    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static Task<DbDataReader> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs) =>
        Lire(cnx, null, sql, ct, valeurs);

    // La commande reste ouverte tant que le lecteur est lu ; elle est libérée avec la connexion
    private static async Task<DbDataReader> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct,
        params object?[] valeurs) =>
        await Commande(cnx, tx, sql, valeurs).ExecuteReaderAsync(ct);
}
