using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Saisie d'un BL produit fini / négoce (EXP010, cahier des charges lot 1) : contrôles du dossier, lignes à livrer, saisie,
// validation, en création, en ajout d'un dossier à un BL existant et en modification d'un élément ; suppression d'un élément.
// Société et profil de la session. Pas de droit PRT000P1 (EXP010 n'en contrôle pas). Dépôt principal.
// ÉCARTS / À VENIR (docs/points-en-attente.md) : source OPR, port et imputation A20005, sous-références, palettes (BL issus
// de palettes non modifiables ici), dépôts autorisés (EXPDEP), sous-traitance synchronisée, verrou du BL posé seulement le
// temps de la validation (pas pendant toute la saisie).
// =====================================================================
internal sealed class SaisieBlService(SaisieBlDb2 donnees, IUtilisateurCourant utilisateur) : ISaisieBl
{
    public async Task<PreparationBl> PreparerAsync(int dossier, string cle, int numeroBl = 0, bool opr = false, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (d, _) = await DossierControleAsync(u.Societe, dossier, cle, ct);
        IReadOnlyList<LigneProposee> lignes = await donnees.LignesAsync(u.Societe, d, ct, opr);
        if (opr && lignes.Count == 0) throw new RegleMetierException("Plus aucune ligne dans l'OPR.");
        if (numeroBl > 0)
        {
            var bl = await BlPourAjoutAsync(u.Societe, numeroBl, d, ct);
            lignes = [.. lignes.Where(l => ReglesSaisieBl.VaAuBl(l, bl.Adresse, bl.CompteGeneralFacture, bl.CompteClientFacture))];
            if (lignes.Count == 0)
                throw new RegleMetierException($"Ce dossier n'a pas de ligne à livrer au destinataire et au client facturé du BL {numeroBl}.");
        }
        if (lignes.Count == 0) throw new RegleMetierException("Ce dossier n'a plus de ligne à livrer.");
        return new PreparationBl(d.Dossier, d.Cle, d.Type, d.Designation, d.CompteGeneral, d.CompteClient, d.NomClient, d.Enseigne, lignes);
    }

    // Contrôles C1 à C6 du cahier des charges (§ 4.1)
    private async Task<(DossierBl Dossier, ParametresBl Parametres)> DossierControleAsync(int societe, int dossier, string cle, CancellationToken ct)
    {
        cle = (cle ?? "").Trim().ToUpperInvariant();
        var d = await donnees.DossierAsync(societe, dossier, cle, ct)
            ?? throw new RegleMetierException("Ce dossier n'existe pas.");
        if (d.Type == "1") throw new RegleMetierException("BL de fabrication non disponible dans cette version.");
        if (d.Type is not ("3" or "4")) throw new RegleMetierException("Ce dossier ne peut pas faire l'objet d'un BL.");
        if (d.QualiteClient is "4" or "5") throw new RegleMetierException("Impossible de créer un BL pour un client de qualité 4 ou 5.");
        if (d.SousTraite) throw new RegleMetierException("Vous ne pouvez pas faire de BL pour ce dossier.");
        if (d.SousTraitanceSynchronisee)
            throw new RegleMetierException("BL de sous-traitance synchronisée non disponible dans cette version.");
        var p = await donnees.ParametresAsync(societe, ct);
        if (await donnees.InventaireEnCoursAsync(societe, p.TypeStockDossier(d.Type, d.Facturation), DateOnly.FromDateTime(DateTime.Today), ct))
            throw new RegleMetierException("Création ou modification de BL impossible, inventaire en cours.");
        if (await donnees.VerrouDossierAsync(societe, d.Dossier, d.Cle, ct) is { } profil)
            throw new RegleMetierException($"Ce dossier est en cours de modification par {profil}.");
        return (d, p);
    }

    // Ajout à un BL existant (C7) : même client, même enseigne, même indicateur revendeur, même sous-traitance synchronisée,
    // BL ni facturé ni retour
    private async Task<BlExistant> BlPourAjoutAsync(int societe, int numeroBl, DossierBl d, CancellationToken ct)
    {
        var bl = await donnees.BlAsync(societe, numeroBl, ct) ?? throw new RegleMetierException("BL inexistant.");
        if ((bl.CompteGeneral, bl.CompteClient) != (d.CompteGeneral, d.CompteClient))
            throw new RegleMetierException("Le client du dossier doit être identique à celui du BL.");
        if (bl.Enseigne != d.Enseigne) throw new RegleMetierException("L'enseigne du dossier doit être identique à celle du BL.");
        if (bl.Revendeur != (d.Revendeur == "O")) throw new RegleMetierException("La zone revendeur du dossier doit être identique à celle du BL.");
        if (bl.SousTraitanceSynchronisee != d.SousTraitanceSynchronisee) throw new RegleMetierException("Incohérence de sous-traitance synchronisée.");
        if (bl.Facture) throw new RegleMetierException("Ce BL est facturé : on ne peut plus y ajouter de dossier.");
        if (bl.Retour) throw new RegleMetierException("Ce BL a un retour de marchandises : on ne peut plus y ajouter de dossier.");
        return bl;
    }

    public async Task<SaisieBl> OuvrirAsync(int dossier, string cle, IReadOnlyList<int> lignes, int numeroBl = 0, bool opr = false,
        CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (d, p) = await DossierControleAsync(u.Societe, dossier, cle, ct);
        var proposees = await donnees.LignesAsync(u.Societe, d, ct, opr);
        IReadOnlyList<LigneProposee> choisies = proposees.Where(l => lignes.Contains(l.NumeroLigne)).ToList();
        if (ReglesSaisieBl.ControlerSelection(choisies) is { } refus) throw new RegleMetierException(refus);
        var premiere = choisies[0];
        if (await donnees.QualiteClientAsync(u.Societe, premiere.CompteGeneralFacture, premiere.CompteClientFacture, ct) is "4" or "5")
            throw new RegleMetierException("Le client facturé est bloqué (qualité 4 ou 5).");

        var s = Nouvelle(d, p);
        s.Adresse = premiere.Adresse;
        // Transporteur des OPR s'ils sont d'accord
        if (opr && s.TypePort != "E" && choisies.Select(l => l.Transporteur).Where(x => x > 0).Distinct().ToList() is [var transporteur])
            s.Transporteur = transporteur;
        (s.CompteGeneralFacture, s.CompteClientFacture) = (premiere.CompteGeneralFacture, premiere.CompteClientFacture);
        if (numeroBl > 0)
        {
            // Ajout : en-tête du BL (destinataire, date, transport, observations), client facturé identique (P12 : par élément)
            var bl = await BlPourAjoutAsync(u.Societe, numeroBl, d, ct);
            if (choisies.Any(l => !ReglesSaisieBl.VaAuBl(l, bl.Adresse, bl.CompteGeneralFacture, bl.CompteClientFacture)))
                throw new RegleMetierException("Les lignes choisies doivent aller au destinataire et au client facturé du BL.");
            s.NumeroBl = numeroBl;
            ReprendreEntete(s, bl);
        }

        var details = (await donnees.DetailLignesAsync(u.Societe, d.Dossier, d.Cle, [.. choisies.Select(l => l.NumeroLigne)], ct))
            .ToDictionary(x => x.Ligne);
        var typesStock = new Dictionary<string, bool>();
        foreach (var l in choisies)
        {
            var (_, codeLigne, article, textes) = details[l.NumeroLigne];
            var ligne = new LigneSaisieBl
            {
                NumeroLigneCommande = l.NumeroLigne, CodeLigne = codeLigne, Article = l.Article, Indice = l.Indice, Designation = l.Designation,
                Quantite = l.AProposer, Reste = l.Reste,
            };
            Completer(ligne, article, p);
            ReglesSaisieBl.Coliser(ligne);
            s.Lignes.Add(ligne);
            s.Lignes.AddRange(ReglesSaisieBl.LignesDeTexte(textes));
        }

        // Un seul lot disponible : pris d'office
        foreach (var ligne in s.Lignes.Where(l => l.GereEnStock).ToList())
            ReglesSaisieBl.PrendreSiUnSeulLot(ligne,
                ReglesSaisieBl.Disponibles(s, ligne, await PositionsBrutesAsync(u.Societe, p, ligne, ct, typesStock)));
        return s;
    }

    // En-tête d'un BL existant (ajout, modification) : date, destinataire, transport, observations, mention, texte de pied
    private static void ReprendreEntete(SaisieBl s, BlExistant bl)
    {
        (s.DateExpedition, s.Adresse, s.TypeLivraison, s.Transporteur, s.TypePort, s.Bareme) =
            (bl.DateExpedition, bl.Adresse, bl.TypeLivraison, bl.Transporteur, bl.TypePort, bl.Bareme);
        (s.Observation1, s.Observation2, s.Observation3, s.Mention, s.Incoterm, s.TextePied) =
            (bl.Observation1, bl.Observation2, bl.Observation3, bl.Mention, bl.Incoterm, [.. bl.TextePied]);
    }

    // Valeurs par défaut d'un BL du dossier (§ 6.1)
    private static SaisieBl Nouvelle(DossierBl d, ParametresBl p)
    {
        var s = new SaisieBl
        {
            Dossier = d.Dossier, Cle = d.Cle, TypeDossier = d.Type, Enseigne = d.Enseigne, CompteGeneral = d.CompteGeneral,
            CompteClient = d.CompteClient, NomClient = d.NomClient, DateExpedition = DateOnly.FromDateTime(DateTime.Today),
            TypePort = ReglesSaisieBl.TypesPort.Contains(d.Port) ? d.Port : "F", Transporteur = d.TransporteurClient, Bareme = d.BaremeClient,
            Representant = d.Representant, ReferenceCommande = d.ReferenceCommande, DateCommande = d.DateCommande,
            Facturable = !(d.Type == "4" && d.Facturation is "2" or "3"),
            FacturationAutomatique = d.FacturationAuto != "" ? d.FacturationAuto == "O" : d.OptionFacturationClient is [>= '1' and <= '8'],
        };
        s.TypeLivraison = s.TypePort == "E" ? "" : p.TypeLivraisonDefaut;
        if (s.TypePort == "E") s.Transporteur = 0;
        return s;
    }

    // Caractéristiques de l'article d'une ligne (SPF010) : stock, emballage, poids, UC, fractionnable
    private static void Completer(LigneSaisieBl ligne, ArticleBl? article, ParametresBl p)
    {
        if (article is null) return;
        ligne.GereEnStock = article.GereEnStock;
        ligne.TypeStock = p.TypeStockArticle(article.TypeArticle, article.TypeFacturation);
        ligne.QuantiteEnUc = article.QuantiteEnUc;
        ligne.Fractionnable = article.Fractionnable;
        if (ligne.TypeEmballage == "") ligne.TypeEmballage = article.TypeEmballage;
        if (ligne.EmballagePar == 0 && ligne.TypeEmballage != "S") ligne.EmballagePar = article.EmballagePar;
        if (ligne.PoidsUc == 0) ligne.PoidsUc = article.PoidsUc;
    }

    // Modification d'un élément (§ 9.1) : ni facturé ni retour, BL client produit fini / négoce, sans sous-références ni palettes
    public async Task<SaisieBl> OuvrirElementAsync(int numeroBl, int element, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var p = await donnees.ParametresAsync(u.Societe, ct);
        var el = await donnees.ElementAModifierAsync(u.Societe, numeroBl, element, ct)
            ?? throw new RegleMetierException($"L'élément {element} du BL {numeroBl} n'existe pas.");
        if (el.Facture) throw new RegleMetierException("Cet élément est facturé : il ne peut pas être modifié.");
        if (el.Etat != "C" || el.TypeBl == 1 || el.Dossier == 0)
            throw new RegleMetierException("Seuls les BL de produit fini ou de négoce se modifient dans cette version.");
        if (el.SousReferences) throw new RegleMetierException("Élément avec sous-références : modification non disponible dans cette version.");
        if (el.IssuDePalettes) throw new RegleMetierException("BL issu de palettes : modification non disponible dans cette version.");
        if (el.LotsSansMouvement)
            throw new RegleMetierException("Élément ancien (lots sans mouvement de stock) : modification non disponible dans cette version.");
        if (el.Lignes.Any(l => l.Article != "" && l.LigneCommande == 0))
            throw new RegleMetierException("Élément avec une ligne article hors commande : modification non disponible dans cette version.");
        var (d, _) = await DossierControleAsync(u.Societe, el.Dossier, el.Cle, ct);
        var bl = await donnees.BlAsync(u.Societe, numeroBl, ct) ?? throw new RegleMetierException("BL inexistant.");
        if (bl.Retour) throw new RegleMetierException("Ce BL a un retour de marchandises : il ne peut plus être modifié.");

        var s = Nouvelle(d, p);
        (s.NumeroBl, s.Element) = (numeroBl, element);
        ReprendreEntete(s, bl);
        (s.Palettes, s.ReferenceCommande, s.DateCommande, s.Representant, s.Facturable, s.FacturationAutomatique) =
            (el.Palettes, el.ReferenceCommande, el.DateCommande, el.Representant, el.Facturable, el.FacturationAutomatique);
        (s.CompteGeneralFacture, s.CompteClientFacture) = (el.CompteGeneralFacture, el.CompteClientFacture);

        var articles = (await donnees.DetailLignesAsync(u.Societe, el.Dossier, el.Cle,
            [.. el.Lignes.Where(l => l.LigneCommande > 0).Select(l => l.LigneCommande).Distinct()], ct)).ToDictionary(x => x.Ligne, x => x.Article);
        foreach (var l in el.Lignes)
        {
            var ligne = new LigneSaisieBl
            {
                NumeroLigneBl = l.NumeroLigneBl, NumeroLigneCommande = l.LigneCommande, CodeLigne = l.CodeLigne, Article = l.Article,
                Indice = l.Indice, Designation = l.Designation, Designation2 = l.Designation2, Designation3 = l.Designation3,
                Texte = l.Article == "", Quantite = l.Quantite, Reste = Math.Max(0, l.ResteCommande + l.Quantite), Solder = l.Soldee,
                TypeEmballage = l.TypeEmballage, EmballagePar = l.EmballagePar, PoidsUc = l.PoidsUc, Etat = l.Etat,
                Prises = [.. l.Lots.Select(x => x.Copie())], PrisesEnregistrees = [.. l.Lots.Select(x => x.Copie())],
            };
            if (!ligne.Texte) Completer(ligne, articles.GetValueOrDefault(l.LigneCommande), p);
            ReglesSaisieBl.Coliser(ligne);
            s.Lignes.Add(ligne);
        }
        return s;
    }

    public async Task<IReadOnlyList<PositionStock>> PositionsAsync(SaisieBl saisie, LigneSaisieBl ligne, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var p = await donnees.ParametresAsync(u.Societe, ct);
        var positions = await PositionsBrutesAsync(u.Societe, p, ligne, ct);
        if (positions.Count == 0 && ligne.PrisesEnregistrees.Count == 0 && ligne.GereEnStock
            && await donnees.StockBloqueAsync(u.Societe, ligne.TypeStock, p.DepotPrincipal, ligne.Article, ligne.Indice, ct) is > 0 and var bloque)
            throw new RegleMetierException($"Article {ligne.Article} : {bloque:#,##0} en stock, mais sur des lots bloqués (CQU150).");
        return ReglesSaisieBl.Disponibles(saisie, ligne, positions);
    }

    private async Task<IReadOnlyList<PositionStock>> PositionsBrutesAsync(int societe, ParametresBl p, LigneSaisieBl ligne, CancellationToken ct,
        Dictionary<string, bool>? typesStock = null)
    {
        if (!ligne.GereEnStock || ligne.TypeStock == "") return [];
        var parEmplacement = await ParEmplacementAsync(societe, ligne.TypeStock, typesStock ?? [], ct);
        return await donnees.PositionsAsync(societe, ligne.TypeStock, parEmplacement, p.DepotPrincipal, ligne.Article, ligne.Indice,
            p.EmplacementExpedition, ct);
    }

    public async Task<ControleBl> ControlerAsync(SaisieBl saisie, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (_, _, controle) = await ControlerAsync(u.Societe, saisie, ct);
        return controle;
    }

    // Gestion par emplacement d'un type de stock (AG00F.TSEMP), lue une fois par opération
    private async Task<bool> ParEmplacementAsync(int societe, string typeStock, Dictionary<string, bool> deja, CancellationToken ct)
    {
        if (!deja.TryGetValue(typeStock, out var parEmplacement))
            deja[typeStock] = parEmplacement = (await donnees.TypeStockAsync(societe, typeStock, ct)).ParEmplacement;
        return parEmplacement;
    }

    private async Task<(DossierBl Dossier, ParametresBl Parametres, ControleBl Controle)> ControlerAsync(int societe, SaisieBl s, CancellationToken ct)
    {
        var typesStock = new Dictionary<string, bool>();
        var (d, p) = await DossierControleAsync(societe, s.Dossier, s.Cle, ct);
        foreach (var l in s.Lignes) ReglesSaisieBl.Coliser(l);
        var (erreurs, avertissements) = ReglesSaisieBl.Controler(s);

        // Date d'expédition : période de stock ouverte ou mois suivant, pour chaque type de stock du BL ; pas d'inventaire
        foreach (var typeStock in s.Lignes.Where(l => l.GereEnStock && l.Quantite > 0).Select(l => l.TypeStock).Distinct())
        {
            var (periode, _) = await donnees.TypeStockAsync(societe, typeStock, ct);
            var mois = new DateOnly(s.DateExpedition.Year, s.DateExpedition.Month, 1);
            if (periode is { } debut && (mois < debut || mois > debut.AddMonths(1)))
                erreurs.Add($"Date d'expédition hors de la période de stock {typeStock} ouverte ({debut:MM/yyyy}) ou du mois suivant.");
            if (await donnees.InventaireEnCoursAsync(societe, typeStock, s.DateExpedition, ct))
                erreurs.Add("Création ou modification de BL impossible, inventaire en cours.");
        }
        if (s.TypePort != "E" && s.Transporteur > 0 && !await donnees.TransporteurExisteAsync(societe, s.Transporteur, ct))
            erreurs.Add($"Le transporteur {s.Transporteur} n'existe pas.");
        if (s.CompteClientFacture != 0)
            switch (await donnees.QualiteClientAsync(societe, s.CompteGeneralFacture, s.CompteClientFacture, ct))
            {
                case null: erreurs.Add("Le client facturé n'existe pas."); break;
                case "4" or "5": erreurs.Add("Le client facturé est bloqué (qualité 4 ou 5)."); break;
            }

        // Stock : variation de tout le BL par dépôt / lot / emplacement = quantité enregistrée − quantité saisie (CTRPOSSTK / CTRPOSEMP)
        if (erreurs.Count == 0)
            foreach (var ((typeStock, article, indice, depot, lot, emplacement), variation) in ReglesSaisieBl.Variations(s))
            {
                if (variation >= 0) continue;
                var parEmplacement = await ParEmplacementAsync(societe, typeStock, typesStock, ct);
                var retour = await donnees.ControlerStockAsync(societe, typeStock, parEmplacement, depot, article, indice, lot, emplacement,
                    s.DateExpedition, variation, ct);
                if (retour != 0) erreurs.Add(SaisieBlDb2.MessageStock(article, lot, emplacement, depot, retour));
            }

        var (poids, colis) = ReglesSaisieBl.Totaux(s, p.PoidsPalettes, p.PoidsPalette);
        // Port franco calculé sur le poids de tout le BL (autres dossiers compris), réparti au poids entre eux à la validation
        decimal? port = null;
        if (p.CalculPort && s.TypePort == "F")
        {
            var poidsBl = poids + (s.NumeroBl > 0 ? await donnees.PoidsAutresElementsAsync(societe, s.NumeroBl, s.Element, ct) : 0);
            port = await donnees.PortAsync(societe, s.Transporteur, s.Adresse.Departement, s.Bareme, poidsBl, ct);
            if (port is null) avertissements.Add("Port franco non calculé : transporteur, tranche de poids ou tarif du département manquant.");
        }
        return (d, p, new ControleBl(erreurs, avertissements, poids, colis, port));
    }

    // Suppression d'un élément (cahier des charges § 10.1) : ni facturé, ni retourné ; BL client ou divers ; dossier ni
    // sous-traité ni en inventaire
    public async Task<bool> SupprimerAsync(int numeroBl, int element, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var el = await donnees.ElementAsync(u.Societe, numeroBl, element, ct)
            ?? throw new RegleMetierException($"L'élément {element} du BL {numeroBl} n'existe pas.");
        if (el.Facture) throw new RegleMetierException("Cet élément est facturé : il ne peut pas être supprimé.");
        if (el.AvecRetour) throw new RegleMetierException("Un retour de marchandises existe sur ce BL : il ne peut pas être supprimé.");
        if (el.Etat is not ("C" or "D"))
            throw new RegleMetierException("La suppression d'un retour, d'une sous-traitance ou d'un BL vierge n'est pas disponible dans cette version.");
        if (el.Etat == "C" && el.TypeBl == 1) throw new RegleMetierException("BL de fabrication : suppression non disponible dans cette version.");
        if (el.SousTraitanceSynchronisee)
            throw new RegleMetierException("BL de sous-traitance synchronisée : suppression non disponible dans cette version.");
        if (el.Dossier != 0 && el.Etat == "C" && await donnees.DossierAsync(u.Societe, el.Dossier, el.Cle, ct) is { SousTraite: true })
            throw new RegleMetierException("Vous ne pouvez pas faire de BL pour ce dossier.");
        foreach (var typeStock in el.TypesStock)
            if (await donnees.InventaireEnCoursAsync(u.Societe, typeStock, DateOnly.FromDateTime(DateTime.Today), ct))
                throw new RegleMetierException("Création ou modification de BL impossible, inventaire en cours.");
        return await donnees.SupprimerElementAsync(u.Societe, u.Profil, el, (await donnees.ParametresAsync(u.Societe, ct)).CalculPort, ct);
    }

    public async Task<int> ValiderAsync(SaisieBl saisie, bool depassementAccepte = false, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (d, p, c) = await ControlerAsync(u.Societe, saisie, ct);
        if (!c.Valide) throw new RegleMetierException(c.Erreurs[0]);
        if (saisie.Ajout) await BlPourAjoutAsync(u.Societe, saisie.NumeroBl, d, ct);
        // La date d'expédition est celle de tout le BL : changée seulement sur un BL d'un seul élément (en modification)
        if (saisie.NumeroBl > 0 && await donnees.BlAsync(u.Societe, saisie.NumeroBl, ct) is { } existant
            && existant.DateExpedition != saisie.DateExpedition && (saisie.Ajout || existant.NombreElements > 1))
            throw new RegleMetierException("La date d'expédition est commune à tous les dossiers du BL : elle ne peut être changée que sur un BL d'un seul dossier.");

        var parEmplacement = new Dictionary<string, bool>();
        foreach (var t in saisie.Lignes.Where(l => l.GereEnStock).Select(l => l.TypeStock).Distinct())
            await ParEmplacementAsync(u.Societe, t, parEmplacement, ct);
        var articles = (await donnees.DetailLignesAsync(u.Societe, d.Dossier, d.Cle,
            [.. saisie.Lignes.Where(l => !l.Texte && l.NumeroLigneCommande > 0).Select(l => l.NumeroLigneCommande).Distinct()], ct))
            .ToDictionary(x => x.Ligne, x => x.Article);

        // Lignes à quantité 0 retirées du BL ; lignes de texte gardées derrière leur article
        var lignes = new List<LigneEcriteBl>();
        var garder = true;
        foreach (var l in saisie.Lignes)
        {
            if (!l.Texte) garder = l.Quantite > 0;
            if (!garder) continue;
            var article = l.Texte ? null : articles.GetValueOrDefault(l.NumeroLigneCommande);
            lignes.Add(new LigneEcriteBl(l.NumeroLigneBl, l.Texte ? 0 : l.NumeroLigneCommande, l.Texte ? "" : l.CodeLigne, l.Texte ? "" : l.Article,
                l.Texte ? "" : l.Indice, l.Designation, l.Designation2, l.Designation3, l.Texte ? 0 : l.Quantite, l.Colis, l.EmballagePar,
                l.TypeEmballage, l.ColisIncomplet, l.Mecompte, l.PoidsUc, l.Etat, l.Solder, l.GereEnStock && !l.Texte, l.TypeStock,
                parEmplacement.GetValueOrDefault(l.TypeStock), article?.Famille ?? "", article?.SousFamille ?? "",
                [.. l.Prises.Where(x => x.Quantite > 0).Select(x => new LotPris(x.Depot, x.Lot, x.Emplacement, x.Quantite))]));
        }
        var soldes = saisie.Lignes.Where(l => !l.Texte && l.Quantite == 0 && l.Solder && l.NumeroLigneCommande > 0)
            .Select(l => l.NumeroLigneCommande).ToList();
        var bl = new BlAEcrire(saisie, d, p, d.Type == "3" ? "3" : "2", lignes, c.Poids, c.Colis, soldes, depassementAccepte);
        return await donnees.EnregistrerAsync(u.Societe, u.Profil, bl, ct);
    }
}
