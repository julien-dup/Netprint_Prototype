namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Paramètres de facturation automatique d'une commande (F13 de DOS505, fenêtre DOS50505, table AB001FA) : la commande
// est-elle prise en facturation automatique, à quelle adresse envoyer la facture, acompte, conditions de règlement.
// Les valeurs par défaut (client) sont écrites à la création de la commande ; cette fenêtre les modifie.
// =====================================================================
public interface IFacturationAutomatique
{
    // null si la commande n'existe pas ou n'est pas accessible
    Task<FacturationAutomatique?> LireAsync(CleCommande commande, CancellationToken ct = default);

    // Refusé si la commande est soldée ou sous-traitée ; SaisieInvalideException si une zone est incorrecte
    Task EnregistrerAsync(CleCommande commande, FacturationAutomatique facturation, CancellationToken ct = default);
}

public sealed class FacturationAutomatique
{
    public bool Automatique { get; set; }                 // FAAUTO
    // Adresse d'envoi de la facture : n° d'adresse du client (FANAL) ou adresse saisie, pas les deux
    public int? NumeroAdresse { get; set; }
    public string FormeJuridique { get; set; } = "";
    public string Nom { get; set; } = "";
    public string Rue1 { get; set; } = "";
    public string Rue2 { get; set; } = "";
    public string CodePostal { get; set; } = "";
    public string Ville { get; set; } = "";
    public string Attention { get; set; } = "";           // FAATT
    public bool EditionDetailBl { get; set; }             // FAEDTB
    public string OrigineTechnique { get; set; } = "";    // FAORTE, table OR
    public decimal? Acompte { get; set; }                 // FACPT : acompte déjà versé
    public string ConditionsReglement { get; set; } = ConditionsFacturation.Facture;   // FACRC
    public string TextePied { get; set; } = ConditionsFacturation.DonneurOrdre;        // FATPC

    // Lecture seule : le client a une adresse spéciale de facturation (AA0003.CADRS)
    public bool AdresseSpecialeClient { get; set; }
    // Valeurs lues (AB001FA), comparées à l'enregistrement : une modification faite entre-temps est refusée
    public string Empreinte { get; set; } = "";
    public bool AdresseSaisie => FormeJuridique != "" || Nom != "" || Rue1 != "" || Rue2 != "" || CodePostal != "" || Ville != "";
}

// Origine des conditions de règlement et du texte de pied de facture
public static class ConditionsFacturation
{
    public const string DonneurOrdre = "1";
    public const string Facture = "2";
}
