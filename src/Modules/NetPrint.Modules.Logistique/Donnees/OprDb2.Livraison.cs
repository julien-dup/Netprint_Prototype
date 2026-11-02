using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Ligne de commande d'un BL d'OPR : désignation, textes (NEG100T), déjà livré, commandé, acompte, emballage, type de stock
internal sealed record LigneCommandeBlOpr(
    int Dossier, string Cle, int Ligne, string Designation, string CodeLigne, IReadOnlyList<string> Textes, int Livre, int Commande, bool Acompte,
    string TypeEmballage, string TypeStock, string Famille, string SousFamille);

// Commande d'un BL d'OPR (en-tête AB001) ; FacturationAutomatique : AB001FA.FAAUTO, sinon option du client (CDOPT2 1 à 8)
internal sealed record CommandeBlOpr(
    int Dossier, string Cle, int CompteGeneral, int CompteClient, string Reference, DateOnly? DateCommande, int Representant,
    bool FacturationAutomatique, string Revendeur, bool SousTraitanceSynchronisee, int NumeroAdresse);

// Tout ce qu'il faut pour écrire les BL d'un OPR
internal sealed record SourceBlOpr(
    int Numero, string Mode, IReadOnlyList<PaletteBlOpr> Palettes, IReadOnlyDictionary<(int, string, int), LigneCommandeBlOpr> Lignes,
    IReadOnlyDictionary<(int, string), CommandeBlOpr> Commandes, AdresseBl Adresse, IReadOnlyList<string> TextePied, int DepotPrincipal,
    bool NumerotationParEnseigne);

internal sealed partial class OprDb2
{
    // Situation (FEN_BonLivraison) : lignes non livrées de l'OPR, palettes au statut 3 et plus avec leur carton
    public async Task<(string Mode, IReadOnlyList<LigneSituationOpr> Lignes)> SituationAsync(int societe, int numero, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var mode = "";
        await using (var rd = await Lire(cnx, null, """
            SELECT COALESCE(c.CDRES1, '') FROM {b}.OPR015 l
            JOIN {b}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.ODNCDE AND a.CCDCLE = l.ODNCLE
            JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            WHERE l.RSOC = ? AND l.ODNUM = ? ORDER BY l.ODNLIG FETCH FIRST 1 ROW ONLY
            """, ct, societe, numero))
            if (await rd.ReadAsync(ct)) mode = rd.Texte(0);
        var colis = new List<(int Ligne, ColisOpr Colis)>();
        await using (var rd = await Lire(cnx, null, """
            SELECT p.NUMERO_LIGNE_OPR, p.NUMERO_SSCC, p.NUMERO_OF, p.CLE_OF, p.QUANTITE_EX, p.STATUT, COALESCE(p.NUMERO_COLISAGE, 0),
                   COALESCE(g.CLOTURE, ''), COALESCE(g.OPERATEUR_COLISAGE, ''), COALESCE(m.GNOM, '')
            FROM {b}.PROD_ENTITE p
            LEFT OUTER JOIN {b}.PROD_COLISAGE g ON g.SOCIETE = p.SOCIETE AND g.NUMERO_OPR = p.NUMERO_OPR AND g.NUMERO_COLISAGE = p.NUMERO_COLISAGE
            LEFT OUTER JOIN {b}.C000F m ON m.RSOC = g.SOCIETE AND m.RMATIE = g.MATIERE_CARTON
            WHERE p.SOCIETE = ? AND p.NUMERO_OPR = ? ORDER BY p.NUMERO_LIGNE_OPR, p.NUMERO_SSCC
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
                colis.Add((rd.Entier(0), new ColisOpr(rd.Texte(1), $"{rd.Entier(2):00000}{rd.Texte(3)}", (int)rd.Nombre(4), rd.Texte(5),
                    rd.Entier(6), rd.Texte(7) == "O", rd.Texte(8), rd.Texte(9))));
        var lignes = new List<LigneSituationOpr>();
        await using (var rd = await Lire(cnx, null, """
            SELECT l.ODNLIG, l.ODNCDE, l.ODNCLE, l.ODART, l.ODIND, COALESCE(s.ARFIMP, ''), l.ODQTEA, l.ODQPREP, COALESCE(s.ASTK, '')
            FROM {b}.OPR015 l LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.ODART AND s.AINDIC = l.ODIND
            WHERE l.RSOC = ? AND l.ODNUM = ? AND l.ODLIVR <> 'O' ORDER BY l.ODNLIG
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var ligne = rd.Entier(0);
                var frais = rd.Texte(8) != "O";
                var siens = colis.Where(c => c.Ligne == ligne).Select(c => c.Colis).ToList();
                var conditionnees = siens.Where(c => c.Statut is "4" or "5").ToList();
                // Palette conditionnée sans carton, ou carton non clôturé : conditionnement pas terminé (FEN_BonLivraison)
                var nonClos = siens.Any(c => c.Statut == "4" && (c.Carton == 0 || !c.CartonClos));
                var quantite = rd.Entier(6);
                var (etat, bloquante) = ReglesGenerationBlOpr.Etat(frais, quantite, rd.Entier(7), conditionnees.Sum(c => c.Quantite), nonClos);
                lignes.Add(new LigneSituationOpr(ligne, rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), quantite,
                    frais ? quantite : rd.Entier(7), frais ? quantite : conditionnees.Sum(c => c.Quantite), etat, bloquante, frais, siens));
            }
        return (mode, lignes);
    }

    // Valeurs proposées : transporteur des lignes non livrées s'il est unique, port et mode de livraison (EXP005), observations
    // de l'adresse de livraison de l'OPR, poids d'une palette
    public async Task<DefautsBlOpr> DefautsBlAsync(int societe, int numero, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var transporteurs = new List<int>();
        await using (var rd = await Lire(cnx, null, "SELECT DISTINCT ODTRS FROM {b}.OPR015 WHERE RSOC = ? AND ODNUM = ? AND ODLIVR <> 'O'", ct, societe, numero))
            while (await rd.ReadAsync(ct)) transporteurs.Add(rd.Entier(0));
        (string Port, string Mode, int Poids) e = ("", "", 0);
        await using (var rd = await Lire(cnx, null, "SELECT FEPORT, FETYPL, FPDSP FROM {b}.EXP005 WHERE RSOC = ? FETCH FIRST 1 ROW ONLY", ct, societe))
            if (await rd.ReadAsync(ct)) e = (rd.Texte(0), rd.Texte(1), (int)rd.Nombre(2));
        var observations = new List<string>();
        await using (var rd = await Lire(cnx, null, """
            SELECT COALESCE(f.ALCOMM, ''), COALESCE(f.ALCOM2, ''), COALESCE(f.ALCOM3, ''), n.DNRS, COALESCE(c.CCOMTR, ''), COALESCE(c.CCOMT2, ''),
                   COALESCE(c.CCOMT3, ''), n.DNLIGA
            FROM {b}.OPR015 l
            JOIN {b}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.ODNCDE AND n.DNDCLE = l.ODNCLE AND n.DNNLIG = l.ODDLIG
            JOIN {b}.AB001 a ON a.RSOC = l.RSOC AND a.RDOSS = l.ODNCDE AND a.CCDCLE = l.ODNCLE
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.A500F f ON f.RSOC = a.RSOC AND f.RGEN = a.RGEN AND f.RCLI = a.RCLI AND f.ALNLIG = n.DNLIGA AND n.DNLIGA <> 0
            WHERE l.RSOC = ? AND l.ODNUM = ? ORDER BY l.ODNLIG FETCH FIRST 1 ROW ONLY
            """, ct, societe, numero))
            if (await rd.ReadAsync(ct))
                observations = rd.Entier(7) != 0 ? [rd.Texte(0), rd.Texte(1), rd.Texte(2)] : rd.Texte(3) != "" ? [] : [rd.Texte(4), rd.Texte(5), rd.Texte(6)];
        var transporteur = transporteurs.Where(t => t != 0).Distinct().ToList();
        return new DefautsBlOpr(transporteur.Count == 1 ? transporteur[0] : 0, e.Port, e.Mode, [.. observations.Where(o => o != "")], e.Poids);
    }

    // Palettes conditionnées (statut 4) et lignes de frais non livrées de l'OPR, avec les lignes et commandes concernées
    public async Task<SourceBlOpr?> SourceBlAsync(int societe, int numero, string mode, ParametresBl parametres, CancellationToken ct)
    {
        var reference = await ReferenceAsync(societe, numero, ct);
        if (reference is null) return null;
        await using var cnx = await OuvrirAsync(ct);
        var palettes = new List<PaletteBlOpr>();
        var lignes = new Dictionary<(int, string, int), LigneCommandeBlOpr>();
        var commandes = new Dictionary<(int, string), CommandeBlOpr>();
        await using (var rd = await Lire(cnx, null, """
            SELECT l.ODNLIG, l.ODNCDE, l.ODNCLE, l.ODDLIG, l.ODART, l.ODIND, l.ODQTEA, CASE WHEN s.ASTK = 'O' THEN 0 ELSE 1 END,
                   COALESCE(p.QUANTITE_EX, 0), COALESCE(p.NUMERO_SSCC, ''), COALESCE(p.NUMERO_OF, 0), COALESCE(p.CLE_OF, ''),
                   COALESCE(p.EMPLACEMENT, ''), COALESCE(p.DEPOT, 0), COALESCE(p.NUMERO_COLISAGE, 0), COALESCE(g.POIDS, 0), COALESCE(g.MATIERE_CARTON, 0),
                   CASE WHEN n.DNCLIF <> 0 THEN n.DNGENF ELSE a.CGENF END, CASE WHEN n.DNCLIF <> 0 THEN n.DNCLIF ELSE a.CCLIF END,
                   a.CCENSE, n.DNDESA, n.DNCLIG, n.DNQTEL, n.DNQTEC, l.ODACOMP, COALESCE(s.ATYEMB, ''), COALESCE(s.AFAM, ''), COALESCE(s.ASFAM, ''),
                   COALESCE(s.ATYPEA, ''), COALESCE(s.ATYFAC, ''), a.RGEN, a.RCLI, a.CCREFC, a.CCDENA, a.CCDENM, a.CCDENJ, a.CCREP, COALESCE(e.FAAUTO, ''), COALESCE(c.CDOPT2, ''),
                   a.CCRCVD, a.CCOP11, n.DNLIGA
            FROM {b}.OPR015 l
            JOIN {b}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = l.ODNCDE AND n.DNDCLE = l.ODNCLE AND n.DNNLIG = l.ODDLIG
            JOIN {b}.AB001 a ON a.RSOC = n.RSOC AND a.RDOSS = n.RDOSS AND a.CCDCLE = n.DNDCLE
            LEFT OUTER JOIN {b}.AB001FA e ON e.RSOC = a.RSOC AND e.RDOSS = a.RDOSS AND e.CCDCLE = a.CCDCLE
            LEFT OUTER JOIN {b}.AA0003 c ON c.RSOC = a.RSOC AND c.RGEN = a.RGEN AND c.RCLI = a.RCLI
            LEFT OUTER JOIN {b}.SPF010 s ON s.RSOC = l.RSOC AND s.AARTIC = l.ODART AND s.AINDIC = l.ODIND
            LEFT OUTER JOIN {b}.PROD_ENTITE p ON p.SOCIETE = l.RSOC AND p.NUMERO_OPR = l.ODNUM AND p.NUMERO_LIGNE_OPR = l.ODNLIG AND p.STATUT = '4'
            LEFT OUTER JOIN {b}.PROD_COLISAGE g ON g.SOCIETE = p.SOCIETE AND g.NUMERO_OPR = p.NUMERO_OPR AND g.NUMERO_COLISAGE = p.NUMERO_COLISAGE
            WHERE l.RSOC = ? AND l.ODNUM = ? AND l.ODLIVR <> 'O' AND (s.ASTK IS NULL OR s.ASTK <> 'O' OR p.SOCIETE IS NOT NULL)
            """, ct, societe, numero))
            while (await rd.ReadAsync(ct))
            {
                var frais = rd.Entier(7) == 1;
                var (dossier, cle, ligneCde) = (rd.Entier(1), rd.Texte(2), rd.Entier(3));
                palettes.Add(new PaletteBlOpr(rd.Entier(0), dossier, cle, ligneCde, rd.Texte(4), rd.Texte(5), frais ? rd.Entier(6) : (int)rd.Nombre(8), frais,
                    rd.Texte(9), frais ? "" : $"{rd.Entier(10):00000}{rd.Texte(11)}", rd.Texte(12), rd.Entier(13) == 0 ? parametres.DepotPrincipal : rd.Entier(13),
                    rd.Entier(14), rd.Nombre(15), rd.Entier(16), rd.Entier(17), rd.Entier(18), rd.Texte(19)));
                lignes.TryAdd((dossier, cle, ligneCde), new LigneCommandeBlOpr(dossier, cle, ligneCde, rd.Texte(20), rd.Texte(21), [], rd.Entier(22),
                    rd.Entier(23), rd.Texte(24) == "O", rd.Texte(25), parametres.TypeStockArticle(rd.Texte(28), rd.Texte(29)), rd.Texte(26), rd.Texte(27)));
                var cdopt2 = int.TryParse(rd.Texte(38), out var o) ? o : 0;
                commandes.TryAdd((dossier, cle), new CommandeBlOpr(dossier, cle, rd.Entier(30), rd.Entier(31), rd.Texte(32),
                    BaseDb2.Date(rd.Entier(33), rd.Entier(34), rd.Entier(35)), rd.Entier(36),
                    rd.Texte(37) != "" ? rd.Texte(37) == "O" : cdopt2 is >= 1 and <= 8, rd.Texte(39), rd.Texte(40) == "O", rd.Entier(41)));
            }
        foreach (var k in lignes.Keys.ToList())
            lignes[k] = lignes[k] with { Textes = await TextesCommandeAsync(cnx, societe, k.Item1, k.Item2, k.Item3, ct) };
        var pied = new List<string>();
        await using (var rd = await Lire(cnx, null, "SELECT CTTXT FROM {b}.CLITXB WHERE RSOC = ? AND RGEN = ? AND RCLI = ? ORDER BY CTNLIG", ct,
                         societe, reference.CompteGeneral, reference.CompteClient))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        return new SourceBlOpr(numero, mode, palettes, lignes, commandes, reference.Adresse, pied, parametres.DepotPrincipal, parametres.NumerotationParEnseigne);
    }
}
