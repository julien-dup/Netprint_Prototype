using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Production.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Production.Donnees;

// =====================================================================
// Demandes d'OF (« dossiers à ouvrir ») : en-tête AB001T (CCTYPC '2') et liens AB0020 article ↔ OF ↔ commande, écrits
// comme NEG515 SPVAL / NEG510 SPVALLANC, mais en une transaction (le RPG n'en avait pas).
// Numérotation (DOS005.POPT02 ≠ 'N', toutes les sociétés de NP11F) : n° de la commande ; POPT02 = 'N' : compteur
// GETDOSSIER (enseigne ou catégorie). Clé (Production:CleOf) :
//   - « Calculee » (défaut) : la lettre de GETCLE (calcul de GETDOSSIER / PRTCLEF, modulo 26 du n° et du client) ;
//   - « PremierChiffreLibre » : ce que fait le programme spécifique RAUCLEF, qui remplace ce calcul chez certains clients
//     (constaté sur les données du pilote : commande 68537/9 → OF 68537/2, alors que GETCLE donnerait « D »).
// Si la clé est prise (AB001 ou AB001T) : chiffres 1 à 9 puis 0 (DEBLEC de NEG515).
// Modèle : compteur NK (AA00082), lu et incrémenté dans la transaction (GETNUM sans appel RPG).
// =====================================================================
internal sealed class DemandesOfDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);
    private bool CleCalculee => !string.Equals(config["Production:CleOf"], "PremierChiffreLibre", StringComparison.OrdinalIgnoreCase);

    public async Task<NumeroOf> CreerAsync(int societe, string profil, DemandeOf d, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            // Verrou de la commande : deux demandes d'OF pour la même commande se numérotent l'une après l'autre
            await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                societe, d.Commande.Numero, d.Commande.Cle);

            var parCommande = true;
            await using (var rd = await Lire(cnx, tx, $"SELECT POPT02 FROM {b}.DOS005 WHERE RSOC = ?", ct, societe))
                if (await rd.ReadAsync(ct)) parCommande = rd.Texte(0) != "N";

            var (numero, cleGetDossier) = parCommande ? (d.Commande.Numero, (string?)null) : await GetDossierAsync(cnx, tx, b, societe, d, ct);
            string? Preferee(int n, string? cleCompteur) =>
                !CleCalculee ? null : cleCompteur is { Length: 1 } ? cleCompteur : Noyau.Compteurs.NumerosDossier.CleCalculee(n, d.CompteClient);
            var preferee = Preferee(numero, cleGetDossier);
            var (modele, indice) = d.Modele > 0 ? (d.Modele, d.IndiceComposition == "" ? "A" : d.IndiceComposition)
                : (await NouveauModeleAsync(cnx, tx, b, societe, ct), "A");

            // Clé libre puis INSERT ; AB001T a une clé unique : si une autre demande a pris la même clé entre-temps, on
            // reprend avec la suivante (une instruction refusée n'annule pas la transaction)
            NumeroOf? of = null;
            for (var essai = 0; of is null; essai++)
            {
                var cle = await CleLibreAsync(cnx, tx, b, societe, numero, preferee, ct);
                if (cle is null && parCommande && essai == 0)
                {
                    // Toutes les clés du n° de la commande sont prises : compteur (comme NEG515 l. 2804-2827)
                    (numero, cleGetDossier) = await GetDossierAsync(cnx, tx, b, societe, d, ct);
                    preferee = Preferee(numero, cleGetDossier);
                    cle = await CleLibreAsync(cnx, tx, b, societe, numero, preferee, ct);
                }
                if (cle is null) throw new RegleMetierException($"Toutes les clés du dossier n° {numero} sont déjà utilisées.");
                var colonnes = ColonnesEntete(d, profil);
                colonnes.InsertRange(0,
                [
                    ("RSOC", societe), ("RDOSS", numero), ("CCDCLE", cle), ("CCTYPC", "2"), ("CCOPT2", "D"),
                    ("CCAT", d.Categorie), ("CCSCAT", d.SousCategorie), ("CCMODL", modele), ("CCINDC", indice),
                    ("CCDATA", d.DateDossier.Year), ("CCDATM", d.DateDossier.Month), ("CCDATJ", d.DateDossier.Day),
                ]);
                try
                {
                    await Executer(cnx, tx,
                        $"INSERT INTO {b}.AB001T ({string.Join(", ", colonnes.Select(c => c.Colonne))}) VALUES ({string.Join(", ", colonnes.Select(_ => "?"))})",
                        ct, [.. colonnes.Select(c => c.Valeur)]);
                    of = new NumeroOf(numero, cle);
                }
                catch (OdbcException ex) when (essai < 3 && ex.Errors.Count > 0 && ex.Errors[0].SQLState == "23505")
                {
                }
            }
            await LiensAsync(cnx, tx, b, societe, of, d, ct);
            await tx.CommitAsync(ct);
            validee = true;
            return of;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Modification d'un OF à ouvrir : en-tête (sauf n°, catégorie, modèle) et liens de la commande ; les liens ne sont
    // jamais supprimés en modification (NEG515 SPVAL). Quantité lancée non saisie : inchangée (l'OF peut servir
    // d'autres commandes)
    public async Task ModifierAsync(int societe, string profil, NumeroOf of, DemandeOf d, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            await ExigerAOuvrirAsync(cnx, tx, b, societe, of, ct);
            var colonnes = ColonnesEntete(d, profil).Where(c => c.Colonne != "CCQTEC" || d.QuantiteLancee > 0).ToList();
            await Executer(cnx, tx,
                $"UPDATE {b}.AB001T SET {string.Join(", ", colonnes.Select(c => c.Colonne + " = ?"))} WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                ct, [.. colonnes.Select(c => c.Valeur), societe, of.Numero, of.Cle]);
            await LiensAsync(cnx, tx, b, societe, of, d, ct);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Rattachement d'un article d'une commande à un OF à ouvrir (AL) : lien créé, ou quantités remplacées s'il existe ;
    // date de fin de fabrication = date de mise à disposition de l'OF ; la quantité lancée suit l'écart d'à fabriquer
    public async Task AjouterAsync(int societe, NumeroOf of, CommandeOrigine commande, BesoinArticle besoin, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            await ExigerAOuvrirAsync(cnx, tx, b, societe, of, ct);
            var date = besoin.DateFinFabrication;
            await using (var rd = await Lire(cnx, tx, $"SELECT CCDTLA, CCDTLM, CCDTLJ FROM {b}.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                             ct, societe, of.Numero, of.Cle))
                if (await rd.ReadAsync(ct) && BaseDb2.Date(rd.Entier(0), rd.Entier(1), rd.Entier(2)) is { } mad) date = mad;
            var ancien = 0;
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT COALESCE(SUM(LAQTEC), 0) FROM {b}.AB0020
                WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LAART = ? AND LAIND = ? AND LADOSC = ? AND LACLDC = ?
                """, ct, societe, of.Numero, of.Cle, besoin.Article, besoin.Indice, commande.Numero, commande.Cle))
                if (await rd.ReadAsync(ct)) ancien = rd.Entier(0);
            var demande = new DemandeOf(commande, 0, 0, "", "", "", "", "", "", "", date, date, null, null, 0, 0, "", null, null, 0, "", "", "",
                false, [besoin with { DateFinFabrication = date }]);
            await LiensAsync(cnx, tx, b, societe, of, demande, ct);
            await Executer(cnx, tx, $"UPDATE {b}.AB001T SET CCQTEC = CCQTEC + ? WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                besoin.AFabriquer - ancien, societe, of.Numero, of.Cle);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    // Suppression d'un OF à ouvrir pour une commande : ses liens à cette commande, puis l'en-tête s'il ne reste aucun
    // lien. ÉCART : NEG515 supprimait les liens de toutes les commandes de l'OF (ajoutées par AL). Renvoie vrai si l'OF
    // lui-même a été supprimé (le suivi du BAG est alors à supprimer aussi).
    public async Task<bool> SupprimerAsync(int societe, NumeroOf of, CommandeOrigine commande, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            await ExigerAOuvrirAsync(cnx, tx, b, societe, of, ct);
            if (await Executer(cnx, tx, $"DELETE FROM {b}.AB0020 WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LADOSC = ? AND LACLDC = ?", ct,
                    societe, of.Numero, of.Cle, commande.Numero, commande.Cle) == 0)
                throw new RegleMetierException($"L'OF {of.Libelle} n'est pas lié à cette commande.");
            var supprime = await SupprimerEnteteSansLienAsync(cnx, tx, b, societe, of, ct);
            await tx.CommitAsync(ct);
            validee = true;
            return supprime;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    public async Task<EtatOf> EtatAsync(int societe, NumeroOf of, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await EtatAsync(cnx, null, Bib, societe, of, ct);
    }

    public async Task<DemandeOfLue?> LireAsync(int societe, NumeroOf of, CommandeOrigine commande, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        if (await EtatAsync(cnx, null, b, societe, of, ct) != EtatOf.AOuvrir) return null;
        DemandeOf? d = null;
        await using (var rd = await Lire(cnx, null, $"""
            SELECT RGEN, RCLI, CCENSE, CCREFC, CCNINT, CCAT, CCSCAT, CCDES1, CCDES2, CCDTLA, CCDTLM, CCDTLJ, CCDATA, CCDATM, CCDATJ,
                   CCDENA, CCDENM, CCDENJ, CCDMADC, CCQTEC, CCPXVE, CCUNIT, CCDEV, CCVERS, CCHYP, CCDOSP, CCCLEP, CCMODL, CCINDC,
                   CCNORM, CCCHEF, CCOP10
            FROM {b}.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
            """, ct, societe, of.Numero, of.Cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            d = new DemandeOf(commande, rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6),
                rd.Texte(7), rd.Texte(8),
                BaseDb2.Date(rd.Entier(9), rd.Entier(10), rd.Entier(11)) ?? DateOnly.MinValue,
                BaseDb2.Date(rd.Entier(12), rd.Entier(13), rd.Entier(14)) ?? DateOnly.MinValue,
                BaseDb2.Date(rd.Entier(15), rd.Entier(16), rd.Entier(17)), BaseDb2.DateAAAAMMJJ(rd.Entier(18)),
                rd.Entier(19), rd.Nombre(20), rd.Texte(21),
                rd.Entier(22) > 0 ? new DevisOrigine(rd.Entier(22), rd.Entier(23), rd.Entier(24)) : null,
                rd.Entier(25) > 0 ? new NumeroOf(rd.Entier(25), rd.Texte(26)) : null,
                rd.Entier(27), rd.Texte(28), rd.Texte(29), rd.Texte(30), rd.Texte(31) == "O", []);
        }
        var articles = new List<BesoinArticle>();
        await using (var rd = await Lire(cnx, null, $"""
            SELECT LAART, LAIND, LAQTEC, LAQTEA, LADTFF FROM {b}.AB0020
            WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LADOSC = ? AND LACLDC = ? ORDER BY LALIG
            """, ct, societe, of.Numero, of.Cle, commande.Numero, commande.Cle))
            while (await rd.ReadAsync(ct))
                articles.Add(new BesoinArticle(rd.Texte(0), rd.Texte(1), rd.Entier(2), rd.Entier(3),
                    BaseDb2.DateAAAAMMJJ(rd.Entier(4)) ?? d.DateMiseADisposition));
        return new DemandeOfLue(of, d with { Articles = articles });
    }

    // ------------------------------------------------------------------
    // Colonnes d'en-tête écrites en création et en modification (NEG515 SPVAL l. 2864-2921)
    internal static List<(string Colonne, object? Valeur)> ColonnesEntete(DemandeOf d, string profil) =>
    [
        ("RGEN", d.CompteGeneral), ("RCLI", d.CompteClient), ("CCENSE", d.Enseigne), ("CCREFC", d.ReferenceCommande),
        ("CCNINT", d.Interlocuteur), ("CCDES1", d.Designation1), ("CCDES2", d.Designation2),
        ("CCDTLA", d.DateMiseADisposition.Year), ("CCDTLM", d.DateMiseADisposition.Month), ("CCDTLJ", d.DateMiseADisposition.Day),
        ("CCDENA", d.DateEntreeCommande?.Year ?? 0), ("CCDENM", d.DateEntreeCommande?.Month ?? 0), ("CCDENJ", d.DateEntreeCommande?.Day ?? 0),
        ("CCDMADC", d.DateMadClient is { } m ? BaseDb2.AAAAMMJJ(m) : 0),
        ("CCQTEC", d.QuantiteLancee), ("CCPXVE", d.Prix), ("CCUNIT", d.Unite),
        ("CCDEV", d.Devis?.Numero ?? 0), ("CCVERS", d.Devis?.Version ?? 0), ("CCHYP", d.Devis?.Hypothese ?? 0),
        ("CCDOSP", d.DossierPrecedent?.Numero ?? 0), ("CCCLEP", d.DossierPrecedent?.Cle ?? ""),
        ("CCNORM", d.Norme), ("CCCHEF", d.CheminFaconnage), ("CCOP10", d.SousTraitee ? "O" : "N"), ("CCPROF", profil.ToUpperInvariant()),
    ];

    // Liens article ↔ OF ↔ commande : mis à jour s'ils existent, sinon créés au n° de ligne suivant ; une ligne sans
    // quantité à fabriquer ni à affecter est ignorée (NEG515 l. 2930-2934)
    private static async Task LiensAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, NumeroOf of, DemandeOf d,
        CancellationToken ct)
    {
        foreach (var a in d.Articles.Where(a => a.AFabriquer != 0 || a.AAffecter != 0))
        {
            var n = await Executer(cnx, tx, $"""
                UPDATE {b}.AB0020 SET LAQTEC = ?, LAQTEA = ?, LADTFF = ?
                WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ? AND LAART = ? AND LAIND = ? AND LADOSC = ? AND LACLDC = ?
                """, ct, a.AFabriquer, a.AAffecter, BaseDb2.AAAAMMJJ(a.DateFinFabrication), societe, of.Numero, of.Cle, a.Article, a.Indice,
                d.Commande.Numero, d.Commande.Cle);
            if (n > 0) continue;
            var ligne = 1;
            await using (var rd = await Lire(cnx, tx, $"SELECT COALESCE(MAX(LALIG), 0) + 1 FROM {b}.AB0020 WHERE RSOC = ? AND RDOSS = ? AND LADCLE = ?",
                             ct, societe, of.Numero, of.Cle))
                if (await rd.ReadAsync(ct)) ligne = rd.Entier(0);
            await Executer(cnx, tx, $"""
                INSERT INTO {b}.AB0020 (RSOC, RDOSS, LADCLE, LALIG, LAART, LAIND, LAQTEC, LAQTEF, LADOSC, LACLDC, LADTFF, LASOLD, LAQTEA, LATYPE)
                VALUES (?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ' ', ?, ' ')
                """, ct, societe, of.Numero, of.Cle, ligne, a.Article, a.Indice, a.AFabriquer, d.Commande.Numero, d.Commande.Cle,
                BaseDb2.AAAAMMJJ(a.DateFinFabrication), a.AAffecter);
        }
    }

    // Clé libre pour ce n° dans les dossiers ouverts et à ouvrir : celle de GETDOSSIER si elle est libre, sinon le premier
    // chiffre libre
    private static async Task<string?> CleLibreAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, int numero,
        string? preferee, CancellationToken ct)
    {
        var prises = new HashSet<string>();
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT CCDCLE FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ?
            UNION SELECT CCDCLE FROM {b}.AB001T WHERE RSOC = ? AND RDOSS = ?
            """, ct, societe, numero, societe, numero))
            while (await rd.ReadAsync(ct)) prises.Add(rd.Texte(0));
        return Noyau.Compteurs.NumerosDossier.PremiereCleLibre(prises, preferee);
    }

    // GETDOSSIER 'D' (compteur de l'enseigne ou de la catégorie) : « nnnnn c » ; -1 = échec (catégorie ou enseigne inconnue).
    // Comme pour la création des commandes, la fonction est appelée dans la bibliothèque des données (NP11F) ; le compteur
    // est mis à jour hors transaction (un n° peut se perdre si l'écriture échoue ensuite, comme dans le RPG).
    private static async Task<(int Numero, string? Cle)> GetDossierAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe,
        DemandeOf d, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, tx, $"""
            VALUES {b}.GETDOSSIER(CAST(? AS NUMERIC(4, 0)), CAST(? AS CHAR(1)), CAST(? AS CHAR(2)), CAST(? AS NUMERIC(6, 0)),
                                  CAST(? AS NUMERIC(7, 0)), 'D')
            """, ct, societe, d.Enseigne == "" ? " " : d.Enseigne, d.Categorie == "" ? "  " : d.Categorie, d.CompteGeneral, d.CompteClient);
        var retour = await rd.ReadAsync(ct) ? Convert.ToString(rd.GetValue(0)) : null;
        var (numero, cle) = Noyau.Compteurs.NumerosDossier.Decouper(retour)
            ?? throw new RegleMetierException("Aucun n° de dossier n'a pu être attribué (compteur de l'enseigne ou de la catégorie).");
        return (numero, cle);
    }

    // Compteur des modèles NK (AA00082, argument blanc), verrouillé jusqu'à la fin de la transaction
    private static async Task<int> NouveauModeleAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, CancellationToken ct)
    {
        int? actuel = null;
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT TNUM FROM {b}.AA00082 WHERE RSOC = ? AND RTABLE = 'NK' AND RARG = ' ' WITH RS USE AND KEEP EXCLUSIVE LOCKS
            """, ct, societe))
            if (await rd.ReadAsync(ct)) actuel = rd.Entier(0);
        var suivant = (actuel ?? 0) + 1;
        if (actuel is null)
            await Executer(cnx, tx, $"INSERT INTO {b}.AA00082 (RSOC, RTABLE, RARG, TNUM) VALUES (?, 'NK', ' ', ?)", ct, societe, suivant);
        else
            await Executer(cnx, tx, $"UPDATE {b}.AA00082 SET TNUM = ? WHERE RSOC = ? AND RTABLE = 'NK' AND RARG = ' '", ct, suivant, societe);
        return suivant;
    }

    private static async Task ExigerAOuvrirAsync(OdbcConnection cnx, OdbcTransaction tx, string b, int societe, NumeroOf of, CancellationToken ct)
    {
        switch (await EtatAsync(cnx, tx, b, societe, of, ct))
        {
            case EtatOf.Ouvert: throw new RegleMetierException("Modification impossible, dossier définitif : l'OF est déjà ouvert.");
            case EtatOf.Inconnu: throw new RegleMetierException($"L'OF {of.Libelle} n'existe plus.");
        }
    }

    private static async Task<EtatOf> EtatAsync(OdbcConnection cnx, OdbcTransaction? tx, string b, int societe, NumeroOf of,
        CancellationToken ct)
    {
        await using (var rd = await Lire(cnx, tx, $"SELECT 1 FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                         societe, of.Numero, of.Cle))
            if (await rd.ReadAsync(ct)) return EtatOf.Ouvert;
        await using (var rd = await Lire(cnx, tx, $"SELECT 1 FROM {b}.AB001T WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                         societe, of.Numero, of.Cle))
            if (await rd.ReadAsync(ct)) return EtatOf.AOuvrir;
        return EtatOf.Inconnu;
    }

    // OF à ouvrir sans plus aucun lien : en-têtes AB001T supprimés (une seule instruction par OF, sur une connexion) ;
    // rend ceux qui l'ont été (leur suivi du BAG est à supprimer aussi)
    public async Task<IReadOnlyList<NumeroOf>> SupprimerSansLienAsync(int societe, IReadOnlyList<NumeroOf> ofs, CancellationToken ct)
    {
        var supprimes = new List<NumeroOf>();
        await using var cnx = await OuvrirAsync(ct);
        foreach (var of in ofs)
            if (await SupprimerEnteteSansLienAsync(cnx, null, Bib, societe, of, ct)) supprimes.Add(of);
        return supprimes;
    }

    // Règle commune : l'en-tête d'un OF à ouvrir disparaît quand plus aucune commande n'y est liée ; jamais celui d'un OF ouvert
    private static async Task<bool> SupprimerEnteteSansLienAsync(OdbcConnection cnx, OdbcTransaction? tx, string b, int societe, NumeroOf of,
        CancellationToken ct) =>
        await Executer(cnx, tx, $"""
            DELETE FROM {b}.AB001T T WHERE T.RSOC = ? AND T.RDOSS = ? AND T.CCDCLE = ?
              AND NOT EXISTS (SELECT 1 FROM {b}.AB0020 L WHERE L.RSOC = T.RSOC AND L.RDOSS = T.RDOSS AND L.LADCLE = T.CCDCLE)
              AND NOT EXISTS (SELECT 1 FROM {b}.AB001 O WHERE O.RSOC = T.RSOC AND O.RDOSS = T.RDOSS AND O.CCDCLE = T.CCDCLE)
            """, ct, societe, of.Numero, of.Cle) > 0;

    // Suivi du BAG de l'OF supprimé (AB009, table non journalisée : après la validation)
    public async Task SupprimerBagAsync(int societe, NumeroOf of, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await Executer(cnx, null, $"DELETE FROM {Bib}.AB009 WHERE RSOC = ? AND RDOSS = ? AND F9DCLE = ?", ct, societe, of.Numero, of.Cle);
    }

    // ------------------------------------------------------------------
    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static async Task<int> Executer(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var cmd = Commande(cnx, tx, sql, valeurs);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, tx, sql, valeurs).ExecuteReaderAsync(ct);
}
