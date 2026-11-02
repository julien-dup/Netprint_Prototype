using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Ventes.Contrats;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Demandes d'OF préparées par l'écran de lancement, et contrôles côté commande. Fonctions pures : les données viennent de
// LancementCommandeDb2.
//   - Proposer : valeurs de la fenêtre de lancement (option CR, groupage), NEG515 SPCHG2 l. 2143-2337 ;
//   - Automatique : création automatique NEG510 F10 (OFAUTO / SPVALLANC), un OF pour tous les articles ;
//   - ControlerAffectations / ControlerAffectationStock : contrôles NEG515C2 et NEG51503 qui portent sur la commande.
// Pour un article géré en stock seulement (le premier qui a la valeur l'emporte) : devis de sa 1re ligne de commande,
// catégorie = famille, dossier précédent (fabrication en cours, sinon dernier dossier de l'article ; ou, si la société
// gère les modèles par article (SOP22), modèle de l'article, désignation du modèle et dernier dossier du modèle), norme ;
// le devis donne la désignation, à défaut le dossier précédent, et le chemin façonnage ; le dossier précédent donne le
// modèle et l'indice de composition.
// ÉCARTS : l'enseigne de la commande est reprise aussi en CR (NEG515 la laissait vide) ; en F10, dossier précédent et
// modèle viennent du premier article géré en stock (NEG510 : du dernier article, et aucun s'il n'était pas stocké) et la
// catégorie, sans article stocké, de la famille du premier article (NEG510 la laissait vide, l'OF ne pouvait être ouvert).
// =====================================================================
internal static class PreparationLancement
{
    internal sealed record Commande(CleCommande Cle, int CompteGeneral, int CompteClient, string Enseigne, string Reference,
        string Interlocuteur, string Designation1, string Designation2, DateOnly? DateCommande, DateOnly? DateEntree,
        DateOnly? DateLivraison, bool SousTraitee);

    // Fiche article et ce qui en découle, lus avant le choix (la règle prend la première valeur trouvée)
    internal sealed record Article(string Code, string Indice, bool Stocke, string Famille, string SousFamille, string Designation,
        string Norme, DevisOrigine? Devis, NumeroOf? FabricationEnCours, NumeroOf? DernierDossier, int Modele,
        (string, string)? DesignationModele, NumeroOf? DernierDossierModele);

    internal sealed record Devis(string Designation1, string Designation2, NumeroOf? DossierPrecedent, string CheminFaconnage);

    internal sealed record Dossier(int Modele, string Indice);

    internal sealed record Donnees(Commande Commande, bool ModeleParArticle, IReadOnlyList<Article> Articles,
        IReadOnlyDictionary<DevisOrigine, Devis> Devis, IReadOnlyDictionary<NumeroOf, Dossier> Dossiers);

    // CR : à fabriquer = à affecter = reste à affecter de chaque article ; date de mise à disposition = livraison de la commande ;
    // quantité lancée vide (la production prend le total à fabriquer)
    internal static DemandeOf Proposer(Donnees d, IReadOnlyDictionary<ReferenceArticle, int> resteAAffecter, DateOnly aujourdhui)
    {
        var c = d.Commande;
        var mad = c.DateLivraison ?? aujourdhui;
        var besoins = d.Articles.Select(a =>
        {
            var reste = Math.Max(0, resteAAffecter.GetValueOrDefault(new(a.Code, a.Indice)));
            return new BesoinArticle(a.Code, a.Indice, reste, reste, mad);
        }).ToList();
        return Initialiser(d, "", "", d.Articles.Count > 1) with
        {
            DateMiseADisposition = mad, DateDossier = aujourdhui, QuantiteLancee = 0, Articles = besoins,
        };
    }

    // F10 : tout le commandé fabriqué et affecté, désignation de la commande, date du dossier = date de la commande
    internal static DemandeOf Automatique(Donnees d, IReadOnlyDictionary<ReferenceArticle, int> commande, DateOnly aujourdhui)
    {
        var c = d.Commande;
        var mad = c.DateLivraison ?? aujourdhui;
        var demande = Initialiser(d, c.Designation1, c.Designation2, groupe: true);
        if (demande.Categorie == "" && d.Articles.Count > 0)
            demande = demande with { Categorie = d.Articles[0].Famille, SousCategorie = d.Articles[0].SousFamille };
        var besoins = d.Articles.Select(a => (a, q: commande.GetValueOrDefault(new(a.Code, a.Indice)))).Where(x => x.q > 0)
            .Select(x => new BesoinArticle(x.a.Code, x.a.Indice, x.q, x.q, mad)).ToList();
        var stockes = d.Articles.Where(a => a.Stocke).Select(a => new ReferenceArticle(a.Code, a.Indice)).ToHashSet();
        return demande with
        {
            DateMiseADisposition = mad, DateDossier = c.DateCommande ?? aujourdhui,
            QuantiteLancee = besoins.Where(b => stockes.Contains(new(b.Article, b.Indice))).Sum(b => b.AFabriquer), Articles = besoins,
        };
    }

    private static DemandeOf Initialiser(Donnees d, string designation1, string designation2, bool groupe)
    {
        var c = d.Commande;
        DevisOrigine? devis = null;
        NumeroOf? precedent = null;
        string categorie = "", sousCategorie = "", norme = "", chemin = "", indice = "";
        var (des1, des2) = (designation1, designation2);
        var modele = 0;
        foreach (var a in d.Articles.Where(a => a.Stocke))
        {
            devis ??= a.Devis;
            if (categorie == "") (categorie, sousCategorie) = (a.Famille, a.SousFamille);
            if (precedent is null)
            {
                if (!d.ModeleParArticle) precedent = a.FabricationEnCours ?? a.DernierDossier;
                else if (a.Modele > 0)
                {
                    modele = a.Modele;
                    if (a.DesignationModele is { } dm) (des1, des2) = dm;
                    precedent = a.DernierDossierModele;
                }
            }
            if (norme == "") norme = a.Norme;
            if (devis is not null && d.Devis.TryGetValue(devis, out var dv))
            {
                if (des1 == "") (des1, des2) = (dv.Designation1, dv.Designation2);
                precedent ??= dv.DossierPrecedent;
                chemin = dv.CheminFaconnage;
            }
            if (des1 == "" && !groupe) des1 = a.Designation;
            if (precedent is not null && d.Dossiers.TryGetValue(precedent, out var dos)) (modele, indice) = (dos.Modele, dos.Indice);
        }
        return new DemandeOf(new CommandeOrigine(c.Cle.Numero, c.Cle.Cle), c.CompteGeneral, c.CompteClient, c.Enseigne, c.Reference,
            c.Interlocuteur, categorie, sousCategorie, des1, des2, DateOnly.MinValue, DateOnly.MinValue, c.DateEntree, c.DateLivraison,
            0, 0, "", devis, precedent, modele, indice, norme, chemin, c.SousTraitee, []);
    }

    // ------------------------------------------------------------------
    // Contrôles de la fenêtre de lancement qui portent sur la commande (NEG515C2) :
    //   - total affecté de l'article (sans ce qui l'était déjà par cet OF) + à affecter ≤ commandé (CDE0008) ;
    //   - en modification, à affecter ≤ disponible de l'OF + affecté par cet OF + écart de l'à fabriquer (CDE0009).
    //     ÉCART : NEG515 ajoutait tout l'à fabriquer saisi, déjà compté dans le disponible de l'OF.
    internal static List<ErreurDemandeOf> ControlerAffectations(DemandeOf d, IReadOnlyList<ArticleALancer> articles, NumeroOf? of,
        IReadOnlyList<BesoinArticle> anciens)
    {
        var erreurs = new List<ErreurDemandeOf>();
        foreach (var (b, i) in d.Articles.Select((b, i) => (b, i)))
        {
            var zone = $"{nameof(DemandeOf.Articles)}[{i}].{nameof(BesoinArticle.AAffecter)}";
            var a = articles.FirstOrDefault(a => Meme(a.Article, b.Article) && Meme(a.Indice, b.Indice));
            if (a is null)
            {
                erreurs.Add(new($"{nameof(DemandeOf.Articles)}[{i}].{nameof(BesoinArticle.Article)}", Messages.ArticleHorsCommande));
                continue;
            }
            var ancien = anciens.FirstOrDefault(x => Meme(x.Article, b.Article) && Meme(x.Indice, b.Indice));
            if (a.Affecte - (ancien?.AAffecter ?? 0) + b.AAffecter > a.Commande)
                erreurs.Add(new(zone, Messages.AffecteSuperieurCommande));
            else if (of is not null && b.AAffecter > (ancien?.AAffecter ?? 0)
                     && b.AAffecter > (a.Sources.FirstOrDefault(s => s.Of == of)?.Disponible ?? 0) + (ancien?.AAffecter ?? 0)
                        + b.AFabriquer - (ancien?.AFabriquer ?? 0))
                erreurs.Add(new(zone, Messages.AffecteSuperieurDisponible));
        }
        return erreurs;
    }

    // Affectation de stock (NEG515 AF, fenêtre NEG51503) : OF ouvert avec du disponible (ou déjà affecté en stock),
    // quantité saisie > 0, total affecté ≤ commandé, quantité ≤ disponible + déjà affecté par cet OF
    internal static List<ErreurDemandeOf> ControlerAffectationStock(ArticleALancer a, SourceArticle? s, int quantite)
    {
        if (s is null || s.Nature == NatureSource.OfAOuvrir || s.Disponible <= 0 && !s.AffectationStock)
            return [new("", Messages.AffectationImpossible)];
        // Lot qui n'est pas celui d'un OF (n° sur 5 + clé) : rien à lier
        if (s.Of.Numero <= 0 || s.Of.Cle.Length != 1) return [new("", Messages.LotSansOf)];
        if (quantite <= 0) return [new(nameof(quantite), Messages.Obligatoire)];
        if (a.Affecte - s.AffecteCommande + quantite > a.Commande) return [new(nameof(quantite), Messages.AffecteSuperieurCommande)];
        if (quantite > s.Disponible + s.AffecteCommande) return [new(nameof(quantite), Messages.AffecteSuperieurDisponible)];
        return [];
    }

    private static bool Meme(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // Messages PRTMSG et NEG515
    internal static class Messages
    {
        public const string Obligatoire = "Cette zone doit être renseignée.";                                      // GEN0001
        public const string AffecteSuperieurCommande = "Total des quantités affectées > à quantité commandée.";     // CDE0008
        public const string AffecteSuperieurDisponible = "Total des quantités affectées > au disponible.";          // CDE0009
        public const string ArticleHorsCommande = "Cet article n'est pas dans la commande.";
        public const string AffectationImpossible = "Affectation impossible : pas de stock ou dossier temporaire.";
        public const string LotSansOf = "Affectation impossible : ce lot n'est pas celui d'un OF.";
    }
}
