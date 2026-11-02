using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Élément de BL écarté de la facturation automatique et pourquoi (FAC401 : liste FAC401PR, et fréquence, que le RPG taisait)
internal sealed record ElementEcarte(int Bl, int Element, int Dossier, string Cle, int CompteGeneral, int CompteClient, string Nom, string Raison);

// =====================================================================
// Lecture de la facturation automatique : contexte de lancement (FAC400), éléments de BL à facturer (sélection de FAC400CLP,
// choix de FAC401) avec leurs lignes (EXP015 + NEG100), éléments en plus (EXP011), compléments des commandes (AB004), et
// paramètres de la société (FAC020, unités UV). L'écriture est dans FacturationAutoDb2.Ecriture.cs.
// =====================================================================
internal sealed partial class FacturationAutoDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    private Task<Lecteur> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] v) =>
        Lecteur.ExecuterAsync(cnx, tx, sql.Replace("{b}", Bib), ct, v);

    public async Task<ContexteLancement> ContexteAsync(int societe, string enseigne, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        bool gere = false;
        var periodes = new List<(int, int)>();
        await using (var rd = await Lire(cnx, null, "SELECT SOP03, SPED1, SPEF1, SPED2, SPEF2 FROM {b}.AA0007 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct))
            {
                gere = rd.Texte(0) == "O";
                periodes.Add((rd.Entier(1), rd.Entier(2)));
                periodes.Add((rd.Entier(3), rd.Entier(4)));
            }
        int? type = null;
        await using (var rd = await Lire(cnx, null, "SELECT PTYPEN FROM {b}.FAC010 WHERE RSOC = ?", ct, societe))
            if (await rd.ReadAsync(ct)) type = rd.Entier(0);
        var existe = false;
        if (enseigne.Trim() != "")
            await using (var rd = await Lire(cnx, null, "SELECT 1 FROM {b}.PRT320F WHERE RSOC = ? AND RENSEI = ?", ct, societe,
                             enseigne.Trim().ToUpperInvariant()))
                existe = await rd.ReadAsync(ct);
        return new ContexteLancement(gere, existe, periodes, type);
    }

    public async Task<ParametresGeneration> ParametresAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var types = new Dictionary<string, (string, string)>();
        await using (var rd = await Lire(cnx, null, "SELECT RLIGNE, LTYPE, LLIB FROM {b}.FAC020 WHERE RSOC = ?", ct, societe))
            while (await rd.ReadAsync(ct)) types[rd.Texte(0)] = (rd.Texte(1), rd.Texte(2));
        var diviseurs = new Dictionary<string, decimal>();
        await using (var rd = await Lire(cnx, null, "SELECT RARG, TNUM FROM {b}.AA00083 WHERE RSOC = ? AND RTABLE = 'UV'", ct, societe))
            while (await rd.ReadAsync(ct)) diviseurs.TryAdd(rd.Texte(0), rd.Nombre(1));
        return new ParametresGeneration(types, diviseurs);
    }

    // ---------- Sélection (FAC400CLP) : éléments de BL complets ou de retour, facturables, pas encore facturés ----------
    private static string Filtre(CriteresFacturationAuto c, List<object?> v, int societe)
    {
        var f = "e.RSOC = ? AND e.HETABL IN ('C', 'R') AND e.HFACTU = 'O' AND e.HNOFAC = 0";
        v.Add(societe);
        if (c.Enseigne != "") { f += " AND b.HENS = ?"; v.Add(c.Enseigne); }
        if (c.BlDu is { } bd) { f += " AND e.HNOBL BETWEEN ? AND ?"; v.Add(bd); v.Add(c.BlAu ?? ReglesFacturationAuto.BlMaxi); }
        if (c.ExpedieDu is not null || c.ExpedieAu is not null)
        {
            f += " AND b.HDEXAA * 10000 + b.HDEXMM * 100 + b.HDEXJJ BETWEEN ? AND ?";
            v.Add(c.ExpedieDu is { } du ? BaseDb2.AAAAMMJJ(du) : 0);
            v.Add(c.ExpedieAu is { } au ? BaseDb2.AAAAMMJJ(au) : 99999999);
        }
        f += " AND b.HGENE BETWEEN ? AND ? AND b.HCLIE BETWEEN ? AND ?";
        v.Add(c.CompteGeneralDu ?? 0); v.Add(c.CompteGeneralAu ?? ReglesFacturationAuto.CompteGeneralMaxi);
        v.Add(c.CompteClientDu ?? 0); v.Add(c.CompteClientAu ?? ReglesFacturationAuto.CompteMaxi);
        return f;
    }

    private const string Jointure = """
        {b}.EXP010 e JOIN {b}.EXP009 b ON b.RSOC = e.RSOC AND b.HNOBL = e.HNOBL
        """;

    public Task<(List<ElementBlAFacturer> Retenus, List<ElementEcarte> Ecartes, bool Tronque)> LireAsync(int societe,
        CriteresFacturationAuto c, int maxi, CancellationToken ct)
    {
        var v = new List<object?>();
        return LireAsync(societe, Filtre(c, v, societe), v, c, maxi, ct);
    }

    // Saisie manuelle (FAC085 / FAC150) : éléments de BL facturables et pas encore facturés d'un donneur d'ordre, d'une commande
    // ou de BL donnés, qu'ils soient ou non à prendre en facturation automatique
    public Task<(List<ElementBlAFacturer> Retenus, List<ElementEcarte> Ecartes, bool Tronque)> LireManuelAsync(int societe,
        (int General, int Client)? donneur, (int Numero, string Cle)? commande, IReadOnlyCollection<int>? bls, int maxi, CancellationToken ct)
    {
        var f = "e.RSOC = ? AND e.HETABL IN ('C', 'R') AND e.HFACTU = 'O' AND e.HNOFAC = 0";
        var v = new List<object?> { societe };
        if (donneur is { } d) { f += " AND b.HGENE = ? AND b.HCLIE = ?"; v.Add(d.General); v.Add(d.Client); }
        if (commande is { } c) { f += " AND e.HNODOS = ? AND e.HCLEF = ?"; v.Add(c.Numero); v.Add(c.Cle); }
        if (bls is { Count: > 0 }) { f += $" AND e.HNOBL IN ({string.Join(", ", bls.Select(_ => "?"))})"; v.AddRange(bls.Cast<object?>()); }
        return LireAsync(societe, f, v, null, maxi, ct);
    }

    // auto : critères de la facturation automatique (éléments à prendre en automatique, mode et fréquence du client contrôlés),
    // null en saisie manuelle
    private async Task<(List<ElementBlAFacturer> Retenus, List<ElementEcarte> Ecartes, bool Tronque)> LireAsync(int societe, string filtre,
        List<object?> v, CriteresFacturationAuto? auto, int maxi, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        // Garde-fou avant de lire les lignes : au-delà de maxi éléments de BL, rien n'est chargé
        await using (var rd = await Lire(cnx, null, $"SELECT COUNT(*) FROM {Jointure} WHERE {filtre}", ct, [.. v]))
            if (await rd.ReadAsync(ct) && rd.Entier(0) > maxi) return ([], [], true);

        // Compléments AB004, éléments en plus EXP011 et lignes EXP015 des éléments sélectionnés
        var complements = new Dictionary<(int, string), List<ComplementDossier>>();
        await using (var rd = await Lire(cnx, null, $"""
            SELECT RDOSS, F4DCLE, F4LIG, F4CL, F4QTE, F4DES, F4UT, F4REM, F4PU FROM {"{b}"}.AB004
            WHERE F4FACT <> 'O' AND (RSOC, RDOSS, F4DCLE) IN (SELECT e.RSOC, e.HNODOS, e.HCLEF FROM {Jointure} WHERE {filtre})
            ORDER BY RDOSS, F4DCLE, F4LIG
            """, ct, [.. v]))
            while (await rd.ReadAsync(ct))
                Liste(complements, (rd.Entier(0), rd.Texte(1))).Add(new ComplementDossier(rd.Entier(2), rd.Texte(3), rd.Nombre(4), rd.Texte(5),
                    rd.Texte(6), rd.Entier(7), rd.Nombre(8)));
        var enPlus = new Dictionary<(int, int), List<LigneEnPlus>>();
        await using (var rd = await Lire(cnx, null, $"""
            SELECT HNOBL, HNUM, HPLIG, HPCL, HPART, HPIND, HPQTE, HPDES, HPUT, HPPU, HPTXCO FROM {"{b}"}.EXP011
            WHERE HPFACT <> 'O' AND (RSOC, HNOBL, HNUM) IN (SELECT e.RSOC, e.HNOBL, e.HNUM FROM {Jointure} WHERE {filtre})
            ORDER BY HNOBL, HNUM, HPLIG
            """, ct, [.. v]))
            while (await rd.ReadAsync(ct))
                Liste(enPlus, (rd.Entier(0), rd.Entier(1))).Add(new LigneEnPlus(rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Nombre(6),
                    rd.Texte(7), rd.Texte(8), rd.Nombre(9), rd.Nombre(10)));
        var lignes = new Dictionary<(int, int), List<LigneBl>>();
        await using (var rd = await Lire(cnx, null, $"""
            SELECT l.DNBL, l.DNUM, l.DNLIG, l.DART, l.DCLIG, l.DQTE, l.DDES, l.DDES2, l.DDES3,
                   n.DNNLIG, n.DNCLIG, n.DNART, n.DNIND, n.DNPUS, n.DNUV, n.DNTXCO, n.DNOPT2, n.DNOPT5
            FROM {"{b}"}.EXP015 l
            JOIN {Jointure} ON e.RSOC = l.RSOC AND e.HNOBL = l.DNBL AND e.HNUM = l.DNUM
            LEFT OUTER JOIN {"{b}"}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = e.HNODOS AND n.DNDCLE = e.HCLEF AND n.DNNLIG = l.DNUMLC
            WHERE {filtre}
            ORDER BY l.DNBL, l.DNUM, l.DNLIG
            """, ct, [.. v]))
            while (await rd.ReadAsync(ct))
                Liste(lignes, (rd.Entier(0), rd.Entier(1))).Add(new LigneBl(rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Nombre(5), rd.Texte(6),
                    rd.Texte(7), rd.Texte(8), rd.Nul(9) ? null : new LigneCommandeNegoce(rd.Entier(9), rd.Texte(10), rd.Texte(11), rd.Texte(12),
                        rd.Nombre(13), rd.Texte(14), rd.Nombre(15), rd.Texte(16) == "O", rd.Texte(17) == "O")));

        // Éléments : commande, donneur d'ordre (mode), client des conditions (AB001FA absent ou FACRC = 1 : donneur d'ordre, sinon
        // client facturé du BL), client de la commande (code vente, relevé, présentation), adresse AB001FA sinon A500F n° FANAL
        var retenus = new List<ElementBlAFacturer>();
        var ecartes = new List<ElementEcarte>();
        var dossiers = new Dictionary<(int, string), DossierAFacturer>();
        var tronque = false;
        await using (var rd = await Lire(cnx, null, $"""
            SELECT e.HNOBL, e.HNUM, b.HGENE, b.HCLIE, e.HGENF, e.HCLIF, e.HETABL, e.HQTELI, e.HFACTO, e.HNODOS, e.HCLEF,
                   b.HFJ, b.HRS, b.HRUE1, b.HRUE2, b.HCP, b.HVILLE, COALESCE(o.CNOM, ''), COALESCE(o.CDOPT2, ''),
                   CASE WHEN a.RSOC IS NULL THEN 0 ELSE 1 END, COALESCE(k.CDOPT3, '$'), COALESCE(k.CMODE, ''), COALESCE(k.CNBJOU, 0),
                   COALESCE(k.CJOULE, 0), COALESCE(k.CESCOM, 0), COALESCE(k.CARRET, 0),
                   a.CCMOD, a.CCECH, a.CCLE, a.CCESC, a.CCART, a.CCREP, a.CCTYPC, a.CCTYPV, COALESCE(d.CDCVD, ''), a.CCUNIT, a.CCDES1,
                   a.CCDES2, a.CCPXVE, a.CTXRM, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCAT, a.CCSCAT, a.CCDEV, a.CCMODL, a.CCTXCO,
                   a.CCMON, COALESCE(x.FACPT, 0), COALESCE(x.FAATT, ''), COALESCE(x.FAORTE, ''), COALESCE(x.FAEDTB, ''),
                   COALESCE(x.FATPC, ''), COALESCE(x.FAFJ, ''), COALESCE(x.FANOM, ''), COALESCE(x.FARUE1, ''), COALESCE(x.FARUE2, ''),
                   COALESCE(x.FAPOST, ''), COALESCE(x.FAVILL, ''), COALESCE(ad.ALFJ, ''), COALESCE(ad.ALRS, ''), COALESCE(ad.ALRUE1, ''),
                   COALESCE(ad.ALRUE2, ''), COALESCE(ad.ALPOST, ''), COALESCE(ad.ALVILL, ''), COALESCE(ad.ALPAY, ''),
                   COALESCE(d.CRELVE, ''), COALESCE(d.CDOPT7, ''), b.HDEXAA, b.HDEXMM, b.HDEXJJ, b.HENS
            FROM {Jointure}
            LEFT OUTER JOIN {"{b}"}.AB001 a ON a.RSOC = e.RSOC AND a.RDOSS = e.HNODOS AND a.CCDCLE = e.HCLEF
            LEFT OUTER JOIN {"{b}"}.AB001FA x ON x.RSOC = e.RSOC AND x.RDOSS = e.HNODOS AND x.CCDCLE = e.HCLEF
            LEFT OUTER JOIN {"{b}"}.AA0003 o ON o.RSOC = e.RSOC AND o.RGEN = b.HGENE AND o.RCLI = b.HCLIE
            LEFT OUTER JOIN {"{b}"}.AA0003 k ON k.RSOC = e.RSOC
                 AND k.RGEN = CASE WHEN x.RSOC IS NULL OR x.FACRC = '1' THEN b.HGENE ELSE e.HGENF END
                 AND k.RCLI = CASE WHEN x.RSOC IS NULL OR x.FACRC = '1' THEN b.HCLIE ELSE e.HCLIF END
            LEFT OUTER JOIN {"{b}"}.AA0003 d ON d.RSOC = a.RSOC AND d.RGEN = a.RGEN AND d.RCLI = a.RCLI
            LEFT OUTER JOIN {"{b}"}.A500F ad ON ad.RSOC = a.RSOC AND ad.RGEN = a.RGEN AND ad.RCLI = a.RCLI AND ad.ALNLIG = x.FANAL AND x.FANAL <> 0
            WHERE {filtre}
            ORDER BY e.HNOBL, e.HNUM
            FETCH FIRST {maxi + 1} ROWS ONLY
            """, ct, [.. v]))
            while (await rd.ReadAsync(ct))
            {
                if (retenus.Count + ecartes.Count >= maxi) { tronque = true; break; }
                int bl = rd.Entier(0), num = rd.Entier(1), gen = rd.Entier(2), cli = rd.Entier(3), dossier = rd.Entier(9);
                string cle = rd.Texte(10), nom = rd.Texte(17);
                ElementEcarte Ecarte(string raison) => new(bl, num, dossier, cle, gen, cli, nom, raison);
                if (auto is not null && rd.Texte(8) != "O") { ecartes.Add(Ecarte("Pas à prendre en facturation automatique (élément de BL)")); continue; }
                if (rd.Entier(19) == 0) { ecartes.Add(Ecarte("Commande introuvable")); continue; }
                var mode = 6;
                if (auto is not null)
                {
                    if (!int.TryParse(rd.Texte(18), out mode) || mode is < 1 or > 8)
                    {
                        ecartes.Add(Ecarte("Client sans mode de facturation automatique (1 à 8)"));
                        continue;
                    }
                    var frequence = rd.Texte(20) == "$" ? null : rd.Texte(20);
                    if (frequence != auto.Frequence)
                    {
                        ecartes.Add(Ecarte(frequence is null ? "Client des conditions de règlement introuvable"
                            : $"Fréquence de facturation du client « {(frequence == "" ? "sans" : frequence)} », autre que celle demandée"));
                        continue;
                    }
                }
                // Conditions : celles de la commande si elle en porte (AB001.CCMOD), sinon celles du client des conditions
                var conditions = rd.Texte(26) != ""
                    ? new ConditionsReglement(rd.Texte(26), rd.Entier(27), rd.Entier(28), rd.Nombre(29), rd.Entier(30))
                    : new ConditionsReglement(rd.Texte(21), rd.Entier(22), rd.Entier(23), rd.Nombre(24), rd.Entier(25));
                if (!dossiers.TryGetValue((dossier, cle), out var d))
                {
                    var adresse = new AdresseEnvoi(rd.Texte(55), rd.Texte(56), rd.Texte(57), rd.Texte(58), rd.Texte(59), rd.Texte(60), "");
                    if (adresse.Vide)
                        adresse = new AdresseEnvoi(rd.Texte(61), rd.Texte(62), rd.Texte(63), rd.Texte(64), rd.Texte(65), rd.Texte(66), rd.Texte(67));
                    d = new DossierAFacturer(dossier, cle, rd.Texte(32), rd.Texte(33) != "" ? rd.Texte(33) : rd.Texte(34), rd.Texte(35),
                        rd.Texte(36), rd.Texte(37), rd.Nombre(38), rd.Nombre(39), rd.Texte(40), BaseDb2.Date(rd.Entier(41), rd.Entier(42), rd.Entier(43)),
                        rd.Texte(44), rd.Texte(45), rd.Entier(46), rd.Entier(47), rd.Nombre(48), rd.Texte(49), rd.Nombre(50), rd.Texte(51),
                        rd.Texte(52), rd.Texte(53), adresse, rd.Texte(54) is "" or "1", rd.Texte(68), rd.Texte(69),
                        complements.GetValueOrDefault((dossier, cle)) ?? []);
                    dossiers[(dossier, cle)] = d;
                }
                retenus.Add(new ElementBlAFacturer(bl, num, gen, cli, rd.Entier(4), rd.Entier(5), mode, conditions,
                    new AdresseEnvoi(rd.Texte(11), rd.Texte(12), rd.Texte(13), rd.Texte(14), rd.Texte(15), rd.Texte(16), ""),
                    rd.Entier(31), rd.Texte(6) == "R", rd.Nombre(7), d, lignes.GetValueOrDefault((bl, num)) ?? [],
                    enPlus.GetValueOrDefault((bl, num)) ?? [], nom, BaseDb2.Date(rd.Entier(70), rd.Entier(71), rd.Entier(72)), rd.Texte(73)));
            }
        return (retenus, ecartes, tronque);
    }

    private static List<T> Liste<TCle, T>(Dictionary<TCle, List<T>> d, TCle cle) where TCle : notnull
    {
        if (!d.TryGetValue(cle, out var l)) d[cle] = l = [];
        return l;
    }
}
