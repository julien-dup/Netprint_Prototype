using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Numérotation des factures de la société (FAC010.PTYPEN : 1 annuelle, 2 mensuelle, 3 par enseigne si AA0007.SOP03 = O)
internal sealed record NumerotationFactures(int Type, bool GereEnseignes, string Enseigne);

// =====================================================================
// Écriture d'une facture automatique (FAC402P) : une transaction par facture, n° compris, pour qu'un arrêt ne laisse ni trou
// de numérotation ni BL pointant sur une facture absente (le RPG marquait les BL avant d'écrire l'en-tête, sans transaction).
// Sous verrou : éléments de BL toujours facturables et pas facturés, éléments en plus et compléments pas encore facturés,
// compteur (FAC040, ou PRT320F par enseigne) qui doit exister (le RPG écrivait alors une facture n° −1).
// Montants d'en-tête à 0 comme le RPG : HT, TVA, port et escompte sont posés par l'édition définitive (FAC201).
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    public async Task<int> EcrireAsync(int societe, string profil, NumerotationFactures numerotation, DateOnly date, FacturePrevue f,
        CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        async Task<int> Executer(string sql, params object?[] v)
        {
            await using var cmd = cnx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql.Replace("{b}", Bib);
            foreach (var x in v) cmd.Parameters.Valeur(x is string t && t == "" ? " " : x);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        var valide = false;
        try
        {
            // Éléments de BL relus verrouillés
            foreach (var (bl, element) in f.Elements.SelectMany(e => e.Bls))
                await using (var rd = await Lire(cnx, tx, """
                    SELECT HNOFAC, HFACTU, HETABL FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?
                    WITH RS USE AND KEEP EXCLUSIVE LOCKS
                    """, ct, societe, bl, element))
                {
                    if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"L'élément {element} du BL {bl} n'existe plus.");
                    if (rd.Entier(0) != 0) throw new RegleMetierException($"Le BL {bl} (élément {element}) a été facturé entre-temps (facture {rd.Entier(0)}).");
                    if (rd.Texte(1) != "O" || rd.Texte(2) != (f.Avoir ? "R" : "C"))
                        throw new RegleMetierException($"Le BL {bl} (élément {element}) a changé entre-temps : relancez la simulation.");
                }

            var numero = await NumeroAsync(cnx, tx, Executer, societe, numerotation, date, ct);
            await using (var rd = await Lire(cnx, tx, "SELECT 1 FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
                             ct, societe, numero, date.Year, date.Month, date.Day))
                if (await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} existe déjà : vérifiez le compteur des factures.");

            var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
            foreach (var e in f.Elements)
            {
                var d = e.Dossier;
                await Executer("""
                    INSERT INTO {b}.FAC090 (RSOC, RNFAC, FEANNE, FEMOIS, FEJOUR, FENUM, FEDOSS, FECLE, FECAT, FESCAT, FEDEV, FEMOD, FETXC, FEQTE,
                        FEFAA, FEDTB, FETYP, FECA, FEVEN, FEGEND, FECLID, FEEDT, FEORTE)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'D', ?, ?, ?, ?, ?, ?, 'N', ?)
                    """, [.. cle, e.Numero, d.Numero, d.Cle, d.Categorie, d.SousCategorie, d.Devis, d.Modele, d.Fabrication ? d.TauxCommission : 0m,
                        e.Quantite, d.EditionBl, d.TypeElement, e.ChiffreAffaires, e.Ventes, f.CompteGeneral, f.CompteClient, d.OrdreTri]);
                foreach (var l in e.Lignes)
                    await Executer("""
                        INSERT INTO {b}.FAC100 (RSOC, RNFAC, FGANNE, FGMOIS, FGJOUR, FGNEL, FGNLG, FGCDL, FGQTE, FGDES, FGPU, FGPER, FGUN, FGART, FGIND,
                            FGMTN, FGTCOL, FGLBL, FGMON, FGLIGD, FGBL, FGBLEL, FGBLLG)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                        """, [.. cle, e.Numero, l.Numero, l.CodeLigne, l.Quantite, Tronquer(l.Designation, 40), l.Pu, l.Remise, l.Unite, l.Article,
                            l.Indice, l.Montant, l.TauxCommission, l.LigneBl, l.Monnaie, l.LigneCommande, l.Bl, l.ElementBl, l.LigneDuBl]);
                foreach (var (bl, element) in e.Bls)
                    await Executer("""
                        UPDATE {b}.EXP010 SET HBLFAC = 'O', HNOFAC = ?, HDAFAA = ?, HDAFAM = ?, HDAFAJ = ?, HNOELT = ?
                        WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?
                        """, numero, date.Year, date.Month, date.Day, e.Numero, societe, bl, element);
                foreach (var (bl, element, ligne) in e.EnPlus)
                    if (await Executer("""
                            UPDATE {b}.EXP011 SET HPFACT = 'O', HPNFAC = ?, HPNELT = ?, HPDFAA = ?, HPDFAM = ?, HPDFAJ = ?
                            WHERE RSOC = ? AND HNOBL = ? AND HNUM = ? AND HPLIG = ? AND HPFACT <> 'O'
                            """, numero, e.Numero, date.Year, date.Month, date.Day, societe, bl, element, ligne) != 1)
                        throw new RegleMetierException($"Une ligne « à facturer en plus » du BL {bl} a été facturée entre-temps : relancez la simulation.");
                foreach (var ligne in e.Complements)
                    if (await Executer("""
                            UPDATE {b}.AB004 SET F4FACT = 'O', F4NFAC = ?, F4NELT = ?, F4DFAA = ?, F4DFAM = ?, F4DFAJ = ?
                            WHERE RSOC = ? AND RDOSS = ? AND F4DCLE = ? AND F4LIG = ? AND F4FACT <> 'O'
                            """, numero, e.Numero, date.Year, date.Month, date.Day, societe, d.Numero, d.Cle, ligne) != 1)
                        throw new RegleMetierException($"Un complément de facturation de la commande {d.Numero}/{d.Cle} a été facturé entre-temps : relancez la simulation.");
            }

            var dd = f.DernierDossier;
            var c = f.Conditions;
            var acompte = f.Acompte;
            var escompte = Math.Truncate(c.Escompte * 10m) / 10m;   // FDESC sur 2,1 (le RPG tronquait déjà à la lecture)
            var echeance = CalculFacture.Echeance(new ConditionsFacture(f.Avoir, date, escompte, acompte, c.Le, c.NombreJours, c.Arrete, null));
            var a = f.Adresse;
            await Executer("""
                INSERT INTO {b}.FAC080 (RSOC, RNFAC, FDANNE, FDMOIS, FDJOUR, FDTYPE, FDREF, FDCNNE, FDCOIS, FDCOUR, FDGEND, FDCLID, FDGENF, FDCLIF,
                    FDRGLT, FDRELE, FDECHA, FDECHM, FDECHJ, FDNECH, FDNLE, FDART, FDESC, FDCPT, FDEDIT, FDPLEL, FDFJ, FDNOM, FDRUE1, FDRUE2,
                    FDPOST, FDVILL, FDPAYS, FDENS, FDREP, FDCOMP, FDJALP, FDJALM, FDTRAT, FDEDT, FDBROU, FDTAGR, FDVEN, FDATT, FDMON, FDCDEV,
                    FDCOURD, FDPROF, FDPRES)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
                    'N', 'N', 'N', 'N', 'N', 'N', 'N', ?, ?, 'E', 'EUR', 1, ?, ?)
                """, [.. cle, f.Avoir ? "A" : "F", dd.Reference, dd.DateCommande?.Year ?? 0, dd.DateCommande?.Month ?? 0, dd.DateCommande?.Day ?? 0,
                    f.CompteGeneral, f.CompteClient, f.CompteGeneralFacture, f.CompteClientFacture, c.Mode, dd.Releve, echeance.Year,
                    echeance.Month, echeance.Day, c.NombreJours, c.Le, c.Arrete, escompte, acompte, f.AControler ? "A" : "O", f.Elements.Count,
                    a.FormeJuridique, a.Nom, a.Rue1, a.Rue2, a.CodePostal, a.Ville, a.Pays, numerotation.Enseigne, f.Representant, f.CodeVente,
                    dd.AlAttention, Tronquer(profil, 10), dd.Presentation]);
            // Textes de pied du client (CLITXF)
            var (gen, cli) = f.Pied;
            await Executer("""
                INSERT INTO {b}.FAC081 (RSOC, RNFAC, FDANNE, FDMOIS, FDJOUR, FDTLIG, FDTXT)
                SELECT CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(6, 0)), CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(2, 0)), CAST(? AS NUMERIC(2, 0)),
                       CTNLIG, CTTXT FROM {b}.CLITXF WHERE RSOC = ? AND RGEN = ? AND RCLI = ?
                """, [.. cle, societe, gen, cli]);
            await tx.CommitAsync(ct);
            valide = true;
            return numero;
        }
        finally
        {
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }

    // N° suivant (GETFACTURE en définitif) : compteur verrouillé, n° = valeur (1 si 0), compteur = n° + 1
    private async Task<int> NumeroAsync(OdbcConnection cnx, OdbcTransaction tx, Func<string, object?[], Task<int>> executer, int societe,
        NumerotationFactures n, DateOnly date, CancellationToken ct)
    {
        if (n.Type == 3 && n.GereEnseignes)
        {
            int suivant;
            await using (var rd = await Lire(cnx, tx, "SELECT ENDNFA FROM {b}.PRT320F WHERE RSOC = ? AND RENSEI = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                             ct, societe, n.Enseigne))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"L'enseigne {n.Enseigne} n'a pas de compteur de factures (PRT320F).");
                suivant = Math.Max(rd.Entier(0), 1);
            }
            await executer("UPDATE {b}.PRT320F SET ENDNFA = ? WHERE RSOC = ? AND RENSEI = ?", [suivant + 1, societe, n.Enseigne]);
            return suivant;
        }
        if (n.Type is not (1 or 2)) throw new RegleMetierException($"Type de numérotation des factures {n.Type} non géré (FAC010).");
        var mois = n.Type == 2 ? date.Month : 0;
        int numero;
        await using (var rd = await Lire(cnx, tx, "SELECT NFACT FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                         ct, societe, date.Year, mois))
        {
            if (!await rd.ReadAsync(ct))
                throw new RegleMetierException($"Pas de compteur de factures pour {(mois == 0 ? $"l'année {date.Year}" : $"{mois:00}/{date.Year}")} (FAC040).");
            numero = Math.Max(rd.Entier(0), 1);
        }
        await executer("UPDATE {b}.FAC040 SET NFACT = ? WHERE RSOC = ? AND NAN = ? AND NMOIS = ?", [numero + 1, societe, date.Year, mois]);
        return numero;
    }

    private static string Tronquer(string s, int n) => s.Length > n ? s[..n] : s;
}
