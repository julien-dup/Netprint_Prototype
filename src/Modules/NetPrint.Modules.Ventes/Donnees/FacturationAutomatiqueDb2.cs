using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Paramètres de facturation automatique d'une commande (AB001FA, DOS505 SPFACT / écriture)
// =====================================================================
internal sealed class FacturationAutomatiqueDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    private const string Colonnes = "FAAUTO, FANAL, FAFJ, FANOM, FARUE1, FARUE2, FAPOST, FAVILL, FAATT, FAEDTB, FAORTE, FACPT, FACRC, FATPC";

    // Paramètres enregistrés ; sans ligne AB001FA (ancienne commande), valeurs par défaut du client comme SPFACT :
    // facturation automatique selon CDOPT2, adresse CDNAF, détail BL de EXP005, règlement du facturé, texte CDRETF.
    // Les codes FACRC / FATPC sont repris tels quels (une valeur autre que 1 / 2 posée par le 5250 est conservée).
    public async Task<FacturationAutomatique> LireAsync(int societe, CleCommande cle, int compteGeneral, int compteClient, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        string cadrs = "", cdopt2 = "", cdretf = "", fedebl = "";
        var cdnaf = 0;
        await using (var rd = await Lire(cnx, null, $"""
            SELECT c.CADRS, c.CDOPT2, c.CDNAF, c.CDRETF, COALESCE((SELECT MIN(e.FEDEBL) FROM {b}.EXP005 e WHERE e.RSOC = c.RSOC), ' ')
            FROM {b}.AA0003 c WHERE c.RSOC = ? AND c.RGEN = ? AND c.RCLI = ?
            """, ct, societe, compteGeneral, compteClient))
            if (await rd.ReadAsync(ct)) (cadrs, cdopt2, cdnaf, cdretf, fedebl) = (rd.Texte(0), rd.Texte(1), rd.Entier(2), rd.Texte(3), rd.Texte(4));

        var f = await LigneAsync(cnx, null, b, societe, cle, ct) ?? new FacturationAutomatique
        {
            Automatique = NumerotationCommande.FacturationAutomatique(cdopt2), NumeroAdresse = cdnaf > 0 ? cdnaf : null,
            EditionDetailBl = fedebl == "O", ConditionsReglement = ConditionsFacturation.Facture,
            TextePied = cdretf == ConditionsFacturation.Facture ? ConditionsFacturation.Facture : ConditionsFacturation.DonneurOrdre,
        };
        f.AdresseSpecialeClient = cadrs == "O";
        return f;
    }

    // Ligne AB001FA (null si absente), avec son empreinte
    private static async Task<FacturationAutomatique?> LigneAsync(OdbcConnection cnx, OdbcTransaction? tx, string b, int societe, CleCommande cle,
        CancellationToken ct)
    {
        await using var rd = await Lire(cnx, tx, $"SELECT {Colonnes} FROM {b}.AB001FA WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
            ct, societe, cle.Numero, cle.Cle);
        if (!await rd.ReadAsync(ct)) return null;
        var valeurs = Enumerable.Range(0, 14).Select(i => Convert.ToString(rd.GetValue(i))?.Trim() ?? "").ToArray();
        return new FacturationAutomatique
        {
            Automatique = rd.Texte(0) == "O", NumeroAdresse = rd.Entier(1) is > 0 and var n ? n : null, FormeJuridique = rd.Texte(2),
            Nom = rd.Texte(3), Rue1 = rd.Texte(4), Rue2 = rd.Texte(5), CodePostal = rd.Texte(6), Ville = rd.Texte(7),
            Attention = rd.Texte(8), EditionDetailBl = rd.Texte(9) == "O", OrigineTechnique = rd.Texte(10),
            Acompte = rd.Nombre(11) is not 0 and var a ? a : null,
            ConditionsReglement = rd.Texte(12) is "" ? ConditionsFacturation.Facture : rd.Texte(12),
            TextePied = rd.Texte(13) is "" ? ConditionsFacturation.DonneurOrdre : rd.Texte(13),
            Empreinte = string.Join("|", valeurs),
        };
    }

    // N° d'adresse du client (A500F) et origine technique active (table OR, AA0008L1 : AA00080 à AA00083)
    public async Task<(bool AdresseExiste, bool OrigineExiste)> VerifierAsync(int societe, int compteGeneral, int compteClient,
        FacturationAutomatique f, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        var adresse = f.NumeroAdresse is not > 0 || await Existe(cnx, $"SELECT 1 FROM {b}.A500F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALNLIG = ?",
            ct, societe, compteGeneral, compteClient, f.NumeroAdresse);
        var origine = f.OrigineTechnique == "" || await Existe(cnx, string.Join(" UNION ALL ",
            new[] { "AA00080", "AA00081", "AA00082", "AA00083" }
                .Select(t => $"SELECT 1 FROM {b}.{t} WHERE RSOC = ? AND RTABLE = 'OR' AND RARG = ? AND TTAG <> '*'")),
            ct, [.. Enumerable.Repeat<object?[]>([societe, f.OrigineTechnique], 4).SelectMany(v => v)]);
        return (adresse, origine);
    }

    // Écriture ou mise à jour (DOS505), en transaction, commande verrouillée ; refusée si la ligne a changé depuis sa lecture.
    // L'acompte en autre monnaie (FAACPT) n'est pas touché, 0 à la création.
    public async Task EnregistrerAsync(int societe, CleCommande cle, FacturationAutomatique f, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            await using (var cmd = Commande(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?",
                             [societe, cle.Numero, cle.Cle]))
                await cmd.ExecuteNonQueryAsync(ct);
            var actuelle = await LigneAsync(cnx, tx, b, societe, cle, ct);
            if ((actuelle?.Empreinte ?? "") != f.Empreinte)
                throw new RegleMetierException("Les paramètres de facturation ont été modifiés par un autre utilisateur depuis leur ouverture : rouvrez la fenêtre.");
            object?[] valeurs = [f.Automatique ? "O" : "N", f.NumeroAdresse ?? 0, f.FormeJuridique, f.Nom, f.Rue1, f.Rue2, f.CodePostal, f.Ville,
                f.Attention, f.EditionDetailBl ? "O" : "N", f.OrigineTechnique, f.Acompte ?? 0, f.ConditionsReglement, f.TextePied];
            var sql = actuelle is not null
                ? $"""
                  UPDATE {b}.AB001FA SET FAAUTO = ?, FANAL = ?, FAFJ = ?, FANOM = ?, FARUE1 = ?, FARUE2 = ?, FAPOST = ?, FAVILL = ?, FAATT = ?,
                      FAEDTB = ?, FAORTE = ?, FACPT = ?, FACRC = ?, FATPC = ?
                  WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?
                  """
                : $"INSERT INTO {b}.AB001FA ({Colonnes}, RSOC, RDOSS, CCDCLE, FAACPT) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0)";
            await using (var cmd = Commande(cnx, tx, sql, [.. valeurs, societe, cle.Numero, cle.Cle]))
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            validee = true;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }

    private static OdbcCommand Commande(OdbcConnection cnx, OdbcTransaction? tx, string sql, object?[] valeurs)
    {
        var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var v in valeurs) cmd.Parameters.Valeur(v is string t && t == "" ? " " : v);
        return cmd;
    }

    private static async Task<DbDataReader> Lire(OdbcConnection cnx, OdbcTransaction? tx, string sql, CancellationToken ct, params object?[] valeurs) =>
        await Commande(cnx, tx, sql, valeurs).ExecuteReaderAsync(ct);

    private static async Task<bool> Existe(OdbcConnection cnx, string sql, CancellationToken ct, params object?[] valeurs)
    {
        await using var rd = await Lire(cnx, null, sql, ct, valeurs);
        return await rd.ReadAsync(ct);
    }
}
