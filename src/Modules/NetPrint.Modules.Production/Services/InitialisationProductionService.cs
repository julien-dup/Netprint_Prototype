using NetPrint.Modules.Production.Contrats;
using NetPrint.Modules.Production.Donnees;
using NetPrint.Socle.Session;

namespace NetPrint.Modules.Production.Services;

// Demandes d'OF : contrôles de la fenêtre de lancement NEG515C2 qui portent sur l'OF (désignation, catégorie,
// date, unité, quantités par article), numérotation et écriture par DemandesOfDb2.
// Les contrôles qui portent sur la commande (total affecté ≤ commandé, ≤ disponible) sont faits par la vente.
internal sealed class InitialisationProductionService(DemandesOfDb2 donnees, ReferencesProductionDb2 references, IUtilisateurCourant utilisateur,
    Microsoft.Extensions.Logging.ILogger<InitialisationProductionService> journal) : IInitialisationProduction
{
    public async Task<NumeroOf> DemanderOfAsync(DemandeOf demande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var d = Normaliser(demande);
        await ControlerAsync(u.Societe, d, creation: true, ct);
        // Quantité lancée non saisie : total à fabriquer (NEG515 l. 2888-2892)
        if (d.QuantiteLancee <= 0) d = d with { QuantiteLancee = d.Articles.Sum(a => a.AFabriquer) };
        return await donnees.CreerAsync(u.Societe, u.Profil, d, ct);
    }

    public async Task ModifierDemandeOfAsync(NumeroOf of, DemandeOf demande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var d = Normaliser(demande);
        await ControlerAsync(u.Societe, d, creation: false, ct);
        await donnees.ModifierAsync(u.Societe, u.Profil, of, d, ct);
    }

    public async Task SupprimerDemandeOfAsync(NumeroOf of, CommandeOrigine commande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        if (await donnees.SupprimerAsync(u.Societe, of, commande, ct))
        {
            try
            {
                await donnees.SupprimerBagAsync(u.Societe, of, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Suivi du BAG orphelin : sans effet sur l'OF supprimé (AB009 non journalisée, hors transaction) ; signalé
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(journal, ex,
                    "Suivi du BAG (AB009) de l'OF {Of} non supprimé", of.Libelle);
            }
        }
    }

    public async Task AjouterAOfAsync(NumeroOf of, CommandeOrigine commande, BesoinArticle besoin, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        var b = besoin with { Article = besoin.Article.Trim().ToUpperInvariant(), Indice = besoin.Indice.Trim().ToUpperInvariant() };
        if (b.AFabriquer <= 0)
            throw new DemandeOfInvalideException([new(nameof(BesoinArticle.AFabriquer), Messages.Obligatoire)]);
        if (b.AAffecter < 0 || b.AAffecter > b.AFabriquer)
            throw new DemandeOfInvalideException([new(nameof(BesoinArticle.AAffecter), Messages.AffecteSuperieurAFabriquer)]);
        await donnees.AjouterAsync(u.Societe, of, commande, b, ct);
    }

    public async Task<DemandeOfLue?> LireDemandeOfAsync(NumeroOf of, CommandeOrigine commande, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return await donnees.LireAsync(u.Societe, of, commande, ct);
    }

    public async Task<EtatOf> EtatAsync(NumeroOf of, CancellationToken ct = default)
    {
        var u = await utilisateur.ExigerAsync();
        return await donnees.EtatAsync(u.Societe, of, ct);
    }

    private async Task ControlerAsync(int societe, DemandeOf d, bool creation, CancellationToken ct)
    {
        // Catégorie et sous-catégorie ne se contrôlent qu'en création (protégées en modification)
        var (categorie, sousCategorie, unite) = await references.VerifierAsync(societe, creation ? d.Categorie : "",
            creation ? d.SousCategorie : "", d.Unite, ct);
        var erreurs = Controler(d, creation, categorie, sousCategorie, unite);
        if (erreurs.Count > 0) throw new DemandeOfInvalideException(erreurs);
    }

    // Contrôles de NEG515C2 (SPAFF2 l. 2420-2632), messages PRTMSG
    internal static List<ErreurDemandeOf> Controler(DemandeOf d, bool creation, bool categorieExiste, bool sousCategorieExiste,
        bool uniteExiste)
    {
        var e = new List<ErreurDemandeOf>();
        void Si(bool condition, string zone, string message)
        {
            if (condition && e.All(x => x.Zone != zone)) e.Add(new(zone, message));
        }
        Si(d.Designation1 == "", nameof(d.Designation1), Messages.Obligatoire);
        if (creation)
        {
            Si(d.Categorie == "", nameof(d.Categorie), Messages.Obligatoire);
            Si(d.Categorie is "$1" or "$2", nameof(d.Categorie), Messages.CategorieReservee);
            Si(!categorieExiste, nameof(d.Categorie), Messages.CategorieInexistante);
            Si(d.SousCategorie == "", nameof(d.SousCategorie), Messages.Obligatoire);
            Si(d.SousCategorie is "$1" or "$2", nameof(d.SousCategorie), Messages.CategorieReservee);
            Si(!sousCategorieExiste, nameof(d.SousCategorie), Messages.SousCategorieInexistante);
        }
        Si(d.DateMiseADisposition == DateOnly.MinValue, nameof(d.DateMiseADisposition), Messages.Obligatoire);
        Si(d.Unite != "" && !uniteExiste, nameof(d.Unite), Messages.UniteInexistante);
        Si(d.Prix != 0 && d.Unite == "", nameof(d.Unite), Messages.Obligatoire);
        // Une ligne sans quantité à fabriquer ni à affecter est ignorée (NEG515 l. 2930-2934) ; il en faut au moins une à fabriquer
        Si(!d.Articles.Any(a => a.AFabriquer > 0), nameof(d.Articles), Messages.AuMoinsUneLigne);
        foreach (var (a, i) in d.Articles.Select((a, i) => (a, i)))
        {
            Si(a.AFabriquer < 0 || a.AFabriquer == 0 && a.AAffecter != 0, $"{nameof(d.Articles)}[{i}].{nameof(a.AFabriquer)}", Messages.Obligatoire);
            Si(a.AAffecter < 0 || a.AAffecter > a.AFabriquer, $"{nameof(d.Articles)}[{i}].{nameof(a.AAffecter)}", Messages.AffecteSuperieurAFabriquer);
        }
        return e;
    }

    internal static DemandeOf Normaliser(DemandeOf d)
    {
        static string Code(string? v, int n) => Coupe((v ?? "").Trim().ToUpperInvariant(), n);
        static string Texte(string? v, int n) => Coupe((v ?? "").Trim(), n);
        static string Coupe(string v, int n) => v.Length > n ? v[..n] : v;
        return d with
        {
            Enseigne = Code(d.Enseigne, 1), ReferenceCommande = Texte(d.ReferenceCommande, 15), Interlocuteur = Texte(d.Interlocuteur, 39),
            Categorie = Code(d.Categorie, 2), SousCategorie = Code(d.SousCategorie, 2), Designation1 = Texte(d.Designation1, 40),
            Designation2 = Texte(d.Designation2, 40), Unite = Code(d.Unite, 2), IndiceComposition = Code(d.IndiceComposition, 1),
            Norme = Code(d.Norme, 4), CheminFaconnage = Code(d.CheminFaconnage, 4),
            Articles = [.. d.Articles.Select(a => a with { Article = Code(a.Article, 15), Indice = Code(a.Indice, 2) })],
        };
    }
}

// Messages de PRTMSG utilisés par la fenêtre de lancement
internal static class Messages
{
    public const string Obligatoire = "Cette zone doit être renseignée.";                                        // GEN0001
    public const string CategorieReservee = "Catégorie réservée aux dossiers de négoce et PFS.";                  // CAT0017
    public const string CategorieInexistante = "Cette catégorie n'existe pas.";                                   // CAT0001
    public const string SousCategorieInexistante = "Cette sous-catégorie n'existe pas.";                          // CAT0003
    public const string UniteInexistante = "Cette unité de vente n'existe pas.";                                  // FAC0004
    public const string AffecteSuperieurAFabriquer = "Quantité à affecter > quantité à fabriquer.";               // CDE0010
    public const string AuMoinsUneLigne = "Au moins une ligne doit être saisie.";                                 // GEN0049
}
