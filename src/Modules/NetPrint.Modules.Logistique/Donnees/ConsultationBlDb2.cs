using System.Data.Common;
using System.Data.Odbc;
using System.Text;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// =====================================================================
// Lecture des bons de livraison : EXP009 (en-tête), EXP010 (éléments), EXP015 (lignes), EXP016 (lots), EXP012 (pied)
// =====================================================================
internal sealed class ConsultationBlDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    // Représentant imposé au profil (B000F.ILIMIT = 'O') : il ne voit que les BL de ses dossiers
    private async Task<int?> RepresentantImposeAsync(OdbcConnection cnx, int societe, string profil, CancellationToken ct)
    {
        await using var rd = await Lire(cnx,
            $"SELECT RREPR, ILIMIT FROM {Bib}.B000F WHERE RSOC = ? AND IPROF = ? AND ITAG <> '*' FETCH FIRST 1 ROW ONLY", ct,
            societe, profil.ToUpperInvariant());
        if (!await rd.ReadAsync(ct)) return null;
        return rd.Texte(1) == "O" ? rd.Entier(0) : null;
    }

    // Nom du représentant : B000F a une ligne par profil, d'où MIN sur les fiches actives
    private static string NomRepresentant(string b, string colonne) =>
        $"COALESCE((SELECT MIN(r.INOM) FROM {b}.B000F r WHERE r.RSOC = e.RSOC AND r.RREPR = {colonne} AND r.ITAG <> '*' AND {colonne} <> 0), '')";

    public async Task<ResultatRecherche<ElementBl>> RechercherAsync(int societe, string profil, CriteresBl c, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        var (filtre, valeurs) = Filtre(c, await RepresentantImposeAsync(cnx, societe, profil, ct));
        var maximum = LimitesRecherche.Maximum;
        await using var rd = await Lire(cnx, $"""
            SELECT e.HNOBL, e.HNUM, h.HDEXAA, h.HDEXMM, h.HDEXJJ, e.HETABL, e.HNODOS, e.HCLEF, e.HREFCD,
                   h.HGENE, h.HCLIE, COALESCE(c.CMODIR, ''), COALESCE(c.CNOM, h.HRS),
                   e.HGENF, e.HCLIF, COALESCE(f.CMODIR, ''),
                   COALESCE((SELECT SUM(l.DQTE) FROM {b}.EXP015 l WHERE l.RSOC = e.RSOC AND l.DNBL = e.HNOBL AND l.DNUM = e.HNUM), 0),
                   h.HNCOLT, h.HNPALT, h.HPDSTT, h.HCP, h.HVILLE, COALESCE(t.MNOM, ''),
                   e.HFACTU, e.HNOFAC, e.HDAFAA, e.HDAFAM, e.HDAFAJ, h.HPROF, e.HREP, {NomRepresentant(b, "e.HREP")}, h.HEDBL, e.HNOBLO
            FROM {b}.EXP010 e
            JOIN {b}.EXP009 h ON h.RSOC = e.RSOC AND h.HNOBL = e.HNOBL
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            LEFT OUTER JOIN {b}.AA0003 f ON f.RSOC = e.RSOC AND f.RGEN = e.HGENF AND f.RCLI = e.HCLIF
            LEFT OUTER JOIN {b}.B200F t ON t.RSOC = h.RSOC AND t.RTRANS = h.HTRAN
            WHERE e.RSOC = ? {filtre}
            ORDER BY e.HNOBL DESC, e.HNUM
            FETCH FIRST {maximum + 1} ROWS ONLY
            """, ct, [societe, .. valeurs]);
        var liste = new List<ElementBl>();
        while (await rd.ReadAsync(ct))
        {
            var (gen, cli, genF, cliF) = (rd.Entier(9), rd.Entier(10), rd.Entier(13), rd.Entier(14));
            var factureA = cliF > 0 && (genF, cliF) != (gen, cli) ? $"{genF} {cliF} {rd.Texte(15)}".Trim() : "";
            liste.Add(new ElementBl(rd.Entier(0), rd.Entier(1), BaseDb2.Date(rd.Entier(2), rd.Entier(3), rd.Entier(4)), rd.Texte(5),
                rd.Entier(6), rd.Texte(7), rd.Texte(8), gen, cli, rd.Texte(11), rd.Texte(12), factureA,
                rd.Entier(16), rd.Entier(17), rd.Entier(18), rd.Entier(19), rd.Texte(20), rd.Texte(21), rd.Texte(22),
                rd.Texte(23) == "O", rd.Entier(24), BaseDb2.Date(rd.Entier(25), rd.Entier(26), rd.Entier(27)),
                rd.Texte(28), rd.Entier(29), rd.Texte(30), rd.Texte(31) == "O", rd.Entier(32)));
        }
        var tronque = liste.Count > maximum;
        return new(tronque ? liste[..maximum] : liste, tronque);
    }

    // Conditions de la recherche (SQL paramétré) ; internal pour les tests
    internal static (string Sql, List<object?> Valeurs) Filtre(CriteresBl c, int? representant)
    {
        var sql = new StringBuilder();
        var valeurs = new List<object?>();
        void Et(string condition, params object?[] v) { sql.Append(" AND ").Append(condition); valeurs.AddRange(v); }

        if (c.NumeroBl is > 0) Et("e.HNOBL = ?", c.NumeroBl);
        if (c.NumeroCommande is > 0) Et("e.HNODOS = ?", c.NumeroCommande);
        if (c.CompteClient is > 0) Et("h.HCLIE = ?", c.CompteClient);
        if (c.CompteGeneral is > 0) Et("h.HGENE = ?", c.CompteGeneral);
        if (Texte(c.ReferenceCommande) is { } refc) Et("UPPER(e.HREFCD) LIKE ? ESCAPE '\\'", Commence(refc));
        if (c.Du is { } du) Et("h.HDEXAA * 10000 + h.HDEXMM * 100 + h.HDEXJJ >= ?", BaseDb2.AAAAMMJJ(du));
        if (c.Au is { } au) Et("h.HDEXAA * 10000 + h.HDEXMM * 100 + h.HDEXJJ <= ?", BaseDb2.AAAAMMJJ(au));
        if (Texte(c.Article) is { } art)
        {
            var (condition, valeur) = Joker("x.DART", art);
            Et($"EXISTS (SELECT 1 FROM {{b}}.EXP015 x WHERE x.RSOC = e.RSOC AND x.DNBL = e.HNOBL AND x.DNUM = e.HNUM AND {condition})", valeur);
        }
        if (c.NumeroFacture is > 0) Et("e.HNOFAC = ?", c.NumeroFacture);
        if (c.Transporteur is > 0) Et("h.HTRAN = ?", c.Transporteur);
        if (Texte(c.Ville) is { } ville) Et("UPPER(h.HVILLE) LIKE ? ESCAPE '\\'", Commence(ville));
        if (Texte(c.Profil) is { } profil) Et("h.HPROF = ?", profil);
        switch (c.Facturation)
        {
            case FiltreFacturation.NonFactures: Et("e.HNOFAC = 0"); break;
            case FiltreFacturation.Factures: Et("e.HNOFAC <> 0"); break;
            case FiltreFacturation.NonFacturables: Et("e.HFACTU <> 'O'"); break;
        }
        var etats = new List<string>();
        if (c.Clients) etats.AddRange([EtatsBl.Client, EtatsBl.Vierge]);
        if (c.Divers) etats.Add(EtatsBl.Divers);
        if (c.SousTraitances) etats.Add(EtatsBl.SousTraitance);
        if (c.Retours) etats.Add(EtatsBl.Retour);
        if (etats.Count < 5) Et($"e.HETABL IN ({string.Join(", ", etats.Select(_ => "?"))})", [.. etats]);
        if (representant is { } rep) Et("e.HREP = ?", rep);
        return (sql.ToString(), valeurs);
    }

    public async Task<FicheBl?> LireAsync(int societe, string profil, int numeroBl, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        var representant = await RepresentantImposeAsync(cnx, societe, profil, ct);
        EnteteBl entete;
        await using (var rd = await Lire(cnx, $"""
            SELECT h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HGENE, h.HCLIE, COALESCE(c.CNOM, ''), h.HNADRE, h.HRS, h.HRUE1, h.HRUE2, h.HCP, h.HVILLE,
                   h.HPAYS, h.HOBS, h.HOBS2, h.HOBS3, h.HMENTI, h.HNCOLT, h.HNPALT, h.HPDSTT, COALESCE(t.MNOM, ''), h.HTRAN, h.HTYPL,
                   h.HTYPP, h.HNOREC, h.HENS, h.HPROF, h.HEDBL, h.HDEDBA, h.HDEDBM, h.HDEDBJ, h.HSTR
            FROM {b}.EXP009 h
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = h.RSOC AND c.RGEN = h.HGENE AND c.RCLI = h.HCLIE
            LEFT OUTER JOIN {b}.B200F t ON t.RSOC = h.RSOC AND t.RTRANS = h.HTRAN
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numeroBl))
        {
            if (!await rd.ReadAsync(ct)) return null;
            entete = new EnteteBl(numeroBl, BaseDb2.Date(rd.Entier(0), rd.Entier(1), rd.Entier(2)), rd.Entier(3), rd.Entier(4), rd.Texte(5),
                rd.Entier(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Texte(12),
                [.. new[] { rd.Texte(13), rd.Texte(14), rd.Texte(15) }.Where(o => o != "")], rd.Texte(16),
                rd.Entier(17), rd.Entier(18), rd.Entier(19), rd.Entier(21) > 0 ? $"{rd.Entier(21)} {rd.Texte(20)}".Trim() : "",
                rd.Texte(22), rd.Texte(23), rd.Entier(24), rd.Texte(25), rd.Texte(26), rd.Texte(27) == "O",
                BaseDb2.Date(rd.Entier(28), rd.Entier(29), rd.Entier(30)), rd.Texte(31) == "O");
        }

        var elements = new List<(int Num, string Etat, int Dos, string Cle, string Des, string Ref, DateOnly? DateCde, string Fact, string Rep,
            int RepCode, bool Facturable, bool Auto, int Facture, DateOnly? DateFact, int BlO, int NumO)>();
        await using (var rd = await Lire(cnx, $"""
            SELECT e.HNUM, e.HETABL, e.HNODOS, e.HCLEF, COALESCE(a.CCDES1, ''), e.HREFCD, e.HDCDEA, e.HDCDEM, e.HDCDEJ,
                   e.HGENF, e.HCLIF, COALESCE(f.CMODIR, ''), e.HREP, {NomRepresentant(b, "e.HREP")}, e.HFACTU, e.HFACTO, e.HNOFAC,
                   e.HDAFAA, e.HDAFAM, e.HDAFAJ, e.HNOBLO, e.HNUMO
            FROM {b}.EXP010 e
            LEFT OUTER JOIN {b}.AB001 a ON a.RSOC = e.RSOC AND a.RDOSS = e.HNODOS AND a.CCDCLE = e.HCLEF
            LEFT OUTER JOIN {b}.AA0003 f ON f.RSOC = e.RSOC AND f.RGEN = e.HGENF AND f.RCLI = e.HCLIF
            WHERE e.RSOC = ? AND e.HNOBL = ? ORDER BY e.HNUM
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
                elements.Add((rd.Entier(0), rd.Texte(1), rd.Entier(2), rd.Texte(3), rd.Texte(4), rd.Texte(5),
                    BaseDb2.Date(rd.Entier(6), rd.Entier(7), rd.Entier(8)),
                    // Client facturé affiché seulement s'il diffère du client du BL
                    rd.Entier(10) > 0 && (rd.Entier(9), rd.Entier(10)) != (entete.CompteGeneral, entete.CompteClient)
                        ? $"{rd.Entier(9)} {rd.Entier(10)} {rd.Texte(11)}".Trim() : "", rd.Texte(13), rd.Entier(12),
                    rd.Texte(14) == "O", rd.Texte(15) == "O", rd.Entier(16), BaseDb2.Date(rd.Entier(17), rd.Entier(18), rd.Entier(19)),
                    rd.Entier(20), rd.Entier(21)));
        // Représentant limité : seuls les éléments de ses dossiers ; aucun → BL inaccessible
        if (representant is { } rep) elements.RemoveAll(e => e.RepCode != rep);
        if (representant is not null && elements.Count == 0) return null;

        var lots = new Dictionary<(int, int), List<LotBl>>();
        await using (var rd = await Lire(cnx, $"""
            SELECT DNUM, DNLIG, DNLOT, DNEMP, DNQTPL FROM {b}.EXP016 WHERE RSOC = ? AND DNBL = ? ORDER BY DNUM, DNLIG, DNLOT, DNEMP
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
            {
                var cle = (rd.Entier(0), rd.Entier(1));
                if (!lots.TryGetValue(cle, out var l)) lots[cle] = l = [];
                l.Add(new LotBl(rd.Texte(2), rd.Texte(3), rd.Entier(4)));
            }

        var lignes = new Dictionary<int, List<LigneBl>>();
        await using (var rd = await Lire(cnx, $"""
            SELECT DNUM, DNLIG, DART, DIND, DDES, DDES2, DDES3, DQTE, DCOLIS, DNUMLC, DETALI, DCLIG
            FROM {b}.EXP015 WHERE RSOC = ? AND DNBL = ? ORDER BY DNUM, DNLIG
            """, ct, societe, numeroBl))
            while (await rd.ReadAsync(ct))
            {
                var (num, lig) = (rd.Entier(0), rd.Entier(1));
                var designation = string.Join(" ", new[] { rd.Texte(4), rd.Texte(5), rd.Texte(6) }.Where(d => d != ""));
                if (!lignes.TryGetValue(num, out var l)) lignes[num] = l = [];
                l.Add(new LigneBl(lig, rd.Texte(2), rd.Texte(3), designation, rd.Entier(7), rd.Entier(8), rd.Entier(9), rd.Texte(10),
                    rd.Texte(11), lots.GetValueOrDefault((num, lig)) ?? []));
            }

        var pied = new List<string>();
        await using (var rd = await Lire(cnx, $"SELECT HTTXT FROM {b}.EXP012 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        while (pied.Count > 0 && pied[^1] == "") pied.RemoveAt(pied.Count - 1);

        return new FicheBl(entete,
            [.. elements.Select(e => new DetailElementBl(e.Num, e.Etat, e.Dos, e.Cle, e.Des, e.Ref, e.DateCde, e.Fact, e.Rep, e.Facturable, e.Auto,
                e.Facture, e.DateFact, e.BlO, e.NumO, lignes.GetValueOrDefault(e.Num) ?? []))],
            pied);
    }

    private static string? Texte(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToUpperInvariant();
    private static string Echapper(string v) => v.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static string Commence(string v) => Echapper(v) + "%";

    // Article : « ABC* » commence par, « *ABC » finit par, « *ABC* » contient, sinon égal (comme les commandes ; TRIM pour
    // « finit par », la zone CHAR étant complétée de blancs)
    internal static (string Condition, string Valeur) Joker(string colonne, string saisie)
    {
        var debut = saisie.StartsWith('*');
        var fin = saisie.EndsWith('*') && saisie.Length > 1;
        var texte = Echapper(saisie.Trim('*'));
        return (debut, fin) switch
        {
            (true, true) => ($"UPPER({colonne}) LIKE ? ESCAPE '\\'", $"%{texte}%"),
            (true, false) => ($"TRIM(UPPER({colonne})) LIKE ? ESCAPE '\\'", $"%{texte}"),
            (false, true) => ($"UPPER({colonne}) LIKE ? ESCAPE '\\'", $"{texte}%"),
            _ => ($"{colonne} = ?", saisie),
        };
    }

    private async Task<OdbcConnection> OuvrirAsync(CancellationToken ct)
    {
        var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return cnx;
    }

    // Lecteur qui libère aussi sa commande
    private async Task<Lecteur> Lire(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.CommandText = sql.Replace("{b}", Bib);
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return new Lecteur(cmd, await cmd.ExecuteReaderAsync(ct));
    }

    private sealed class Lecteur(OdbcCommand commande, DbDataReader lecteur) : IAsyncDisposable
    {
        public Task<bool> ReadAsync(CancellationToken ct) => lecteur.ReadAsync(ct);
        public string Texte(int i) => lecteur.Texte(i);
        public int Entier(int i) => lecteur.Entier(i);

        public async ValueTask DisposeAsync()
        {
            await lecteur.DisposeAsync();
            await commande.DisposeAsync();
        }
    }
}
