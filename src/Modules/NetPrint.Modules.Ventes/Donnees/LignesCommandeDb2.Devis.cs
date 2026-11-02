using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Services;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Ventes.Donnees;

// =====================================================================
// Reprise d'un devis dans une ligne de commande (NEG520 F10) : lecture des devis chiffrés (D5000F, RCOUT = 'C') du client
// de la commande et de leurs adresses de livraison (D50001)
// =====================================================================
internal sealed partial class LignesCommandeDb2
{
    // Devis du client, les plus récents d'abord ; recherche : n° de devis, ou mots de la désignation / référence imprimée
    public async Task<ResultatRecherche<DevisAReprendre>> DevisDuClientAsync(int societe, int compteGeneral, int compteClient, string? recherche,
        CancellationToken ct)
    {
        var b = Bib;
        var criteres = new List<string>();
        var valeurs = new List<object?> { societe, compteGeneral, compteClient };
        var r = (recherche ?? "").Trim();
        if (int.TryParse(r, out var numero)) { criteres.Add("d.RDEV = ?"); valeurs.Add(numero); }
        else
            foreach (var mot in r.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // % et _ tapés sont cherchés tels quels
                criteres.Add(@"(UPPER(d.DEDES1) LIKE ? ESCAPE '\' OR UPPER(d.DEDES2) LIKE ? ESCAPE '\' OR UPPER(d.DERFIM) LIKE ? ESCAPE '\')");
                var motif = $"%{mot.ToUpperInvariant().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
                valeurs.AddRange([motif, motif, motif]);
            }
        var maximum = LimitesRecherche.Maximum;
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT d.RDEV, d.RVERS, d.RHYPO, d.DEDATA, d.DEDATM, d.DEDATJ, d.DEDES1, d.DERFIM, d.DEQTED, d.DEUNIT, d.DEPVR, d.DETAG,
                   (SELECT COUNT(*) FROM {b}.D50001 a WHERE a.RSOC = d.RSOC AND a.RCOUT = d.RCOUT AND a.RDEV = d.RDEV
                      AND a.RVERS = d.RVERS AND a.RHYPO = d.RHYPO)
            FROM {b}.D5000F d
            WHERE d.RSOC = ? AND d.RCOUT = 'C' AND d.RGEN = ? AND d.RCLI = ? {string.Concat(criteres.Select(c => " AND " + c))}
            ORDER BY d.RDEV DESC, d.RVERS DESC, d.RHYPO
            FETCH FIRST {maximum + 1} ROWS ONLY
            """, ct, [.. valeurs]);
        var liste = new List<DevisAReprendre>();
        while (await rd.ReadAsync(ct))
            liste.Add(new DevisAReprendre(new ReferenceDevis(rd.Entier(0), rd.Entier(1), rd.Entier(2)),
                BaseDb2.Date(rd.Entier(3), rd.Entier(4), rd.Entier(5)), rd.Texte(6), rd.Texte(7), rd.Entier(8), rd.Texte(9), rd.Nombre(10),
                rd.Texte(11) != "", rd.Entier(12)));
        var tronque = liste.Count > maximum;
        return new(tronque ? liste[..maximum] : liste, tronque);
    }

    // Devis du client, avec les diviseurs de son unité et du mille et l'option SOP18 ; null s'il n'existe pas pour ce client
    public async Task<RepriseDevis.Devis?> DevisAsync(int societe, int compteGeneral, int compteClient, ReferenceDevis devis, CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT d.DEDES1, d.DERFIM, d.DEUNIT, d.DEQTED, d.DEPVR, d.DEPR,
                   (SELECT MIN(u.TNUM) FROM {b}.AA00083 u WHERE u.RSOC = d.RSOC AND u.RTABLE = 'UV' AND u.RARG = d.DEUNIT AND u.TTAG <> '*'),
                   (SELECT MIN(u.TNUM) FROM {b}.AA00083 u WHERE u.RSOC = d.RSOC AND u.RTABLE = 'UV' AND u.RARG = ? AND u.TTAG <> '*'),
                   (SELECT MIN(s.SOP18) FROM {b}.AA0007 s WHERE s.RSOC = d.RSOC)
            FROM {b}.D5000F d
            WHERE d.RSOC = ? AND d.RCOUT = 'C' AND d.RDEV = ? AND d.RVERS = ? AND d.RHYPO = ? AND d.RGEN = ? AND d.RCLI = ?
            """, ct, RepriseDevis.UniteLeMille, societe, devis.Numero, devis.Version, devis.Hypothese, compteGeneral, compteClient);
        if (!await rd.ReadAsync(ct)) return null;
        long? Tnum(int i) => rd.IsDBNull(i) ? null : Convert.ToInt64(rd.GetValue(i));
        return new RepriseDevis.Devis(devis, rd.Texte(0), rd.Texte(1), rd.Texte(2), rd.Entier(3), rd.Nombre(4), rd.Nombre(5),
            Tnum(6), Tnum(7), rd.Texte(8) == "O");
    }

    // Adresses de livraison d'un devis du client, dans l'ordre de la clé de D50001 (D50001R)
    public async Task<IReadOnlyList<AdresseDevis>> AdressesDevisAsync(int societe, int compteGeneral, int compteClient, ReferenceDevis devis,
        CancellationToken ct)
    {
        var b = Bib;
        await using var cnx = await OuvrirAsync(ct);
        await using var rd = await Lire(cnx, $"""
            SELECT a.DEPRIO, a.DERS, a.DERUE1, a.DERUE2, a.DEPOST, a.DEVILL, a.DEQTEL, a.DECOMM, a.DECOM2, a.DECOM3
            FROM {b}.D50001 a
            WHERE a.RSOC = ? AND a.RCOUT = 'C' AND a.RDEV = ? AND a.RVERS = ? AND a.RHYPO = ?
              AND EXISTS (SELECT 1 FROM {b}.D5000F d WHERE d.RSOC = a.RSOC AND d.RCOUT = a.RCOUT AND d.RDEV = a.RDEV AND d.RVERS = a.RVERS
                            AND d.RHYPO = a.RHYPO AND d.RGEN = ? AND d.RCLI = ?)
            ORDER BY a.DEPRIO, RRN(a)
            """, ct, societe, devis.Numero, devis.Version, devis.Hypothese, compteGeneral, compteClient);
        var liste = new List<AdresseDevis>();
        while (await rd.ReadAsync(ct))
            liste.Add(new AdresseDevis(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Entier(6),
                rd.Texte(7), rd.Texte(8), rd.Texte(9)));
        return liste;
    }
}
