using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Clé de facturation résolue : refus (champ, message), ou donneur d'ordre, commande et éléments de BL à cocher d'office
internal sealed record CleResolue(ErreurLancement? Erreur, int CompteGeneral, int CompteClient, string NomClient,
    (int Numero, string Cle)? Commande, IReadOnlySet<(int Bl, int Element)> Proposes);

// =====================================================================
// Lectures de la saisie des factures : clé de facturation (FAC085 R1 à R6 : BL, commande ou client), listes de choix de l'en-tête
// (modes de règlement RG, représentants B000F), existence du client facturé. Les éléments de BL eux-mêmes sont lus par
// FacturationAutoDb2.LireManuelAsync (même lecture que la facturation automatique).
// =====================================================================
internal sealed class SaisieFacturesDb2(IConfiguration config)
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

    public async Task<CleResolue> ResoudreAsync(int societe, CleFacturation c, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        CleResolue Refus(string champ, string message) => new(new ErreurLancement(champ, message), 0, 0, "", null, new HashSet<(int, int)>());

        if (c.Bl is { } bl)
        {
            var proposes = new HashSet<(int, int)>();
            int gen = 0, cli = 0, elements = 0, factures = 0, nonFacturables = 0;
            var etats = new HashSet<string>();
            await using (var rd = await Lire(cnx, """
                SELECT e.HNUM, e.HETABL, e.HNOFAC, e.HFACTU, b.HGENE, b.HCLIE FROM {b}.EXP010 e
                JOIN {b}.EXP009 b ON b.RSOC = e.RSOC AND b.HNOBL = e.HNOBL
                WHERE e.RSOC = ? AND e.HNOBL = ? ORDER BY e.HNUM
                """, ct, societe, bl))
                while (await rd.ReadAsync(ct))
                {
                    elements++;
                    (gen, cli) = (rd.Entier(4), rd.Entier(5));
                    var etat = rd.Texte(1);
                    etats.Add(etat);
                    if (etat is not ("C" or "R")) continue;
                    if (rd.Entier(2) != 0) { factures = rd.Entier(2); continue; }
                    if (rd.Texte(3) != "O") { nonFacturables++; continue; }
                    proposes.Add((bl, rd.Entier(0)));
                }
            if (elements == 0) return Refus(nameof(c.Bl), $"Le BL {bl} n'existe pas.");
            if (proposes.Count == 0)
                return Refus(nameof(c.Bl), factures != 0 ? $"Le BL {bl} est déjà facturé (facture {factures})."
                    : nonFacturables > 0 ? $"Le BL {bl} n'est pas facturable (élément marqué « ne pas facturer »)."
                    : etats.Overlaps(["S", "D"]) ? $"Le BL {bl} est un BL de sous-traitance ou divers : il ne se facture pas."
                    : $"Le BL {bl} n'est pas encore livré (BL vierge).");
            return new CleResolue(null, gen, cli, await NomAsync(cnx, societe, gen, cli, ct) ?? "", null, proposes);
        }
        if (c.Commande is { } commande)
        {
            var cle = c.Cle.Trim().ToUpperInvariant();
            string? type = null;
            int gen = 0, cli = 0;
            await using (var rd = await Lire(cnx, "SELECT CCTYPC, RGEN, RCLI FROM {b}.AB001 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                             societe, commande, cle))
                if (await rd.ReadAsync(ct)) (type, gen, cli) = (rd.Texte(0), rd.Entier(1), rd.Entier(2));
            if (type is null) return Refus(nameof(c.Commande), $"La commande {commande}/{cle} n'existe pas.");
            // FAC085 R2 (A7 : codification à confirmer) : type 2 refusé
            if (type == "2") return Refus(nameof(c.Commande), $"La commande {commande}/{cle} est une mise en stock : elle ne se facture pas.");
            return new CleResolue(null, gen, cli, await NomAsync(cnx, societe, gen, cli, ct) ?? "", (commande, cle), new HashSet<(int, int)>());
        }
        if (c.CompteGeneral is { } g && c.CompteClient is { } k)
        {
            var nom = await NomAsync(cnx, societe, g, k, ct);
            return nom is null ? Refus(nameof(c.CompteGeneral), $"Le client {g} {k} n'existe pas.")
                : new CleResolue(null, g, k, nom, null, new HashSet<(int, int)>());
        }
        return Refus("", "Indiquez un BL, une commande ou un client.");
    }

    private async Task<string?> NomAsync(OdbcConnection cnx, int societe, int gen, int cli, CancellationToken ct)
    {
        await using var rd = await Lire(cnx, "SELECT CNOM FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?", ct, societe, gen, cli);
        return await rd.ReadAsync(ct) ? rd.Texte(0) : null;
    }

    public async Task<bool> ClientExisteAsync(int societe, int gen, int cli, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        return await NomAsync(cnx, societe, gen, cli, ct) is not null;
    }

    // Modes de règlement (table RG) et représentants (B000F) de la société, hors supprimés
    public async Task<(List<CodeLibelle> Modes, List<CodeLibelle> Representants)> ListesAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var modes = new List<CodeLibelle>();
        await using (var rd = await Lire(cnx, "SELECT RARG, TLIB1 FROM {b}.AA00081 WHERE RSOC = ? AND RTABLE = 'RG' AND TTAG <> '*' ORDER BY RARG", ct, societe))
            while (await rd.ReadAsync(ct)) modes.Add(new CodeLibelle(rd.Texte(0), rd.Texte(1)));
        var representants = new List<CodeLibelle>();
        await using (var rd = await Lire(cnx, "SELECT RREPR, INOM FROM {b}.B000F WHERE RSOC = ? AND ITAG <> '*' ORDER BY RREPR", ct, societe))
            while (await rd.ReadAsync(ct)) representants.Add(new CodeLibelle(rd.Entier(0).ToString(), rd.Texte(1)));
        return (modes, representants);
    }
}
