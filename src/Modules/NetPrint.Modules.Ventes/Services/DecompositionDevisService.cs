using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Lot E : décomposition analytique et prix remis (ex-FEN_Decomposition), règles R15 à R24.
// Écarts avec le WinDev (validés le 30/10/2026, docs/compte-rendu-decomposition.md) :
//   A1 toutes les lignes D60002 fusionnées à l'affichage reçoivent la ventilation (pas seulement la première) ;
//   A2 pseudo-éléments 31 Prépresse / 32 Façonnage (la branche DLLNew lisait 16 / 17) ;
//   A4 un total des % différent de 100 est refusé (le WinDev acceptait plus de 100) ;
//      sur une ligne ventilée sur plusieurs chapitres, chaque chapitre doit avoir son % (le WinDev ne contrôlait
//      pas le premier, qui était alors compté deux fois à l'impression) ;
//   A6 écriture en transaction ; A7 SQL paramétré ; Q6 refus si le devis a été modifié par ailleurs.
// La restriction représentant (R6) s'applique comme pour les autres compléments.
// =====================================================================
internal sealed class DecompositionDevisService(
    DecompositionDevisDb2 donnees, ComplementsDevisDb2 complements, ConsultationDevisDb2 consultation,
    IUtilisateurCourant utilisateur) : IDecompositionDevis
{
    private const int LeTout = LimitesDecomposition.TnumLeTout;
    private const int Chapitres = LimitesDecomposition.Chapitres;
    private const decimal PrixMax = 9_999_999.99m;   // D5000F.DEPVR… : S 9 2

    private const string MessageConflit =
        "Le devis a été modifié par ailleurs depuis l'ouverture de la décomposition. " +
        "Fermez la fenêtre puis rouvrez-la pour repartir des données à jour.";

    // ------------------------------------------------------------------
    // Lecture
    // ------------------------------------------------------------------
    public async Task<DecompositionDevis?> LireAsync(CleDevis cle, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) return null;
        var d = await donnees.LireAsync(societe, cle, ct);
        if (d is null) return null;

        // Unité du devis absente de la table UV : on la garde dans la liste pour ne pas la perdre
        var unites = d.Unites;
        if (d.Entete.Unite != "" && unites.All(u => u.Code != d.Entete.Unite))
            unites.Add(new UniteVente(d.Entete.Unite, d.Entete.Unite, d.Entete.Tnum));

        var m = d.Entete.M;
        var fg = d.FraisGeneraux;
        var parametres = new ParametresDecomposition(d.Methode, d.Entete.Quantite,
            Pra: m[0], PrixRevient: m[1], Commission: m[2], Escompte: m[3], Ristourne: m[4], MajorationReglement: m[5],
            FraisFinanciers: m[6], FraisStockage: m[7], FraisAdministratifs: m[8], FraisManutention: m[9],
            FgPapier: fg[0], FgMainOeuvre: fg[1], FgMachine: fg[2], FgSousTraitance: fg[3], FgMatiere: fg[4]);

        return new DecompositionDevis(entete, d.Entete.PrixRemis, d.Entete.Unite, unites,
            ConstruireLegende(d.Legende), ConstruireArbre(d), parametres);
    }

    // D50007 : ligne 0 = titre du chapitre, lignes 1 à 99 = valeurs. La lettre donne la position du chapitre.
    internal static List<ChapitreLegende> ConstruireLegende(IEnumerable<(string Chapitre, int Ligne, string Texte)> lignes)
    {
        var titres = new string[Chapitres];
        var valeurs = Enumerable.Range(0, Chapitres).Select(_ => new List<string>()).ToArray();
        var nombre = 0;
        foreach (var (chapitre, ligne, texte) in lignes)
        {
            var i = chapitre.Length == 1 ? LimitesDecomposition.Lettres.IndexOf(chapitre[0]) : -1;
            if (i < 0 || ligne < 0 || ligne > LimitesDecomposition.Valeurs) continue;
            if (ligne == 0) titres[i] = texte;
            else
            {
                while (valeurs[i].Count < ligne) valeurs[i].Add("");
                valeurs[i][ligne - 1] = texte;
            }
            nombre = Math.Max(nombre, i + 1);
        }
        return [.. Enumerable.Range(0, nombre).Select(i => new ChapitreLegende(titres[i] ?? "", valeurs[i]))];
    }

    // Arbre de FEN_Decomposition : élément → papier, Matières → matière, Machines → section → opération,
    // Sous-traitances → lien ; puis 31 Prépresse, 32 Façonnage et Transport.
    // Des lignes consécutives identiques (même opération ou matière, même commentaire) sont fusionnées.
    internal static List<LigneDecomposition> ConstruireArbre(DonneesDecomposition d)
    {
        var noeuds = new List<Noeud>();
        int Ajouter(Noeud n) { n.Id = noeuds.Count + 1; noeuds.Add(n); return n.Id; }

        var fabrication = d.Lignes.ToLookup(l => l.Element);
        foreach (var p in d.Papiers)
        {
            var element = Ajouter(new Noeud(null, 0, NatureLigneDecomposition.Element, $"{p.Element} {p.Designation}".Trim()));
            var (coef, _) = CalculBobine.Coefficient(p.FeuilleBobine, p.Conditionnement, d.PerteBobine, p.HauteurTirage,
                p.Tours + p.GacheFixe);
            var libelle = string.Join(" ", new[] { p.Sorte, p.SousSorte, p.Grammage, p.Coloris, $"{p.Laize}x{p.Hauteur}" }
                .Where(t => t != ""));
            Ajouter(new Noeud(element, 1, NatureLigneDecomposition.Papier, libelle)
            {
                Table = TableVentilation.Papier, Element = p.Element, Valeur = Math.Round(p.Valeur * coef, 2),
                Valeurs = p.Valeurs, Pourcents = p.Pourcents, PapierStock = p.Provenance == "S",
            });
            AjouterFabrication(element, p.Element, fabrication[p.Element]);
        }

        foreach (var (numero, titre) in new[] { (31, "31 Prépresse"), (32, "32 Façonnage") })
        {
            if (!fabrication[numero].Any()) continue;
            var element = Ajouter(new Noeud(null, 0, NatureLigneDecomposition.Element, titre));
            AjouterFabrication(element, numero, fabrication[numero]);
        }

        if (d.Port != 0)
            Ajouter(new Noeud(null, 0, NatureLigneDecomposition.Transport, "Transport")
            {
                Table = TableVentilation.Transport, Valeur = d.Port,
                Valeurs = d.Transport?.Valeurs ?? new int[Chapitres], Pourcents = d.Transport?.Pourcents ?? new int[Chapitres],
            });

        return [.. noeuds.Select(n => n.EnLigne())];

        void AjouterFabrication(int element, int numero, IEnumerable<LigneFabricationDecompositionBrute> lignes)
        {
            int? matieres = null, machines = null, sousTraitances = null, section = null;
            string? codeSection = null, cleFusion = null;
            Noeud? derniere = null;
            foreach (var l in lignes)
            {
                switch (l.Type)
                {
                    case "O":
                        machines ??= Ajouter(new Noeud(element, 1, NatureLigneDecomposition.Rubrique, "Machines"));
                        var sect = l.Ref.Length >= 3 ? l.Ref[..3] : l.Ref;
                        if (sect != codeSection)
                        {
                            section = Ajouter(new Noeud(machines, 2, NatureLigneDecomposition.Section, $"{sect} {l.LibSection}".Trim()));
                            codeSection = sect;
                            cleFusion = null;
                        }
                        var operation = l.Operation != 0 ? l.Operation.ToString()
                            : l.Ref.Length >= 6 ? l.Ref.Substring(3, 3) : "";
                        Feuille($"O|{sect}|{operation}|{l.Commentaire}", section!.Value, 3,
                            NatureLigneDecomposition.Operation, $"{operation} {l.LibOperation}".Trim(), l);
                        break;
                    case "M":
                        matieres ??= Ajouter(new Noeud(element, 1, NatureLigneDecomposition.Rubrique, "Matières"));
                        var diverse = l.Ref.Trim() == "999999";
                        var libelle = diverse ? l.LibelleRef : l.LibMatiere;
                        Feuille($"M|{l.Ref.Trim()}|{libelle}|{l.Commentaire}", matieres.Value, 2,
                            NatureLigneDecomposition.Matiere, $"{(diverse ? "999999" : l.Matiere.ToString())} {libelle}".Trim(), l);
                        break;
                    case "S":
                        sousTraitances ??= Ajouter(new Noeud(element, 1, NatureLigneDecomposition.Rubrique, "Sous-traitances"));
                        Feuille(null, sousTraitances.Value, 2, NatureLigneDecomposition.SousTraitance, l.LibelleRef, l);
                        break;
                }
            }

            // cle null : jamais fusionnée (sous-traitance)
            void Feuille(string? cle, int parent, int niveau, NatureLigneDecomposition nature, string libelle,
                LigneFabricationDecompositionBrute l)
            {
                if (cle is not null && cle == cleFusion && derniere is not null)
                {
                    derniere.Valeur += l.Valeur;
                    derniere.Quantite += l.Quantite;
                    derniere.Lignes.Add(l.Ligne);
                    return;
                }
                derniere = new Noeud(parent, niveau, nature, libelle)
                {
                    Commentaire = l.Commentaire, Table = TableVentilation.Fabrication, Element = numero, Valeur = l.Valeur,
                    Valeurs = l.Valeurs, Pourcents = l.Pourcents, Quantite = l.Quantite, PrixMachine = l.PrixMachine,
                    PrixMainOeuvre = l.PrixMainOeuvre,
                };
                derniere.Lignes.Add(l.Ligne);
                Ajouter(derniere);
                cleFusion = cle;
            }
        }
    }

    private sealed class Noeud(int? parent, int niveau, NatureLigneDecomposition nature, string libelle)
    {
        public int Id { get; set; }
        public string Commentaire { get; init; } = "";
        public TableVentilation? Table { get; init; }
        public int Element { get; init; }
        public List<int> Lignes { get; } = [];
        public decimal Valeur { get; set; }
        public int[] Valeurs { get; init; } = new int[Chapitres];
        public int[] Pourcents { get; init; } = new int[Chapitres];
        public bool PapierStock { get; init; }
        public decimal Quantite { get; set; }
        public decimal PrixMachine { get; init; }
        public decimal PrixMainOeuvre { get; init; }

        public LigneDecomposition EnLigne() => new(
            Id, parent, niveau, nature, libelle, Commentaire,
            Table is { } t ? new CibleVentilation(t, Element, [.. Lignes]) : null,
            Valeur, Valeurs, Pourcents, PapierStock, Quantite, PrixMachine, PrixMainOeuvre);
    }

    // ------------------------------------------------------------------
    // Calcul (procédure Calculs) : R20 à R23
    // ------------------------------------------------------------------
    public CalculDecomposition Calculer(DecompositionDevis decomposition, decimal prixRemis, string unite) =>
        CalculerDecomposition(decomposition, prixRemis, unite);

    internal static CalculDecomposition CalculerDecomposition(DecompositionDevis decomposition, decimal prixRemis, string unite)
    {
        var p = decomposition.Parametres;
        var tnum = decomposition.Unites.FirstOrDefault(u => u.Code == unite)?.Tnum ?? 0;
        var leTout = tnum == LeTout || tnum <= 0;

        // PV remis total (pour la quantité devisée)
        var pvTotal = leTout ? prixRemis : Diviser(prixRemis * p.Quantite, tnum);
        var globale = p.MethodeValorisation == "B";
        var coefGlobal = Diviser(pvTotal, p.Pra + p.Escompte + p.Ristourne + p.MajorationReglement + p.FraisFinanciers
                                          + p.FraisStockage + p.FraisAdministratifs + p.FraisManutention);
        var quotePart = Diviser(p.Commission + p.Ristourne + p.Escompte + p.MajorationReglement, p.Pra);

        var montants = new Dictionary<int, (decimal Pr, decimal Pv, decimal Pvu)>();
        decimal sommePr = 0, sommePv = 0, sommePvu = 0;
        LigneDecomposition? derniere = null;
        foreach (var l in decomposition.Lignes)
        {
            if (!l.EstFeuille) { montants[l.Id] = (0, 0, 0); continue; }
            decimal pr, pv = 0, pvu = 0;
            if (prixRemis != 0)
            {
                if (globale)
                {
                    pr = l.Valeur;
                    pv = l.Valeur * coefGlobal;
                }
                else
                {
                    pr = CoutMajore(l, p) + l.Valeur * quotePart;
                    pv = Diviser(pr * pvTotal, p.PrixRevient);
                }
                pvu = leTout ? pv : Diviser(pv * tnum, p.Quantite);
            }
            else
            {
                pr = globale ? l.Valeur : 0;
            }
            montants[l.Id] = (pr, pv, pvu);
            sommePr += pr; sommePv += pv; sommePvu += pvu;
            derniere = l;
        }

        var totalPr = globale ? p.Pra : p.PrixRevient;
        // R22 : l'écart avec les totaux du devis est imputé sur la dernière ligne
        if (derniere is not null)
        {
            var (pr, pv, pvu) = montants[derniere.Id];
            if (sommePr != 0) pr += totalPr - sommePr;
            if (sommePv != 0) pv += pvTotal - sommePv;
            if (sommePvu != 0) pvu += prixRemis - sommePvu;
            montants[derniere.Id] = (pr, pv, pvu);
        }

        // Cumuls des nœuds : parcours à l'envers (les descendants suivent toujours leur nœud)
        for (var i = decomposition.Lignes.Count - 1; i >= 0; i--)
        {
            var l = decomposition.Lignes[i];
            if (l.Parent is not { } parent) continue;
            var (pr, pv, pvu) = montants[l.Id];
            var (ppr, ppv, ppvu) = montants[parent];
            montants[parent] = (ppr + pr, ppv + pv, ppvu + pvu);
        }

        decimal? coefficient = prixRemis == 0 ? null
            : globale ? coefGlobal
            : Diviser(pvTotal, p.PrixRevient + p.Escompte + p.Ristourne + p.MajorationReglement + p.FraisFinanciers + p.FraisStockage);

        return new CalculDecomposition(
            montants.ToDictionary(m => m.Key, m => new MontantsDecomposition(m.Value.Pr, m.Value.Pv, m.Value.Pvu)),
            totalPr, pvTotal, coefficient, leTout ? LeTout : tnum,
            leTout ? "PV pour le tout" : $"PV unitaire pour {tnum:N0}");
    }

    // Coût direct majoré des frais généraux de la société (AA000Z.CKFG1 à CKFG5), méthodes autres que B
    private static decimal CoutMajore(LigneDecomposition l, ParametresDecomposition p) => l.Nature switch
    {
        NatureLigneDecomposition.Papier => l.PapierStock ? l.Valeur : l.Valeur * (1 + p.FgPapier / 100),
        NatureLigneDecomposition.Matiere => l.Valeur * (1 + p.FgMatiere / 100),
        NatureLigneDecomposition.SousTraitance => l.Valeur * (1 + p.FgSousTraitance / 100),
        NatureLigneDecomposition.Transport => l.Valeur,
        _ => l.Quantite * l.PrixMachine * (1 + p.FgMachine / 100) + l.Quantite * l.PrixMainOeuvre * (1 + p.FgMainOeuvre / 100),
    };

    private static decimal Diviser(decimal a, decimal b) => b == 0 ? 0 : a / b;

    // ------------------------------------------------------------------
    // Contrôle (procédure Controle) : R18
    // ------------------------------------------------------------------
    public ErreurDecomposition? Controler(IReadOnlyList<ChapitreLegende> legende, IEnumerable<VentilationLigne> lignes) =>
        ControlerVentilation(legende, lignes);

    internal static ErreurDecomposition? ControlerVentilation(IReadOnlyList<ChapitreLegende> legende, IEnumerable<VentilationLigne> lignes)
    {
        var n = Math.Min(legende.Count, Chapitres);
        foreach (var l in lignes)
        {
            if (l.Valeurs.Count != Chapitres || l.Pourcents.Count != Chapitres)
                return new ErreurDecomposition(l.Id, null, "La ventilation de la ligne est incomplète.");
            for (var j = 0; j < Chapitres; j++)
            {
                if (l.Valeurs[j] is < 0 or > LimitesDecomposition.Valeurs)
                    return new ErreurDecomposition(l.Id, j, "La valeur n'existe pas dans la légende.");
                if (l.Pourcents[j] is < 0 or > LimitesDecomposition.PourcentMax)
                    return new ErreurDecomposition(l.Id, j, "Le pourcentage doit être compris entre 1 et 99.");
            }

            var nombre = 0;
            for (var j = 0; j < n; j++) if (l.Valeurs[j] != 0) nombre++;
            var cumul = 0;
            for (var j = 0; j < n; j++)
            {
                var valeur = l.Valeurs[j];
                var pourcent = l.Pourcents[j];
                if (pourcent != 0 && valeur == 0)
                    return new ErreurDecomposition(l.Id, j, "La valeur doit être renseignée.");
                if (valeur != 0)
                {
                    if (valeur > legende[j].Valeurs.Count)
                        return new ErreurDecomposition(l.Id, j, "La valeur n'existe pas dans la légende.");
                    if (nombre > 1 && pourcent == 0)
                        return new ErreurDecomposition(l.Id, j, "Le pourcentage doit être renseigné.");
                }
                cumul += pourcent;
            }
            if (cumul != 0 && cumul != 100)
                return new ErreurDecomposition(l.Id, 0, "La somme des pourcentages doit être égale à 100.");
            if (nombre == 0)
                return new ErreurDecomposition(l.Id, 0, "Au moins une valeur doit être renseignée.");
        }
        return null;
    }

    // Légende (contrôle de FEN_Legendes + tailles des colonnes de D50007)
    internal static string? ControlerLegende(IReadOnlyList<ChapitreLegende> legende)
    {
        if (legende.Count > Chapitres) return $"La légende compte au plus {Chapitres} chapitres.";
        foreach (var c in legende)
        {
            if (string.IsNullOrWhiteSpace(c.Titre)) return "La désignation doit être renseignée.";
            if (c.Valeurs.Count > LimitesDecomposition.Valeurs)
                return $"Un chapitre compte au plus {LimitesDecomposition.Valeurs} valeurs.";
            if (c.Titre.Length > LimitesDecomposition.LongueurTexte || c.Valeurs.Any(v => v.Length > LimitesDecomposition.LongueurTexte))
                return $"Un libellé de la légende ne doit pas dépasser {LimitesDecomposition.LongueurTexte} caractères.";
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Validation (BTN_Valider) : R19
    // ------------------------------------------------------------------
    public async Task<PrixDevis> ValiderAsync(CleDevis cle, SaisieDecomposition saisie, CancellationToken ct = default)
    {
        var (societe, entete) = await EnteteAsync(cle, ct);
        if (entete is null) throw new RegleMetierException("Le devis n'existe pas ou ne vous est pas accessible.");

        if (saisie.PrixRemis is < 0m or > PrixMax) throw new RegleMetierException("Le prix de vente remis est invalide.");
        var prixRemis = Math.Round(saisie.PrixRemis, 2);
        var unites = await donnees.LireUnitesAsync(societe, ct);
        var unite = unites.FirstOrDefault(u => u.Code == saisie.Unite.Trim())
                    ?? throw new RegleMetierException("L'unité de vente doit être renseignée.");

        var legende = saisie.Legende
            .Select(c => new ChapitreLegende(c.Titre.Trim(), [.. c.Valeurs.Select(v => v.Trim())]))
            .ToList();
        if (ControlerLegende(legende) is { } erreurLegende) throw new RegleMetierException(erreurLegende);
        if (ControlerVentilation(legende, saisie.Lignes) is { } erreur) throw new RegleMetierException(erreur.Message);

        var actuel = await donnees.LirePrixAsync(societe, cle, ct)
                     ?? throw new RegleMetierException("Le devis n'existe pas ou ne vous est pas accessible.");
        if (actuel.Depvr != saisie.PrixRemisLu || actuel.Deunit != saisie.UniteLue.Trim())
            throw new RegleMetierException(MessageConflit);

        // Prix mini et conseillés convertis dans la nouvelle unité (« le tout » = quantité devisée)
        var nouveau = unite.Tnum == LeTout ? actuel.Quantite : unite.Tnum;
        var ancien = actuel.Tnum == LeTout ? actuel.Quantite : actuel.Tnum;
        decimal Convertir(decimal v) => ancien <= 0 || nouveau <= 0 ? v : Math.Round(v * nouveau / ancien, 2);
        var pvMini = Convertir(actuel.Depvm);
        var pvMiniMillePlus = Convertir(actuel.Depvmm);
        var pvConseille = Convertir(actuel.Depvc);
        var pvConseilleMillePlus = Convertir(actuel.Depvcm);
        if (new[] { pvMini, pvMiniMillePlus, pvConseille, pvConseilleMillePlus }.Any(v => Math.Abs(v) > PrixMax))
            throw new RegleMetierException("Les prix mini et conseillés convertis dans cette unité de vente sont trop grands.");

        var ecriture = new EcritureDecomposition(prixRemis, unite.Code, pvMini, pvMiniMillePlus, pvConseille,
            pvConseilleMillePlus, actuel, legende, saisie.Lignes);
        if (!await donnees.EnregistrerAsync(societe, cle, ecriture, ct)) throw new RegleMetierException(MessageConflit);

        return new PrixDevis(pvMini, pvConseille, prixRemis);
    }

    // ------------------------------------------------------------------
    // Bandeau commun + restriction représentant (comme ComplementsDevisService)
    // ------------------------------------------------------------------
    private async Task<(int Societe, EnteteComplement? Entete)> EnteteAsync(CleDevis cle, CancellationToken ct)
    {
        var u = await utilisateur.ExigerAsync();
        var e = await complements.LireEnteteAsync(u.Societe, cle, ct);
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
}
