using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Numérotation des factures de la société (FAC010.PTYPEN : 1 annuelle, 2 mensuelle, 3 par enseigne si AA0007.SOP03 = O)
internal sealed record NumerotationFactures(int Type, bool GereEnseignes, string Enseigne)
{
    public bool ParEnseigne => Type == 3 && GereEnseignes;

    // Compteur FAC040 (numérotation annuelle ou mensuelle) ; sinon PRT320F par enseigne, ou type non géré
    public bool ParCompteurFac040 => Type is 1 or 2;

    // Mois du compteur FAC040 : celui de la facture en mensuel, 0 en annuel
    public int Mois(DateOnly date) => Type == 2 ? date.Month : 0;

    // N° de départ habituel d'un nouveau compteur : 110001 pour novembre en mensuel, 260001 pour 2026 en annuel
    public static int PremierPropose(DateOnly date, int mois) => (mois == 0 ? date.Year % 100 : mois) * 10000 + 1;

    public CompteurFacturesAbsentException CompteurAbsent(DateOnly date) => new(date.Year, Mois(date), PremierPropose(date, Mois(date)));
}

// =====================================================================
// Écriture d'une facture automatique (FAC402P) ou saisie (FAC150, BL choisis) : une transaction par facture, n° compris, pour qu'un arrêt ne laisse ni trou
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
        Task<int> Executer(string sql, params object?[] v) => ExecuterAsync(cnx, tx, sql, ct, v);
        var valide = false;
        try
        {
            await VerrouillerBlsAsync(cnx, tx, societe, f, ct);

            var numero = await NumeroAsync(cnx, tx, Executer, societe, numerotation, date, ct);
            await using (var rd = await Lire(cnx, tx, "SELECT 1 FROM {b}.FAC080 WHERE RSOC = ? AND RNFAC = ? AND FDANNE = ? AND FDMOIS = ? AND FDJOUR = ?",
                             ct, societe, numero, date.Year, date.Month, date.Day))
                if (await rd.ReadAsync(ct)) throw new RegleMetierException($"La facture {numero} du {date:dd/MM/yyyy} existe déjà : vérifiez le compteur des factures.");

            await EcrireElementsAsync(cnx, tx, societe, numero, date, f, 0, ct);

            var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
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

    // Éléments de BL relus verrouillés : pas facturés, toujours facturables, dans l'état attendu (livré, ou retour pour un avoir)
    private async Task VerrouillerBlsAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, FacturePrevue f, CancellationToken ct)
    {
        foreach (var (bl, element) in f.Elements.SelectMany(e => e.Bls))
            await using (var rd = await Lire(cnx, tx, """
                SELECT HNOFAC, HFACTU, HETABL FROM {b}.EXP010 WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?
                WITH RS USE AND KEEP EXCLUSIVE LOCKS
                """, ct, societe, bl, element))
            {
                if (!await rd.ReadAsync(ct)) throw new RegleMetierException($"L'élément {element} du BL {bl} n'existe plus.");
                if (rd.Entier(0) != 0) throw new RegleMetierException($"Le BL {bl} (élément {element}) a été facturé entre-temps (facture {rd.Entier(0)}).");
                if (rd.Texte(1) != "O" || rd.Texte(2) != (f.Avoir ? "R" : "C"))
                    throw new RegleMetierException($"Le BL {bl} (élément {element}) a changé entre-temps (état ou « facturable ») : recommencez.");
            }
    }

    // Éléments (FAC090), lignes (FAC100), BL, éléments en plus et compléments marqués facturés ; decalage : n° des éléments déjà sur
    // la facture (ajout à une facture existante), 0 en création
    private async Task EcrireElementsAsync(OdbcConnection cnx, OdbcTransaction tx, int societe, int numero, DateOnly date, FacturePrevue f,
        int decalage, CancellationToken ct)
    {
        var cle = new object[] { societe, numero, date.Year, date.Month, date.Day };
        Task<int> Executer(string sql, params object?[] v) => ExecuterAsync(cnx, tx, sql, ct, v);
        foreach (var e in f.Elements)
        {
            var numeroElement = e.Numero + decalage;
            var d = e.Dossier;
            await Executer("""
                INSERT INTO {b}.FAC090 (RSOC, RNFAC, FEANNE, FEMOIS, FEJOUR, FENUM, FEDOSS, FECLE, FECAT, FESCAT, FEDEV, FEMOD, FETXC, FEQTE,
                    FEFAA, FEDTB, FETYP, FECA, FEVEN, FEGEND, FECLID, FEEDT, FEORTE)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'D', ?, ?, ?, ?, ?, ?, 'N', ?)
                """, [.. cle, numeroElement, d.Numero, d.Cle, d.Categorie, d.SousCategorie, d.Devis, d.Modele, d.Fabrication ? d.TauxCommission : 0m,
                    e.Quantite, d.EditionBl, d.TypeElement, e.ChiffreAffaires, e.Ventes, f.CompteGeneral, f.CompteClient, d.OrdreTri]);
            foreach (var l in e.Lignes) await InsererLigneAsync(cnx, tx, cle, numeroElement, l, ct);
            foreach (var (bl, element) in e.Bls)
                await Executer("""
                    UPDATE {b}.EXP010 SET HBLFAC = 'O', HNOFAC = ?, HDAFAA = ?, HDAFAM = ?, HDAFAJ = ?, HNOELT = ?
                    WHERE RSOC = ? AND HNOBL = ? AND HNUM = ?
                    """, numero, date.Year, date.Month, date.Day, numeroElement, societe, bl, element);
            foreach (var (bl, element, ligne) in e.EnPlus)
                if (await Executer("""
                        UPDATE {b}.EXP011 SET HPFACT = 'O', HPNFAC = ?, HPNELT = ?, HPDFAA = ?, HPDFAM = ?, HPDFAJ = ?
                        WHERE RSOC = ? AND HNOBL = ? AND HNUM = ? AND HPLIG = ? AND HPFACT <> 'O'
                        """, numero, numeroElement, date.Year, date.Month, date.Day, societe, bl, element, ligne) != 1)
                    throw new RegleMetierException($"Une ligne « à facturer en plus » du BL {bl} a été facturée entre-temps : recommencez.");
            foreach (var ligne in e.Complements)
                if (await Executer("""
                        UPDATE {b}.AB004 SET F4FACT = 'O', F4NFAC = ?, F4NELT = ?, F4DFAA = ?, F4DFAM = ?, F4DFAJ = ?
                        WHERE RSOC = ? AND RDOSS = ? AND F4DCLE = ? AND F4LIG = ? AND F4FACT <> 'O'
                        """, numero, numeroElement, date.Year, date.Month, date.Day, societe, d.Numero, d.Cle, ligne) != 1)
                    throw new RegleMetierException($"Un complément de facturation de la commande {d.Numero}/{d.Cle} a été facturé entre-temps : recommencez.");
        }
    }

    // N° suivant (GETFACTURE en définitif) : compteur verrouillé, n° = valeur (1 si 0), compteur = n° + 1
    private async Task<int> NumeroAsync(OdbcConnection cnx, OdbcTransaction tx, Func<string, object?[], Task<int>> executer, int societe,
        NumerotationFactures n, DateOnly date, CancellationToken ct)
    {
        if (n.ParEnseigne)
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
        var mois = n.Mois(date);
        int numero;
        await using (var rd = await Lire(cnx, tx, "SELECT NFACT FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ? WITH RS USE AND KEEP EXCLUSIVE LOCKS",
                         ct, societe, date.Year, mois))
        {
            if (!await rd.ReadAsync(ct)) throw n.CompteurAbsent(date);
            numero = Math.Max(rd.Entier(0), 1);
        }
        await executer("UPDATE {b}.FAC040 SET NFACT = ? WHERE RSOC = ? AND NAN = ? AND NMOIS = ?", [numero + 1, societe, date.Year, mois]);
        return numero;
    }

    // Ligne FAC100 d'un élément ; cle : société, n°, année, mois, jour de la facture
    private Task<int> InsererLigneAsync(OdbcConnection cnx, OdbcTransaction tx, object[] cle, int element, LignePrevue l, CancellationToken ct)
    {
        var c = l.Conserve;
        var autreMonnaie = c is not null && c.MontantLu == l.Montant;   // montant changé : l'autre monnaie n'est plus juste
        return ExecuterAsync(cnx, tx, """
            INSERT INTO {b}.FAC100 (RSOC, RNFAC, FGANNE, FGMOIS, FGJOUR, FGNEL, FGNLG, FGCDL, FGQTE, FGDES, FGPU, FGPER, FGUN, FGART, FGIND,
                FGMTN, FGTCOL, FGLBL, FGMON, FGLIGD, FGBL, FGBLEL, FGBLLG, FGCTC, FGNBR, FGPUA, FGMTNA)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, ct, [.. cle, element, l.Numero, l.CodeLigne, l.Quantite, Tronquer(l.Designation, 40), l.Pu, l.Remise, l.Unite, l.Article,
                l.Indice, l.Montant, l.TauxCommission, l.LigneBl, l.Monnaie, l.LigneCommande, l.Bl, l.ElementBl, l.LigneDuBl,
                c?.CodeProduitClient ?? "", c?.Nombre ?? 0m, autreMonnaie ? c!.PuAutreMonnaie : 0m, autreMonnaie ? c!.MontantAutreMonnaie : 0m]);
    }

    // Ordre SQL dans la transaction (chaîne vide passée en blanc, comme les zones CHAR) ; nombre de lignes touchées
    private async Task<int> ExecuterAsync(OdbcConnection cnx, OdbcTransaction tx, string sql, CancellationToken ct, params object?[] v)
    {
        await using var cmd = cnx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql.Replace("{b}", Bib);
        foreach (var x in v) cmd.Parameters.Valeur(x is string t && t == "" ? " " : x);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Compteur FAC040 de la date présent (vrai hors numérotation FAC040 : enseigne PRT320F ou type non géré, contrôlés à l'écriture)
    public async Task<bool> CompteurExisteAsync(int societe, NumerotationFactures n, DateOnly date, CancellationToken ct)
    {
        if (!n.ParCompteurFac040) return true;
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, "SELECT 1 FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ?", ct, societe, date.Year, n.Mois(date));
        return await rd.ReadAsync(ct);
    }

    // Création d'un compteur (écran FAC040) : refus s'il existe ou si le premier n° est déjà pris par une facture de la période
    public async Task CreerCompteurAsync(int societe, int annee, int mois, int premier, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var valide = false;
        try
        {
            await using (var rd = await Lire(cnx, tx, "SELECT NFACT FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ?", ct, societe, annee, mois))
                if (await rd.ReadAsync(ct)) throw new RegleMetierException($"Le compteur existe déjà (prochain n° {rd.Entier(0)}).");
            var periode = mois == 0 ? "" : " AND FDMOIS = ?";
            object?[] v = mois == 0 ? [societe, annee] : [societe, annee, mois];
            await using (var rd = await Lire(cnx, tx, $"SELECT COALESCE(MAX(RNFAC), 0) FROM {{b}}.FAC080 WHERE RSOC = ? AND FDANNE = ?{periode}", ct, v))
                if (await rd.ReadAsync(ct) && rd.Entier(0) >= premier)
                    throw new RegleMetierException($"Le n° {premier} est déjà pris : la dernière facture de la période porte le n° {rd.Entier(0)}.");
            // Insertion conditionnelle : deux créations simultanées ne donnent pas deux compteurs
            if (await ExecuterAsync(cnx, tx, """
                    INSERT INTO {b}.FAC040 (RSOC, NAN, NMOIS, NFACT)
                    SELECT CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(4, 0)), CAST(? AS NUMERIC(2, 0)), CAST(? AS NUMERIC(6, 0)) FROM SYSIBM.SYSDUMMY1
                    WHERE NOT EXISTS (SELECT 1 FROM {b}.FAC040 WHERE RSOC = ? AND NAN = ? AND NMOIS = ?)
                    """, ct, societe, annee, mois, premier, societe, annee, mois) != 1)
                throw new RegleMetierException("Le compteur vient d'être créé par quelqu'un d'autre.");
            await tx.CommitAsync(ct);
            valide = true;
        }
        finally
        {
            if (!valide) try { await tx.RollbackAsync(CancellationToken.None); } catch (OdbcException) { }
        }
    }

    private static string Tronquer(string s, int n) => s.Length > n ? s[..n] : s;
}
