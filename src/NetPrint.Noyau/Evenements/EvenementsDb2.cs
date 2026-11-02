using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Socle.Donnees;

namespace NetPrint.Noyau.Evenements;

// =====================================================================
// EVE100 / EVE105 / DOS030 (EVE300). Écritures en transaction (tables journalisées), avec le compteur de commentaires
// du dossier (AB001.CCTCOM : événements « affichés dans le navigateur », EVNAVI = 'O').
// =====================================================================
internal sealed class EvenementsDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Indicateur « apparaît en … » de DOS030 selon le code de l'objet (tableau tea d'EVE300)
    private static string Indicateur(string code) => code switch
    {
        ObjetEvenements.CodeCommande => "TEACDC",
        ObjetEvenements.CodeLancement => "TEALAN",
        _ => throw new ArgumentException($"Code non pris en charge : {code}", nameof(code)),
    };

    // Sélection de la liste (curseur V1 d'EVE300) pour les codes repris
    private static (string Sql, object?[] Valeurs) Critere(ObjetEvenements o) => o.Code switch
    {
        ObjetEvenements.CodeCommande => ("""
            B.TEACDC = 'O' AND A.EVCDCL = 0 AND ((A.EVCDC = ? AND A.EVCDCC = ?) OR (A.EVDOSS = ? AND A.EVDCLE = ?))
            """, [o.Commande, o.CleCommande, o.Commande, o.CleCommande]),
        ObjetEvenements.CodeLancement => ("""
            B.TEALAN = 'O' AND A.EVDOSS = ? AND A.EVDCLE = ? AND A.EVART = ? AND A.EVIND = ?
            """, [o.Dossier, o.Cle, o.Article, o.Indice]),
        _ => throw new ArgumentException($"Code non pris en charge : {o.Code}", nameof(o)),
    };

    // Dossier dont le compteur de commentaires est tenu, d'après les liens de l'événement : le dossier (lancement), sinon la
    // commande client. Un événement de lancement, listé aussi avec la commande, garde ainsi toujours le même compteur.
    private static (int Numero, string Cle) DossierCompteur(int dossier, string cle, int commande, string cleCommande) =>
        dossier > 0 ? (dossier, cle) : (commande, cleCommande);

    public async Task<NetPrint.Socle.ResultatRecherche<Evenement>> ListerAsync(int societe, ObjetEvenements objet, CancellationToken ct)
    {
        var (critere, valeurs) = Critere(objet);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        var liste = new List<Evenement>();
        await using var rd = await Lire(cnx, null, $"""
            SELECT A.EVLIG, A.EVTYPE, B.TELIB, A.EVDATE, A.EVPROF, A.EVDATP, A.EVHREP, A.EVDTRA, A.EVHRRA,
                   A.EVTXT1, A.EVTXT2, A.EVTXT3, A.EVTXT4, A.EVAUTO, A.EVAGEN, A.EVNAVI
            FROM {Bib}.EVE100 A JOIN {Bib}.DOS030 B ON B.RSOC = A.RSOC AND B.TETYPE = A.EVTYPE
            WHERE A.RSOC = ? AND {critere}
            ORDER BY A.EVLIG DESC FETCH FIRST {NetPrint.Socle.LimitesRecherche.Maximum + 1} ROWS ONLY
            """, ct, [societe, .. valeurs]);
        while (await rd.ReadAsync(ct))
            liste.Add(new Evenement(Convert.ToInt64(rd.GetValue(0)), rd.Texte(1), rd.Texte(2), BaseDb2.DateAAAAMMJJ(rd.Entier(3)), rd.Texte(4),
                BaseDb2.DateAAAAMMJJ(rd.Entier(5)), ReglesEvenements.Heure(rd.Nombre(6)), BaseDb2.DateAAAAMMJJ(rd.Entier(7)),
                ReglesEvenements.Heure(rd.Nombre(8)), [rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Texte(12)], rd.Texte(13) == "O",
                rd.Texte(14) == "O", rd.Texte(15) == "O"));
        var tronque = liste.Count > NetPrint.Socle.LimitesRecherche.Maximum;
        return new(tronque ? liste[..NetPrint.Socle.LimitesRecherche.Maximum] : liste, tronque);
    }

    // Types de la société ; code d'objet donné : ceux qui « apparaissent » pour cet objet
    public async Task<IReadOnlyList<TypeEvenement>> TypesAsync(int societe, string? code, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        var liste = new List<TypeEvenement>();
        await using var rd = await Lire(cnx, null, $"""
            SELECT TETYPE, TELIB, TEGRAP, TEMRAP, TEHEUR, TEAGEN, TEAAUT, TEPRF1, TEPRF2, TEPRF3, TEPRF4, TEPRF5, TEEMFA, TEEMRP
            FROM {Bib}.DOS030 WHERE RSOC = ? {(code is null ? "" : $"AND {Indicateur(code)} = 'O'")} ORDER BY TETYPE
            """, ct, societe);
        while (await rd.ReadAsync(ct))
            liste.Add(new TypeEvenement(rd.Texte(0), rd.Texte(1), rd.Texte(2) == "O", rd.Texte(3), ReglesEvenements.Heure(rd.Nombre(4)),
                rd.Texte(5) == "O", rd.Texte(6) == "O",
                [.. Enumerable.Range(7, 5).Select(i => rd.Texte(i).ToUpperInvariant()).Where(p => p != "")], rd.Texte(12) == "O", rd.Texte(13) == "O"));
        return liste;
    }

    // Profils de l'agent technique (AI00F par les initiales CCINAT) et du représentant (B000F.IPROF) du dossier (SPEVT)
    public async Task<(string Fabricant, string Representant)> ProfilsDuDossierAsync(int societe, ObjetEvenements o, CancellationToken ct)
    {
        var (numero, cle) = o.Commande > 0 ? (o.Commande, o.CleCommande) : (o.Dossier, o.Cle);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var rd = await Lire(cnx, null, $"""
            SELECT COALESCE((SELECT MIN(RPRF) FROM {Bib}.AI00F WHERE ACINI = A.CCINAT AND A.CCINAT <> ' '), ''),
                   COALESCE((SELECT MIN(IPROF) FROM {Bib}.B000F WHERE RSOC = A.RSOC AND RREPR = A.CCREP AND A.CCREP <> 0), '')
            FROM {Bib}.AB001 A WHERE A.RSOC = ? AND A.RDOSS = ? AND A.CCDCLE = ?
            """, ct, societe, numero, cle);
        return await rd.ReadAsync(ct) ? (rd.Texte(0).ToUpperInvariant(), rd.Texte(1).ToUpperInvariant()) : ("", "");
    }

    public async Task<HashSet<string>> ProfilsExistantsAsync(IReadOnlyList<string> profils, CancellationToken ct)
    {
        var existants = new HashSet<string>();
        if (profils.Count == 0) return existants;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var rd = await Lire(cnx, null,
            $"SELECT RPRF FROM {Bib}.AI00F WHERE RPRF IN ({string.Join(", ", profils.Select(_ => "?"))})", ct, [.. profils]);
        while (await rd.ReadAsync(ct)) existants.Add(rd.Texte(0).ToUpperInvariant());
        return existants;
    }

    public async Task<SaisieEvenement?> LireAsync(int societe, ObjetEvenements objet, long numero, CancellationToken ct)
    {
        var (critere, valeurs) = Critere(objet);
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        SaisieEvenement s;
        await using (var rd = await Lire(cnx, null, $"""
            SELECT A.EVTYPE, B.TELIB, A.EVPROF, A.EVAUTO, A.EVDATP, A.EVHREP, A.EVTXT1, A.EVTXT2, A.EVTXT3, A.EVTXT4,
                   A.EVDTRA, A.EVHRRA, A.EVAGEN, A.EVNAVI
            FROM {Bib}.EVE100 A JOIN {Bib}.DOS030 B ON B.RSOC = A.RSOC AND B.TETYPE = A.EVTYPE
            WHERE A.RSOC = ? AND A.EVLIG = ? AND {critere}
            """, ct, [societe, numero, .. valeurs]))
        {
            if (!await rd.ReadAsync(ct)) return null;
            s = new SaisieEvenement
            {
                Numero = numero, Type = rd.Texte(0), LibelleType = rd.Texte(1), Createur = rd.Texte(2), Automatique = rd.Texte(3) == "O",
                DatePrevue = BaseDb2.DateAAAAMMJJ(rd.Entier(4)), HeurePrevue = ReglesEvenements.Heure(rd.Nombre(5)),
                Textes = [rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9)],
                DateRappel = BaseDb2.DateAAAAMMJJ(rd.Entier(10)), HeureRappel = ReglesEvenements.Heure(rd.Nombre(11)),
                Agenda = rd.Texte(12) == "O", Navigateur = rd.Texte(13) == "O",
            };
            // Rappel coché si l'événement en a un (EVE300 cochait selon le type et en ajoutait un à l'enregistrement)
            s.Rappel = s.DateRappel is not null;
        }
        await using (var rd = await Lire(cnx, null, $"SELECT ELPPRF FROM {Bib}.EVE105 WHERE EVLIG = ? ORDER BY ELPPRF", ct, numero))
            while (await rd.ReadAsync(ct)) s.Profils.Add(rd.Texte(0).ToUpperInvariant());
        return s;
    }

    public async Task<long> CreerAsync(int societe, ObjetEvenements o, SaisieEvenement s, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // N° sur toute la table, toutes sociétés (AttribLigne) ; clé unique : un doublon concurrent fait échouer l'insertion
        long numero;
        await using (var cmd = Commande(cnx, tx, $"SELECT COALESCE(MAX(EVLIG), 0) + 1 FROM {Bib}.EVE100", []))
            numero = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        await Executer(cnx, tx, $"""
            INSERT INTO {Bib}.EVE100 (EVLIG, RSOC, EVTYPE, EVDATE, EVTXT1, EVTXT2, EVTXT3, EVTXT4, EVPROF, EVDATP, EVHREP, EVAUTO,
                EVDOSS, EVDCLE, EVART, EVIND, EVMOD, EVCDC, EVCDCC, EVCDCL, EVCDS, EVCDSL, EVCDPA, EVLGPA, EVCDMA, EVLGMA,
                EVDTRA, EVHRRA, EVDTFN, EVHRFN, EVAGEN, EVNAVI)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'N', ?, ?, ?, ?, 0, ?, ?, 0, 0, 0, 0, 0, 0, 0, ?, ?, 0, 0, ?, ?)
            """, ct, numero, societe, s.Type, BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(DateTime.Today)),
            s.Textes[0], s.Textes[1], s.Textes[2], s.Textes[3], s.Createur, Date(s.DatePrevue), ReglesEvenements.Heure(s.HeurePrevue),
            o.Dossier, o.Cle, o.Article, o.Indice, o.Commande, o.CleCommande,
            Date(s.DateRappel), ReglesEvenements.Heure(s.HeureRappel), ON(s.Agenda), ON(s.Navigateur));
        // Profils prévenus, et le créateur s'il n'y est pas
        foreach (var p in s.Profils.Append(s.Createur).Distinct())
            await Executer(cnx, tx, $"INSERT INTO {Bib}.EVE105 (EVLIG, ELPPRF, ELPLRA, ELMAIE) VALUES (?, ?, 'N', 'N')", ct, numero, p);
        if (s.Navigateur) await CompteurAsync(cnx, tx, societe, DossierCompteur(o.Dossier, o.Cle, o.Commande, o.CleCommande), +1, ct);
        await tx.CommitAsync(ct);
        return numero;
    }

    public async Task ModifierAsync(int societe, ObjetEvenements o, long numero, SaisieEvenement s, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        int ancienRappel, ancienneHeure;
        bool ancienNavigateur;
        (int, string) compteur;
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT EVDTRA, EVHRRA, EVNAVI, EVDOSS, EVDCLE, EVCDC, EVCDCC FROM {Bib}.EVE100 WHERE RSOC = ? AND EVLIG = ? FOR UPDATE
            """, ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) throw new NetPrint.Socle.RegleMetierException(MessagesEvenements.EvenementInexistant);
            (ancienRappel, ancienneHeure, ancienNavigateur) = (rd.Entier(0), (int)(rd.Nombre(1) * 100), rd.Texte(2) == "O");
            compteur = DossierCompteur(rd.Entier(3), rd.Texte(4), rd.Entier(5), rd.Texte(6));
        }
        var anciens = new Dictionary<string, (string NePlusRappeler, string MailsEnvoyes)>();
        await using (var rd = await Lire(cnx, tx, $"SELECT ELPPRF, ELPLRA, ELMAIE FROM {Bib}.EVE105 WHERE EVLIG = ?", ct, numero))
            while (await rd.ReadAsync(ct)) anciens[rd.Texte(0).ToUpperInvariant()] = (rd.Texte(1), rd.Texte(2));

        await Executer(cnx, tx, $"""
            UPDATE {Bib}.EVE100 SET EVTXT1 = ?, EVTXT2 = ?, EVTXT3 = ?, EVTXT4 = ?, EVAGEN = ?, EVNAVI = ?, EVDTRA = ?, EVHRRA = ?,
                EVDATP = ?, EVHREP = ?
            WHERE RSOC = ? AND EVLIG = ?
            """, ct, s.Textes[0], s.Textes[1], s.Textes[2], s.Textes[3], ON(s.Agenda), ON(s.Navigateur), Date(s.DateRappel),
            ReglesEvenements.Heure(s.HeureRappel), Date(s.DatePrevue), ReglesEvenements.Heure(s.HeurePrevue), societe, numero);
        // Rappel déplacé : il est de nouveau dû à tous ; sinon chacun garde ses indicateurs
        var rappelChange = s.DateRappel is not null
            && (Date(s.DateRappel) != ancienRappel || (int)(ReglesEvenements.Heure(s.HeureRappel) * 100) != ancienneHeure);
        await Executer(cnx, tx, $"DELETE FROM {Bib}.EVE105 WHERE EVLIG = ?", ct, numero);
        foreach (var p in s.Profils)
        {
            var (plra, maie) = !rappelChange && anciens.TryGetValue(p, out var a) ? a : ("N", "N");
            await Executer(cnx, tx, $"INSERT INTO {Bib}.EVE105 (EVLIG, ELPPRF, ELPLRA, ELMAIE) VALUES (?, ?, ?, ?)", ct, numero, p, plra, maie);
        }
        if (s.Navigateur != ancienNavigateur) await CompteurAsync(cnx, tx, societe, compteur, s.Navigateur ? +1 : -1, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SupprimerAsync(int societe, ObjetEvenements o, long numero, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        bool navigateur;
        (int, string) compteur;
        await using (var rd = await Lire(cnx, tx, $"""
            SELECT EVNAVI, EVDOSS, EVDCLE, EVCDC, EVCDCC FROM {Bib}.EVE100 WHERE RSOC = ? AND EVLIG = ? AND EVAUTO <> 'O' FOR UPDATE
            """, ct, societe, numero))
        {
            if (!await rd.ReadAsync(ct)) throw new NetPrint.Socle.RegleMetierException(MessagesEvenements.EvenementInexistant);
            navigateur = rd.Texte(0) == "O";
            compteur = DossierCompteur(rd.Entier(1), rd.Texte(2), rd.Entier(3), rd.Texte(4));
        }
        await Executer(cnx, tx, $"DELETE FROM {Bib}.EVE105 WHERE EVLIG = ?", ct, numero);
        await Executer(cnx, tx, $"DELETE FROM {Bib}.EVE100 WHERE RSOC = ? AND EVLIG = ?", ct, societe, numero);
        if (navigateur) await CompteurAsync(cnx, tx, societe, compteur, -1, ct);
        await tx.CommitAsync(ct);
    }

    // AB001.CCTCOM (jamais négatif) ; un OF encore à ouvrir (AB001T seulement) n'a pas de compteur
    private async Task CompteurAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, (int Numero, string Cle) dossier, int sens,
        CancellationToken ct)
    {
        var (numero, cle) = dossier;
        await Executer(cnx, tx, $"""
            UPDATE {Bib}.AB001 SET CCTCOM = CASE WHEN CCTCOM + ? < 0 THEN 0 WHEN CCTCOM + ? > 9999 THEN 9999 ELSE CCTCOM + ? END
            WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
            """, ct, sens, sens, sens, societe, numero, cle);
    }

    private static int Date(DateOnly? d) => d is { } v ? BaseDb2.AAAAMMJJ(v) : 0;
    private static string ON(bool b) => b ? "O" : "N";

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static async Task Executer(OdbcConnection cnx, OdbcTransaction tx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var cmd = Commande(cnx, tx, sql, valeurs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, tx, sql, valeurs).ExecuteReaderAsync(ct);
}
