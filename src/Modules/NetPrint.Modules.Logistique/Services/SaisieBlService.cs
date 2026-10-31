using NetPrint.Modules.Logistique.Contrats;
using NetPrint.Modules.Logistique.Donnees;
using NetPrint.Socle;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Logistique.Services;

// =====================================================================
// Saisie d'un BL produit fini / négoce (EXP010, cahier des charges lot 1) : contrôles du dossier, lignes à livrer, saisie,
// validation. Société et profil de la session. Pas de droit PRT000P1 (EXP010 n'en contrôle pas).
// Étape 1 : création d'un BL d'un dossier à partir des lignes de commande, dépôt principal.
// ÉCARTS / À VENIR (docs/points-en-attente.md) : ajout d'un dossier, modification, suppression, source OPR, port et
// imputation A20005, sous-références, palettes, dépôts autorisés (EXPDEP), sous-traitance synchronisée.
// =====================================================================
internal sealed class SaisieBlService(SaisieBlDb2 donnees, IUtilisateurCourant utilisateur) : ISaisieBl
{
    public async Task<PreparationBl> PreparerAsync(int dossier, string cle, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (d, _) = await DossierControleAsync(u.Societe, dossier, cle, ct);
        var lignes = await donnees.LignesAsync(u.Societe, d, ct);
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

    public async Task<SaisieBl> OuvrirAsync(int dossier, string cle, IReadOnlyList<int> lignes, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (d, p) = await DossierControleAsync(u.Societe, dossier, cle, ct);
        var proposees = await donnees.LignesAsync(u.Societe, d, ct);
        var choisies = proposees.Where(l => lignes.Contains(l.NumeroLigne)).ToList();
        if (ReglesSaisieBl.ControlerSelection(choisies) is { } refus) throw new RegleMetierException(refus);
        var premiere = choisies[0];
        if (await donnees.QualiteClientAsync(u.Societe, premiere.CompteGeneralFacture, premiere.CompteClientFacture, ct) is "4" or "5")
            throw new RegleMetierException("Le client facturé est bloqué (qualité 4 ou 5).");

        var facturationAuto = d.FacturationAuto != "" ? d.FacturationAuto == "O" : d.OptionFacturationClient is [>= '1' and <= '8'];
        var s = new SaisieBl
        {
            Dossier = d.Dossier, Cle = d.Cle, TypeDossier = d.Type, Enseigne = d.Enseigne, CompteGeneral = d.CompteGeneral,
            CompteClient = d.CompteClient, NomClient = d.NomClient, DateExpedition = DateOnly.FromDateTime(DateTime.Today),
            Adresse = premiere.Adresse, TypePort = ReglesSaisieBl.TypesPort.Contains(d.Port) ? d.Port : "F",
            Transporteur = d.TransporteurClient, Bareme = d.BaremeClient, Representant = d.Representant,
            ReferenceCommande = d.ReferenceCommande, DateCommande = d.DateCommande,
            Facturable = !(d.Type == "4" && d.Facturation is "2" or "3"), FacturationAutomatique = facturationAuto,
            CompteGeneralFacture = premiere.CompteGeneralFacture, CompteClientFacture = premiere.CompteClientFacture,
        };
        s.TypeLivraison = s.TypePort == "E" ? "" : p.TypeLivraisonDefaut;
        if (s.TypePort == "E") s.Transporteur = 0;

        var details = (await donnees.DetailLignesAsync(u.Societe, d.Dossier, d.Cle, [.. choisies.Select(l => l.NumeroLigne)], ct))
            .ToDictionary(x => x.Ligne);
        var typesStock = new Dictionary<string, bool>();
        foreach (var l in choisies)
        {
            var (_, codeLigne, article, textes) = details[l.NumeroLigne];
            var ligne = new LigneSaisieBl
            {
                NumeroLigneCommande = l.NumeroLigne, CodeLigne = codeLigne, Article = l.Article, Indice = l.Indice, Designation = l.Designation,
                Quantite = l.Reste, Reste = l.Reste,
            };
            if (article is not null)
            {
                ligne.GereEnStock = article.GereEnStock;
                ligne.TypeStock = p.TypeStockArticle(article.TypeArticle, article.TypeFacturation);
                ligne.TypeEmballage = article.TypeEmballage;
                ligne.EmballagePar = article.EmballagePar;
                ligne.PoidsUc = article.PoidsUc;
                ligne.QuantiteEnUc = article.QuantiteEnUc;
                ligne.Fractionnable = article.Fractionnable;
            }
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

    public async Task<IReadOnlyList<PositionStock>> PositionsAsync(SaisieBl saisie, LigneSaisieBl ligne, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var p = await donnees.ParametresAsync(u.Societe, ct);
        var positions = await PositionsBrutesAsync(u.Societe, p, ligne, ct);
        if (positions.Count == 0 && ligne.GereEnStock
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

        // Stock : variation cumulée de tout le BL par dépôt / lot / emplacement (CTRPOSSTK / CTRPOSEMP)
        if (erreurs.Count == 0)
            foreach (var g in s.Lignes.Where(l => l.GereEnStock && l.Quantite > 0)
                         .SelectMany(l => l.Prises.Where(x => x.Quantite > 0).Select(x => (l.TypeStock, l.Article, l.Indice, x)))
                         .GroupBy(x => (x.TypeStock, x.Article, x.Indice, x.x.Depot, x.x.Lot, x.x.Emplacement)))
            {
                var k = g.Key;
                var parEmplacement = await ParEmplacementAsync(societe, k.TypeStock, typesStock, ct);
                var retour = await donnees.ControlerStockAsync(societe, k.TypeStock, parEmplacement, k.Depot, k.Article, k.Indice, k.Lot,
                    k.Emplacement, s.DateExpedition, -g.Sum(x => x.x.Quantite), ct);
                if (retour != 0) erreurs.Add(SaisieBlDb2.MessageStock(k.Article, k.Lot, k.Emplacement, k.Depot, retour));
            }

        var (poids, colis) = ReglesSaisieBl.Totaux(s, p.PoidsPalettes, p.PoidsPalette);
        return (d, p, new ControleBl(erreurs, avertissements, poids, colis));
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
        return await donnees.SupprimerElementAsync(u.Societe, u.Profil, el, ct);
    }

    public async Task<int> CreerAsync(SaisieBl saisie, bool depassementAccepte = false, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var (d, p, c) = await ControlerAsync(u.Societe, saisie, ct);
        if (!c.Valide) throw new RegleMetierException(c.Erreurs[0]);

        var parEmplacement = new Dictionary<string, bool>();
        foreach (var t in saisie.Lignes.Where(l => l.GereEnStock).Select(l => l.TypeStock).Distinct())
            await ParEmplacementAsync(u.Societe, t, parEmplacement, ct);
        var articles = (await donnees.DetailLignesAsync(u.Societe, d.Dossier, d.Cle,
            [.. saisie.Lignes.Where(l => !l.Texte).Select(l => l.NumeroLigneCommande)], ct)).ToDictionary(x => x.Ligne, x => x.Article);

        // Lignes à quantité 0 retirées ; lignes de texte gardées derrière leur article
        var lignes = new List<LigneEcriteBl>();
        var garder = true;
        foreach (var l in saisie.Lignes)
        {
            if (!l.Texte) garder = l.Quantite > 0;
            if (!garder) continue;
            var article = l.Texte ? null : articles.GetValueOrDefault(l.NumeroLigneCommande);
            lignes.Add(new LigneEcriteBl(l.Texte ? 0 : l.NumeroLigneCommande, l.Texte ? "" : l.CodeLigne, l.Texte ? "" : l.Article,
                l.Texte ? "" : l.Indice, l.Designation, l.Designation2, l.Designation3, l.Texte ? 0 : l.Quantite, l.Colis, l.EmballagePar,
                l.TypeEmballage, l.ColisIncomplet, l.Mecompte, l.PoidsUc, l.Etat, l.Solder, l.GereEnStock && !l.Texte, l.TypeStock,
                parEmplacement.GetValueOrDefault(l.TypeStock), article?.Famille ?? "", article?.SousFamille ?? "",
                [.. l.Prises.Where(x => x.Quantite > 0).Select(x => new LotPris(x.Depot, x.Lot, x.Emplacement, x.Quantite))]));
        }
        var soldes = saisie.Lignes.Where(l => !l.Texte && l.Quantite == 0 && l.Solder).Select(l => l.NumeroLigneCommande).ToList();
        var bl = new BlAEcrire(saisie, d, p, d.Type == "3" ? "3" : "2", lignes, c.Poids, c.Colis, soldes, depassementAccepte);
        return await donnees.CreerAsync(u.Societe, u.Profil, bl, ct);
    }
}
