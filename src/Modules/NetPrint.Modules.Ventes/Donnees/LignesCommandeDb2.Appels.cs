using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Appels de livraison : effets sur la ligne d'origine (« à la demande »), report du prix (NEG520B), maxi livrable.
// L'appel lui-même est écrit par EnregistrerAsync / SupprimerAsync, comme toute ligne (LignesCommandeDb2.Saisie).
// =====================================================================
internal sealed partial class LignesCommandeDb2
{
    // Ligne d'origine recalculée depuis la quantité du contrat et la somme des appels relue en base (CalculAppels.Origine) ;
    // montant = reste à appeler × prix ; journal « M » si le reste change. Origine absente ou plus « à la demande » : rien
    // (comme NEG300NAV). Une origine soldée par l'utilisateur alors qu'il restait à appeler (DNQTEC > 0) le reste ; seul un
    // solde venu des appels (reste 0) est remis en cause.
    private static async Task RecalculerOrigineAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, string profil,
        CleCommande cle, ContexteLigne c, int origine, CancellationToken ct)
    {
        int contrat, livree, avant;
        decimal prixNet, prixFranc, cession;
        string unite, article, indice;
        bool soldeeAvant;
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT DNQTEO, DNQTEL, DNQTEC, DNPUS, DNPUSA, DNUV, DNART, DNIND, DNPXCE, DNSOLD FROM {b}.NEG100
            WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ? AND DNSTAT = '3'
            """, ct, societe, cle.Numero, cle.Cle, origine))
        {
            if (!await rd.ReadAsync(ct)) return;
            (contrat, livree, avant, prixNet, prixFranc, unite, article, indice, cession) = (rd.Entier(0), rd.Entier(1), rd.Entier(2),
                rd.Nombre(3), rd.Nombre(4), rd.Texte(5), rd.Texte(6), rd.Texte(7), rd.Nombre(8));
            soldeeAvant = rd.Texte(9) == "O";
        }
        var appelee = 0;
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT COALESCE(SUM(DNQTEC), 0) FROM {b}.NEG100
            WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNSTAT = '1' AND DNLORI = ? AND DNNLIG <> ?
            """, ct, societe, cle.Numero, cle.Cle, origine, origine))
            if (await rd.ReadAsync(ct)) appelee = rd.Entier(0);
        var tnum = await TnumAsync(cnx, tx, b, societe, unite, ct);

        var (reste, resteALivrer, soldee) = CalculAppels.Origine(contrat, appelee, livree);
        soldee |= CalculAppels.SoldeeParLUtilisateur(soldeeAvant, avant);
        await Executer(cnx, tx, $"""
            UPDATE {b}.NEG100 SET DNQTEA = ?, DNQTEC = ?, DNQTER = ?, DNSOLD = ?, DNMTHT = ?, DNMTHA = ?
            WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
            """, ct, appelee, reste, resteALivrer, soldee ? "O" : " ", MoteurPrixCommande.Montant(prixNet, reste, tnum),
            MoteurPrixCommande.Montant(prixFranc, reste, tnum), societe, cle.Numero, cle.Cle, origine);
        if (reste != avant)
        {
            LigneSaisie Image(int q) => new() { Article = article, Indice = indice, Quantite = q, PrixNet = prixNet, Unite = unite, PrixCession = cession };
            await JournalAsync(cnx, tx, b, societe, cle, origine, "M", Image(avant), Image(reste), prixNet, c, profil, ct);
        }
    }

    // Appel créé sur une commande soldée « D » : elle est remise en cours (NEG300NAV, pour qu'elle reparte en préparation) ;
    // un autre code de solde la laisse fermée
    // Renvoie vrai si la commande a été remise en cours (audit « RA » après la validation, comme l'option RE)
    private static async Task<bool> ReactiverPourAppelAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, CleCommande cle,
        CancellationToken ct)
    {
        var n = await Executer(cnx, tx, $"""
            UPDATE {b}.AB001 SET CCTAGS = ' ', CCSLDA = 0, CCSLDM = 0, CCSLDJ = 0 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ? AND CCTAGS = 'D'
            """, ct, societe, cle.Numero, cle.Cle);
        await using var rd = await Lire(cnx, tx, $"SELECT CCTAGS FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
            societe, cle.Numero, cle.Cle);
        if (await rd.ReadAsync(ct) && rd.Texte(0) != "") throw new RegleMetierException("Option impossible, dossier soldé.");
        return n > 0;
    }

    private static async Task<long> TnumAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, string unite, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, tx, $"SELECT TNUM FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = 'UV' AND RARG = ? AND TTAG <> '*'",
            ct, societe, unite);
        return await rd.ReadAsync(ct) ? Convert.ToInt64(rd.GetValue(0)) : 0;
    }

    // ------------------------------------------------------------------
    // Report du prix de l'origine sur ses appels (NEG520B / SpModPrix), en une transaction
    // ÉCART : la remise de l'appel est réécrite avec celle de l'origine (NEG520B recalculait le prix net avec la remise de
    // l'origine mais laissait l'ancienne remise sur l'appel) ; les appels soldés ou déjà facturés ne sont pas touchés
    // (NEG520B les modifiait tous) — à valider.
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<int>> ReporterPrixAsync(int societe, string profil, CleCommande cle, ContexteLigne c, int origine,
        CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                societe, cle.Numero, cle.Cle);
            decimal brut, remise, net, netFranc, cession, cessionFranc, commission;
            string unite, gratuit, aDefinir;
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT DNPUNR, DNTXRE, DNPUS, DNPUSA, DNUV, DNPXCE, DNPXCA, DNTXCO, DNOPT2, DNOPT5 FROM {b}.NEG100
                WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ? AND DNSTAT = '3'
                """, ct, societe, cle.Numero, cle.Cle, origine))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La ligne {origine} n'est pas une ligne « à la demande ».");
                (brut, remise, net, netFranc, unite, cession, cessionFranc, commission, gratuit, aDefinir) = (rd.Nombre(0), rd.Nombre(1),
                    rd.Nombre(2), rd.Nombre(3), rd.Texte(4), rd.Nombre(5), rd.Nombre(6), rd.Nombre(7), OuiNon(rd.Texte(8)), OuiNon(rd.Texte(9)));
            }
            var tnum = await TnumAsync(cnx, tx, b, societe, unite, ct);

            var appels = new List<(int Ligne, int Quantite, LigneSaisie Avant, bool Different)>();
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT DNNLIG, DNQTEC, DNPUNR, DNTXRE, DNPUS, DNUV, DNPXCE, DNTXCO, DNOPT2, DNOPT5, DNART, DNIND FROM {b}.NEG100
                WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNSTAT = '1' AND DNLORI = ? AND DNNLIG <> ?
                  AND DNSOLD <> 'O' AND DNQTEF = 0
                ORDER BY DNNLIG
                """, ct, societe, cle.Numero, cle.Cle, origine, origine))
                while (await rd.ReadAsync(ct))
                {
                    var avant = new LigneSaisie
                    {
                        Article = rd.Texte(10), Indice = rd.Texte(11), Quantite = rd.Entier(1), PrixNet = rd.Nombre(4), Unite = rd.Texte(5),
                        PrixCession = rd.Nombre(6),
                    };
                    var different = rd.Nombre(2) != brut || rd.Nombre(3) != remise || rd.Nombre(4) != net || rd.Texte(5) != unite
                                    || rd.Nombre(6) != cession || rd.Nombre(7) != commission || OuiNon(rd.Texte(8)) != gratuit
                                    || OuiNon(rd.Texte(9)) != aDefinir;
                    appels.Add((rd.Entier(0), rd.Entier(1), avant, different));
                }

            var modifies = new List<int>();
            foreach (var (ligne, quantite, avant, different) in appels.Where(a => a.Different))
            {
                await Executer(cnx, tx, $"""
                    UPDATE {b}.NEG100 SET DNPUNR = ?, DNTXRE = ?, DNPUS = ?, DNPUSA = ?, DNUV = ?, DNPXCE = ?, DNPXCA = ?, DNTXCO = ?,
                        DNOPT2 = ?, DNOPT5 = ?, DNMTHT = ?, DNMTHA = ?
                    WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?
                    """, ct, brut, remise, net, netFranc, unite, cession, cessionFranc, commission, gratuit, aDefinir,
                    MoteurPrixCommande.Montant(net, quantite, tnum),
                    MoteurPrixCommande.Montant(netFranc, quantite, tnum), societe, cle.Numero, cle.Cle, ligne);
                if (avant.PrixNet != net || avant.Unite != unite || (avant.PrixCession ?? 0) != cession)
                {
                    var apres = new LigneSaisie
                    {
                        Article = avant.Article, Indice = avant.Indice, Quantite = quantite, Unite = unite, PrixCession = cession,
                    };
                    await JournalAsync(cnx, tx, b, societe, cle, ligne, "M", avant, apres, net, c, profil, ct);
                }
                modifies.Add(ligne);
            }
            if (modifies.Count > 0) await RecalculerEnTeteAsync(cnx, tx, b, societe, profil, cle, c, ct);
            await tx.CommitAsync(ct);
            validee = true;
            return modifies;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Options O/N de NEG100 : une zone vide (lignes écrites par le RPG ou le WinDev) vaut N
    private static string OuiNon(string v) => v == "O" ? "O" : "N";

    // Maxi livrable et quantité du contrat de l'origine, quantité livrée de ses appels
    public async Task<(int MaxiOrigine, int Contrat, int LivreAppels)> MaxiOrigineAsync(int societe, CleCommande cle, int origine,
        CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT o.DNQMAX, o.DNQTEO,
                   (SELECT COALESCE(SUM(a.DNQTEL), 0) FROM {b}.NEG100 a WHERE a.RSOC = o.RSOC AND a.RDOSS = o.RDOSS
                      AND a.DNDCLE = o.DNDCLE AND a.DNSTAT = '1' AND a.DNLORI = o.DNNLIG AND a.DNNLIG <> o.DNNLIG)
            FROM {b}.NEG100 o WHERE o.RSOC = ? AND o.RDOSS = ? AND o.DNDCLE = ? AND o.DNNLIG = ? AND o.DNSTAT = '3'
            """, ct, societe, cle.Numero, cle.Cle, origine);
        return await rd.ReadAsync(ct) ? (rd.Entier(0), rd.Entier(1), rd.Entier(2)) : (0, 0, 0);
    }

    // Appel créé par le tableau de bord EDI (lien EDI105 commande → appel)
    public async Task<bool> AppelEdiAsync(int societe, CleCommande cle, int ligne, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        try
        {
            await using var rd = await Lire(cnx, $"""
                SELECT 1 FROM {b}.EDI105 WHERE RSOC = ? AND LEDOSS = ? AND LEDCLE = ? AND LELIGC = ? FETCH FIRST 1 ROW ONLY
                """, ct, societe, cle.Numero, cle.Cle, ligne);
            return await rd.ReadAsync(ct);
        }
        catch (OdbcException ex) when (ex.Errors.Count > 0 && ex.Errors[0].SQLState is "42704")
        {
            return false;   // installation sans EDI
        }
    }
}
