namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Saisie d'un BL produit fini / négoce (cahier des charges lot 1 du 15/10, D:\docs\Netprint\BL-bons-de-livraison) :
// choix du dossier et des lignes de commande, saisie (en-tête, lignes, répartition par lot / emplacement), validation en
// une transaction. Étape 1 : création d'un BL sur un dossier, à partir des lignes de commande.
// =====================================================================
public interface ISaisieBl
{
    // Contrôles du dossier (C1 à C6) et lignes de commande restant à livrer
    Task<PreparationBl> PreparerAsync(int dossier, string cle, CancellationToken ct = default);

    // Saisie initialisée à partir des lignes de commande choisies (même adresse, même client facturé)
    Task<SaisieBl> OuvrirAsync(int dossier, string cle, IReadOnlyList<int> lignes, CancellationToken ct = default);

    // Lots / emplacements disponibles pour un article, dans son type de stock (dépôt principal)
    Task<IReadOnlyList<PositionStock>> PositionsAsync(SaisieBl saisie, LigneSaisieBl ligne, CancellationToken ct = default);

    // Contrôles et calculs sans écriture : erreurs bloquantes, avertissements (colisage, poids et totaux recalculés)
    Task<ControleBl> ControlerAsync(SaisieBl saisie, CancellationToken ct = default);

    // Validation : n° de BL attribué. Si le reste à livrer d'une ligne a diminué depuis l'ouverture (autre BL validé) et que la
    // quantité le dépasse : DepassementResteException, à confirmer puis à relancer avec depassementAccepte.
    Task<int> CreerAsync(SaisieBl saisie, bool depassementAccepte = false, CancellationToken ct = default);
}

public sealed record AdresseBl(
    int Numero, string FormeJuridique, string RaisonSociale, string Rue1, string Rue2, string CodePostal, string Ville,
    string Departement, string Pays)
{
    public string Libelle => string.Join(" · ", new[] { RaisonSociale, Rue1, $"{CodePostal} {Ville}".Trim() }.Where(x => x != ""));

    // Même destinataire (n° d'adresse, raison sociale, code postal, ville)
    public bool MemeDestinataire(AdresseBl a) =>
        Numero == a.Numero && RaisonSociale == a.RaisonSociale && CodePostal == a.CodePostal && Ville == a.Ville;
}

public sealed record LigneProposee(
    int NumeroLigne, string Article, string Indice, string Designation, int Commande, int Livre, int Reste, bool ADemande,
    AdresseBl Adresse, int CompteGeneralFacture, int CompteClientFacture, int? Opr, DateOnly? DateLivraison);

public sealed record PreparationBl(
    int Dossier, string Cle, string TypeDossier, string Designation, int CompteGeneral, int CompteClient, string NomClient,
    string Enseigne, IReadOnlyList<LigneProposee> Lignes);

// Position de stock proposée : dépôt / lot / emplacement, disponible (stock − déjà pris par les autres lignes du BL)
public sealed record PositionStock(int Depot, string Lot, string Emplacement, DateOnly? DateLimite, DateOnly? DateEntree, int Stock);

public sealed class PriseLot
{
    public int Depot { get; set; }
    public string Lot { get; set; } = "";
    public string Emplacement { get; set; } = "";
    public int Quantite { get; set; }
}

public sealed class LigneSaisieBl
{
    public int NumeroLigneCommande { get; set; }
    public string CodeLigne { get; set; } = "";
    public string Article { get; set; } = "";
    public string Indice { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Designation2 { get; set; } = "";
    public string Designation3 { get; set; } = "";
    public bool Texte { get; set; }                     // ligne de texte (désignations seules)
    public int Quantite { get; set; }
    public int Reste { get; set; }                      // reste à livrer à l'ouverture
    public string TypeEmballage { get; set; } = "";
    public int EmballagePar { get; set; }
    public decimal PoidsUc { get; set; }
    public int Colis { get; set; }
    public int ColisIncomplet { get; set; }
    public int Mecompte { get; set; }
    public bool Solder { get; set; }
    public string Etat { get; set; } = "T";
    // Article (SPF010) : géré en stock, type de stock, quantités en UC, fractionnable
    public bool GereEnStock { get; set; }
    public string TypeStock { get; set; } = "";
    public bool QuantiteEnUc { get; set; }
    public bool Fractionnable { get; set; } = true;
    public List<PriseLot> Prises { get; set; } = [];

    public int Reparti => Prises.Sum(p => p.Quantite);
    public decimal Poids => EmballagePar > 0 ? Colis * PoidsUc + PoidsUc * Mecompte / EmballagePar : 0;
}

public sealed class SaisieBl
{
    public int Dossier { get; set; }
    public string Cle { get; set; } = "";
    public string TypeDossier { get; set; } = "";
    public string Enseigne { get; set; } = "";
    public int CompteGeneral { get; set; }
    public int CompteClient { get; set; }
    public string NomClient { get; set; } = "";
    public DateOnly DateExpedition { get; set; }
    public AdresseBl Adresse { get; set; } = new(0, "", "", "", "", "", "", "", "");
    public string TypeLivraison { get; set; } = "";
    public int Transporteur { get; set; }
    public string TypePort { get; set; } = "F";
    public int Bareme { get; set; }
    public int Representant { get; set; }
    public string ReferenceCommande { get; set; } = "";
    public DateOnly? DateCommande { get; set; }
    public bool Facturable { get; set; } = true;
    public bool FacturationAutomatique { get; set; }
    public int CompteGeneralFacture { get; set; }
    public int CompteClientFacture { get; set; }
    public string Observation1 { get; set; } = "";
    public string Observation2 { get; set; } = "";
    public string Observation3 { get; set; } = "";
    public string Mention { get; set; } = "";
    public string Incoterm { get; set; } = "";
    public int Palettes { get; set; }
    public List<LigneSaisieBl> Lignes { get; set; } = [];
    public List<string> TextePied { get; set; } = [];
}

// Reste à livrer diminué entre l'ouverture et la validation : avertissement à confirmer (cahier des charges § 9.2)
public sealed class DepassementResteException(string message) : Exception(message);

public sealed record ControleBl(IReadOnlyList<string> Erreurs, IReadOnlyList<string> Avertissements, int Poids, int Colis)
{
    public bool Valide => Erreurs.Count == 0;
}
