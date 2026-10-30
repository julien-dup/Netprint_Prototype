using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Compléments de la consultation (lot A, lecture seule) :
//   - texte libre (FEN_VisuTexteLibre) ;
//   - récapitulatif des sous-totaux (FEN_RecapSousTotaux, règle R25) ;
//   - tableau des coefficients (FEN_CalculRecap, règle R26).
// Lot B (écriture, mêmes tables que le WinDev) : échec (R8), documents (R9, R10), tags (R11).
// Lot C : tableau des prix (FEN_TableauPrix), récapitulatif PDF (R12, DEV100NAV).
// La société vient de la session ; la restriction représentant (R6) s'applique comme pour le détail.
// =====================================================================
internal sealed class ComplementsDevisService(
    ComplementsDevisDb2 donnees, ConsultationDevisDb2 consultation, IUtilisateurCourant utilisateur) : IComplementsDevis
{
    private const int TnumLeTout = 999999999;   // unité de vente « Le tout » (prix forfaitaire)

    public async Task<TexteLibreDevis?> LireTexteLibreAsync(CleDevis cle, int element = 0, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var lignes = await donnees.LireTexteLibreAsync(societe, cle, element, ct);
        return new TexteLibreDevis(entete, element, lignes);
    }

    // ------------------------------------------------------------------
    // Récapitulatif des sous-totaux (R25)
    // ------------------------------------------------------------------
    public async Task<RecapSousTotaux?> LireSousTotauxAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;

        var brut = await donnees.LireSousTotauxAsync(societe, cle, ct);
        var support = await donnees.LireValeurSupportAsync(societe, cle, ct);
        return new RecapSousTotaux(entete, CalculerSousTotaux(brut, support));
    }

    // Regroupement par code ST (une section, une matière et un lien de sous-traitance peuvent partager un code) ;
    // lignes sans code dans « DIVERS MATIERES / OPERATION / SOUS-TRAITANCE » (dans cet ordre, après les codes) ;
    // puis Support (valeur du papier) et Total.
    internal static List<LigneSousTotal> CalculerSousTotaux(IEnumerable<SousTotalBrut> brut, decimal? support)
    {
        var codes = new List<Cumul>();
        var divers = new List<Cumul>();
        foreach (var l in brut)
        {
            Cumul? c;
            if (l.Code == "")
            {
                var (ordre, libelle) = l.TypeLigne switch
                {
                    "M" => (10, "DIVERS MATIERES"),
                    "O" => (11, "DIVERS OPERATION"),
                    _ => (12, "DIVERS SOUS-TRAITANCE"),
                };
                c = divers.FirstOrDefault(d => d.Ordre == ordre);
                if (c is null) divers.Add(c = new Cumul { Ordre = ordre, Libelle = libelle });
            }
            else
            {
                c = codes.FirstOrDefault(d => d.Code == l.Code);
                if (c is null) codes.Add(c = new Cumul { Code = l.Code, Libelle = l.Libelle });
            }
            c.Fixes += l.Fixes;
            c.Variables += l.Variables;
            c.MillePlus += l.MillePlus;
        }

        var lignes = codes.Concat(divers.OrderBy(d => d.Ordre))
            .Select(c =>
            {
                var fixes = Math.Round(c.Fixes, 2);
                var variables = Math.Round(c.Variables, 2);
                return new LigneSousTotal(c.Libelle, fixes, variables, fixes + variables, Math.Round(c.MillePlus, 2));
            })
            .ToList();
        if (support is { } s) lignes.Add(new LigneSousTotal("Support", null, null, Math.Round(s, 2), null));

        lignes.Add(new LigneSousTotal("Total",
            lignes.Sum(l => l.FraisFixes ?? 0), lignes.Sum(l => l.FraisVariables ?? 0),
            lignes.Sum(l => l.Total), lignes.Sum(l => l.MillePlus ?? 0), EstTotal: true));
        return lignes;
    }

    // ------------------------------------------------------------------
    // Tableau des coefficients (R26)
    // ------------------------------------------------------------------
    public async Task<TableauCoefficients?> LireCoefficientsAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var brut = await donnees.LireCoefficientsAsync(societe, cle, ct);
        return CalculerCoefficients(entete, brut);
    }

    internal static TableauCoefficients CalculerCoefficients(EnteteComplement entete, IReadOnlyList<CoefficientsBrut> brut)
    {
        var premiere = brut.FirstOrDefault();
        var surPrTotal = premiere?.Base == "1";
        var unite = premiere?.Unite ?? "";
        var libelle = (surPrTotal ? "PR total" : "PRA") + (unite != "" ? $" ({unite})" : "");

        // Les coefficients sont ceux de la version ; le premier coefficient nul arrête la liste
        var coefficients = (premiere?.Coefficients ?? []).TakeWhile(c => c != 0).ToList();

        var lignes = brut.Select(l =>
        {
            var montant = surPrTotal ? l.PrTotal : l.Pra;
            // Base ramenée à l'unité de vente, arrondie à 2 décimales par défaut (ArrondiInférieur du WinDev).
            // ÉCART (validé 30/10/2026) : le WinDev testait TNUM <> 99999999 (8 chiffres) ; « Le tout » (999999999)
            // était donc calculé avec un diviseur absurde. Ici, « Le tout » = montant total, sans conversion.
            var base_ = l.Tnum == TnumLeTout ? montant
                : l.Quantite == 0 ? 0
                : montant * (l.Tnum != 0 ? l.Tnum : 1000) / l.Quantite;
            base_ = Math.Floor(base_ * 100) / 100;
            // Prix = base × coefficient, tronqué à 2 décimales (pas arrondi)
            var prix = coefficients.Select(c => Math.Truncate(base_ * c * 100) / 100).ToList();
            return new LigneCoefficients(l.Hypothese, l.Quantite, base_, prix);
        }).ToList();

        return new TableauCoefficients(entete, libelle, coefficients, lignes);
    }

    // ------------------------------------------------------------------
    // Échec (R8) : un seul par n° de devis, toutes versions confondues
    // ------------------------------------------------------------------
    public async Task<EchecDevis?> LireEchecAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var motifs = await donnees.LireMotifsEchecAsync(societe, ct);
        var echec = await donnees.LireEchecAsync(societe, cle.Numero, ct);
        return echec is { } e
            ? new EchecDevis(entete, true, e.Motif, BaseDb2.DateAAAAMMJJ(e.Date), e.Texte, motifs)
            : new EchecDevis(entete, false, "", null, "", motifs);
    }

    // Procédure Controle de FEN_SaisieEchec (mêmes messages)
    public async Task EnregistrerEchecAsync(CleDevis cle, SaisieEchec saisie, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) throw new RegleMetierException($"Le devis {cle.Libelle} n'existe pas ou ne vous est pas accessible.");

        var motif = (saisie.Motif ?? "").Trim().ToUpperInvariant();
        if (motif == "") throw new RegleMetierException("Le motif de l'échec doit être renseigné.");
        var motifs = await donnees.LireMotifsEchecAsync(societe, ct);
        if (!motifs.Any(m => m.Code == motif)) throw new RegleMetierException("Le motif de l'échec n'existe pas.");
        if (saisie.DateCloture is not { } date) throw new RegleMetierException("La date de clôture doit être renseignée.");
        var descriptif = (saisie.Descriptif ?? "").Trim();
        if (descriptif == "") throw new RegleMetierException("Le descriptif doit être renseigné.");
        if (descriptif.Length > LongueurDescriptifEchec)
            throw new RegleMetierException($"Le descriptif ne doit pas dépasser {LongueurDescriptifEchec} caractères.");

        await donnees.EnregistrerEchecAsync(societe, cle.Numero, motif, BaseDb2.AAAAMMJJ(date), descriptif, ct);
    }

    public const int LongueurDescriptifEchec = 800;   // D50006.F6TXT

    public async Task SupprimerEchecAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) throw new RegleMetierException($"Le devis {cle.Libelle} n'existe pas ou ne vous est pas accessible.");
        await donnees.SupprimerEchecAsync(societe, cle.Numero, ct);
    }

    // ------------------------------------------------------------------
    // Documents (R9, R10) : rattachés au n° de devis
    // ------------------------------------------------------------------
    public async Task<DocumentsDevis?> LireDocumentsAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var u = await utilisateur.ExigerAsync();
        return new DocumentsDevis(entete,
            await donnees.LireDocumentsAsync(societe, cle.Numero, ct),
            await donnees.PeutSupprimerDocumentsAsync(u.Profil, ct));
    }

    public async Task<DocumentDevis> AjouterDocumentAsync(CleDevis cle, string nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) throw new RegleMetierException($"Le devis {cle.Libelle} n'existe pas ou ne vous est pas accessible.");
        if (contenu.LongLength > LimitesDocuments.TailleMaximum)
            throw new RegleMetierException($"La taille du fichier {nomFichier} est supérieure à 7 Mo.");

        var (nom, extension) = NomDocument(nomFichier);
        var id = await donnees.AjouterDocumentAsync(societe, cle.Numero, nom, extension, contenu, ct);
        return new DocumentDevis(id, nom, DateTime.Now);
    }

    // Nom enregistré (FEN_Documents) : caractères | : \ / * ? " < > ' remplacés par des blancs, nom limité à
    // 80 caractères extension comprise (le nom est raccourci, l'extension gardée). DEDEXT = 5 premiers caractères
    // de l'extension (point compris).
    internal static (string Nom, string Extension) NomDocument(string nomFichier)
    {
        var fichier = Path.GetFileName(nomFichier.Replace('\\', '/'));
        var extension = Path.GetExtension(fichier);
        var nom = Path.GetFileNameWithoutExtension(fichier);
        foreach (var c in "|:\\/*?\"<>'") nom = nom.Replace(c, ' ');
        if (nom.Length + extension.Length > LimitesDocuments.LongueurNom)
            nom = nom[..Math.Max(1, LimitesDocuments.LongueurNom - extension.Length)];
        if (string.IsNullOrWhiteSpace(nom)) nom = "document";
        return (nom + extension, extension.Length > 5 ? extension[..5] : extension);
    }

    public async Task SupprimerDocumentAsync(CleDevis cle, int id, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) throw new RegleMetierException($"Le devis {cle.Libelle} n'existe pas ou ne vous est pas accessible.");
        var u = await utilisateur.ExigerAsync();
        if (!await donnees.PeutSupprimerDocumentsAsync(u.Profil, ct))
            throw new RegleMetierException("Vous n'êtes pas autorisé à supprimer des documents.");
        await donnees.SupprimerDocumentAsync(societe, cle.Numero, id, ct);
    }

    public async Task<FichierDocument?> LireFichierAsync(int id, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (await donnees.LireFichierAsync(u.Societe, id, ct) is not { } f) return null;
        var impose = await consultation.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        if (impose is { } rep && !await donnees.DevisDuRepresentantAsync(u.Societe, f.Devis, rep, ct)) return null;
        return new FichierDocument(f.Nom, f.Contenu);
    }

    // ------------------------------------------------------------------
    // Tags (R11) : liste commune TAG_DEVIS, association par devis / version / hypothèse (D50010)
    // ------------------------------------------------------------------
    public async Task<TagsDevis?> LireTagsAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        return new TagsDevis(entete, await donnees.LireTagsAsync(ct), await donnees.LireTagsDuDevisAsync(societe, cle, ct));
    }

    // BTN_Ajouter de FEN_Tags (mêmes messages). ÉCART : 20 caractères acceptés (le WinDev refusait dès 20,
    // alors que son message annonce « 20 caractères maximum ») ; libellé déjà présent : comparaison sans tenir
    // compte des majuscules.
    public async Task<TagDevis> CreerTagAsync(string libelle, CancellationToken ct = default)
    {
        await utilisateur.ExigerAsync();
        var tag = (libelle ?? "").Trim();
        if (tag == "") throw new RegleMetierException("Le tag doit être renseigné.");
        if (tag.Length > LimitesTags.Longueur) throw new RegleMetierException("Un tag doit avoir 20 caractères maximum.");
        var tous = await donnees.LireTagsAsync(ct);
        if (tous.Count >= LimitesTags.Nombre) throw new RegleMetierException("Le nombre de tag ne peut excèder 50.");
        if (tous.Any(t => string.Equals(t.Libelle, tag, StringComparison.OrdinalIgnoreCase)))
            throw new RegleMetierException("Ce tag est déjà présent en base.");
        return await donnees.CreerTagAsync(tag, ct);
    }

    public async Task EnregistrerTagsAsync(CleDevis cle, IReadOnlyCollection<int> tags, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) throw new RegleMetierException($"Le devis {cle.Libelle} n'existe pas ou ne vous est pas accessible.");
        var existants = (await donnees.LireTagsAsync(ct)).Select(t => t.Id).ToHashSet();
        await donnees.EnregistrerTagsAsync(societe, cle, tags.Where(existants.Contains), ct);
    }

    // ------------------------------------------------------------------
    // Tableau des prix (FEN_TableauPrix) : une colonne par hypothèse (quantité croissante), valeur et « 1000 + »
    // ------------------------------------------------------------------
    public async Task<TableauPrix?> LireTableauPrixAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var methode = await donnees.LireMethodeValorisationAsync(societe, ct);
        var d = await donnees.LireTableauPrixAsync(societe, cle, ct);
        return CalculerTableauPrix(entete, methode, d);
    }

    internal static TableauPrix CalculerTableauPrix(EnteteComplement entete, string methode, DonneesTableauPrix d)
    {
        var colonnes = d.Valorisations;
        var n = colonnes.Count;
        var position = colonnes.Select((v, i) => (v.Hypothese, i)).ToDictionary(x => x.Hypothese, x => x.i);
        var lignes = new List<LigneTableauPrix>();
        decimal?[] Vide() => new decimal?[n];
        void Ajouter(string libelle, StyleLignePrix style, decimal?[] valeurs, decimal?[]? mille = null, bool obs = false) =>
            lignes.Add(new LigneTableauPrix(libelle, style, valeurs, mille ?? Vide(), obs));
        void Blanc() => lignes.Add(new LigneTableauPrix("", StyleLignePrix.Vide, Vide(), Vide()));
        decimal?[] Par(Func<ValorisationBrute, decimal?> f) => colonnes.Select(f).ToArray();

        // --- Ateliers (une ligne par atelier « code libellé »), puis leur total ---
        var totalV = new decimal?[n]; var totalM = new decimal?[n];
        for (var i = 0; i < n; i++) { totalV[i] = 0; totalM[i] = 0; }
        foreach (var atelier in d.Ateliers.GroupBy(a => (a.Atelier, a.Libelle)))
        {
            var v = Vide(); var m = Vide();
            foreach (var a in atelier)
            {
                if (!position.TryGetValue(a.Hypothese, out var i)) continue;
                v[i] = (v[i] ?? 0) + a.Valeur; m[i] = (m[i] ?? 0) + a.MillePlus;
                totalV[i] += a.Valeur; totalM[i] += a.MillePlus;
            }
            Ajouter($"{atelier.Key.Atelier} {atelier.Key.Libelle}".Trim(), StyleLignePrix.Ligne, v, m);
        }
        Ajouter("Total ateliers", StyleLignePrix.Total, totalV, totalM);
        Blanc();

        // --- Support, matière consommable, sous-traitance, transport ---
        var support = Vide();
        foreach (var (h, valeur) in d.Support) if (position.TryGetValue(h, out var i)) support[i] = valeur;
        Ajouter("Support", StyleLignePrix.Ligne, support);
        foreach (var (type, libelle) in new[] { ("M", "Matière consommable"), ("S", "Sous-traitance"), ("T", "Transport") })
        {
            var v = Vide(); var m = Vide();
            foreach (var a in d.Autres.Where(a => a.Type == type))
                if (position.TryGetValue(a.Hypothese, out var i)) { v[i] = a.Valeur; m[i] = a.MillePlus; }
            Ajouter(libelle, StyleLignePrix.Ligne, v, m);
        }
        Blanc();

        // --- Valorisation ---
        // Quantité ramenée à l'unité de vente du devis ouvert (« pour … ») ; « Le tout » : prix pour le tout
        decimal? Q(ValorisationBrute v) =>
            d.TnumDevis == TnumLeTout ? 1 : (decimal)v.Quantite / (d.TnumDevis == 0 ? 1 : d.TnumDevis);
        decimal? Pour(decimal? montant, ValorisationBrute v) => Q(v) is { } q && q != 0 && montant is { } x ? x / q : null;
        // Prix de vente (saisis par unité de vente de l'hypothèse) ramenés à la quantité devisée
        decimal PourQuantite(decimal prix, ValorisationBrute v) =>
            v.Tnum != 0 && v.Tnum != TnumLeTout ? prix / v.Tnum * v.Quantite : prix;
        static decimal? Rapport(decimal a, decimal b) => b == 0 ? null : a / b;
        var pour = $"pour {d.Unite}".Trim();

        void PrixDeVente(string libelle, int prix, int mille)
        {
            Ajouter(libelle, StyleLignePrix.Total, Par(v => PourQuantite(v.M[prix], v)), Par(v => v.M[mille]), obs: true);
            Ajouter(pour, StyleLignePrix.Unite, Par(v => Pour(PourQuantite(v.M[prix], v), v)), Par(v => Pour(v.M[mille], v)), obs: true);
        }

        if (methode == "A")
        {
            // Frais généraux par poste : PRA, frais généraux, frais commerciaux, majorations (escompte, ristourne,
            // conditions de règlement, frais financiers, frais de stockage), PR total
            decimal Maj(ValorisationBrute v) => v.M[6] + v.M[8] + v.M[10] + v.M[12] + v.M[14];
            decimal MajM(ValorisationBrute v) => v.M[7] + v.M[9] + v.M[11] + v.M[13] + v.M[15];
            Ajouter("PRA", StyleLignePrix.Total, Par(v => v.M[0]), Par(v => v.M[1]));
            Ajouter(pour, StyleLignePrix.Unite, Par(v => Pour(v.M[0], v)), Par(v => Pour(v.M[1], v)));
            Blanc();
            Ajouter("Frais généraux", StyleLignePrix.Ligne, Par(v => v.M[2]), Par(v => v.M[3]));
            Ajouter("Frais commerciaux", StyleLignePrix.Ligne, Par(v => v.M[4]), Par(v => v.M[5]));
            Ajouter("Majorations", StyleLignePrix.Ligne, Par(v => Maj(v)), Par(v => MajM(v)));
            Blanc();
            Ajouter("PR TOTAL", StyleLignePrix.Total, Par(v => v.M[16]), Par(v => v.M[17]));
            Ajouter(pour, StyleLignePrix.Unite, Par(v => Pour(v.M[16], v)), Par(v => Pour(v.M[17], v)));
            Blanc();
            PrixDeVente("PV mini", 18, 19);
            PrixDeVente("PV conseillé", 20, 21);
            PrixDeVente("PV remis unité", 22, 23);
            Blanc();
            Ajouter("Coefficient", StyleLignePrix.Coefficient, Par(v => Rapport(PourQuantite(v.M[22], v), v.M[16])), obs: true);
        }
        else
        {
            // Méthode globale : les majorations comprennent aussi frais administratifs et frais de manutention
            decimal Maj(ValorisationBrute v) => v.M[6] + v.M[8] + v.M[10] + v.M[12] + v.M[14] + v.M[24] + v.M[26];
            decimal MajM(ValorisationBrute v) => v.M[7] + v.M[9] + v.M[11] + v.M[13] + v.M[15] + v.M[25] + v.M[27];
            Ajouter("Sous-total PRA", StyleLignePrix.Total, Par(v => v.M[0]), Par(v => v.M[1]));
            Ajouter("Majorations", StyleLignePrix.Ligne, Par(v => Maj(v)), Par(v => MajM(v)));
            Blanc();
            Ajouter("PRA", StyleLignePrix.Total, Par(v => v.M[0] + Maj(v)), Par(v => v.M[1] + MajM(v)));
            Ajouter(pour, StyleLignePrix.Unite, Par(v => Pour(v.M[0] + Maj(v), v)), Par(v => Pour(v.M[1] + MajM(v), v)));
            Blanc();
            Ajouter("Frais généraux", StyleLignePrix.Ligne, Par(v => v.M[2]), Par(v => v.M[3]));
            Ajouter("Frais commerciaux", StyleLignePrix.Ligne, Par(v => v.M[4]), Par(v => v.M[5]));
            Blanc();
            Ajouter("PR TOTAL", StyleLignePrix.Total, Par(v => v.M[16]), Par(v => v.M[17]));
            Ajouter(pour, StyleLignePrix.Unite, Par(v => Pour(v.M[16], v)), Par(v => Pour(v.M[17], v)));
            Blanc();
            // ÉCART : le WinDev affichait le « 1000 + » du PV conseillé « pour … » sans le diviser
            PrixDeVente("PV mini", 18, 19);
            PrixDeVente("PV conseillé", 20, 21);
            PrixDeVente("PV remis unité", 22, 23);
            Blanc();
            Ajouter("Coefficient", StyleLignePrix.Coefficient, Par(v => Rapport(PourQuantite(v.M[22], v), v.M[0] + Maj(v))), obs: true);
        }

        return new TableauPrix(entete, methode, d.Unite,
            colonnes.Select(v => v.Quantite).ToList(), colonnes.Select(v => v.Hypothese).ToList(),
            colonnes.Select(v => v.Observations).ToList(), lignes);
    }

    // ------------------------------------------------------------------
    // Récapitulatif PDF (R12) : DEV100NAV sur l'IBM i, PDF relu dans l'IFS
    // ------------------------------------------------------------------
    public async Task<FichierDocument?> EditerRecapitulatifAsync(CleDevis cle, CancellationToken ct = default)
    {
        // La clé entre dans une commande IBM i : elle doit être strictement conforme
        if (cle.Numero is < 1 or > 99999 || cle.Version is < 0 or > 9 || cle.Hypothese is < 0 or > 9
            || cle.TypeCout is not ("C" or "T"))
            return null;
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var pdf = await donnees.EditerRecapitulatifAsync(societe, cle, ct)
            ?? throw new RegleMetierException("Le récapitulatif du devis n'a pas été produit par l'IBM i (DEV100NAV).");
        return new FichierDocument($"Devis {cle.Numero:00000}-{cle.Version}-{cle.Hypothese}.pdf", pdf);
    }

    // ------------------------------------------------------------------
    // Bandeau commun + restriction représentant
    // ------------------------------------------------------------------
    private async Task<(int Societe, EnteteComplement? Entete)> EnteteAsync(CleDevis cle, CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var e = await donnees.LireEnteteAsync(u.Societe, cle, ct);
        if (e is null) return (u.Societe, null);

        var impose = await consultation.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        if (impose is not null && e.Representant != impose) return (u.Societe, null);

        var estClient = e.Rcli != 0;
        return (u.Societe, new EnteteComplement(
            cle, estClient,
            estClient ? $"{e.Rgen} {e.Rcli}" : e.Rprosp.ToString(),
            estClient ? e.Cnom : e.Drs,
            e.Dedes1, e.Dedes2));
    }

    private sealed class Cumul
    {
        public string Code { get; init; } = "";
        public int Ordre { get; init; }
        public string Libelle { get; init; } = "";
        public decimal Fixes { get; set; }
        public decimal Variables { get; set; }
        public decimal MillePlus { get; set; }
    }
}
