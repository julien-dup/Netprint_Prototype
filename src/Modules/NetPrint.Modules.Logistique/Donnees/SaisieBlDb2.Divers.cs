using System.Data;
using System.Data.Odbc;
using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;

namespace NetPrint.Modules.Logistique.Donnees;

// Tiers d'un BL divers : nom, adresses, client (fournisseur : compte général 0, auxiliaire = n° fournisseur), dossier,
// enseigne et transport par défaut
internal sealed record TiersDivers(
    string Nom, IReadOnlyList<AdresseBl> Adresses, int CompteGeneral, int CompteClient, int Dossier, string Cle, string Enseigne,
    int Transporteur, int Bareme, string QualiteClient, int Representant, string ReferenceCommande, DateOnly? DateCommande,
    int Devis = 0, int Hypothese = 0, int Modele = 0, string RefImprime = "");

// BL divers (cahier des charges § 11) : EXP009, EXP010 (HETABL = 'D', HTYPBL = 1, non facturable), lignes de désignation
// EXP015 sans article, texte de pied EXP012 ; ni stock, ni commande, ni dossier mis à jour
internal sealed partial class SaisieBlDb2
{
    // actifSeulement : fournisseur non supprimé (création) ; un BL existant garde son fournisseur
    public async Task<TiersDivers?> TiersDiversAsync(int societe, TiersBlDivers t, CancellationToken ct, bool actifSeulement = true)
    {
        await using var cnx = await OuvrirAsync(ct);
        if (t.Fournisseur > 0)
        {
            await using var rd = await Lire(cnx, $"SELECT PRS, PFJ, PRUE1, PRUE2, PPOSTA, PVILLE FROM {{b}}.B500F WHERE RSOC = ? AND RFOURN = ? {(actifSeulement ? "AND PTAG <> '*'" : "")}",
                ct, societe, t.Fournisseur);
            if (!await rd.ReadAsync(ct)) return null;
            var adresse = new AdresseBl(0, rd.Texte(1), rd.Texte(0), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), Departement(rd.Texte(4)), "");
            return new TiersDivers(rd.Texte(0), [adresse], 0, t.Fournisseur, 0, "", "", 0, 0, "", 0, "", null);
        }

        var (gen, cli, dossier, cle, enseigne, rep, refCde) = (t.CompteGeneral, t.CompteClient, 0, "", "", 0, "");
        DateOnly? dateCde = null;
        DossierBl? d = null;
        if (t.Dossier > 0)
        {
            d = await DossierAsync(societe, t.Dossier, t.Cle ?? "", ct);
            if (d is null) return null;
            (gen, cli, dossier, cle, enseigne, rep, refCde, dateCde) =
                (d.CompteGeneral, d.CompteClient, d.Dossier, d.Cle, d.Enseigne, d.Representant, d.ReferenceCommande, d.DateCommande);
        }

        var adresses = new List<AdresseBl>();
        string nom, qualite;
        int transporteur, bareme;
        await using (var rd = await Lire(cnx, """
            SELECT CNOM, CFJ, CRUE1, CRUE2, CPOSTA, CVILLE, CDEPT, CPAYS, CTRANS, CODBAR, CQUACL FROM {b}.AA0003 WHERE RSOC = ? AND RGEN = ? AND RCLI = ?
            """, ct, societe, gen, cli))
        {
            if (!await rd.ReadAsync(ct)) return null;
            (nom, transporteur, bareme, qualite) = (rd.Texte(0), rd.Entier(8), rd.Entier(9), rd.Texte(10));
            adresses.Add(new AdresseBl(0, rd.Texte(1), rd.Texte(0), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7)));
        }
        await using (var rd = await Lire(cnx, """
            SELECT ALNLIG, ALFJ, ALRS, ALRUE1, ALRUE2, ALPOST, ALVILL, ALDEP, ALPAY FROM {b}.A500F
            WHERE RSOC = ? AND RGEN = ? AND RCLI = ? AND ALTAG <> '*' ORDER BY RPRIO, ALNLIG
            """, ct, societe, gen, cli))
            while (await rd.ReadAsync(ct))
                adresses.Add(new AdresseBl(rd.Entier(0), rd.Texte(1), rd.Texte(2), rd.Texte(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7),
                    rd.Texte(8)));
        return new TiersDivers(nom, adresses, gen, cli, dossier, cle, enseigne, transporteur, bareme, qualite, rep, refCde, dateCde,
            d?.Devis ?? 0, d?.Hypothese ?? 0, d?.Modele ?? 0, d?.RefImprime ?? "");
    }

    // Département d'un code postal français (2 premiers chiffres)
    private static string Departement(string codePostal) => codePostal.Length >= 2 && char.IsAsciiDigit(codePostal[0]) ? codePostal[..2] : "";

    // Enseignes de la société (compteurs PRT320F)
    public async Task<IReadOnlyList<string>> EnseignesAsync(int societe, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        var r = new List<string>();
        await using var rd = await Lire(cnx, "SELECT RENSEI FROM {b}.PRT320F WHERE RSOC = ? ORDER BY RENSEI", ct, societe);
        while (await rd.ReadAsync(ct)) r.Add(rd.Texte(0));
        return r;
    }

    // BL divers enregistré : null s'il n'existe pas ; refus s'il n'est pas un BL divers d'un seul élément
    public async Task<SaisieBlDivers?> DiversAsync(int societe, int numeroBl, CancellationToken ct)
    {
        await using var cnx = await OuvrirAsync(ct);
        SaisieBlDivers s;
        await using (var rd = await Lire(cnx, """
            SELECT h.HDEXAA, h.HDEXMM, h.HDEXJJ, h.HNADRE, h.HFJ, h.HRS, h.HRUE1, h.HRUE2, h.HCP, h.HVILLE, h.HDPT, h.HPAYS, h.HTYPL, h.HTRAN,
                   h.HTYPP, h.HBART, h.HOBS, h.HOBS2, h.HOBS3, h.HMENTI, h.HENS, e.HGENE, e.HCLIE, e.HNODOS, e.HCLEF, e.HQTELI, e.HNUC1,
                   e.HNUC2, e.HPDST, e.HNBPAL, e.HETABL, e.HBLFAC, e.HNOFAC,
                   (SELECT COUNT(*) FROM {b}.EXP010 x WHERE x.RSOC = h.RSOC AND x.HNOBL = h.HNOBL)
            FROM {b}.EXP009 h JOIN {b}.EXP010 e ON e.RSOC = h.RSOC AND e.HNOBL = h.HNOBL AND e.HNUM = 1
            WHERE h.RSOC = ? AND h.HNOBL = ?
            """, ct, societe, numeroBl))
        {
            if (!await rd.ReadAsync(ct)) return null;
            if (rd.Texte(30) != "D" || rd.Entier(33) != 1) throw new RegleMetierException("Ce BL n'est pas un BL divers d'un seul élément.");
            if (rd.Texte(31) == "O" || rd.Entier(32) != 0) throw new RegleMetierException("Ce BL est facturé : il ne peut plus être modifié.");
            var (gen, cli, dossier) = (rd.Entier(21), rd.Entier(22), rd.Entier(23));
            s = new SaisieBlDivers
            {
                NumeroBl = numeroBl,
                Tiers = dossier > 0 ? new TiersBlDivers(0, 0, dossier, rd.Texte(24), 0)
                    : gen == 0 ? new TiersBlDivers(0, 0, 0, "", cli) : new TiersBlDivers(gen, cli, 0, "", 0),
                DateExpedition = BaseDb2.Date(rd.Entier(0), rd.Entier(1), rd.Entier(2)) ?? DateOnly.FromDateTime(DateTime.Today),
                Adresse = new AdresseBl(rd.Entier(3), rd.Texte(4), rd.Texte(5), rd.Texte(6), rd.Texte(7), rd.Texte(8), rd.Texte(9), rd.Texte(10),
                    rd.Texte(11)),
                TypeLivraison = rd.Texte(12), Transporteur = rd.Entier(13), TypePort = rd.Texte(14), Bareme = rd.Entier(15),
                Observation1 = rd.Texte(16), Observation2 = rd.Texte(17), Observation3 = rd.Texte(18), Mention = rd.Texte(19),
                Enseigne = rd.Texte(20), Quantite = rd.Entier(25), Colis = rd.Entier(26) + rd.Entier(27), Poids = rd.Entier(28),
                Palettes = rd.Entier(29),
            };
        }
        await using (var rd = await Lire(cnx, "SELECT DDES, DDES2, DDES3 FROM {b}.EXP015 WHERE RSOC = ? AND DNBL = ? AND DNUM = 1 ORDER BY DNLIG",
                         ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) s.Designations.AddRange([rd.Texte(0), rd.Texte(1), rd.Texte(2)]);
        while (s.Designations.Count > 0 && s.Designations[^1] == "") s.Designations.RemoveAt(s.Designations.Count - 1);
        await using (var rd = await Lire(cnx, "SELECT HTTXT FROM {b}.EXP012 WHERE RSOC = ? AND HNOBL = ? ORDER BY HTNLIG", ct, societe, numeroBl))
            while (await rd.ReadAsync(ct)) s.TextePied.Add(rd.Texte(0));
        return s;
    }

    // Création ou modification d'un BL divers, en une transaction (verrous BL et dossier le temps de la validation)
    public async Task<int> EnregistrerDiversAsync(int societe, string profil, SaisieBlDivers s, TiersDivers tiers, ParametresBl p, CancellationToken ct)
    {
        profil = profil.ToUpperInvariant();
        await using var cnx = await OuvrirAsync(ct);
        var verrouBl = s.NumeroBl > 0 ? CleVerrouBl(societe, s.NumeroBl) : null;
        var verrouDossier = tiers.Dossier > 0 ? CleVerrouDossier(societe, tiers.Dossier, tiers.Cle) : null;
        if (verrouBl is not null) await PoserVerrouAsync(cnx, "BL", verrouBl, profil, "Ce BL est en cours de modification par", ct);
        int numero;
        try
        {
            if (verrouDossier is not null) await PoserVerrouAsync(cnx, "DOSSIER", verrouDossier, profil, "Ce dossier est en cours de modification par", ct);
            try
            {
                await using var tx = (OdbcTransaction)await cnx.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var valide = false;
                try
                {
                    numero = s.NumeroBl;
                    var a = s.Adresse;
                    if (numero == 0)
                    {
                        numero = await NumeroBlAsync(cnx, tx, societe, s.Enseigne, p.NumerotationParEnseigne, ct);
                        var recepisse = s.Enseigne == "" ? await CompteurAsync(cnx, tx, societe, "TR", ct) : 0;
                        await Executer(cnx, tx, """
                            INSERT INTO {b}.EXP009 (RSOC, HNOBL, HDEXAA, HDEXMM, HDEXJJ, HFJ, HRS, HRUE1, HRUE2, HCP, HVILLE, HOBS, HGENE, HCLIE,
                                HNCOLT, HNPALT, HPDSTT, HTRAN, HTYPL, HTYPP, HNBEPL, HEDBL, HEDETP, HENS, HBART, HNOREC, HBLGRP, HPROF, HDPT, HREV,
                                HNADRE, HPAYS, HOBSIN, HOBS2, HOBS3, HSTR, HMENTI)
                            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?, ?, 'N', ?, ?, 'N', ?, ?, ' ', ?, ?, 'N', ?)
                            """, ct, societe, numero, s.DateExpedition.Year, s.DateExpedition.Month, s.DateExpedition.Day, a.FormeJuridique,
                            a.RaisonSociale, a.Rue1, a.Rue2, a.CodePostal, a.Ville, s.Observation1, tiers.CompteGeneral, tiers.CompteClient,
                            s.Colis, s.Palettes, s.Poids, s.Transporteur, s.TypeLivraison, s.TypePort,
                            Oui(p.EditionBl, "N"), Oui(p.EditionPalettes, "N"), s.Enseigne, s.Bareme,
                            recepisse, profil, a.Departement, a.Numero, a.Pays, s.Observation2, s.Observation3, s.Mention);
                        await Executer(cnx, tx, """
                            INSERT INTO {b}.EXP010 (RSOC, HNOBL, HNUM, HNODOS, HCLEF, HETABL, HQTELI, HNUC1, HQTUC1, HUCDT1, HNUC2, HQTUC2, HUCDT2,
                                HNBPAL, HPDST, HETALI, HTYPBL, HEDETC, HBLFAC, HGENE, HCLIE, HREFCD, HDCDEA, HDCDEM, HDCDEJ, HNODEV, HNOHYP, HNOMOD,
                                HREFIM, HREP, HNOFAC, HFACTU, HNOBLO, HNUMO, HGENF, HCLIF, HFACTO)
                            VALUES (?, ?, 1, ?, ?, 'D', ?, ?, 0, 'C', 0, 0, 'P', ?, ?, 'T', 1, ?, 'N', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, 'N', 0, 0, 0, 0, 'N')
                            """, ct, societe, numero, tiers.Dossier, tiers.Cle, s.Quantite, s.Colis, s.Palettes, s.Poids,
                            Oui(p.EditionEtiquettes, "N"), tiers.CompteGeneral, tiers.CompteClient, tiers.ReferenceCommande,
                            tiers.DateCommande?.Year ?? 0, tiers.DateCommande?.Month ?? 0, tiers.DateCommande?.Day ?? 0, tiers.Devis, tiers.Hypothese,
                            tiers.Modele, tiers.RefImprime, tiers.Representant);
                    }
                    else
                    {
                        if (await Executer(cnx, tx, """
                                UPDATE {b}.EXP009 SET HDEXAA = ?, HDEXMM = ?, HDEXJJ = ?, HFJ = ?, HRS = ?, HRUE1 = ?, HRUE2 = ?, HCP = ?, HVILLE = ?,
                                       HOBS = ?, HOBS2 = ?, HOBS3 = ?, HTRAN = ?, HTYPL = ?, HTYPP = ?, HBART = ?, HDPT = ?, HNADRE = ?, HPAYS = ?,
                                       HMENTI = ?, HNCOLT = ?, HNPALT = ?, HPDSTT = ?
                                WHERE RSOC = ? AND HNOBL = ?
                                """, ct, s.DateExpedition.Year, s.DateExpedition.Month, s.DateExpedition.Day, a.FormeJuridique, a.RaisonSociale,
                                a.Rue1, a.Rue2, a.CodePostal, a.Ville, s.Observation1, s.Observation2, s.Observation3, s.Transporteur,
                                s.TypeLivraison, s.TypePort, s.Bareme, a.Departement, a.Numero, a.Pays, s.Mention, s.Colis, s.Palettes, s.Poids,
                                societe, numero) == 0)
                            throw new RegleMetierException($"Le BL {numero} n'existe plus.");
                        if (await Executer(cnx, tx, """
                                UPDATE {b}.EXP010 SET HQTELI = ?, HNUC1 = ?, HNUC2 = 0, HNBPAL = ?, HPDST = ?
                                WHERE RSOC = ? AND HNOBL = ? AND HNUM = 1 AND HETABL = 'D' AND HBLFAC <> 'O' AND HNOFAC = 0
                                """, ct, s.Quantite, s.Colis, s.Palettes, s.Poids, societe, numero) == 0)
                            throw new RegleMetierException("Ce BL vient d'être facturé : il ne peut plus être modifié.");
                        await Executer(cnx, tx, "DELETE FROM {b}.EXP015 WHERE RSOC = ? AND DNBL = ? AND DNUM = 1", ct, societe, numero);
                    }

                    // Désignations libres : trois par ligne EXP015, sans article
                    var designations = s.Designations.Select(x => x.Trim()).ToList();
                    while (designations.Count > 0 && designations[^1] == "") designations.RemoveAt(designations.Count - 1);
                    for (int i = 0, ligne = 1; i < designations.Count; i += 3, ligne++)
                        await Executer(cnx, tx, """
                            INSERT INTO {b}.EXP015 (RSOC, DNBL, DNUM, DNLIG, DART, DDES, DQTE, DIND, DCOLIS, DNUMLC, DETALI, DCLIG, DDES2, DDES3,
                                DEMBP1, DTYEMB, DCOLIM, DEXMEC, DPDSU)
                            VALUES (?, ?, 1, ?, ' ', ?, 0, ' ', 0, 0, ' ', ' ', ?, ?, 0, ' ', 0, 0, 0)
                            """, ct, societe, numero, ligne, Tronquer(designations[i], 40),
                            i + 1 < designations.Count ? Tronquer(designations[i + 1], 40) : "",
                            i + 2 < designations.Count ? Tronquer(designations[i + 2], 40) : "");
                    await EcrireTextePiedAsync(cnx, tx, societe, numero, s.TextePied, ct);
                    await tx.CommitAsync(ct);
                    valide = true;
                }
                finally
                {
                    if (!valide) await tx.RollbackAsync(CancellationToken.None);
                }
            }
            finally
            {
                if (verrouDossier is not null) await LeverVerrouAsync(cnx, "DOSSIER", verrouDossier, profil);
            }
        }
        finally
        {
            if (verrouBl is not null) await LeverVerrouAsync(cnx, "BL", verrouBl, profil);
        }
        await AuditAsync(cnx, societe, profil, numero, s.NumeroBl > 0 ? "MO" : "CR");
        return numero;
    }
}
