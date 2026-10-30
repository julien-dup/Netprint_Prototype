using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Lot C : tableau des prix (FEN_TableauPrix, projet ComposantsExternes) et récapitulatif PDF (DEV100NAV).
// ÉCART : toutes les lectures du tableau des prix filtrent sur le type de coût du devis. Le WinDev mélangeait
// « rcout = 'C' » en dur et le type demandé (tableau vide pour un devis type) et joignait parfois D5000F sans
// le type de coût (montants comptés deux fois quand le devis existe en C et en T).
// =====================================================================
internal sealed partial class ComplementsDevisDb2
{
    public async Task<string> LireMethodeValorisationAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, $"SELECT CFTYPE FROM {Bib}.AA000Z WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", societe);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Trim() ?? "";
    }

    public async Task<DonneesTableauPrix> LireTableauPrixAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var b = Bib;
        object[] devis = [societe, cle.TypeCout, cle.Numero, cle.Version];
        const string Devis = "a.RSOC = ? AND a.RCOUT = ? AND a.RDEV = ? AND a.RVERS = ?";
        await using var cnx = await OuvrirAsync(ct);

        // Unité de vente du devis ouvert (lignes « pour … ») et son diviseur
        var unite = ""; var tnumDevis = 0;
        await using (var cmd = Commande(cnx, $"""
            SELECT a.DEUNIT, d.TNUM FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA00083 d ON a.RSOC = d.RSOC AND a.DEUNIT = d.RARG AND d.RTABLE = 'UV' AND d.TTAG <> '*'
            WHERE a.RSOC = ? AND a.RCOUT = ? AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ?
            """, [.. devis, cle.Hypothese]))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            if (await rd.ReadAsync(ct)) { unite = rd.Texte(0); tnumDevis = rd.Entier(1); }

        // Valorisation de chaque hypothèse, par quantité croissante (colonnes du tableau)
        var valos = new List<ValorisationBrute>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RHYPO, a.DEQTED, b.TNUM, a.DEPRA, a.DEPRAM, a.DEMFG, a.DEMFGM, a.DEMCO, a.DEMCOM, a.DEMES, a.DEMESM,
                   a.DEMRI, a.DEMRIM, a.DEMCR, a.DEMCRM, a.DEMFF, a.DEMFFM, a.DEMFS, a.DEMFSM, a.DEPR, a.DEPRM, a.DEPVM, a.DEPVMM,
                   a.DEPVC, a.DEPVCM, a.DEPVR, a.DEPVRM, a.DEMFA, a.DEMFAM, a.DEMFMA, a.DEMFMM, a.DEOBSP
            FROM {b}.D5000F a
            LEFT OUTER JOIN {b}.AA00083 b ON a.RSOC = b.RSOC AND a.DEUNIT = b.RARG AND b.RTABLE = 'UV' AND b.TTAG <> '*'
            WHERE {Devis}
            ORDER BY a.DEQTED, a.RHYPO
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var m = new decimal[28];
                for (var i = 0; i < 28; i++) m[i] = rd.Nombre(3 + i);
                valos.Add(new ValorisationBrute(rd.Entier(0), rd.Entier(1), rd.Entier(2), m, rd.Texte(31)));
            }

        // Ateliers : opérations (DDTYPL = O) par atelier (table AT) ; « 1000 + » = résultat mille + × (prix main-d'œuvre + machine)
        var ateliers = new List<MontantAtelierBrut>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RHYPO, a.DDATEL, c.TLIB1, SUM(a.DDVALO), SUM((a.DDRSMP * (a.DDPRMO + a.DDPRMA)) / 100)
            FROM {b}.D60002 a
            JOIN {b}.AA00081 c ON a.RSOC = c.RSOC AND c.RTABLE = 'AT' AND a.DDATEL = c.RARG AND c.TTAG <> '*'
            WHERE {Devis} AND a.DDTYPL = 'O' AND a.DDRSQT > 0
            GROUP BY a.RHYPO, a.DDATEL, c.TLIB1
            ORDER BY a.DDATEL
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                ateliers.Add(new MontantAtelierBrut(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Nombre(3), rd.Nombre(4)));

        // Support : papier hors papier fourni par le client et hors « pas de papier »
        var support = new Dictionary<int, decimal>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RHYPO, SUM(CASE WHEN a.DDUA = 'KG' THEN a.DDPDSP * a.DDPA
                    ELSE {b}.CONVUP(CHAR('KG'), CHAR(a.DDUA), CHAR(a.DDFOUB), a.DDLRFP, a.DDHAFP, a.DDGRAM, a.DDPDSP) * a.DDPA END)
            FROM {b}.D60001 a
            WHERE {Devis} AND a.DDPROV <> 'C' AND a.DDSORT <> '*'
            GROUP BY a.RHYPO
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) support[rd.Entier(0)] = rd.Nombre(1);

        // Matière consommable (M) et sous-traitance (S) ; « 1000 + » = valeur × résultat mille + / résultat / 100
        var autres = new List<MontantTypeBrut>();
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RHYPO, a.DDTYPL, SUM(a.DDVALO), SUM(a.DDVALO * a.DDRSMP / a.DDRSQT / 100)
            FROM {b}.D60002 a
            WHERE {Devis} AND a.DDTYPL IN ('M', 'S') AND a.DDRSQT > 0
            GROUP BY a.RHYPO, a.DDTYPL
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) autres.Add(new MontantTypeBrut(rd.Entier(0), rd.Texte(1), rd.Nombre(2), rd.Nombre(3)));

        // Transport (D50001)
        await using (var cmd = Commande(cnx, $"""
            SELECT a.RHYPO, SUM(a.DEMTP1), SUM(a.DEMTPM) FROM {b}.D50001 a WHERE {Devis} GROUP BY a.RHYPO
            """, devis))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) autres.Add(new MontantTypeBrut(rd.Entier(0), "T", rd.Nombre(1), rd.Nombre(2)));

        return new DonneesTableauPrix(unite, tnumDevis, valos, ateliers, support, autres);
    }

    // ---------- Récapitulatif PDF (OPt_VisuRecap, BTN_RecapDevis) ----------
    // Commande CL DEV100NAV appelée par QSYS2.QCMDEXC, comme le WinDev : paramètre société(4) + devis(5) + version
    // + hypothèse + type de coût + « 000000 ». DEV100NAV produit /home/pdf/temp/Devis<soc><devis><vers><hypo>.pdf,
    // que l'on relit par SQL (plus de partage réseau \\<host>\home\pdf\temp sur le poste).
    // La clé doit avoir été contrôlée par l'appelant (chiffres, type de coût C ou T) : elle entre dans une commande.
    public async Task<byte[]?> EditerRecapitulatifAsync(int societe, CleDevis cle, CancellationToken ct)
    {
        var parametre = $"{societe:0000}{cle.Numero:00000}{cle.Version}{cle.Hypothese}{cle.TypeCout}000000";
        var commande = $"CALL PGM({BibPgm}/DEV100NAV) PARM('{parametre}')";
        var chemin = $"/home/pdf/temp/Devis{societe:0000}{cle.Numero:00000}{cle.Version}{cle.Hypothese}.pdf";

        await using (var cnx = await OuvrirAsync(ct))
        await using (var cmd = Commande(cnx, "CALL QSYS2.QCMDEXC(?)", commande))
        {
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Le PDF est normalement prêt au retour de la commande ; on laisse quelques secondes au cas où
        for (var essai = 0; essai < 20; essai++)
        {
            if (await LireFichierIfsAsync(chemin, ct) is { } pdf) return pdf;
            await Task.Delay(500, ct);
        }
        return null;
    }

    // Lecture d'un fichier de l'IFS par SQL (QSYS2.IFS_READ_BINARY) ; null si le fichier n'existe pas (encore)
    public async Task<byte[]?> LireFichierIfsAsync(string chemin, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var cmd = Commande(cnx, """
            SELECT LINE FROM TABLE(QSYS2.IFS_READ_BINARY(PATH_NAME => ?, END_OF_LINE => 'NONE', MAXIMUM_LINE_LENGTH => 1048576))
            ORDER BY LINE_NUMBER
            """, chemin);
        // Sans limite : avec le pilote ODBC IBM i, le délai d'une commande devient une limite du « query governor »
        // évaluée AVANT l'exécution ; l'estimation de IFS_READ_BINARY la dépasse (SQL0666) même pour un petit fichier.
        cmd.CommandTimeout = 0;
        try
        {
            using var contenu = new MemoryStream();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                if (!rd.IsDBNull(0)) contenu.Write((byte[])rd.GetValue(0));
            return contenu.Length > 0 ? contenu.ToArray() : null;
        }
        catch (System.Data.Odbc.OdbcException ex) when (ex.Message.Contains("CPE3025") || ex.Message.Contains("ENOENT")
                                                        || ex.Message.Contains("No such path", StringComparison.OrdinalIgnoreCase))
        {
            return null;   // fichier pas encore créé
        }
    }
}

// Montants de D5000F pour une hypothèse, dans l'ordre :
//  0 DEPRA  1 DEPRAM  2 DEMFG  3 DEMFGM  4 DEMCO  5 DEMCOM  6 DEMES  7 DEMESM  8 DEMRI  9 DEMRIM 10 DEMCR 11 DEMCRM
// 12 DEMFF 13 DEMFFM 14 DEMFS 15 DEMFSM 16 DEPR 17 DEPRM 18 DEPVM 19 DEPVMM 20 DEPVC 21 DEPVCM 22 DEPVR 23 DEPVRM
// 24 DEMFA 25 DEMFAM 26 DEMFMA 27 DEMFMM
internal sealed record ValorisationBrute(int Hypothese, int Quantite, int Tnum, decimal[] M, string Observations);

internal sealed record MontantAtelierBrut(int Hypothese, string Atelier, string Libelle, decimal Valeur, decimal MillePlus);

// Type : M matière consommable, S sous-traitance, T transport
internal sealed record MontantTypeBrut(int Hypothese, string Type, decimal Valeur, decimal MillePlus);

internal sealed record DonneesTableauPrix(
    string Unite, int TnumDevis, List<ValorisationBrute> Valorisations, List<MontantAtelierBrut> Ateliers,
    Dictionary<int, decimal> Support, List<MontantTypeBrut> Autres);
