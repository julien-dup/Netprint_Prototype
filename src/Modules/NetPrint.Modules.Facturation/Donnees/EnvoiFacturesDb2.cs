using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Paramètres d'envoi du client facturé (PRT057F) : canal PFFTP (N ou blanc mail, O FTP, X plateforme), format PFFMT, destinataires
// PFDEST1-5, objet PFOBJM (variables &NOFAC, &NOM, &NBFAC), un envoi par facture PFOPT1, BL joints PFOPT2, envoi automatique PFAUTO
internal sealed record ParametresEnvoiClient(string Canal, string Format, IReadOnlyList<string> Destinataires, string Objet, bool UnParFacture,
    bool AvecBl, bool Automatique);

// =====================================================================
// Envoi des factures (EnvoideFactures) : paramètres d'envoi du client, factures à envoyer (éditées FDEDT = O, pas supprimées,
// pas exclues FDNONE, pas encore envoyées FDTAGE <> O sauf demande), marque d'envoi FAC080 (FDTAGE, FDDATEE, FDHEURE, FDMANAU,
// FDMETHE, FDTYPEE, comme MAJFAC080). Client facturé : FDGENF / FDCLIF, ou le donneur d'ordre s'il n'est pas renseigné.
// =====================================================================
internal sealed class EnvoiFacturesDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private Task<Lecteur> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] v) =>
        Lecteur.ExecuterAsync(cnx, sql.Replace("{b}", Bib), ct, v);

    // Nom du client (null s'il n'existe pas) et ses paramètres d'envoi (null s'il n'en a pas : mail, PDF, destinataires = contacts)
    public async Task<(string? Nom, ParametresEnvoiClient? Parametres)> ClientAsync(int societe, int general, int client, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        string? nom = null;
        await using (var rd = await Lire(cnx, "SELECT CNOM FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", ct, societe, general, client))
            if (await rd.ReadAsync(ct)) nom = rd.Texte(0).Trim();
        await using (var rd = await Lire(cnx, """
            SELECT PFFTP, PFFMT, PFDEST1, PFDEST2, PFDEST3, PFDEST4, PFDEST5, PFOBJM, PFOPT1, PFOPT2, PFAUTO
            FROM {b}.PRT057F WHERE RSOC = ? AND RGEN = ? AND RCLI = ?
            """, ct, societe, general, client))
            if (await rd.ReadAsync(ct))
                return (nom, new ParametresEnvoiClient(rd.Texte(0).Trim(), rd.Texte(1).Trim(),
                    [.. Enumerable.Range(2, 5).Select(i => rd.Texte(i).Trim()).Where(x => x != "")], rd.Texte(7).Trim(), rd.Texte(8) == "O",
                    rd.Texte(9) == "O", rd.Texte(10) == "O"));
        return (nom, null);
    }

    // Factures envoyables du client facturé sur la période (du / au facultatifs), ou celles demandées (cles : n°, date), au plus maxi
    // (Tronque au-delà) ; representant : représentant imposé au profil (ses factures seulement)
    public async Task<(List<FactureAEnvoyer> Factures, bool Tronque)> FacturesAsync(int societe, int general, int client, DateOnly? du, DateOnly? au,
        bool avecDejaEnvoyees, int maxi, CancellationToken ct, int? representant = null, IReadOnlyCollection<(int Numero, DateOnly Date)>? cles = null)
    {
        await using var cnx = await OuvrirAsync(ct);
        var factures = new List<FactureAEnvoyer>();
        var v = new List<object?> { societe, general, client, general, client };
        var filtre = "";
        if (representant is { } r) { filtre += " AND a.FDREP = ?"; v.Add(r); }
        if (cles is { Count: > 0 })
        {
            filtre += $" AND ({string.Join(" OR ", cles.Select(_ => "(a.RNFAC = ? AND a.FDANNE = ? AND a.FDMOIS = ? AND a.FDJOUR = ?)"))})";
            foreach (var (n, d) in cles) v.AddRange([n, d.Year, d.Month, d.Day]);
        }
        else
        {
            filtre += " AND a.FDANNE * 10000 + a.FDMOIS * 100 + a.FDJOUR BETWEEN ? AND ?";
            v.Add(du is { } d1 ? BaseDb2.AAAAMMJJ(d1) : 0);
            v.Add(au is { } d2 ? BaseDb2.AAAAMMJJ(d2) : 99999999);
        }
        await using var rd = await Lire(cnx, $"""
            SELECT a.RNFAC, a.FDANNE, a.FDMOIS, a.FDJOUR, a.FDTYPE, COALESCE(b.CNOM, ''), a.FDMTN, a.FDTAGE, a.FDNONE, a.FDDATEE, a.FDMANAU,
                   a.FDMETHE, a.FDTYPEE
            FROM {"{b}"}.FAC080 a
            LEFT OUTER JOIN {"{b}"}.AA0003 b ON b.RSOC = a.RSOC AND b.RGEN = a.FDGEND AND b.RCLI = a.FDCLID
            WHERE a.RSOC = ? AND ((a.FDGENF = ? AND a.FDCLIF = ?) OR (a.FDCLIF = 0 AND a.FDGEND = ? AND a.FDCLID = ?))
              AND a.FDEDT = 'O' AND a.FDEDIT <> 'S' AND a.FDNONE <> 'O' {(avecDejaEnvoyees ? "" : "AND a.FDTAGE <> 'O'")}{filtre}
            ORDER BY a.FDANNE, a.FDMOIS, a.FDJOUR, a.RNFAC
            FETCH FIRST {maxi + 1} ROWS ONLY
            """, ct, [.. v]);
        while (await rd.ReadAsync(ct))
        {
            if (factures.Count == maxi) return (factures, true);
            factures.Add(new FactureAEnvoyer(rd.Entier(0), new DateOnly(rd.Entier(1), rd.Entier(2), rd.Entier(3)), rd.Texte(4) == "A", rd.Texte(5).Trim(),
                rd.Nombre(6), ReglesFactures.Envoi(rd.Texte(7), rd.Texte(8), rd.Entier(9), rd.Texte(10), rd.Texte(11), rd.Texte(12))));
        }
        return (factures, false);
    }

    // Client facturé : FDGENF / FDCLIF, ou le donneur d'ordre s'il n'est pas renseigné (FAC150 R7)
    private const string GeneralFacture = "CASE WHEN a.FDCLIF = 0 THEN a.FDGEND ELSE a.FDGENF END";
    private const string ClientFacture = "CASE WHEN a.FDCLIF = 0 THEN a.FDCLID ELSE a.FDCLIF END";

    // Envoi automatique : factures à envoyer des clients « envoi automatique » (PRT057F.PFAUTO = O), triées par client facturé
    public async Task<(List<(int General, int Client, string Nom, ParametresEnvoiClient Parametres, FactureAEnvoyer Facture)> Factures, bool Tronque)>
        FacturesAutoAsync(int societe, CriteresEnvoiAuto c, int? representant, int maxi, CancellationToken ct)
    {
        var v = new List<object?> { societe };
        var filtre = "";
        if (c.Du is not null || c.Au is not null)
        {
            filtre += " AND a.FDANNE * 10000 + a.FDMOIS * 100 + a.FDJOUR BETWEEN ? AND ?";
            v.Add(c.Du is { } du ? BaseDb2.AAAAMMJJ(du) : 0);
            v.Add(c.Au is { } au ? BaseDb2.AAAAMMJJ(au) : 99999999);
        }
        if (c.NumeroDu is not null || c.NumeroAu is not null) { filtre += " AND a.RNFAC BETWEEN ? AND ?"; v.Add(c.NumeroDu ?? 0); v.Add(c.NumeroAu ?? 999999); }
        if (c.CompteGeneral is { } g && c.CompteClient is { } k) { filtre += $" AND {GeneralFacture} = ? AND {ClientFacture} = ?"; v.Add(g); v.Add(k); }
        if (representant is { } r) { filtre += " AND a.FDREP = ?"; v.Add(r); }
        await using var cnx = await OuvrirAsync(ct);
        var lignes = new List<(int, int, string, ParametresEnvoiClient, FactureAEnvoyer)>();
        await using var rd = await Lire(cnx, $"""
            SELECT {GeneralFacture}, {ClientFacture}, COALESCE(c.CNOM, ''), b.PFFTP, b.PFFMT, b.PFDEST1, b.PFDEST2, b.PFDEST3, b.PFDEST4, b.PFDEST5,
                   b.PFOBJM, b.PFOPT1, b.PFOPT2, a.RNFAC, a.FDANNE, a.FDMOIS, a.FDJOUR, a.FDTYPE, COALESCE(d.CNOM, ''), a.FDMTN, b.PFAUTO
            FROM {"{b}"}.FAC080 a
            JOIN {"{b}"}.PRT057F b ON b.RSOC = a.RSOC AND b.RGEN = {GeneralFacture} AND b.RCLI = {ClientFacture} AND b.PFAUTO = 'O'
            LEFT OUTER JOIN {"{b}"}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = {GeneralFacture} AND c.RCLI = {ClientFacture}
            LEFT OUTER JOIN {"{b}"}.AA0003 d ON d.RSOC = a.RSOC AND d.RGEN = a.FDGEND AND d.RCLI = a.FDCLID
            WHERE a.RSOC = ? AND a.FDEDT = 'O' AND a.FDEDIT <> 'S' AND a.FDNONE <> 'O' AND a.FDTAGE <> 'O'{filtre}
            ORDER BY 1, 2, a.FDANNE, a.FDMOIS, a.FDJOUR, a.RNFAC
            FETCH FIRST {maxi + 1} ROWS ONLY
            """, ct, [.. v]);
        while (await rd.ReadAsync(ct))
        {
            if (lignes.Count == maxi) return (lignes, true);
            var p = new ParametresEnvoiClient(rd.Texte(3).Trim(), rd.Texte(4).Trim(), [.. Enumerable.Range(5, 5).Select(i => rd.Texte(i).Trim()).Where(x => x != "")],
                rd.Texte(10).Trim(), rd.Texte(11) == "O", rd.Texte(12) == "O", rd.Texte(20) == "O");
            lignes.Add((rd.Entier(0), rd.Entier(1), rd.Texte(2).Trim(), p, new FactureAEnvoyer(rd.Entier(13),
                new DateOnly(rd.Entier(14), rd.Entier(15), rd.Entier(16)), rd.Texte(17) == "A", rd.Texte(18).Trim(), rd.Nombre(19), "")));
        }
        return (lignes, false);
    }

    // Marque d'envoi (MAJFAC080) : manuel M / automatique A, méthode M (mail), format (PDF…), date et heure de l'envoi
    // En une transaction : toutes les factures sont marquées, ou aucune (une facture introuvable annule tout)
    public async Task MarquerAsync(int societe, IEnumerable<(int Numero, DateOnly Date)> factures, bool manuel, string methode, string format,
        DateTime quand, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = cnx.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
        foreach (var (numero, date) in factures)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE {Bib}.FAC080 SET FDTAGE = 'O', FDDATEE = ?, FDHEURE = ?, FDMANAU = ?, FDMETHE = ?, FDTYPEE = ?
                WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
                """;
            foreach (var v in new object[] { BaseDb2.AAAAMMJJ(DateOnly.FromDateTime(quand)), quand.Hour * 10000 + quand.Minute * 100 + quand.Second,
                         manuel ? "M" : "A", methode, format, societe, numero, date.Year, date.Month, date.Day })
                cmd.Parameters.Valeur(v);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                throw new NetPrint.Socle.RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} n'existe plus : aucune facture n'a été marquée.");
        }
        tx.Commit();
    }
}
