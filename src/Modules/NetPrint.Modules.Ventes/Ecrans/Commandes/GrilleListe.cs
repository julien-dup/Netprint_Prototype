using NetPrint.Web.Commun;

namespace NetPrint.Modules.Ventes.Ecrans.Commandes;

// Colonne d'une grille : clé, titre, largeur par défaut (px), classe CSS, type de filtre,
// valeur brute (tri, filtres nombre / date / oui-non), texte affiché (et filtré), bulle éventuelle.
internal sealed record ColonneListe<T>(
    string Cle, string Libelle, int Largeur, string Classe, TypeColonne Type,
    Func<T, object?> CleTri, Func<T, string> Texte, Func<T, string?>? Bulle = null, bool Obligatoire = false);

// Réglages d'une grille pendant la session : tri, filtres de colonnes, affichage mémorisé (colonnes masquées, largeurs)
internal sealed class EtatGrille(string nom)
{
    public string Nom { get; } = nom;
    public string? TriCle { get; set; }
    public bool TriDescendant { get; set; }
    public List<FiltreColonne> Filtres { get; } = [];
    public PreferencesGrille Preferences { get; set; } = new();
    public bool PreferencesLues { get; set; }

    public void Filtrer(ChangementFiltre changement)
    {
        var position = Filtres.FindIndex(f => f.Colonne == changement.Colonne);
        if (changement.Filtre is null)
        {
            if (position >= 0) Filtres.RemoveAt(position);
        }
        else if (position >= 0) Filtres[position] = changement.Filtre;
        else Filtres.Add(changement.Filtre);
    }

    public void Trier(ChangementTri tri)
    {
        TriCle = tri.Descendant is null ? null : tri.Colonne;
        TriDescendant = tri.Descendant ?? false;
        Preferences.TriCle = TriCle;
        Preferences.TriDescendant = TriDescendant;
    }

    // Préférences lues une fois par session : colonnes inconnues retirées, tri mémorisé appliqué
    public void AppliquerPreferences<T>(PreferencesGrille p, IReadOnlyList<ColonneListe<T>> colonnes)
    {
        p.Masquees.RemoveAll(cle => !colonnes.Any(c => c.Cle == cle && !c.Obligatoire));
        Preferences = p;
        PreferencesLues = true;
        if (colonnes.Any(c => c.Cle == p.TriCle))
        {
            TriCle = p.TriCle;
            TriDescendant = p.TriDescendant;
        }
    }

    public int Largeur<T>(ColonneListe<T> c) =>
        Preferences.Largeurs.TryGetValue(c.Cle, out var px) && px > 0 ? px : c.Largeur;

    public ColonneListe<T>[] Visibles<T>(IEnumerable<ColonneListe<T>> colonnes) =>
        colonnes.Where(c => !Preferences.Masquees.Contains(c.Cle)).ToArray();

    // Lignes affichées : filtrées par colonne (en mémoire), puis triées ; sans tri choisi, l'ordre de la recherche
    public List<T> Appliquer<T>(IEnumerable<T> lignes, IReadOnlyList<ColonneListe<T>> colonnes)
    {
        foreach (var f in Filtres)
        {
            var colonne = colonnes.FirstOrDefault(c => c.Cle == f.Colonne);
            if (colonne is null) continue;
            lignes = lignes.Where(l => FiltresColonnes.Accepte(f, colonne.Type,
                colonne.Type == TypeColonne.Texte ? colonne.Texte(l) : colonne.CleTri(l)));
        }
        var tri = colonnes.FirstOrDefault(c => c.Cle == TriCle);
        if (tri is not null)
            lignes = TriDescendant
                ? lignes.OrderByDescending(tri.CleTri, Comparer<object?>.Default)
                : lignes.OrderBy(tri.CleTri, Comparer<object?>.Default);
        return lignes.ToList();
    }
}
