using NetPrint.Modules.Facturation.Contrats;
using NetPrint.Modules.Facturation.Services;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Facturation.Donnees;

// Donneur d'ordre d'une facture sans commande : nom, conditions de règlement, représentant, code vente par défaut (CDCVD),
// relevé et présentation (AA0003)
internal sealed record ClientAFacturer(string Nom, ConditionsReglement Conditions, int Representant, string CodeVente, string Releve,
    string Presentation);

// =====================================================================
// Facture d'un client sans commande (FAC085 clé client → FAC150, élément sans dossier) : fiche du client, catégories (AA0009) et
// sous-catégories (AE0003) non supprimées, codes vente par catégorie (FAC030, catégorie « ** » à défaut : R20, R34). Avoir d'un client
// sans facture d'origine (remise de fin d'année) : période de référence FDRFAD / FDRFAF, obligatoire pour un client EDI (R14, R15).
// =====================================================================
internal sealed partial class FacturationAutoDb2
{
    public async Task<ClientAFacturer?> LireClientAsync(int societe, int general, int client, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, """
            SELECT CNOM, CMODE, CNBJOU, CJOULE, CESCOM, CARRET, CREPRE, CDCVD, CRELVE, CDOPT7
            FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND CTAG <> '*'
            """, ct, societe, general, client);
        if (!await rd.ReadAsync(ct)) return null;
        return new ClientAFacturer(rd.Texte(0), new ConditionsReglement(rd.Texte(1), rd.Entier(2), rd.Entier(3), rd.Nombre(4), rd.Entier(5)),
            rd.Entier(6), rd.Texte(7), rd.Texte(8), rd.Texte(9));
    }

    // Catégories et sous-catégories (code « catégorie/sous-catégorie ») utilisables
    public async Task<(IReadOnlyList<CodeLibelle> Categories, IReadOnlyList<CodeLibelle> SousCategories)> CategoriesAsync(int societe,
        CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var categories = new List<CodeLibelle>();
        await using (var rd = await Lire(cnx, null, "SELECT RCAT, TLIB1 FROM {b}.AA0009 WHERE RSOC = ? AND TTAG <> '*' ORDER BY RCAT", ct, societe))
            while (await rd.ReadAsync(ct)) categories.Add(new CodeLibelle(rd.Texte(0).Trim(), rd.Texte(1).Trim()));
        var sous = new List<CodeLibelle>();
        await using (var rd = await Lire(cnx, null, "SELECT RCAT, RSCAT, TLIB1 FROM {b}.AE0003 WHERE RSOC = ? AND TTAG <> '*' ORDER BY RCAT, RSCAT",
                         ct, societe))
            while (await rd.ReadAsync(ct)) sous.Add(new CodeLibelle($"{rd.Texte(0).Trim()}/{rd.Texte(1).Trim()}", rd.Texte(2).Trim()));
        return (categories, sous);
    }

    // Client facturé par EDI (PRT057F.PFFMT commence par EDI) : un avoir doit citer sa facture d'origine ou une période (FAC150 R15)
    public async Task<bool> FactureParEdiAsync(int societe, int general, int client, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, "SELECT 1 FROM {b}.PRT057F WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND PFFMT LIKE 'EDI%'",
            ct, societe, general, client);
        return await rd.ReadAsync(ct);
    }

    // Chiffre d'affaires HT facturé au client sur une période (remise de fin d'année) : factures et avoirs édités dont il est le client
    // facturé (ou le donneur d'ordre sans autre client facturé), avoirs déduits, hors avoirs de remise de fin d'année (période FDRFAD ;
    // un avoir EDI qui porte une période pour une autre raison est donc écarté aussi)
    public async Task<(decimal ChiffreAffaires, int Documents)> ChiffreAffairesAsync(int societe, int general, int client, DateOnly du, DateOnly au,
        CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, """
            SELECT COALESCE(SUM(CASE WHEN FDTYPE = 'A' THEN -FDMTN ELSE FDMTN END), 0), COUNT(*) FROM {b}.FAC080
            WHERE RSOC = ? AND FDEDIT = 'E' AND FDRFAD = 0 AND FDANNE * 10000 + FDMOIS * 100 + FDJOUR BETWEEN ? AND ?
              AND ((FDGENF = ? AND FDCLIF = ?) OR (FDCLIF = 0 AND FDGEND = ? AND FDCLID = ?))
            """, ct, societe, BaseDb2.AAAAMMJJ(du), BaseDb2.AAAAMMJJ(au), general, client, general, client);
        return await rd.ReadAsync(ct) ? (rd.Nombre(0), rd.Entier(1)) : (0, 0);
    }

    // Codes de FAC030 (codes vente, ES pour l'escompte) utilisables pour la catégorie, ou à défaut pour la catégorie « ** », et s'ils
    // ont un compte de comptabilisation (R20) ; une seule lecture. RVENTE (2 caractères) est calé à droite (« ␣0 » pour la ligne de
    // code 0, MOVE du RPG) : comparé sans les blancs
    public async Task<IReadOnlyDictionary<string, bool>> CodesVenteAsync(int societe, IReadOnlyCollection<string> codes, string categorie,
        CancellationToken ct)
    {
        var trouves = new Dictionary<string, bool>();
        if (codes.Count == 0) return trouves;
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, null, $"""
            SELECT TRIM(RVENTE), VGEN, VAUX FROM {"{b}"}.FAC030
            WHERE RSOC = ? AND TRIM(RVENTE) IN ({string.Join(", ", codes.Select(_ => "?"))}) AND RCAT IN (?, '**') AND VTAG <> '*'
            ORDER BY CASE WHEN RCAT = '**' THEN 1 ELSE 0 END
            """, ct, [societe, .. codes.Select(c => c.Trim()), categorie]);
        while (await rd.ReadAsync(ct))
            trouves.TryAdd(rd.Texte(0).Trim(), rd.Entier(1) != 0 || rd.Texte(2).Trim() is not ("" or "0"));   // catégorie avant « ** »
        return trouves;
    }
}
