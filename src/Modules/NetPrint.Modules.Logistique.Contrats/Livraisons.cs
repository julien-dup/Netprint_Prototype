using NetPrint.Socle;

namespace NetPrint.Modules.Logistique.Contrats;

// =====================================================================
// Livraisons à effectuer (reprise de l'application WinDev LivraisonsAEffectuer) : lignes de commande restant à livrer sur une
// période, avec stock disponible, en-cours de fabrication, conditionnement, OPR et pré-BL ; « Créer le BL » sur les lignes
// choisies d'un dossier ; fiche de préparation en PDF (EXP078).
// =====================================================================
public interface ILivraisons
{
    Task<ResultatRecherche<LigneALivrer>> RechercherAsync(CriteresLivraisons criteres, CancellationToken ct = default);
}

public enum PeriodeLivraison { SemaineEnCours, SemaineSuivante, Dates }

public enum FiltreOpr { Tous, SansOpr, AvecOpr }

public sealed class CriteresLivraisons
{
    public PeriodeLivraison Periode { get; set; } = PeriodeLivraison.SemaineEnCours;
    public DateOnly? Du { get; set; }
    public DateOnly? Au { get; set; }
    public int? CompteGeneral { get; set; }
    public int? CompteClient { get; set; }
    public int? NumeroAdresse { get; set; }
    public int? NumeroCommande { get; set; }
    public string? Article { get; set; }
    public string? ReferenceImprime { get; set; }
    public string? Appel { get; set; }
    public string? Fabricant { get; set; }
    public int? Representant { get; set; }
    public string? Enseigne { get; set; }
    public int? NumeroOpr { get; set; }
    public bool ArticlesNonStockes { get; set; }
    public bool LignesSoldees { get; set; }
    public FiltreOpr Opr { get; set; } = FiltreOpr.Tous;

    // Bornes de la période (semaine ISO du lundi au dimanche, ou dates saisies)
    public (DateOnly Du, DateOnly Au) Bornes(DateOnly aujourdhui)
    {
        var lundi = aujourdhui.AddDays(-(((int)aujourdhui.DayOfWeek + 6) % 7));
        return Periode switch
        {
            PeriodeLivraison.SemaineSuivante => (lundi.AddDays(7), lundi.AddDays(13)),
            PeriodeLivraison.Dates => (Du ?? DateOnly.MinValue, Au ?? DateOnly.MaxValue),
            _ => (lundi, lundi.AddDays(6)),
        };
    }
}

// Une ligne de commande à livrer
public sealed record LigneALivrer(
    int Dossier, string Cle, int Ligne, string ReferenceCommande, int CompteGeneral, int CompteClient, string NomClient, bool ClientBloque,
    string Article, string Indice, string ReferenceImprime, string Designation, DateOnly? DateLivraison, DateOnly? DateConfirmee,
    string Appel, int Commande, int Livre, int Reste, bool Soldee, AdresseBl Adresse, int? Opr, int? PreBl, int Stock, int EnFabrication,
    int Uc, int Cartons, int Palettes, decimal Poids, decimal MontantAFacturer, int MaxiLivrable, string Fabricant, int Representant)
{
    public string LibelleCommande => $"{Dossier:00000}/{Cle}";
    public bool StockSuffisant => Stock >= Reste;
}
