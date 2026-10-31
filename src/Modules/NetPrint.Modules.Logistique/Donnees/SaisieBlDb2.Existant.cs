using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// BL existant (ajout d'un dossier, modification) : en-tête EXP009, état de ses éléments, client facturé du premier élément
internal sealed record BlExistant(
    int NumeroBl, DateOnly DateExpedition, int CompteGeneral, int CompteClient, string Enseigne, bool Revendeur, bool SousTraitanceSynchronisee,
    AdresseBl Adresse, string TypeLivraison, int Transporteur, string TypePort, int Bareme, string Observation1, string Observation2,
    string Observation3, string Mention, string Incoterm, bool Facture, bool Retour, int CompteGeneralFacture, int CompteClientFacture,
    IReadOnlyList<string> TextePied, int NombreElements = 0);

// Ligne enregistrée d'un élément (EXP015) avec la ligne de commande (NEG100) et les lots livrés (sorties A700F)
internal sealed record LigneEnregistree(
    int NumeroLigneBl, int LigneCommande, string CodeLigne, string Article, string Indice, string Designation, string Designation2,
    string Designation3, int Quantite, int EmballagePar, string TypeEmballage, decimal PoidsUc, string Etat, int ResteCommande,
    bool Soldee, IReadOnlyList<PriseLot> Lots);

// Élément enregistré, pour sa modification
internal sealed record ElementAModifier(
    int Dossier, string Cle, string Etat, int TypeBl, bool Facture, int Palettes, string ReferenceCommande, DateOnly? DateCommande,
    int Representant, bool Facturable, bool FacturationAutomatique, int CompteGeneralFacture, int CompteClientFacture,
    bool SousReferences, bool IssuDePalettes, IReadOnlyList<LigneEnregistree> Lignes, bool LotsSansMouvement = false);

internal sealed partial class SaisieBlDb2
{
    public async Task<BlExistant?> BlAsync(int societe, int numeroBl, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        BlExistant? bl;
        await using (var rd = await Lire(cnx, """
            SELECT h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HGENE, h.HCLIE, h.HENS, h.HREV, h.HSTR, h.HNADRE, h.HFJ, h.HRS, h.HRUE1, h.HRUE2, h.HCP,
                   h.HVILLE, h.HDPT, h.HPAYS, h.HTYPL, h.HTRAN, h.HTYPP, h.HBART, h.HOBS, h.HOBS2, h.HOBS3, h.HMENTI, h.HOBSIN,
                   (SELECT COUNT(*) FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL AND (e.HBLFAC = 'O' OR e.HNOFAC <> 0)),
                   (SELECT COUNT(*) FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND (e.HNOBLO = h.HNOBL OR (e.HNOBL = h.HNOBL AND e.HETABL = 'R'))),
                   COALESCE((SELECT e.HGENF FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL ORDER BY e.HNUM FETCH FIRST 1 ROW ONLY), 0),
                   COALESCE((SELECT e.HCLIF FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL ORDER BY e.HNUM FETCH FIRST 1 ROW ONLY), 0),
                   (SELECT COUNT(*) FROM {b}.EXP010 e WHERE e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL)
            FROM {b}.EXP009 h WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numeroBl))
        {
            if (!await rd.ReadAsync(ct)) return null;
            bl = new BlExistant(numeroBl, BaseDb2.Date(rd.Entier(0), rd.Entier(1), rd.Entier(2)) ?? DateOnly.FromDateTime(DateTime.Today),
                rd.Entier(3), rd.Entier(4), rd.Texte(5), rd.Texte(6) == "O", rd.Texte(7) == "O",
                new AdresseBl(rd.Entier(8), rd.Texte(9), rd.Texte(10), rd.Texte(11), rd.Texte(12), rd.Texte(13), rd.Texte(14), rd.Texte(15),
                    rd.Texte(16)),
                rd.Texte(17), rd.Entier(18), rd.Texte(19), rd.Entier(20), rd.Texte(21), rd.Texte(22), rd.Texte(23), rd.Texte(24),
                rd.Texte(25), rd.Entier(26) > 0, rd.Entier(27) > 0, rd.Entier(28), rd.Entier(29), [], rd.Entier(30));
        }
        var pied = new List<string>();
        await using (var rd = await Lire(cnx, "SELECT HTTXT FROM {b}.EXP012 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) pied.Add(rd.Texte(0));
        return bl with { TextePied = pied };
    }

    public async Task<ElementAModifier?> ElementAModifierAsync(int societe, int numeroBl, int element, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        int dossier;
        string cle;
        ElementAModifier el;
        await using (var rd = await Lire(cnx, """
            SELECT e.HNODOS, e.HCLEF, e.HETABL, e.HTYPBL, e.HBLFAC, e.HNOFAC, e.HNBPAL, e.HREFCD, e.HDCDEA, e.HDCDEM, e.HDCDEJ, e.HREP,
                   e.HFACTU, e.HFACTO, e.HGENF, e.HCLIF,
                   (SELECT COUNT(*) FROM {b}.EXP018E s WHERE s.RSOC = e.RSOC AND s.DNBL = e.HNOBL AND s.DNUM = e.HNUM),
                   (SELECT COUNT(*) FROM {b}.PROD_ENTITE p WHERE p.SOCIETE = e.RSOC AND p.NUMERO_BL = e.HNOBL AND p.ELEMENT_BL = e.HNUM)
            FROM {b}.EXP010 e WHERE e.RSOC = ? AND e.HNOBL = ? AND e.HNUM = ?
            """, ct, societe, numeroBl, element))
        {
            if (!await rd.ReadAsync(ct)) return null;
            (dossier, cle) = (rd.Entier(0), rd.Texte(1));
            el = new ElementAModifier(dossier, cle, rd.Texte(2), rd.Entier(3), rd.Texte(4) == "O" || rd.Entier(5) != 0, rd.Entier(6),
                rd.Texte(7), BaseDb2.Date(rd.Entier(8), rd.Entier(9), rd.Entier(10)), rd.Entier(11), rd.Texte(12) == "O", rd.Texte(13) == "O",
                rd.Entier(14), rd.Entier(15), rd.Entier(16) > 0, rd.Entier(17) > 0, []);
        }

        // Lots livrés par ligne : sorties d'expédition A700F (portent le dépôt), à défaut EXP016 au dépôt principal
        var lots = new Dictionary<int, List<PriseLot>>();
        await using (var rd = await Lire(cnx, """
            SELECT MVLIGB, MVDEP, MVLOT, MVEMP, SUM(MVQTUS) FROM {b}.A700F
            WHERE RSOC = ? AND MVBL = ? AND MVELBL = ? AND MVCMVT = 'EX' AND MVSENS = 'S'
            GROUP BY MVLIGB, MVDEP, MVLOT, MVEMP ORDER BY MVLIGB, MVLOT, MVEMP
            """, ct, societe, numeroBl, element))
            while (await rd.ReadAsync(ct))
            {
                if (!lots.TryGetValue(rd.Entier(0), out var l)) lots[rd.Entier(0)] = l = [];
                l.Add(new PriseLot { Depot = rd.Entier(1), Lot = rd.Texte(2), Emplacement = rd.Texte(3), Quantite = (int)rd.Nombre(4) });
            }
        // Lots livrés (EXP016) sans sortie de stock A700F : élément ancien, non modifiable ici (la sortie serait comptée deux fois)
        var sansMouvement = false;
        if (lots.Count == 0)
            await using (var rd = await Lire(cnx, "SELECT 1 FROM {b}.EXP016 WHERE RSOC = ? AND DNBL = ? AND DNUM = ? FETCH FIRST 1 ROW ONLY",
                             ct, societe, numeroBl, element))
                sansMouvement = await rd.ReadAsync(ct);

        var lignes = new List<LigneEnregistree>();
        await using (var rd = await Lire(cnx, """
            SELECT l.DNLIG, l.DNUMLC, l.DCLIG, l.DART, l.DIND, l.DDES, l.DDES2, l.DDES3, l.DQTE, l.DEMBP1, l.DTYEMB, l.DPDSU, l.DETALI,
                   COALESCE(n.DNQTEC - n.DNQTEL, 0), COALESCE(n.DNSOLD, '')
            FROM {b}.EXP015 l
            LEFT OUTER JOIN {b}.NEG100 n ON n.RSOC = l.RSOC AND n.RDOSS = ? AND n.DNDCLE = ? AND n.DNNLIG = l.DNUMLC AND l.DNUMLC <> 0
            WHERE l.RSOC = ? AND l.DNBL = ? AND l.DNUM = ? ORDER BY l.DNLIG
            """, ct, dossier, cle, societe, numeroBl, element))
            while (await rd.ReadAsync(ct))
                lignes.Add(new LigneEnregistree(rd.Entier(0), rd.Entier(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6),
                    rd.Texte(7), rd.Entier(8), rd.Entier(9), rd.Texte(10), rd.Nombre(11), rd.Texte(12), rd.Entier(13), rd.Texte(14) == "O",
                    lots.GetValueOrDefault(rd.Entier(0)) ?? []));
        return el with { Lignes = lignes, LotsSansMouvement = sansMouvement };
    }
}
