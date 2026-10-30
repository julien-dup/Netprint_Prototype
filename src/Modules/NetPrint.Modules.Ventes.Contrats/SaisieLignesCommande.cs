namespace NetPrint.Modules.Ventes.Contrats;

// =====================================================================
// Saisie des lignes d'une commande : reprise de NEG520 (liste, options), CTL520 (contrôles et prix), MAJ520 (écriture).
// Différence de fond avec le 5250 : chaque ligne est enregistrée seule, en une transaction, avec le recalcul de
// l'en-tête (le 5250 réécrivait toutes les lignes à la validation F9 de la liste).
// =====================================================================
public interface ISaisieLignesCommande
{
    // Nouvelle ligne avec ses valeurs par défaut (code vente de l'en-tête, statut normal, adresse par défaut du client)
    Task<LigneSaisie> NouvelleAsync(CleCommande commande, CancellationToken ct = default);

    Task<LigneSaisie?> LireAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default);

    // « Entrée » du 5250 : reprend les valeurs de l'article (désignation, unité, emballage, norme, emplacement, textes)
    // quand l'article change, propose le prix (cascade de tarifs) tant qu'aucun prix n'a été saisi, recalcule montant,
    // commission et maxi livrable. Ne contrôle pas et n'écrit rien.
    Task<LigneSaisie> CompleterAsync(CleCommande commande, LigneSaisie ligne, CancellationToken ct = default);

    // Contrôles de CTL520 puis écriture (MAJ520) et recalcul de l'en-tête. Les avertissements (stock insuffisant,
    // date passée...) doivent avoir été acceptés : sinon SaisieInvalideException avec Forcable = true.
    // Renvoie le n° de la ligne.
    Task<int> EnregistrerAsync(CleCommande commande, LigneSaisie ligne, CancellationToken ct = default);

    // Options de la liste (NEG520) : suppression (physique, comme MAJ520), solde, réactivation
    Task SupprimerAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default);
    Task SolderAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default);
    Task ReactiverAsync(CleCommande commande, int numeroLigne, CancellationToken ct = default);
}

// Actions sur la commande entière (options SO, RE et 4 de DOS500S)
public interface IActionsCommande
{
    // Toutes les lignes soldées, commande soldée à la date du jour (droit « Création » comme DOS500S)
    Task SolderAsync(CleCommande commande, CancellationToken ct = default);

    // Commande remise en cours ; ses lignes soldées le restent
    Task ReactiverAsync(CleCommande commande, CancellationToken ct = default);

    // Refusée s'il existe un BL ou une facture (droit « Suppression »). Renvoie un avertissement si des données annexes
    // n'ont pas pu être nettoyées (la commande, elle, est supprimée), sinon null.
    Task<string?> SupprimerAsync(CleCommande commande, CancellationToken ct = default);
}

public static class StatutsLigne
{
    public const string Normal = "1";
    public const string ALaDemande = "3";
}

// Ligne saisie (écran NEG52002)
public sealed class LigneSaisie
{
    public int NumeroLigne { get; set; }               // 0 = nouvelle ligne
    public string CodeVente { get; set; } = "";
    public string Article { get; set; } = "";
    public string Indice { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Statut { get; set; } = StatutsLigne.Normal;

    // Quantités (en exemplaires ; en UC pour un article géré par conditionnement)
    public int? Quantite { get; set; }
    public int? QuantiteUc { get; set; }
    public string Unite { get; set; } = "";
    public int Emballage { get; set; }                 // nombre par UC (repris de l'article)
    public bool SaisieEnUc { get; set; }               // SPF010.AOPT3 = 'O'
    public int? MaxiLivrable { get; set; }
    public bool MaxiSaisi { get; set; }                 // maxi tapé : il ne suit plus la quantité

    // Prix
    public decimal? PrixBrut { get; set; }
    public bool PrixSaisi { get; set; }                // prix tapé par l'utilisateur : le tarif ne le remplace pas
    public decimal? Remise { get; set; }               // %
    public decimal? PrixCession { get; set; }
    public bool Gratuite { get; set; }
    public bool PrixADefinir { get; set; }
    public decimal? TauxCommission { get; set; }
    public bool CommissionSaisie { get; set; }          // taux tapé par l'utilisateur : il ne suit plus le prix
    public string SourcePrix { get; set; } = "";       // tarif retenu (information)

    // Calculés (CompleterAsync)
    public decimal PrixNet { get; set; }
    public decimal Montant { get; set; }

    // Livraison
    public DateOnly? DateLivraison { get; set; }
    public DateOnly? DateConfirmee { get; set; }
    public bool AdresseSiege { get; set; }             // adresse = siège du client (ZADRF)
    public int? NumeroAdresse { get; set; }            // A500F
    public string AdresseRaisonSociale { get; set; } = "";
    public string AdresseRue1 { get; set; } = "";
    public string AdresseRue2 { get; set; } = "";
    public string AdresseCodePostal { get; set; } = "";
    public string AdresseVille { get; set; } = "";
    public string AdressePays { get; set; } = "";
    public string NumeroAppel { get; set; } = "";
    public DateOnly? DateAppel { get; set; }

    // Divers
    public string Commentaire1 { get; set; } = "";
    public string Commentaire2 { get; set; } = "";
    public string Commentaire3 { get; set; } = "";
    public string Texte { get; set; } = "";            // texte libre (NEG100T, 19 lignes de 40)
    public string Norme { get; set; } = "";
    public string Emplacement { get; set; } = "";
    public bool RegroupementOf { get; set; }
    public string NouvelleApplication { get; set; } = "";
    public int? CompteGeneralFacture { get; set; }
    public int? CompteClientFacture { get; set; }

    // Lecture seule (ligne existante)
    public int QuantiteLivree { get; set; }
    public int QuantiteFacturee { get; set; }
    public bool Soldee { get; set; }
    public int LigneOrigine { get; set; }
    public int QuantiteOrigine { get; set; }           // DNQTEO : quantité initiale d'une ligne « à la demande »
    public bool EstAppel => Statut == StatutsLigne.Normal && LigneOrigine != 0;
    public string ArticleEnregistre { get; set; } = "";   // pour détecter un changement d'article
    public string IndiceEnregistre { get; set; } = "";
    public string ArticleComplete { get; set; } = "";     // article dont les valeurs ont été reprises (« article|indice »)
    public string Empreinte { get; set; } = "";

    // Avertissements acceptés par l'utilisateur (codes de AvertissementsLigne)
    public HashSet<string> AvertissementsAcceptes { get; set; } = [];

    public LigneSaisie Copier()
    {
        var c = (LigneSaisie)MemberwiseClone();
        c.AvertissementsAcceptes = [.. AvertissementsAcceptes];
        return c;
    }
}

// Avertissements forçables de CTL520 : codés pour être acceptés un par un
public static class AvertissementsLigne
{
    public const string ArticleRemplace = "article-remplace";          // NEG0037
    public const string ArticleAutreClient = "article-autre-client";   // ART0014
    public const string StockInsuffisant = "stock";                    // NEG0047
    public const string QuantiteArrondie = "arrondi";                  // NEG0035
    public const string QuantiteSousLivre = "sous-livre";              // NEG0028
    public const string DateHorsCalendrier = "calendrier";             // DAT0023
    public const string DatePassee = "date-passee";                    // DAT0005
    public const string JourNonTravaille = "jour-non-travaille";       // DAT0017
    public const string JourNonAutorise = "jour-client";               // BL00049
    public const string MaxiNonMultiple = "maxi-conditionnement";      // NEG0041
}
