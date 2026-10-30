using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Modules.Ventes.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Consultation des devis : règles de FEN_ConsultationDevis (Controle, restriction représentant, couleurs, valeur)
// et mise en forme du détail de FEN_DetailDevis (onglets par atelier / par élément, sous-totaux de section,
// gâche, papier en bobine). Le SQL est dans Donnees/ConsultationDevisDb2, l'affichage dans Ecrans/.
// =====================================================================
internal sealed class ConsultationDevisService(ConsultationDevisDb2 donnees, IUtilisateurCourant utilisateur) : IConsultationDevis
{
    public const int Prepresse = 31;
    public const int Faconnage = 32;
    private const int TnumLeTout = 999999999;   // unité de vente « Le tout » (prix forfaitaire)

    public async Task<ContexteConsultationDevis> LireContexteAsync(CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return new(await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct));
    }

    // ------------------------------------------------------------------
    // Recherche
    // ------------------------------------------------------------------
    public async Task<ResultatRecherche<LigneListeDevis>> RechercherAsync(CriteresDevis criteres, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var c = criteres.Copier();

        // Restriction commerciale (R6) : le représentant imposé remplace toujours la saisie
        var impose = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        if (impose is not null) c.Representant = impose;
        if (c.CompteClient is > 0 && c.CompteGeneral is null or 0) c.CompteGeneral = u.CompteGeneral;

        Controler(c);

        var brut = await donnees.RechercherAsync(u.Societe, c, ct);
        return new(brut.Lignes.Select(VersLigneListe).ToList(), brut.Tronque);
    }

    // Procédure Controle de FEN_ConsultationDevis (mêmes messages)
    internal static void Controler(CriteresDevis c)
    {
        var aucunCritere = c.Numero is null or 0 && c.Etude is null or 0 && c.CompteClient is null or 0
            && c.Prospect is null or 0 && Vide(c.Profil) && Vide(c.Categorie) && Vide(c.SousCategorie)
            && c.DateDu is null && c.DateAu is null && Vide(c.Fabricant) && c.Representant is null or 0
            && c.QuantiteDe is null or 0 && c.QuantiteA is null or 0 && c.Dossier is null or 0;
        if (aucunCritere) throw new RegleMetierException("Vous devez renseigner au moins un critère.");
        if (c.CompteClient is > 0 && c.Prospect is > 0)
            throw new RegleMetierException("Vous ne pouvez pas renseigner le client et le prospect.");
        if (c.FournisseurSousTraitance is null or 0 && c.OperationSousTraitance is > 0)
            throw new RegleMetierException("Le fournisseur est obligatoire si l'opération est renseignée.");
        if (c.DateDu is { } du && c.DateAu is { } au && du > au)
            throw new RegleMetierException("La date de début doit être inférieure ou égale à la date de fin.");
        if (c.TypeDevis == "T" && c.CompteClient is > 0)
            throw new RegleMetierException("Le client ne doit pas être renseigné pour les devis types.");
        if (c.Epaisseur is > 0 && Vide(c.UniteEpaisseur))
            throw new RegleMetierException("L'unité de l'épaisseur doit être renseignée.");
        if (c.Epaisseur is null or 0 && !Vide(c.UniteEpaisseur))
            throw new RegleMetierException("L'unité de l'épaisseur ne doit pas être renseignée.");
    }

    private static LigneListeDevis VersLigneListe(LigneListeBrute l)
    {
        var estClient = l.Rgen != 0 && l.Rcli != 0;
        var formatFini = $"{l.Delrff:0.00}x{l.Dehaff:0.00}" + (l.De3cff != 0 ? $"x{l.De3cff:0.00}" : "");
        var formatOuvert = l.Delrfa != 0 && l.Dehafa != 0 ? $"{l.Delrfa:0.00}x{l.Dehafa:0.00}" : "";

        // Valeur du devis (R7b) : prix remis × quantité / diviseur de l'unité de vente, ou prix « pour le tout »
        decimal? valeur = null;
        if (l.Depvr > 0)
            valeur = l.Tnum == TnumLeTout ? l.Depvr : l.Tnum != 0 ? l.Depvr * l.Deqted / l.Tnum : null;

        return new LigneListeDevis(
            Cle: new CleDevis(l.Rdev, l.Rvers, l.Rhypo, l.Rcout),
            CodeClientProspect: estClient ? $"{l.Rgen} {l.Rcli}" : l.Rprosp.ToString(),
            NomClientProspect: estClient ? l.Cnom : l.Drs,
            DateDevis: BaseDb2.Date(l.Dedata, l.Dedatm, l.Dedatj),
            Quantite: l.Deqted,
            Designation1: l.Dedes1,
            Designation2: l.Dedes2,
            FormatFini: formatFini,
            FormatOuvert: formatOuvert,
            NbElements: l.Dencah,
            Norme: l.Denorm,
            Representant: $"{l.Derepr} {l.Inom}".Trim(),
            Fabricant: $"{l.Deinat} {l.Acnom}".Trim(),
            Contact: l.Decont,
            Profil: l.Deprof,
            PvMini: l.Depvm,
            PvConseille: l.Depvc,
            PvRemis: l.Depvr,
            DatePrix: BaseDb2.Date(l.Dedtpa, l.Dedtpm, l.Dedtpj),
            Categorie: $"{l.Decat} {l.LibCat}".Trim(),
            SousCategorie: $"{l.Descat} {l.LibSousCat}".Trim(),
            Modele: l.Demodp,
            Dossier: l.Rdoss != 0 ? $"{l.Rdoss:00000}/{l.Ccdcle}" : "",
            DossierPrecedent: l.Dedosp != 0 ? $"{l.Dedosp:00000}/{l.Declep}" : "",
            Etude: l.Deetud,
            DateEchec: l.F6datee != 0 ? BaseDb2.DateAAAAMMJJ(l.F6datee) : null,
            MotifEchec: l.F6motif,
            AvecOffre: l.NbOffres > 0,
            EnCommande: l.Detag == "*",
            NbDocuments: l.NbDocuments,
            ValeurDevis: valeur,
            ReferenceImprime: l.Derfim,
            DateDemande: l.Deddem != 0 ? BaseDb2.DateAAAAMMJJ(l.Deddem) : null,
            Observations: l.Deobsp);
    }

    // ------------------------------------------------------------------
    // Détail
    // ------------------------------------------------------------------
    public async Task<DetailDevis?> LireDetailAsync(CleDevis cle, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var brut = await donnees.LireDetailAsync(u.Societe, cle, ct);
        if (brut is null) return null;

        // Restriction commerciale appliquée aussi à l'ouverture directe d'un devis (le WinDev ne la vérifiait pas)
        var impose = await donnees.RepresentantImposeAsync(u.Societe, u.Profil, ct);
        if (impose is not null && brut.Entete.Representant != impose) return null;

        var e = brut.Entete;
        var estClient = e.Rcli != 0;
        var entete = new EnteteDetailDevis(
            EstClient: estClient,
            CodeTiers: estClient ? $"{e.Rgen} {e.Rcli}" : e.Rprosp.ToString(),
            NomTiers: estClient ? e.Cnom : e.Drs,
            Designation1: e.Dedes1,
            Designation2: e.Dedes2,
            Quantite: e.Deqted,
            NbElements: e.Dencah,
            FormatFini: $"{N(e.Delrff)} x {N(e.Dehaff)}" + (e.De3cff != 0 ? $" x {N(e.De3cff)}" : ""),
            FormatOuvert: e.Delrfa != 0 ? $"{N(e.Delrfa)} x {N(e.Dehafa)}" : "");

        var operations = brut.Lignes.Where(l => l.TypeLigne == "O").ToList();
        var matieres = brut.Lignes.Where(l => l.TypeLigne == "M").ToList();
        var sousTraitances = brut.Lignes.Where(l => l.TypeLigne == "S").ToList();

        return new DetailDevis(
            cle,
            entete,
            Ateliers: OngletsAteliers(operations),
            Matieres: LignesMatieres(matieres, avecTitres: true),
            Papiers: brut.Elements.Select(el => LignePapierAtelier(el, e.TauxGache, brut.PerteBobine)).ToList(),
            SousTraitances: LignesSousTraitance(sousTraitances, avecTitres: true),
            Transports: brut.Transports,
            FraisAnnexes: brut.Frais,
            Elements: Elements(brut));
    }

    // ---------- Vue par atelier (InitAtelier) : un onglet par atelier, un titre par élément, sous-totaux de section ----------
    private static List<OngletAtelier> OngletsAteliers(List<LigneFabBrute> operations)
    {
        var onglets = new List<OngletAtelier>();
        foreach (var groupe in GroupesConsecutifs(operations, l => l.Atelier))
        {
            var lignes = new List<LigneOperation>();
            var cumul = new CumulSection();
            int? elementPrecedent = null;
            string? sectionPrecedente = null;
            foreach (var l in groupe)
            {
                if (elementPrecedent != l.Element || Section(l) != sectionPrecedente) cumul.Fermer(lignes);
                if (elementPrecedent != l.Element)
                    lignes.Add(Titre(l.Element switch
                    {
                        Prepresse => "Prépresse",
                        Faconnage => "Façonnage",
                        _ => l.Sens != "" ? $"{l.Element} {l.Sens} {l.DesignationElement}" : $"{l.Element} {l.DesignationElement}",
                    }));
                sectionPrecedente = Section(l);
                elementPrecedent = l.Element;
                cumul.Ajouter(l);
                lignes.Add(Operation(l));
            }
            cumul.Fermer(lignes);
            onglets.Add(new OngletAtelier(groupe[0].LibAtelier != "" ? groupe[0].LibAtelier : groupe[0].Atelier, lignes));
        }
        return onglets;
    }

    // ---------- Vue par élément (InitElt, InitPapier, InitMatiere, InitStr, InitRub, InitOutil) ----------
    private static List<ElementDetail> Elements(DetailBrut brut)
    {
        // Éléments du devis (D60001) sans ligne ou avec au moins une ligne à résultat > 0, puis Prépresse et Façonnage
        var numeros = brut.Elements
            .Where(el => el.NbLignes == 0 || brut.Lignes.Any(l => l.Element == el.Element))
            .Select(el => el.Element)
            .Concat(brut.Lignes.Where(l => l.Element is Prepresse or Faconnage).Select(l => l.Element))
            .Distinct().OrderBy(n => n).ToList();

        var resultat = new List<ElementDetail>();
        foreach (var n in numeros)
        {
            var el = brut.Elements.FirstOrDefault(x => x.Element == n);
            var lignesElement = brut.Lignes.Where(l => l.Element == n).ToList();
            var titre = n switch
            {
                Prepresse => "Prépresse",
                Faconnage => "Façonnage",
                _ => el is null ? $"{n:00}"
                    : el.Sens != "" ? $"{n:00} - {el.Sens} - {el.Designation}" : $"{n:00} - {el.Designation}",
            };

            // Fabrication : opérations de l'élément par atelier puis référence, sous-totaux de section
            var fabrication = new List<LigneOperation>();
            var cumul = new CumulSection();
            string? sectionPrecedente = null;
            foreach (var l in lignesElement.Where(l => l.TypeLigne == "O").OrderBy(l => l.Atelier, StringComparer.Ordinal)
                         .ThenBy(l => l.Ref, StringComparer.Ordinal))
            {
                if (Section(l) != sectionPrecedente) cumul.Fermer(fabrication);
                sectionPrecedente = Section(l);
                cumul.Ajouter(l);
                fabrication.Add(Operation(l));
            }
            cumul.Fermer(fabrication);

            resultat.Add(new ElementDetail(
                Numero: n,
                Titre: titre,
                Description: el is not null && n is not (Prepresse or Faconnage) && lignesElement.Count > 0 ? Description(el) : "",
                Fabrication: fabrication,
                Rubriques: Rubriques(brut.Rubriques.Where(r => r.Element == n)),
                Papier: el is null ? null : PapierElementDetail(el, brut.Entete.TauxGache, brut.PerteBobine),
                Matieres: LignesMatieres(lignesElement.Where(l => l.TypeLigne == "M").ToList(), avecTitres: false),
                SousTraitances: LignesSousTraitance(lignesElement.Where(l => l.TypeLigne == "S").ToList(), avecTitres: false),
                Outil: el is not null && el.Outil != 0 && brut.Outils.TryGetValue(el.Outil, out var o) ? o : null,
                AvecTexteLibre: el is not null && el.NbTextesLibres > 0));
        }
        return resultat;
    }

    // Libellé descriptif de l'élément (LibOnglet du WinDev)
    private static string Description(ElementBrut el)
    {
        var s = "";
        if (el.QuantiteElement != 0) s = $"{el.QuantiteElement} ex ";
        if (el.FiniLargeur != 0)
        {
            s += $" Fmt fini {N(el.FiniLargeur)}x{N(el.FiniHauteur)}";
            if (el.FiniTroisiemeCote != 0) s += $"x{N(el.FiniTroisiemeCote)}";
        }
        if (el.OuvertLargeur != 0)
            s += (el.FiniLargeur != 0 ? " Ouv : " : " Fmt ouv : ") + $"{N(el.OuvertLargeur)}x{N(el.OuvertHauteur)}";
        s += "\n";
        if (el.Poses != 0) s += $" {el.Poses} poses";
        if (el.Recto > 0) s += $" {el.Recto}R°";
        if (el.Verso > 0) s += $" {el.Verso}V°";
        if (el.Recto == 0 && el.Verso == 0) s += " sans impression";
        if (el.Recto > 0 && el.Verso == 0) s += " seul";
        if (el.Basculees > 0) s += " bascule";
        if (el.Outil != 0) s += $" Outil n° {el.Outil}";
        if (el.Outil1 != 0) s += $" Outil n° {el.Outil1}";
        if (el.Outil2 != 0) s += $" Outil n° {el.Outil2}";
        return string.Join("\n", s.Split('\n').Select(p => p.Trim())).Trim();
    }

    // Rubriques et questions (InitRub) : la 1re ligne porte le libellé, les suivantes seulement si la question existe
    private static List<RubriqueReponse> Rubriques(IEnumerable<RubriqueBrute> rubriques)
    {
        var lignes = new List<RubriqueReponse>();
        foreach (var r in rubriques)
            for (var i = 0; i < 5; i++)
            {
                if (i == 0) lignes.Add(new RubriqueReponse(r.Libelle, r.Questions[0], N(r.Reponses[0])));
                else if (r.Questions[i] != "") lignes.Add(new RubriqueReponse("", r.Questions[i], N(r.Reponses[i])));
            }
        return lignes;
    }

    // Matières : lignes consécutives de même référence et même libellé regroupées (InitAtelier « M », InitMatiere)
    private static List<LigneMatiere> LignesMatieres(List<LigneFabBrute> lignes, bool avecTitres)
    {
        var resultat = new List<LigneMatiere>();
        int? elementPrecedent = null;
        string? refPrecedente = null, libPrecedent = null;
        foreach (var l in lignes)
        {
            if (elementPrecedent != l.Element)
            {
                elementPrecedent = l.Element;
                if (avecTitres) resultat.Add(new LigneMatiere(NatureLigne.Titre, "", TitreElement(l), 0, 0, 0, 0, ""));
                refPrecedente = libPrecedent = null;
            }
            var libelle = l.Ref != "999999" ? l.LibMatiere : l.LibelleLibre;   // 999999 = matière libre
            var millePlus = l.Valeur * l.ResultatMillePlus / l.Resultat / 100;
            if (refPrecedente != l.Ref || libPrecedent != libelle)
            {
                resultat.Add(new LigneMatiere(NatureLigne.Ligne, l.Ref, libelle, l.Resultat, l.PrixProportionnel, l.Valeur,
                    millePlus, l.Commentaire));
                refPrecedente = l.Ref;
                libPrecedent = libelle;
            }
            else
            {
                var d = resultat[^1];
                resultat[^1] = d with
                {
                    Quantite = d.Quantite + l.Resultat, Valeur = d.Valeur + l.Valeur, ValeurMillePlus = d.ValeurMillePlus + millePlus,
                };
            }
        }
        return resultat;
    }

    // Sous-traitance (InitAtelier « S », InitStr)
    private static List<LigneSousTraitance> LignesSousTraitance(List<LigneFabBrute> lignes, bool avecTitres)
    {
        var resultat = new List<LigneSousTraitance>();
        int? elementPrecedent = null;
        foreach (var l in lignes)
        {
            if (avecTitres && elementPrecedent != l.Element)
                resultat.Add(new LigneSousTraitance(NatureLigne.Titre, TitreElement(l), 0, 0, 0, 0, 0, 0, ""));
            elementPrecedent = l.Element;
            resultat.Add(new LigneSousTraitance(NatureLigne.Ligne, avecTitres ? "   " + l.LibelleLibre : l.LibelleLibre,
                l.Resultat, l.PrixProportionnel, l.PrixFixe, l.QuantiteProportionnelle, l.Valeur,
                l.Valeur * l.ResultatMillePlus / l.Resultat / 100, avecTitres ? "" : l.Commentaire));
        }
        return resultat;
    }

    // ---------- Papier : ligne de l'onglet Papier de la vue par atelier ----------
    private static LignePapier LignePapierAtelier(ElementBrut el, int tauxGacheDevis, decimal perteBobine)
    {
        // ÉCART : le WinDev prenait ici un taux de gâche du devis non initialisé (0) ; on prend celui du devis, comme la vue par élément
        var taux = el.GacheVariable > 0 ? 100 + el.GacheVariable : tauxGacheDevis + 100;
        var horsGache = (int)(el.Tours * 100m / taux);
        var sansPapier = el.Sorte == "*";
        return new LignePapier(
            el.Element, el.Designation, $"{el.Sorte} {el.LibSorte}".Trim(), $"{el.SousSorte} {el.LibSousSorte}".Trim(),
            el.Grammage, FormatPapier(el), el.Coloris, Provenance(el.Provenance), $"{el.LargeurTirage}x{el.HauteurTirage}",
            el.Pages, el.Recto, el.Verso, el.Basculees, el.GacheFixe, el.GacheVariable,
            sansPapier ? 0 : (int)el.Poids, sansPapier ? 0 : el.Metrage, el.Prix, el.UniteAchat,
            Math.Round(el.Valeur * CoefficientBobine(el, perteBobine).Coefficient, 2),
            el.Poses, el.GacheFixe + (el.Tours - horsGache), horsGache, el.GacheFixe + el.Tours, el.Conditionnement,
            el.Epaisseur, el.UniteEpaisseur);
    }

    // ---------- Papier : panneau « Support » de la vue par élément (InitPapier) ----------
    private static PapierElement PapierElementDetail(ElementBrut el, int tauxGacheDevis, decimal perteBobine)
    {
        var taux = el.GacheVariable > 0 ? 100 + el.GacheVariable : tauxGacheDevis + 100;
        var tauxTexte = el.GacheVariable > 0 ? $"{el.GacheVariable} %" : tauxGacheDevis > 0 ? $"{tauxGacheDevis} %" : "";
        var sansPapier = el.Sorte == "*";
        var poids = sansPapier ? 0 : el.Poids;

        // R13 : feuilles bonnes = arrondi supérieur(tours × 100 / taux) ; gâche proportionnelle = tours − bonnes
        var bonnes = (int)Math.Ceiling(el.Tours * 100m / taux);
        var gacheProportionnelle = (int)Math.Floor((decimal)(el.Tours - bonnes));
        var totalGache = el.GacheFixe + gacheProportionnelle;
        var total = bonnes + totalGache;
        decimal metrage = el.FeuillesParTour != 0 ? (decimal)total / el.FeuillesParTour : total;

        var prix = "";
        if (el.Prix > 0)
            prix = $"{el.Prix:0.00} / " + el.UniteAchat switch { "CM" => "cent m²", "ML" => "mètre linéaire", _ => el.UniteAchat };

        var bobine = el.FeuilleBobine == "B";
        var (coef, nbBobines) = CoefficientBobine(el, perteBobine);
        var valeur = bobine ? el.Valeur * coef : el.Valeur;
        decimal Ml(decimal x) => Math.Floor(x * el.HauteurTirage / 1000m);
        string Vide0(decimal x) => x == 0 ? "" : N(x);

        return new PapierElement(
            Reference: el.Papier != 0 ? el.Papier.ToString() : "",
            Nature: el.NatureSupport != 0 ? $"{el.LibNature} ({el.NatureSupport})" : "",
            Sorte: $"{el.Sorte} {el.LibSorte}".Trim(),
            SousSorte: $"{el.SousSorte} {el.LibSousSorte}".Trim(),
            GrammageEpaisseur: $"{el.Grammage} gr" + (el.Epaisseur != 0 ? $" {el.Epaisseur} {el.UniteEpaisseur}" : ""),
            Coloris: $"{el.Coloris} {el.LibColoris}".Trim(),
            Norme: el.Norme,
            Provenance: Provenance(el.Provenance),
            Bobine: bobine,
            GacheFixe: Vide0(el.GacheFixe),
            GachePourcentage: Vide0(gacheProportionnelle),
            TauxGache: tauxTexte,
            TotalGache: Vide0(totalGache),
            BonnesFeuilles: N(bonnes),
            Total: N(total),
            FormatTirage: $"{el.LargeurTirage}x{el.HauteurTirage}",
            FormatAppro: FormatPapier(el),
            Metrage: N(metrage),
            Poids: poids > 0 ? $"{poids:0.0} kg" : "",
            Conditionnement: el.Conditionnement != 0 ? el.Conditionnement + (bobine ? " ml" : "") : "",
            Prix: prix,
            Valeur: valeur > 0 ? $"{valeur:0.00} €" : "",
            NbBobines: bobine && nbBobines > 0 ? $"{nbBobines} bobine(s)" : "",
            GacheFixeMl: bobine ? N(Ml(el.GacheFixe)) : "",
            GachePourcentageMl: bobine ? N(Ml(gacheProportionnelle)) : "",
            TotalGacheMl: bobine ? N(Ml(el.GacheFixe) + Ml(gacheProportionnelle)) : "",
            BonnesFeuillesMl: bobine ? N(Ml(bonnes)) : "",
            TotalMl: bobine ? N(Ml(total)) : "",
            MetrageMl: bobine ? N(Ml(metrage)) : "");
    }

    // R14 : papier en bobine — coefficient de prix et nombre de bobines
    // (longueur utile d'une bobine = conditionnement − perte de fin de bobine AA0002.TPPERB)
    private static (decimal Coefficient, int NbBobines) CoefficientBobine(ElementBrut el, decimal perteBobine)
    {
        if (el.FeuilleBobine != "B" || el.Conditionnement <= perteBobine || el.HauteurTirage <= 0) return (1m, 0);
        var toursParBobine = Math.Floor((el.Conditionnement - perteBobine) / (el.HauteurTirage / 1000m));
        var tours = el.Tours + el.GacheFixe;
        if (toursParBobine <= 0 || tours <= 0) return (1m, 0);
        var nbBobines = tours / toursParBobine;
        var reste = tours % toursParBobine;
        var longueur = tours * el.HauteurTirage / 1000m;
        var coef = (Math.Truncate(nbBobines) * el.Conditionnement + reste * el.HauteurTirage / 1000m) / longueur;
        return (coef, (int)Math.Ceiling(nbBobines));
    }

    // ---------- Utilitaires ----------
    private static LigneOperation Operation(LigneFabBrute l) => new(
        NatureLigne.Ligne, "   " + $"{l.LibSection} {l.LibOperation}".Trim(), l.Base, l.Nombre, Heures(l.Temps), l.Cadence,
        (int)l.Quantite, l.Diviseur, Heures(l.Resultat), l.PrixMachine + l.PrixMainOeuvre, l.Valeur,
        l.ResultatMillePlus * (l.PrixMainOeuvre + l.PrixMachine) / 100, l.Commentaire);

    private static LigneOperation Titre(string libelle) => new(NatureLigne.Titre, libelle, 0, 0, "", 0, 0, 0, "", 0, 0, 0, "");

    private static string TitreElement(LigneFabBrute l) => l.Element switch
    {
        Prepresse => "Prépresse",
        Faconnage => "Façonnage",
        _ => $"{l.Element} {l.DesignationElement}",
    };

    private static string Section(LigneFabBrute l) => l.Ref.Length >= 3 ? l.Ref[..3] : l.Ref;

    // Sous-total d'une section quand elle compte plusieurs lignes (gras, bleu)
    private sealed class CumulSection
    {
        private int nb;
        private decimal resultat, valeur, millePlus;

        public void Ajouter(LigneFabBrute l)
        {
            nb++;
            resultat += l.Resultat;
            valeur += l.Valeur;
            millePlus += l.ResultatMillePlus * (l.PrixMainOeuvre + l.PrixMachine) / 100;
        }

        public void Fermer(List<LigneOperation> lignes)
        {
            if (nb > 1)
                lignes.Add(new LigneOperation(NatureLigne.SousTotal, "", 0, 0, "", 0, 0, 0, Heures(resultat), 0, valeur, millePlus, ""));
            nb = 0;
            resultat = valeur = millePlus = 0;
        }
    }

    // Heures affichées comme le WinDev : partie entière + « h » + 2 premières décimales (1,33 → « 1h33 »), vide si 0
    internal static string Heures(decimal v)
    {
        var h = Math.Truncate(v);
        var c = (int)Math.Round((v - h) * 100, MidpointRounding.AwayFromZero);
        if (c >= 100) { h += 1; c -= 100; }
        var s = $"{h}h{c:00}";
        return s == "0h00" ? "" : s;
    }

    private static string FormatPapier(ElementBrut el) =>
        el.HauteurPapier != 0 ? $"{el.LargeurPapier}x{el.HauteurPapier}" : $"Laize {el.LargeurPapier}";

    // R14b : provenance du papier
    private static string Provenance(string code) => code switch
    {
        "F" => "Fabrication", "N" => "Fournisseur", "S" => "Stock", "C" => "Client", _ => "",
    };

    private static string N(decimal d) => d.ToString("0.###");

    private static bool Vide(string? s) => string.IsNullOrWhiteSpace(s);

    private static IEnumerable<List<T>> GroupesConsecutifs<T>(IEnumerable<T> source, Func<T, string> cle)
    {
        List<T>? groupe = null;
        string? courant = null;
        foreach (var x in source)
        {
            var k = cle(x);
            if (groupe is null || k != courant)
            {
                if (groupe is not null) yield return groupe;
                groupe = [];
                courant = k;
            }
            groupe.Add(x);
        }
        if (groupe is not null) yield return groupe;
    }
}
