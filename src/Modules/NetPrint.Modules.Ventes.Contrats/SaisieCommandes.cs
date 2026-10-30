namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Saisie de l'en-tête d'une commande de négoce (3) ou de produit fini (4) : reprise de DOS505.
// Création en deux temps (comme les écrans DOS50501 puis DOS50512) : le client, le type, l'enseigne et la date
// donnent les valeurs par défaut, puis l'en-tête complet est contrôlé et enregistré en une transaction.
// Le n° de dossier n'est réservé (GETDOSSIER 'D') qu'à l'enregistrement : pas de n° provisoire affiché.
// =====================================================================
public interface ISaisieCommandes
{
    Task<ContexteSaisieCommande> LireContexteAsync(CancellationToken ct = default);

    // Écran 1 : contrôles du client (existence, clôture, qualité, encours), de l'enseigne, valeurs par défaut.
    // Lève SaisieInvalideException si un contrôle bloque.
    Task<EnteteSaisie> PreparerAsync(DebutCommande debut, CancellationToken ct = default);

    // Écran 2 + validation : contrôles de l'en-tête, n° de dossier, écriture (AB001, AB001FA, AA0003, DOS040, EVE200)
    Task<CleCommande> CreerAsync(EnteteSaisie saisie, CancellationToken ct = default);

    // Modification : null si la commande n'existe pas ou n'est pas accessible
    Task<EnteteSaisie?> LireAsync(CleCommande cle, CancellationToken ct = default);

    // Refusée si la commande est soldée, sous-traitée, ou modifiée par quelqu'un d'autre depuis sa lecture
    Task ModifierAsync(CleCommande cle, EnteteSaisie saisie, CancellationToken ct = default);
}

public sealed record ContexteSaisieCommande(
    bool GereEnseignes,                    // AA0007.SOP03 = 'O' : enseigne obligatoire
    bool SousTraitanceSaisissable,         // DOS005.PSTRIS = 'O'
    IReadOnlyList<CodeLibelle> Enseignes,
    IReadOnlyList<CodeLibelle> ModesReglement,   // table RG
    IReadOnlyList<CodeLibelle> CodesVente,       // FAC020 de type V
    DroitsCommandes Droits);

// Écran 1 (DOS50501)
public sealed class DebutCommande
{
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public string TypeCommande { get; set; } = TypesCommande.ProduitFini;
    public string? Enseigne { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    // F19 de DOS505 : passer outre la qualité client 1 (alerte) ou 5 (bloqué en livraison) après avertissement
    public bool ForcerQualite { get; set; }
}

// En-tête saisi ou modifié (écran DOS50512). Les textes sont repris tels quels, en majuscules pour les codes.
public sealed class EnteteSaisie
{
    // Client et dossier (écran 1 ; non modifiables ensuite)
    public int CompteGeneral { get; set; }
    public int CompteClient { get; set; }
    public string NomClient { get; set; } = "";
    public string Enseigne { get; set; } = "";
    public DateOnly DateDossier { get; set; }
    public string TypeCommande { get; set; } = TypesCommande.ProduitFini;
    public string Categorie { get; set; } = "";        // $1 négoce, $2 produit fini
    public string SousCategorie { get; set; } = "";

    // Commande
    public string TypeFacturation { get; set; } = "1";   // 1 à chaque livraison, 2 en totalité, 3 multi-échéance
    public string Designation1 { get; set; } = "";
    public string Designation2 { get; set; } = "";
    public DateOnly? DateLivraison { get; set; }
    public int? SemaineLivraison { get; set; }          // date ou semaine (au moins l'une)
    public int? AnneeLivraison { get; set; }
    public int? AdresseArc { get; set; }
    public string Interlocuteur { get; set; } = "";
    public string ReferenceCommandeClient { get; set; } = "";
    public DateOnly? DateCommandeClient { get; set; }

    // Facturation et commercial
    public int? CompteGeneralFacture { get; set; }
    public int? CompteClientFacture { get; set; }
    public bool Revendeur { get; set; }
    public int? Representant { get; set; }
    public decimal? TauxCommission { get; set; }
    public string Fabricant { get; set; } = "";
    public string CodeVente { get; set; } = "";
    public string Monnaie { get; set; } = "";           // CCMON, repris du client
    public bool SousTraitee { get; set; }

    // Livraison
    public string EnlevementOuLivraison { get; set; } = "A";   // E enlèvement, A à livrer
    public string Port { get; set; } = "F";                     // D dû, A avancé, F franco
    public int? NbLieux { get; set; } = 1;
    public string AdresseFacturation { get; set; } = "F";       // F, L, R, C

    // Règlement
    public string ModeReglement { get; set; } = "";
    public int? Echeance { get; set; }                 // jours
    public int? Le { get; set; }
    public int? Arrete { get; set; }
    public decimal? Escompte { get; set; }
    public string Releve { get; set; } = "";           // R N H D Q M

    // Divers
    public string NomHonoraires { get; set; } = "";
    public decimal? MontantHonoraires { get; set; }
    public decimal? TauxHonoraires { get; set; }
    public int? CommandesGroupees { get; set; }
    public string TexteCommandesGroupees { get; set; } = "";
    public bool EditionEtiquettes { get; set; } = true;

    // Texte complémentaire de la commande (DOS560, option 3 de NEG510 ; table AB0011) : 8 lignes de 40 caractères,
    // imprimé sur l'ARC, le BL ou la facture selon les cases (non par défaut, comme NEG510)
    public string TexteComplementaire { get; set; } = "";
    public bool TexteSurArc { get; set; }
    public bool TexteSurBl { get; set; }
    public bool TexteSurFacture { get; set; }

    // Modification : facturation figée dès qu'une ligne existe ; commande soldée ou sous-traitée (CCOP11) non modifiable ;
    // empreinte de la lecture (contrôle de concurrence)
    public bool AvecLignes { get; set; }
    public bool Soldee { get; set; }
    public bool Bloquee { get; set; }
    public string Empreinte { get; set; } = "";

    // Avertissements de la préparation (qualité client forcée...)
    public List<string> Avertissements { get; set; } = [];

    public EnteteSaisie Copier()
    {
        var c = (EnteteSaisie)MemberwiseClone();
        c.Avertissements = [.. Avertissements];
        return c;
    }
}

// Valeurs autorisées (DDS et tables de DOS505)
public static class ValeursEntete
{
    public static readonly int[] Echeances = [0, 20, 30, 45, 60, 90, 120, 150, 180];   // table TJE
    public static readonly string[] Releves = ["R", "N", "H", "D", "Q", "M"];          // table TTR
}

// Contrôle de saisie : un message par zone (nom de propriété de EnteteSaisie / DebutCommande), affiché à côté de la zone
public sealed record ErreurSaisie(string Zone, string Message);

public sealed class SaisieInvalideException(IReadOnlyList<ErreurSaisie> erreurs)
    : Exception(string.Join("\n", erreurs.Select(e => e.Message)))
{
    public IReadOnlyList<ErreurSaisie> Erreurs { get; } = erreurs;

    // Qualité client 1 ou 5 : l'utilisateur peut confirmer et forcer (F19 de DOS505)
    public bool Forcable { get; init; }
}
