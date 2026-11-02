namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Facturation automatique des BL (chaîne FAC400 → FAC401 → FAC402P, provisoire FAC412 / FAC412P) : les éléments de BL
// facturables et pas encore facturés deviennent des factures (BL complets) et des avoirs (BL de retour), regroupés selon le
// mode de facturation automatique du client (AA0003.CDOPT2, 1 à 8) et sa fréquence (CDOPT3). Simulation sans écriture
// (l'ancien « provisoire »), puis génération définitive.
// =====================================================================
public static class FrequencesFacturation
{
    public const string Aucune = "";
    public const string Trimestrielle = "Q";
    public const string Hebdomadaire = "H";
    public const string Mensuelle = "M";

    public static readonly IReadOnlyList<(string Code, string Libelle)> Toutes =
        [(Aucune, "Sans fréquence"), (Mensuelle, "Mensuelle"), (Hebdomadaire, "Hebdomadaire"), (Trimestrielle, "Trimestrielle")];
}

// Critères de lancement (écran FAC40001). Au moins une plage de BL : n° ou dates d'expédition.
public sealed class CriteresFacturationAuto
{
    public string Enseigne { get; set; } = "";
    public int? BlDu { get; set; }
    public int? BlAu { get; set; }
    public DateOnly? ExpedieDu { get; set; }
    public DateOnly? ExpedieAu { get; set; }
    public int? CompteGeneralDu { get; set; }
    public int? CompteGeneralAu { get; set; }
    public int? CompteClientDu { get; set; }
    public int? CompteClientAu { get; set; }
    public DateOnly? DateFacture { get; set; }
    public string Frequence { get; set; } = FrequencesFacturation.Aucune;
}

// Critère refusé : champ concerné (nom de la propriété) et message
public sealed record ErreurLancement(string Champ, string Message);

public interface IFacturationAutomatique
{
    // Ce que l'écran doit savoir avant la saisie : enseignes gérées, droit d'éditer en définitif (FAC200)
    Task<ContexteFacturationAuto> PreparerAsync(CancellationToken ct = default);

    // Simulation (ancien provisoire FAC412 / FAC412P) : factures qui seraient créées, sans rien écrire
    Task<ResultatFacturationAuto> SimulerAsync(CriteresFacturationAuto criteres, CancellationToken ct = default);

    // Génération définitive (FAC402P), une transaction par facture ; puis, si demandé, édition définitive des factures sans anomalie.
    // Empreinte : celle de la simulation confirmée ; refus si les factures à créer ne sont plus les mêmes (BL, prix, client changés)
    Task<ResultatFacturationAuto> GenererAsync(CriteresFacturationAuto criteres, bool editerDefinitif, string empreinte,
        CancellationToken ct = default);
}

public sealed record ContexteFacturationAuto(bool GereEnseignes, bool PeutEditerDefinitif);

public sealed record LigneFactureAuto(string Code, string Designation, decimal Quantite, string Unite, decimal PrixUnitaire, decimal Montant,
    int Bl, bool Texte);

public sealed record ElementFactureAuto(int Numero, int Dossier, string Cle, IReadOnlyList<int> Bls, decimal Montant,
    IReadOnlyList<LigneFactureAuto> Lignes);

// Facture prévue (simulation) ou créée (Numero renseigné) ; Refus : raison si la génération de cette facture a été refusée ;
// EditeeDefinitif : éditée en définitif dans la foulée ; Avertissement : créée, mais l'édition définitive a échoué
public sealed record FactureAuto(
    int Rang, bool Avoir, int CompteGeneral, int CompteClient, string NomClient, int CompteGeneralFacture, int CompteClientFacture, int Mode,
    decimal Montant, IReadOnlyList<string> Anomalies, IReadOnlyList<ElementFactureAuto> Elements, int? Numero = null, string? Refus = null,
    bool EditeeDefinitif = false, string? Avertissement = null)
{
    public bool AControler => Anomalies.Count > 0;
    public int NombreBl => Elements.SelectMany(e => e.Bls).Distinct().Count();
}

public sealed record BlEcarte(int Bl, int Element, int Dossier, string Cle, int CompteGeneral, int CompteClient, string NomClient, string Raison);

// Erreurs : critères refusés (rien n'a été fait) ; Tronque : plus de 10 000 éléments de BL, sélection à resserrer
public sealed record ResultatFacturationAuto(IReadOnlyList<ErreurLancement> Erreurs, DateOnly? DateFacture, IReadOnlyList<FactureAuto> Factures,
    IReadOnlyList<BlEcarte> Ecartes, bool Tronque)
{
    // Ce qui sera créé : type, clients, mode, BL et montant de chaque facture
    public string Empreinte => Empreindre(Factures);

    public static string Empreindre(IEnumerable<FactureAuto> factures) => string.Join(";", factures.Select(f =>
        $"{(f.Avoir ? "A" : "F")}{f.CompteGeneral}.{f.CompteClient}.{f.CompteGeneralFacture}.{f.CompteClientFacture}.{f.Mode}.{f.Montant}."
        + string.Join(",", f.Elements.SelectMany(e => e.Bls))));
}
