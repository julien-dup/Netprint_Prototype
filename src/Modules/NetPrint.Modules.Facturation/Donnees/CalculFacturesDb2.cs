using System.Data.Odbc;
using Microsoft.Extensions.Configuration;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Facture lue pour le calcul ; Enregistre : totaux posés par la dernière édition définitive (FAC080), pour comparaison
internal sealed record FacturePourCalcul(ConditionsFacture Conditions, IReadOnlyList<LigneCalculFacture> Lignes, bool Editee,
    (decimal Ht, decimal PortTaxable, decimal PortExonere, decimal Escompte, decimal Tva) Enregistre);

// =====================================================================
// Données du calcul d'une facture : en-tête FAC080 (conditions), lignes FAC100 avec leur type (FAC020.LTYPE) et le taux de TVA
// de leur code TVA (FAC025 : CTTVA2 à partir de la date CTDAT2, sinon CTXTVA, comme FAC201 R2 ; code absent : taux 0).
// =====================================================================
internal sealed class CalculFacturesDb2(IConfiguration config)
{
    private string Bib => BaseDb2.Bibliotheque(config);

    public async Task<FacturePourCalcul?> LireAsync(int societe, int numero, DateOnly date, CancellationToken ct)
    {
        await using var cnx = BaseDb2.Connexion(config);
        await cnx.OpenAsync(ct);
        return await LireAsync(cnx, null, societe, numero, date, ct);
    }

    // Lecture dans une transaction en cours (édition définitive : totaux recalculés sous le verrou de la facture)
    public async Task<FacturePourCalcul?> LireAsync(OdbcConnection cnx, OdbcTransaction? tx, int societe, int numero, DateOnly date,
        CancellationToken ct)
    {
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
        ConditionsFacture conditions;
        bool editee;
        (decimal, decimal, decimal, decimal, decimal) enregistre;
        await using (var rd = await Lecteur.ExecuterAsync(cnx, tx, $"""
            SELECT FDTYPE, FDESC, FDCPT, FDNLE, FDNECH, FDART, FDECHA, FDECHM, FDECHJ, FDEDIT, FDMTN, FDPOT, FDPOE, FDMES, FDTVA
            FROM {Bib}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?
            """, ct, cle))
        {
            if (!await rd.ReadAsync(ct)) return null;
            conditions = new ConditionsFacture(rd.Texte(0) == "A", date, rd.Nombre(1), rd.Nombre(2), rd.Entier(3), rd.Entier(4), rd.Entier(5),
                BaseDb2.Date(rd.Entier(6), rd.Entier(7), rd.Entier(8)));
            editee = rd.Texte(9) == "E";
            enregistre = (rd.Nombre(10), rd.Nombre(11), rd.Nombre(12), rd.Nombre(13), rd.Nombre(14));
        }
        var lignes = new List<LigneCalculFacture>();
        await using (var rd = await Lecteur.ExecuterAsync(cnx, tx, $"""
            SELECT COALESCE(t.LTYPE, ''), l.FGMTN,
                   COALESCE(CASE WHEN v.CTTVA2 <> 0 AND v.CTDAT2 <> 0 AND v.CTDAT2 <= ? THEN v.CTTVA2 ELSE v.CTXTVA END, 0)
            FROM {Bib}.FAC100 l
            LEFT OUTER JOIN {Bib}.FAC020 t ON t.RSOC = l.RSOC AND t.RLIGNE = l.FGCDL
            LEFT OUTER JOIN {Bib}.FAC025 v ON v.RSOC = t.RSOC AND v.CTVA = t.LTVA
            WHERE l.RSOC = ? AND l.RNFAC = ? AND l.FGANNE = ? AND l.FGMOIS = ? AND l.FGJOUR = ?
            ORDER BY l.FGNEL, l.FGNLG
            """, ct, [BaseDb2.AAAAMMJJ(date), .. cle]))
            while (await rd.ReadAsync(ct))
                if (rd.Texte(0) is "V" or "R" or "P" or "E") lignes.Add(new LigneCalculFacture(rd.Texte(0), rd.Nombre(1), rd.Nombre(2)));
        return new FacturePourCalcul(conditions, lignes, editee, enregistre);
    }
}
