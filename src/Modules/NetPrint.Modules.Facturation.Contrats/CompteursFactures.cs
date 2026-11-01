namespace NetPrint.Modules.Facturation.Contrats;

// =====================================================================
// Compteurs de factures (FAC040 : prochain n° par année, ou par mois en numérotation mensuelle). GETFACTURE ne les crée
// jamais : ils se créaient à la main par l'écran FAC040. Dans le SaaS, la facturation qui en manque le signale
// (CompteurFacturesAbsentException) et l'écran propose de le créer sur place (David, 01/11), puis on relance.
// =====================================================================

// Compteur absent pour l'année (Mois = 0) ou le mois de la facture ; PremierPropose : n° de départ habituel (mois × 10 000 + 1
// en mensuel, année sur 2 chiffres × 10 000 + 1 en annuel, comme les données)
public sealed class CompteurFacturesAbsentException(int annee, int mois, int premierPropose)
    : Exception($"Il n'y a pas de compteur de factures pour {(mois == 0 ? $"l'année {annee}" : $"{mois:00}/{annee}")} : aucune facture ne peut être numérotée.")
{
    public int Annee => annee;
    public int Mois => mois;
    public int PremierPropose => premierPropose;
}

public interface ICompteursFactures
{
    // Crée le compteur (droit FAC040) ; refus s'il existe déjà ou si le premier n° est déjà pris par une facture de la période
    Task CreerAsync(int annee, int mois, int premier, CancellationToken ct = default);
}
