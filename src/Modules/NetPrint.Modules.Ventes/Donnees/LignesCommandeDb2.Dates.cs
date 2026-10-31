using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Report de date de livraison en masse (NEG520 F19) : en une transaction, commande verrouillée, en-tête recalculé comme
// après l'écriture d'une ligne (SYN100 si sous-traitée, historique DOS040)
// =====================================================================
internal sealed partial class LignesCommandeDb2
{
    public sealed record DatesLigne(string Statut, DateOnly? Livraison, DateOnly? Confirmee);

    // Applique le report (règle du service) aux lignes non soldées ; renvoie l'article de chaque ligne reportée
    public async Task<IReadOnlyList<(string Article, string Indice)>> ReporterDatesAsync(int societe, string profil, CleCommande cle,
        ContexteLigne c, Func<DatesLigne, (DateOnly Livraison, DateOnly? Confirmee)?> reporter, CancellationToken ct)
    {
        var b = Bib;
        var reportees = new List<(string, string)>();
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var validee = false;
        try
        {
            await Executer(cnx, tx, $"UPDATE {b}.AB001 SET CCDES1 = CCDES1 WHERE RSOC = ? AND RDOSS = ? AND CCDCLE = ?", ct,
                societe, cle.Numero, cle.Cle);
            // Une ligne dans un ordre de préparation non livré bloque le report (comme NEG520, toutes lignes confondues)
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT ODNUM, ODDLIG FROM {b}.OPR015 WHERE RSOC = ? AND ODNCDE = ? AND ODNCLE = ? AND ODLIVR <> 'O'
                ORDER BY ODDLIG FETCH FIRST 1 ROW ONLY
                """, ct, societe, cle.Numero, cle.Cle))
                if (await rd.ReadAsync(ct))
                    throw new RegleMetierException($"Ligne {rd.Entier(1)} : {MessagesLigne.LigneDansOpr(rd.Entier(0))}");

            var lignes = new List<(int Numero, string Article, string Indice, string Statut, int Datb, int Dliv)>();
            await using (var rd = await Lire(cnx, tx, $"""
                SELECT DNNLIG, DNART, DNIND, DNSTAT, DNDATB, DNDLIV FROM {b}.NEG100
                WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNSOLD <> 'O' ORDER BY DNNLIG
                """, ct, societe, cle.Numero, cle.Cle))
                while (await rd.ReadAsync(ct))
                    lignes.Add((rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Entier(4), rd.Entier(5)));

            foreach (var (numero, article, indice, statut, datb, dliv) in lignes)
            {
                // Date saisie DNDATB (à défaut DNDLIV, seule date d'une ligne ancienne), date confirmée si DNDLIV en diffère
                var livraison = BaseDb2.DateAAAAMMJJ(datb != 0 ? datb : dliv);
                var confirmee = datb != 0 && dliv != 0 && dliv != datb ? BaseDb2.DateAAAAMMJJ(dliv) : null;
                if (reporter(new DatesLigne(statut, livraison, confirmee)) is not { } r) continue;
                // MAJ520 : DNDATB = date saisie, DNDLIV = date confirmée ou, à défaut, date saisie
                var (nouvelleDatb, nouvelleDliv) = (BaseDb2.AAAAMMJJ(r.Livraison), BaseDb2.AAAAMMJJ(r.Confirmee ?? r.Livraison));
                if (nouvelleDatb == datb && nouvelleDliv == dliv) continue;
                await Executer(cnx, tx, $"UPDATE {b}.NEG100 SET DNDATB = ?, DNDLIV = ? WHERE RSOC = ? AND RDOSS = ? AND DNDCLE = ? AND DNNLIG = ?",
                    ct, nouvelleDatb, nouvelleDliv, societe, cle.Numero, cle.Cle, numero);
                reportees.Add((article, indice));
            }
            if (reportees.Count > 0) await RecalculerEnTeteAsync(cnx, tx, b, societe, profil, cle, c, ct);
            await tx.CommitAsync(ct);
            validee = true;
            return reportees;
        }
        finally
        {
            if (!validee) await tx.RollbackAsync(CancellationToken.None);
        }
    }
}
