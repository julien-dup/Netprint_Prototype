using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

internal sealed partial class OprDb2
{
    // Verrou applicatif d'un OPR (PRTLCK « OPR », société 4 + n° 6, comme FEN_GestionOPR), posé le temps de l'écriture
    internal static string CleVerrou(int societe, int numero) => $"{societe:0000}{numero:000000}";

    private Task PoserVerrouAsync(OdbcConnection cnx, int societe, int numero, string profil, CancellationToken ct) =>
        VerrousApplicatifs.PoserAsync(cnx, Bib, "OPR", CleVerrou(societe, numero), profil, $"L'OPR {numero} est en cours de modification par", ct);

    private Task LeverVerrouAsync(OdbcConnection cnx, int societe, int numero, string profil) =>
        VerrousApplicatifs.LeverAsync(cnx, Bib, "OPR", CleVerrou(societe, numero), profil);

    // Exécute une écriture sous verrou de l'OPR (s'il existe déjà) et en une transaction
    private async Task<T> EcrireAsync<T>(int societe, string profil, int numeroVerrouille, Func<OdbcConnection, OdbcTransaction, Task<T>> ecriture,
        CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        if (numeroVerrouille > 0) await PoserVerrouAsync(cnx, societe, numeroVerrouille, profil, ct);
        try
        {
            await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var valide = false;
            try
            {
                var r = await ecriture(cnx, tx);
                await tx.CommitAsync(ct);
                valide = true;
                return r;
            }
            finally
            {
                if (!valide) await tx.RollbackAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (numeroVerrouille > 0) await LeverVerrouAsync(cnx, societe, numeroVerrouille, profil);
        }
    }

    // ---------- Création (FEN_SaisieOPR.Validation) : groupes déjà formés par le service ; un OPR par groupe ----------
    // Les lignes de commande sont relues verrouillées : une ligne mise en préparation entre-temps par un autre poste est refusée.
    public Task<IReadOnlyList<int>> CreerAsync(int societe, string profil, IReadOnlyList<IReadOnlyList<LigneSaisieOpr>> groupes,
        ParametresOpr parametres, CancellationToken ct) =>
        EcrireAsync<IReadOnlyList<int>>(societe, profil, 0, async (cnx, tx) =>
        {
            var lues = await VerifierCandidatesAsync(cnx, tx, societe, parametres, groupes.SelectMany(g => g).ToList(), ct);
            var numeros = new List<int>();
            var maintenant = DateTime.Now;
            foreach (var groupe in groupes)
            {
                var numero = await NumeroAsync(cnx, tx, societe, ct);
                await Executer(cnx, tx, """
                    INSERT INTO {b}.OPR010 (RSOC, OENUM, OEPROF, OEDATE, OEHEUR, OESTAT, OEATT, DO_STATUT, OEPROFPR)
                    VALUES (?, ?, ?, ?, ?, '3', 'N', 0, ' ')
                    """, ct, societe, numero, profil.ToUpperInvariant(), BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(maintenant)),
                    maintenant.Hour * 10000 + maintenant.Minute * 100 + maintenant.Second);
                var ligne = 0;
                foreach (var l in groupe) await InsererLigneAsync(cnx, tx, societe, numero, ++ligne, l, lues[(l.Dossier, l.Cle, l.LigneCommande)], ct);
                numeros.Add(numero);
            }
            return numeros;
        }, ct);

    // ---------- Ajout de lignes à un OPR (FEN_SaisieOPR.AjoutElement) : OPR non livré, n° de ligne à la suite ----------
    public Task AjouterAsync(int societe, string profil, int numero, IReadOnlyList<LigneSaisieOpr> lignes, ParametresOpr parametres,
        CancellationToken ct) =>
        EcrireAsync(societe, profil, numero, async (cnx, tx) =>
        {
            await using (var rd = await Lire(cnx, tx, "SELECT OESTAT FROM {b}.OPR010 WHERE RSOC = ? AND OENUM = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                             ct, societe, numero))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"L'OPR {numero} n'existe plus.");
                if (rd.Texte(0) == "4") throw new RegleMetierException($"L'OPR {numero} est livré : on ne peut plus y ajouter de lignes.");
            }
            var lues = await VerifierCandidatesAsync(cnx, tx, societe, parametres, lignes, ct);
            int derniere;
            await using (var rd = await Lire(cnx, tx, "SELECT COALESCE(MAX(ODNLIG), 0) FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ?", ct, societe, numero))
            {
                await rd.ReadAsync(ct);
                derniere = rd.Entier(0);
            }
            if (derniere + lignes.Count > 999) throw new RegleMetierException("Un OPR compte 999 lignes au plus.");
            foreach (var l in lignes) await InsererLigneAsync(cnx, tx, societe, numero, ++derniere, l, lues[(l.Dossier, l.Cle, l.LigneCommande)], ct);
            return true;
        }, ct);

    // Lignes de commande verrouillées d'abord (mise à jour sans effet, dans un ordre fixe) : une création simultanée sur la même
    // ligne attend la fin de celle-ci et voit alors son OPR. Rend les lignes relues (article, indice).
    private async Task<Dictionary<(int, string, int), LigneCandidateOpr>> VerifierCandidatesAsync(OdbcConnection cnx, OdbcTransaction tx,
        int societe, ParametresOpr parametres, IReadOnlyList<LigneSaisieOpr> lignes, CancellationToken ct)
    {
        var cles = lignes.Select(l => (l.Dossier, l.Cle, l.LigneCommande)).Distinct().OrderBy(k => k.Dossier).ThenBy(k => k.Cle, StringComparer.Ordinal)
            .ThenBy(k => k.LigneCommande).ToList();
        foreach (var (dossier, cle, ligne) in cles)
            await Executer(cnx, tx, "UPDATE {b}.NEG100 SET DNNLIG = DNNLIG WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?", ct,
                societe, dossier, cle, ligne);
        var encore = (await CandidatesAsync(societe, new CriteresCandidatesOpr(), parametres, null, cles, cnx, tx, ct)).Lignes
            .ToDictionary(c => (c.Dossier, c.Cle, c.LigneCommande));
        var perdues = cles.Where(k => !encore.ContainsKey(k)).ToList();
        if (perdues.Count > 0)
            throw new RegleMetierException("Ces lignes ne sont plus à préparer (déjà dans un OPR, soldées ou modifiées entre-temps) : " +
                                           string.Join(", ", perdues.Select(k => $"{k.Dossier}/{k.Cle} ligne {k.LigneCommande}")) + ".");
        return encore;
    }

    // N° d'OPR : compteur AA00082 « NQ » incrémenté dans la transaction ; un n° déjà pris (compteur en retard), ou déjà cité par
    // des palettes (PROD_ENTITE.NUMERO_OPR, préparation d'un ancien OPR du même n°), est sauté
    private async Task<int> NumeroAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, CancellationToken ct)
    {
        for (var essai = 0; essai < 1000; essai++)
        {
            if (await Executer(cnx, tx, "UPDATE {b}.AA00082 SET TNUM = TNUM + 1 WHERE RSOC = ? AND RTABLE = 'NQ' AND RARG = ' ' AND TTAG <> '*'", ct, societe) == 0)
                throw new RegleMetierException("Pas de compteur des OPR (« NQ ») dans la table AA00082.");
            int numero;
            await using (var rd = await Lire(cnx, tx, "SELECT TNUM FROM {b}.AA00082 WHERE RSOC = ? AND RTABLE = 'NQ' AND RARG = ' ' AND TTAG <> '*'", ct, societe))
            {
                await rd.ReadAsync(ct);
                numero = (int)rd.Nombre(0);
            }
            if (numero > 999999) throw new RegleMetierException("Le compteur des OPR a atteint 999999.");
            await using var existe = await Lire(cnx, tx, """
                SELECT 1 FROM {b}.OPR010 WHERE RSOC = ? AND OENUM = ?
                UNION ALL SELECT 1 FROM {b}.PROD_ENTITE WHERE SOCIETE = ? AND NUMERO_OPR = ?
                """, ct, societe, numero, societe, numero);
            if (!await existe.ReadAsync(ct)) return numero;
        }
        throw new RegleMetierException("Aucun n° d'OPR libre trouvé : vérifiez le compteur des OPR.");
    }

    private async Task InsererLigneAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, int ligne, LigneSaisieOpr l,
        LigneCandidateOpr lue, CancellationToken ct)
    {
        var (article, indice) = (lue.Article, lue.Indice);
        await Executer(cnx, tx, """
            INSERT INTO {b}.OPR015 (RSOC, ODNUM, ODNLIG, ODNCDE, ODNCLE, ODDLIG, ODART, ODIND, ODQTEA, ODDTLV, ODIMPE, ODMECO, ODENLE, ODRETU,
                ODNGRP, ODSLDA, ODTRS, ODLIVR, ODSTAT, ODDTEN, ODHEEN, ODDESI, ODEMPL, ODBL, ODQTEX, ODQPREP, ODACOMP, ODEDIT)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'N', ?, 0, ?, ?, 'N', '3', 0, 0, ' ', ' ', 0, 0, 0, ?, 'N')
            """, ct, societe, numero, ligne, l.Dossier, l.Cle, l.LigneCommande, article, indice, l.Quantite, BaseDb2.AAAAMMJJ(l.DatePrevue),
            On(l.Imperatif), On(l.Mecompte), On(l.Prelevement), On(l.ASolder), l.Transporteur, On(l.Acompte));
        await EcrireObservationAsync(cnx, tx, societe, numero, ligne, l.Observation, ct);
    }

    // OPR016 : observation découpée en 10 zones de 40 caractères ; supprimée si vide
    private async Task EcrireObservationAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, int ligne, string observation,
        CancellationToken ct)
    {
        await Executer(cnx, tx, "DELETE FROM {b}.OPR016 WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?", ct, societe, numero, ligne);
        var zones = ZonesObservation(observation);
        if (zones.All(z => z == "")) return;
        await Executer(cnx, tx, """
            INSERT INTO {b}.OPR016 (RSOC, ODNUM, ODNLIG, OTTXT1, OTTXT2, OTTXT3, OTTXT4, OTTXT5, OTTXT6, OTTXT7, OTTXT8, OTTXT9, OTTX10)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, ct, [societe, numero, ligne, .. zones]);
    }

    internal static string[] ZonesObservation(string observation)
    {
        var texte = (observation ?? "").Trim();
        return [.. Enumerable.Range(0, 10).Select(i => texte.Length > i * 40 ? texte.Substring(i * 40, Math.Min(40, texte.Length - i * 40)) : "")];
    }

    // ---------- Modification (FEN_ModificationOPR.Validation) : lignes à préparer seulement, état relu sous verrou ----------
    public Task ModifierAsync(int societe, string profil, ModificationOpr m, CancellationToken ct) =>
        EcrireAsync(societe, profil, m.Numero, async (cnx, tx) =>
        {
            await using (var rd = await Lire(cnx, tx, "SELECT OESTAT FROM {b}.OPR010 WHERE RSOC = ? AND OENUM = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                             ct, societe, m.Numero))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"L'OPR {m.Numero} n'existe plus.");
                if (rd.Texte(0) == "4") throw new RegleMetierException($"L'OPR {m.Numero} est livré : il ne peut plus être modifié.");
            }
            foreach (var l in m.Lignes)
            {
                await VerifierLigneModifiableAsync(cnx, tx, societe, m.Numero, l.NumeroLigne, ct);
                if (l.Supprimer)
                {
                    await Executer(cnx, tx, "DELETE FROM {b}.OPR016 WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?", ct, societe, m.Numero, l.NumeroLigne);
                    await Executer(cnx, tx, "DELETE FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?", ct, societe, m.Numero, l.NumeroLigne);
                    continue;
                }
                await Executer(cnx, tx, """
                    UPDATE {b}.OPR015 SET ODQTEA = ?, ODDTLV = ?, ODIMPE = ?, ODMECO = ?, ODSLDA = ?, ODRETU = ?, ODTRS = ?, ODACOMP = ?
                    WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?
                    """, ct, l.Quantite, BaseDb2.AAAAMMJJ(l.DatePrevue), On(l.Imperatif), On(l.Mecompte), On(l.ASolder), On(l.Prelevement),
                    l.Transporteur, On(l.Acompte), societe, m.Numero, l.NumeroLigne);
                await EcrireObservationAsync(cnx, tx, societe, m.Numero, l.NumeroLigne, l.Observation, ct);
            }
            var textes = Enumerable.Range(0, 4).Select(i => Tronquer((m.Textes.ElementAtOrDefault(i) ?? "").Trim(), 40)).ToArray();
            await Executer(cnx, tx, "UPDATE {b}.OPR010 SET OETXT1 = ?, OETXT2 = ?, OETXT3 = ?, OETXT4 = ? WHERE RSOC = ? AND OENUM = ?", ct,
                textes[0], textes[1], textes[2], textes[3], societe, m.Numero);
            await LivreSiToutLivreAsync(cnx, tx, societe, m.Numero, ct);
            return true;
        }, ct);

    // Une ligne livrée ou en préparation (quantité préparée, palettes) n'est plus modifiable
    private async Task VerifierLigneModifiableAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, int ligne, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, tx, """
            SELECT l.ODLIVR, l.ODQPREP, (SELECT COUNT(*) FROM {b}.PROD_ENTITE p WHERE p.SOCIETE = l.RSOC AND p.NUMERO_OPR = l.ODNUM
                                         AND p.NUMERO_LIGNE_OPR = l.ODNLIG)
            FROM {b}.OPR015 l WHERE l.RSOC = ? AND l.ODNUM = ? AND l.ODNLIG = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS
            """, ct, societe, numero, ligne);
        if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"La ligne {ligne} de l'OPR {numero} n'existe plus.");
        if (rd.Texte(0) == "O") throw new RegleMetierException($"La ligne {ligne} de l'OPR {numero} est livrée : elle ne peut plus être modifiée.");
        if (rd.Entier(1) > 0 || rd.Entier(2) > 0)
            throw new RegleMetierException($"La ligne {ligne} de l'OPR {numero} est en préparation : elle ne peut plus être modifiée.");
    }

    // OPR livré (OESTAT 4) quand toutes ses lignes restantes le sont (FEN_ModificationOPR)
    private async Task LivreSiToutLivreAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, CancellationToken ct) =>
        await Executer(cnx, tx, """
            UPDATE {b}.OPR010 o SET OESTAT = '4' WHERE o.RSOC = ? AND o.OENUM = ? AND o.OESTAT <> '4'
              AND EXISTS (SELECT 1 FROM {b}.OPR015 l WHERE l.RSOC = o.RSOC AND l.ODNUM = o.OENUM)
              AND NOT EXISTS (SELECT 1 FROM {b}.OPR015 l WHERE l.RSOC = o.RSOC AND l.ODNUM = o.OENUM AND l.ODLIVR <> 'O')
            """, ct, societe, numero);

    // ---------- Suppression d'un OPR ou d'un élément (une commande de l'OPR) : aucune ligne livrée ni en préparation ----------
    // Rend true si l'OPR a disparu (plus aucune ligne)
    public Task<bool> SupprimerAsync(int societe, string profil, int numero, int? dossier, string? cle, CancellationToken ct) =>
        EcrireAsync(societe, profil, numero, async (cnx, tx) =>
        {
            var lignes = new List<int>();
            await using (var rd = await Lire(cnx, tx, """
                SELECT l.ODNLIG FROM {b}.OPR015 l WHERE l.RSOC = ? AND l.ODNUM = ? AND (CAST(? AS INTEGER) = 0 OR (l.ODNCDE = ? AND l.ODNCLE = ?))
                """, ct, societe, numero, dossier ?? 0, dossier ?? 0, cle ?? " "))
                while (await rd.ReadAsync(ct)) lignes.Add(rd.Entier(0));
            if (lignes.Count == 0)
                throw new RegleMetierException(dossier is null ? $"L'OPR {numero} n'existe plus." : $"La commande {dossier}/{cle} n'est plus dans l'OPR {numero}.");
            foreach (var ligne in lignes) await VerifierLigneModifiableAsync(cnx, tx, societe, numero, ligne, ct);
            foreach (var ligne in lignes)
            {
                await Executer(cnx, tx, "DELETE FROM {b}.OPR016 WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?", ct, societe, numero, ligne);
                await Executer(cnx, tx, "DELETE FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ? AND ODNLIG = ?", ct, societe, numero, ligne);
            }
            await using (var rd = await Lire(cnx, tx, "SELECT COUNT(*) FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ?", ct, societe, numero))
            {
                await rd.ReadAsync(ct);
                if (rd.Entier(0) > 0) return false;
            }
            await Executer(cnx, tx, "DELETE FROM {b}.OPR010 WHERE RSOC = ? AND OENUM = ?", ct, societe, numero);
            return true;
        }, ct);

    private static string On(bool valeur) => valeur ? "O" : "N";
    private static string Tronquer(string v, int n) => v.Length <= n ? v : v[..n];
}
