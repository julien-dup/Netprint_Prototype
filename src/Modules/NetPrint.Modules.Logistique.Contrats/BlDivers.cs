namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// BL divers (cahier des charges lot 1 § 11) : BL d'un client, d'un dossier ou d'un fournisseur (un et un seul), sans article,
// sans stock ni commande : destinataire, transport, quantité, colis, poids, palettes et lignes de désignation libres.
// =====================================================================
public interface IBlDivers
{
    // Nouveau BL divers pour un tiers : adresses proposées (client : adresse principale puis adresses de livraison ; dossier :
    // celles de son client ; fournisseur : sa fiche)
    Task<SaisieBlDivers> NouveauAsync(TiersBlDivers tiers, CancellationToken ct = default);

    // BL divers enregistré (un seul élément), pour le modifier
    Task<SaisieBlDivers> OuvrirAsync(int numeroBl, CancellationToken ct = default);

    // Contrôles sans écriture
    Task<IReadOnlyList<string>> ControlerAsync(SaisieBlDivers saisie, CancellationToken ct = default);

    // Création ou modification : n° de BL
    Task<int> ValiderAsync(SaisieBlDivers saisie, CancellationToken ct = default);
}

// Un et un seul tiers : client (compte général + auxiliaire), dossier ou fournisseur
public sealed record TiersBlDivers(int CompteGeneral, int CompteClient, int Dossier, string Cle, int Fournisseur)
{
    public int Renseignes => (CompteClient > 0 ? 1 : 0) + (Dossier > 0 ? 1 : 0) + (Fournisseur > 0 ? 1 : 0);
}

public sealed class SaisieBlDivers
{
    public int NumeroBl { get; set; }   // 0 : nouveau BL
    public TiersBlDivers Tiers { get; set; } = new(0, 0, 0, "", 0);
    public string NomTiers { get; set; } = "";
    public string Enseigne { get; set; } = "";
    public IReadOnlyList<string> Enseignes { get; set; } = [];
    public IReadOnlyList<AdresseBl> Adresses { get; set; } = [];
    public DateOnly DateExpedition { get; set; }
    public AdresseBl Adresse { get; set; } = new(0, "", "", "", "", "", "", "", "");
    public string TypeLivraison { get; set; } = "";
    public int Transporteur { get; set; }
    public string TypePort { get; set; } = "F";
    public int Bareme { get; set; }
    public string Observation1 { get; set; } = "";
    public string Observation2 { get; set; } = "";
    public string Observation3 { get; set; } = "";
    public string Mention { get; set; } = "";
    public int Quantite { get; set; }
    public int Colis { get; set; }
    public int Poids { get; set; }
    public int Palettes { get; set; }
    public List<string> Designations { get; set; } = [];
    public List<string> TextePied { get; set; } = [];
}
