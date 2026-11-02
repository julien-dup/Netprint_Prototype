namespace NetPrint.Web.Commun;

// =====================================================================
// Glossaire des écrans : un texte unique par notion métier, réutilisé partout (bulles ⓘ du composant Explication,
// titres de colonnes, légendes). Chaque module fournit ses termes par une ISourceGlossaire enregistrée dans son
// AddModuleXxx() ; les clés sont préfixées par le domaine (« devis. », « commande. »…).
// Les textes sont relus par le métier (compte rendu docs/compte-rendu-explications.md pour le devis).
// =====================================================================
public sealed record TermeGlossaire(string Cle, string Titre, string Definition);

public interface ISourceGlossaire
{
    IEnumerable<TermeGlossaire> Termes { get; }
}

public sealed class Glossaire
{
    private readonly Dictionary<string, TermeGlossaire> termes = new(StringComparer.OrdinalIgnoreCase);

    public Glossaire(IEnumerable<ISourceGlossaire> sources)
    {
        foreach (var t in sources.SelectMany(s => s.Termes)) termes[t.Cle] = t;
    }

    public TermeGlossaire? Trouver(string cle) => termes.GetValueOrDefault(cle);

    // Définition seule (bulles natives title, titres de colonnes) ; null si le terme n'existe pas
    public string? Definition(string cle) => Trouver(cle)?.Definition;
}
