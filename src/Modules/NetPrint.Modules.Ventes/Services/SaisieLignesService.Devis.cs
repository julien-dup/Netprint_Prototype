using NetPrint.Modules.Ventes.Contrats;
using NetPrint.Socle;

namespace NetPrint.Modules.Ventes.Services;

// =====================================================================
// Reprise d'un devis dans une nouvelle ligne (NEG520 F10, fenêtre NEG52009, routines SPDEV / SPADR). Seuls les devis du
// client de la commande sont repris (NEG520 acceptait n'importe quel n°).
// =====================================================================
internal sealed partial class SaisieLignesService
{
    public async Task<ResultatRecherche<DevisAReprendre>> DevisAReprendreAsync(CleCommande commande, string? recherche, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, false, ct);
        return await donnees.DevisDuClientAsync(u.Societe, c.CompteGeneral, c.CompteClient, recherche, ct);
    }

    public async Task<IReadOnlyList<AdresseDevis>> AdressesDevisAsync(CleCommande commande, ReferenceDevis devis, CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, false, ct);
        return await donnees.AdressesDevisAsync(u.Societe, c.CompteGeneral, c.CompteClient, devis, ct);
    }

    public async Task<LigneSaisie> NouvelleDepuisDevisAsync(CleCommande commande, ReferenceDevis devis, AdresseDevis? adresse = null,
        CancellationToken ct = default)
    {
        var (u, c) = await ContexteAsync(commande, false, ct);
        var d = await donnees.DevisAsync(u.Societe, c.CompteGeneral, c.CompteClient, devis, ct)
            ?? throw new RegleMetierException(MessagesLigne.DevisInexistant);
        var adresses = await donnees.AdressesDevisAsync(u.Societe, c.CompteGeneral, c.CompteClient, devis, ct);
        // Adresse choisie retrouvée par son contenu : modifiée entre-temps dans le devis, elle est à choisir de nouveau
        AdresseDevis? choisie = adresses.Count switch
        {
            0 => null,
            1 => adresses[0],
            _ => adresses.FirstOrDefault(a => a == adresse) ?? throw new RegleMetierException(MessagesLigne.AdresseDevisAChoisir),
        };
        var l = Nouvelle(c);
        RepriseDevis.Appliquer(l, d, choisie);
        // Prix net, montant et maxi livrable affichés dès la reprise, avant la saisie de l'article
        l.PrixNet = MoteurPrixCommande.PrixNet(l.PrixBrut ?? 0, l.Remise ?? 0);
        l.Montant = RepriseDevis.TnumLigne(d) is { } tnum ? MoteurPrixCommande.Montant(l.PrixNet, l.Quantite ?? 0, tnum) : 0;
        RecalculerMaxi(c, l);
        if (l.Article == "" || await donnees.ArticleAsync(u.Societe, l.Article, l.Indice, ct) is not { } a) return l;
        // Article repris (SOP18) : ses valeurs (emballage, norme, textes…) ; désignation, unité et prix du devis gardés
        return await CompleterAsync(u.Societe, commande, c, l, a, ct);
    }
}
